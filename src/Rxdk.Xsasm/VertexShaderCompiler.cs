namespace Rxdk.Xsasm;

/// <summary>
/// Translates a parsed D3D vertex-shader token stream into NV2A microcode
/// (<see cref="VsInstruction"/>s), the port of api.cpp's D3DTokensToUCode +
/// the InstructionsToMicrocode screen-space postfix. See VERTEX_BACKEND_SPEC.md.
///
/// This is Phase 1: faithful translation. It reproduces the unoptimized goldens
/// (those whose .xvu is exactly source-length + 2). The pairing/reorder/rename
/// optimizer that the shrunk goldens need is Phase 2 and not done here.
/// </summary>
internal sealed class VertexShaderCompiler
{
    private readonly List<Diagnostic> _diags;

    // D3D token bitfields (d3d8types.h), mirrored from Isa / the parser.
    private const uint OpcodeMask   = 0x0000FFFF;
    private const uint Coissue      = 0x40000000;
    private const uint RegNumMask   = 0x00000FFF;
    private const int  RegTypeShift = 28;
    private const uint RegTypeMask  = 0x70000000;
    private const uint WriteMaskAll = 0x000F0000;
    private const int  SwizzleShift = 16;
    private const int  SrcModShift  = 24;
    private const uint SrcModMask   = 0x0F000000;
    private const uint AddressMode  = 0x00002000;   // D3DVS_ADDRESSMODE (a0 relative)

    public VertexShaderCompiler(List<Diagnostic> diags) => _diags = diags;

    /// <summary>Assemble the token stream (from the parser's Result.Code) to microcode.</summary>
    public List<VsInstruction> Compile(IReadOnlyList<uint> tokens, ShaderKind kind, bool screenSpace, bool stateShader)
    {
        var ucode = new List<VsInstruction>();
        int pos = 1;                        // skip the version DWORD
        Translate(tokens, ref pos, ucode);

        // InstructionsToMicrocode: append the fixed screen-space postfix unless
        // this is a screen-space or vertex-state shader.
        if (!screenSpace && !stateShader)
        {
            AppendPostfix(ucode);
        }

        if (ucode.Count > 136)
            Error("vertex shader exceeds the 136-instruction hardware limit");

        if (ucode.Count > 0)
            ucode[^1].Eos = 1;

        return ucode;
    }

    // --- D3DTokensToUCode ------------------------------------------------------

    private void Translate(IReadOnlyList<uint> tokens, ref int pos, List<VsInstruction> ucode)
    {
        while (pos < tokens.Count)
        {
            uint opcode = tokens[pos] & OpcodeMask;

            if (opcode == 0xFFFF)           // D3DSIO_END
                return;
            if (opcode == 0xFFFD)           // TOKEN_RETURN (end of a macro expansion)
                return;
            if (opcode == 0xFFFE)           // D3DSIO_COMMENT
            {
                int n = (int)((tokens[pos] & 0x7FFF0000) >> 16);
                pos += 1 + n;
                continue;
            }

            // Matrix macros expand (recursively) into runs of dp3/dp4.
            if (opcode is >= 20 and <= 24)  // M4x4..M3x2
            {
                ExpandMatrix(tokens, ref pos, ucode, opcode);
                continue;
            }

            var inst = NewInstruction();
            bool coissue = (tokens[pos] & Coissue) != 0;

            // outputs: 0 none, 1 MAC, 2 ILU, 3 ARL (dst parsed then discarded).
            // inputs: slot bitmask -- bit0 A, bit1 B, bit2 C.
            int outputs, inputs;
            DispatchOpcode(opcode, tokens, pos, inst, out outputs, out inputs);

            pos++;                          // past the opcode token

            // Output.
            if (outputs == 3)               // ARL: consume+discard the dst token
            {
                pos++;
                outputs = 0;
            }
            if (outputs != 0)
            {
                ParseOutput(tokens[pos], inst, outputs);
                pos++;
            }

            // Inputs A(1) / B(2) / C(4).
            if ((inputs & 1) != 0) { ParseInput(tokens[pos], inst, 'A'); pos++; }
            if ((inputs & 2) != 0) { ParseInput(tokens[pos], inst, 'B'); pos++; }
            if ((inputs & 4) != 0) { ParseInput(tokens[pos], inst, 'C'); pos++; }

            if (coissue)
            {
                // Co-issue pairing folds this instruction into the previous one.
                // Phase 1 does not implement the pairer; a co-issued source is
                // rare in the unoptimized goldens. Flag rather than mis-emit.
                Error("co-issue pairing is not yet supported by the vertex back end");
            }

            ucode.Add(inst);
        }
    }

    private void DispatchOpcode(uint opcode, IReadOnlyList<uint> tokens, int pos,
                                VsInstruction inst, out int outputs, out int inputs)
    {
        outputs = 1; inputs = 0;
        switch (opcode)
        {
            case 0:  inst.Mac = VsInstruction.MacNop; outputs = 0; inputs = 0; break;   // NOP
            case 1:  // MOV -- ARL if the destination is address register 0
                if (IsAddrDest(tokens[pos + 1]))
                {
                    inst.Mac = VsInstruction.MacArl; outputs = 3; inputs = 1;
                }
                else { inst.Mac = VsInstruction.MacMov; inputs = 1; }
                break;
            case 2:  inst.Mac = VsInstruction.MacAdd; inputs = 5; break;                // ADD (A|C)
            case 3:  inst.Mac = VsInstruction.MacAdd; inst.Cne = 1; inputs = 5; break;  // SUB = ADD, C negated
            case 4:  inst.Mac = VsInstruction.MacMad; inputs = 7; break;                // MAD (A|B|C)
            case 5:  inst.Mac = VsInstruction.MacMul; inputs = 3; break;                // MUL (A|B)
            case 6:  inst.Ilu = VsInstruction.IluRcp; outputs = 2; inputs = 4; break;   // RCP (C)
            case 7:  inst.Ilu = VsInstruction.IluRsq; outputs = 2; inputs = 4; break;   // RSQ
            case 8:  inst.Mac = VsInstruction.MacDp3; inputs = 3; break;                // DP3
            case 9:  inst.Mac = VsInstruction.MacDp4; inputs = 3; break;                // DP4
            case 10: inst.Mac = VsInstruction.MacMin; inputs = 3; break;                // MIN
            case 11: inst.Mac = VsInstruction.MacMax; inputs = 3; break;                // MAX
            case 12: inst.Mac = VsInstruction.MacSlt; inputs = 3; break;                // SLT
            case 13: inst.Mac = VsInstruction.MacSge; inputs = 3; break;                // SGE
            case 16: inst.Ilu = VsInstruction.IluLit; outputs = 2; inputs = 4; break;   // LIT
            case 17: inst.Mac = VsInstruction.MacDst; inputs = 3; break;                // DST
            case 41: inst.Ilu = VsInstruction.IluExp; outputs = 2; inputs = 4; break;   // EXPP
            case 42: inst.Ilu = VsInstruction.IluLog; outputs = 2; inputs = 4; break;   // LOGP
            case 256: inst.Mac = VsInstruction.MacDph; inputs = 3; break;               // DPH
            case 257: inst.Ilu = VsInstruction.IluRcc; outputs = 2; inputs = 4; break;  // RCC
            case 19:  // FRC macro -- expanded separately
            case 14:  // EXP macro
            case 15:  // LOG macro
                Error("frc/exp/log macro expansion is not yet supported");
                outputs = 0; inputs = 0;
                break;
            default:
                Error($"unsupported vertex opcode 0x{opcode:x}");
                outputs = 0; inputs = 0;
                break;
        }
    }

    // --- operand parsing -------------------------------------------------------

    private static bool IsAddrDest(uint token) =>
        (RegFile)((token & RegTypeMask) >> RegTypeShift) == RegFile.AddrOrTexture &&
        (token & RegNumMask) == 0;

    private void ParseOutput(uint token, VsInstruction inst, int outputs)
    {
        var file = (RegFile)((token & RegTypeMask) >> RegTypeShift);
        uint mask2 = ReverseMask((token & WriteMaskAll) >> 16);

        if (file == RegFile.Temp)
        {
            inst.Rw = token & RegNumMask;
            if (outputs == 1) inst.Rwm = mask2; else inst.Swm = mask2;
            return;
        }

        inst.Owm = mask2;
        inst.Om = (uint)(outputs - 1);      // 0 MAC, 1 ILU

        if (file == RegFile.Const)
        {
            inst.Oc = MapDx8ToUcode(token & RegNumMask);
            if ((token & AddressMode) != 0) inst.Cin = 1;
        }
        else
        {
            inst.Oc = 0x100u | OutputRegOffset(file, token & RegNumMask, token);
        }
    }

    private void ParseInput(uint token, VsInstruction inst, char slot)
    {
        var file = (RegFile)((token & RegTypeMask) >> RegTypeShift);
        uint mux, rr = 0;

        switch (file)
        {
            case RegFile.Temp:  mux = VsInstruction.MuxR; rr = token & RegNumMask; break;
            case RegFile.Const: mux = VsInstruction.MuxC; inst.Ca = MapDx8ToUcode(token & RegNumMask);
                                if ((token & AddressMode) != 0) inst.Cin = 1; break;
            default:            mux = VsInstruction.MuxV; inst.Va = token & RegNumMask; break;  // Input
        }

        uint neg = ((token & SrcModMask) >> SrcModShift) != 0 ? 1u : 0u;

        // Swizzle selects: x@16, y@18, z@20, w@22.
        uint xs = (token >> (SwizzleShift + 0)) & 3;
        uint ys = (token >> (SwizzleShift + 2)) & 3;
        uint zs = (token >> (SwizzleShift + 4)) & 3;
        uint ws = (token >> (SwizzleShift + 6)) & 3;

        // Scalar ILU (rcp/rcc/rsq/exp/log, not lit) reads a single replicated
        // channel -- the W select.
        if (slot == 'C' && IsScalarIlu(inst.Ilu))
            xs = ys = zs = ws;

        switch (slot)
        {
            case 'A': inst.Amx = mux; inst.Arr = rr; inst.Ane = neg;
                      inst.Axs = xs; inst.Ays = ys; inst.Azs = zs; inst.Aws = ws; break;
            case 'B': inst.Bmx = mux; inst.Brr = rr; inst.Bne = neg;
                      inst.Bxs = xs; inst.Bys = ys; inst.Bzs = zs; inst.Bws = ws; break;
            case 'C': inst.Cmx = mux; inst.Crr = rr; inst.Cne ^= neg;   // SUB preset combines
                      inst.Cxs = xs; inst.Cys = ys; inst.Czs = zs; inst.Cws = ws; break;
        }
    }

    // --- matrix macro expansion ------------------------------------------------

    private void ExpandMatrix(IReadOnlyList<uint> tokens, ref int pos, List<VsInstruction> ucode, uint opcode)
    {
        // op/iRepeat per the fall-through table: M4x4->DP4x4, M3x4->DP3x4,
        // M4x3->DP4x3, M3x3->DP3x3, M3x2->DP3x2.
        (uint dp, int rows) = opcode switch
        {
            20 => (9u, 4),   // M4x4 -> DP4
            22 => (8u, 4),   // M3x4 -> DP3
            21 => (9u, 3),   // M4x3 -> DP4
            23 => (8u, 3),   // M3x3 -> DP3
            24 => (8u, 2),   // M3x2 -> DP3
            _  => (9u, 4),
        };

        uint dst = tokens[pos + 1];
        uint src = tokens[pos + 2];
        uint mat = tokens[pos + 3];

        for (int j = 0; j < rows; j++)
        {
            uint rowMask = (uint)(0x00010000 << j);           // WRITEMASK_j
            uint rowDst = dst & (~WriteMaskAll | rowMask);
            if ((rowDst & WriteMaskAll) == 0)
                continue;                                     // dst lacks this component

            var mini = new List<uint> { 0, dp, rowDst, src, mat + (uint)j, 0xFFFF };
            int mp = 1;
            Translate(mini, ref mp, ucode);                   // reuses the dp3/dp4 path
        }

        pos += 4;
    }

    // --- InstructionsToMicrocode postfix ---------------------------------------

    private static void AppendPostfix(List<VsInstruction> ucode)
    {
        // The two fixed screen-space-transform instructions, decoded from their
        // packed DWORDs (eos forced to 0; the optimizers might move them).
        var a = VsInstruction.Unpack(0x00000000, 0x0647401b, 0xc4361bff, 0x1078e800);
        var b = VsInstruction.Unpack(0x00000000, 0x0087601b, 0xc400286c, 0x3070e800);
        a.Eos = 0; b.Eos = 0;
        ucode.Add(a);
        ucode.Add(b);
    }

    // --- helpers ---------------------------------------------------------------

    private static VsInstruction NewInstruction() => new()
    {
        Mac = VsInstruction.MacNop, Ilu = VsInstruction.IluNop,
        // identity swizzles
        Axs = 0, Ays = 1, Azs = 2, Aws = 3,
        Bxs = 0, Bys = 1, Bzs = 2, Bws = 3,
        Cxs = 0, Cys = 1, Czs = 2, Cws = 3,
        // absent operands read v0
        Amx = VsInstruction.MuxV, Bmx = VsInstruction.MuxV, Cmx = VsInstruction.MuxV,
        Rw = 7, Rwm = 0, Swm = 0, Owm = 0, Oc = 0x1ff, Om = VsInstruction.OmMac,
        Eos = 0, Cin = 0,
    };

    private static uint ReverseMask(uint m) =>
        ((m & 1) << 3) | ((m & 2) << 1) | ((m & 4) >> 1) | ((m & 8) >> 3);

    private static bool IsScalarIlu(uint ilu) =>
        ilu is VsInstruction.IluRcp or VsInstruction.IluRcc or VsInstruction.IluRsq
            or VsInstruction.IluExp or VsInstruction.IluLog;

    private static uint MapDx8ToUcode(uint reg)
    {
        // DX8 constant index (-96..95 in 12-bit two's complement) -> hardware 0..191.
        if (reg <= 95) return reg + 96;
        return 96 - (0xfff & ((~reg) + 1));
    }

    private static uint OutputRegOffset(RegFile file, uint regnum, uint token)
    {
        // Offset of the output register from o0 (oPos). Internal enum: O0=oPos,
        // O3=oD0, O5=oFog, O6=oPts, O7=oB0, O9=oT0.
        return file switch
        {
            RegFile.RastOut   => regnum == 0 ? 0u : 4u + regnum,   // pos / fog(5) / pts(6)
            RegFile.AttrOut   => ((token & 0x100) != 0 ? 7u : 3u) + (regnum & 1),
            RegFile.TexCrdOut => 9u + regnum,
            _                 => regnum,
        };
    }

    private void Error(string message) =>
        _diags.Add(new Diagnostic("", 0, true, message));
}
