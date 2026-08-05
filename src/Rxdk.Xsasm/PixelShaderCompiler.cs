namespace Rxdk.Xsasm;

/// <summary>
/// Lowers a D3D8 pixel-shader token stream to a D3DPIXELSHADERDEF, following
/// pixelshader.cpp's CompilePixelShaderToUCode().
///
/// Two kinds of instruction go to two different places. Texture instructions
/// (tex, texbem, texm3x3...) do not consume a combiner stage at all -- they
/// configure one of the four texture units, and are gathered into PSTextureModes /
/// PSInputTexture / PSDotMapping. Arithmetic instructions each program one general
/// combiner stage.
/// </summary>
internal sealed class PixelShaderCompiler
{
    private readonly List<Diagnostic> _diags;
    private readonly PixelShaderDef _psd = new();

    /// <summary>D3D constant register -> packed A8R8G8B8, filled in by 'def'.</summary>
    private readonly uint[] _d3dConstants = new uint[Ps.MaxConstants];

    /// <summary>
    /// [stage][0..1] -> which D3D constant occupies that stage's C0/C1 slot.
    /// Index 8 is the final combiner. A stage has only two constant slots, so a
    /// third distinct constant in one stage is an error rather than a spill.
    /// </summary>
    private readonly uint[,] _constMapping = new uint[Ps.MaxCombinerStages + 1, 2];

    private int _stage;

    /// <summary>
    /// True for a ps.1.0 shader. 5849 lowers those differently from ps.1.1 and xps
    /// in two ways, each confirmed against golden .xpu by BOTH ps.1.0 samples: an
    /// unmodified source decodes to UNSIGNED_IDENTITY rather than SIGNED_IDENTITY
    /// (a DX8 pixel shader clamps its inputs to [0,1]), and the texture-mode adjust
    /// global flag is left clear. Note the boundary is the version, not "not xps":
    /// ps.1.1 lowers exactly like xps here.
    /// </summary>
    private readonly bool _legacy;

    public PixelShaderCompiler(List<Diagnostic> diags, bool legacy = false)
    {
        _diags = diags;
        _legacy = legacy;

        for (int i = 0; i <= Ps.MaxCombinerStages; i++)
            _constMapping[i, 0] = _constMapping[i, 1] = Ps.ConstantUnused;
    }

    private void Fail(string message) => throw new AssemblyException(message);

    /// <summary>Number of source operands each arithmetic opcode takes.</summary>
    private static int NumSrc(Op op) => op switch
    {
        Op.Mov => 1,
        Op.Add or Op.Sub or Op.Mul or Op.Dp3 => 2,
        Op.Mad or Op.Lrp or Op.Cnd => 3,
        Op.Xmma or Op.Xmmc or Op.Xdm or Op.Xdd => 4,
        Op.Xfc => 7,
        _ => 0,
    };

    /// <summary>Number of destination operands each arithmetic opcode takes.</summary>
    private static int NumDst(Op op) => op switch
    {
        Op.Mov or Op.Add or Op.Sub or Op.Mad or Op.Mul or Op.Dp3 or Op.Lrp or Op.Cnd => 1,
        Op.Xmma or Op.Xmmc => 3,
        Op.Xdm or Op.Xdd => 2,
        _ => 0,
    };

    /// <summary>Operand count of each texture instruction, indexed from D3DSIO_TEXCOORD.</summary>
    private static readonly int[] NumTexArgs =
    {
        1, // texcoord
        1, // texkill
        1, // tex
        2, // texbem
        2, // texbeml
        2, // texreg2ar
        2, // texreg2gb
        2, // texm3x2pad
        2, // texm3x2tex
        2, // texm3x3pad
        2, // texm3x3tex
        2, // texm3x3diff
        3, // texm3x3spec
        2, // texm3x3vspec
    };

    public PixelShaderDef Compile(IReadOnlyList<uint> code)
    {
        var texMode = new uint[Ps.MaxShaderStages];
        var otherReg = new uint[Ps.MaxShaderStages];
        var dotMap = new uint[Ps.MaxShaderStages];

        for (int i = 0; i < Ps.MaxShaderStages; i++)
        {
            texMode[i] = Ps.TexNone;
            otherReg[i] = 0;
            dotMap[i] = Ps.DotMappingZeroToOne;
        }

        int pc = 1;     // skip the version token

        while (pc < code.Count)
        {
            uint token = code[pc++];
            var opcode = (Op)(token & 0xFFFF);
            bool coIssue = (token & 0x40000000) != 0;

            if (opcode == Op.End) break;
            if (opcode == Op.Nop) continue;

            if (opcode == Op.Comment)
            {
                pc += (int)((token & 0x7FFF0000) >> 16);
                continue;
            }

            if (opcode == Op.Def)
            {
                ReadDef(code, ref pc);
                continue;
            }

            if (IsArithmetic(opcode))
                Arithmetic(opcode, coIssue, code, ref pc);
            else if (IsTexture(opcode))
                Texture(opcode, code, ref pc, texMode, otherReg, dotMap);
            else
                Fail($"unrecognized instruction '{opcode}'");
        }

        _psd.TextureModes = Ps.TextureModes(texMode[0], texMode[1], texMode[2], texMode[3]);

        // Only stages 2 and 3 can name another texture as their input, which is why
        // t0 and t1 have no field here.
        _psd.InputTexture = (otherReg[3] << 20) | (otherReg[2] << 16);

        _psd.DotMapping = Ps.DotMapping(dotMap[1], dotMap[2], dotMap[3]);

        _psd.CombinerCount = Ps.CombinerCount((uint)_stage,
            CombinerCountMuxMsb | CombinerCountUniqueC0 | CombinerCountUniqueC1);

        _psd.C0Mapping = ConstantMapping(0);
        _psd.C1Mapping = ConstantMapping(1);

        _psd.FinalCombinerConstants =
            (_legacy ? 0u : GlobalFlagsTexModeAdjust << 8) |
            ((_constMapping[8, 0] & 0xF) << 0) |
            ((_constMapping[8, 1] & 0xF) << 4);

        return _psd;
    }

    private const uint CombinerCountMuxMsb = 0x0001;
    private const uint CombinerCountUniqueC0 = 0x0010;
    private const uint CombinerCountUniqueC1 = 0x0100;
    private const uint GlobalFlagsTexModeAdjust = 0x0001;

    private uint ConstantMapping(int slot)
    {
        uint v = 0;
        for (int s = 0; s < Ps.MaxCombinerStages; s++)
            v |= (_constMapping[s, slot] & 0xF) << (4 * s);
        return v;
    }

    private static bool IsArithmetic(Op op) =>
        op < Op.TexCoord || op == Op.Cnd || (op >= Op.Xmma && op <= Op.Xfc);

    private static bool IsTexture(Op op) =>
        (op >= Op.TexCoord && op <= Op.TexM3x3VSpec) ||
        op == Op.TexM3x2Depth || op == Op.TexBrdf;

    /// <summary>
    /// 'def cN, r, g, b, a' -- the combiners take 8-bit colour, so the four floats
    /// are quantised to a packed A8R8G8B8 here rather than kept as floats.
    /// </summary>
    private void ReadDef(IReadOnlyList<uint> code, ref int pc)
    {
        uint reg = code[pc++] & Isa.RegNumMask;
        if (reg >= Ps.MaxConstants) Fail("invalid constant register in def");

        static uint Component(IReadOnlyList<uint> c, int at) =>
            (uint)(BitConverter.UInt32BitsToSingle(c[at]) * 255.0f + 0.5f) & 0xff;

        uint r = Component(code, pc++);
        uint g = Component(code, pc++);
        uint b = Component(code, pc++);
        uint a = Component(code, pc++);

        _d3dConstants[reg] = (a << 24) | (r << 16) | (g << 8) | b;
    }

    private void Arithmetic(Op opcode, bool coIssue, IReadOnlyList<uint> code, ref int pc)
    {
        // A co-issued instruction shares the stage of the one before it: it is the
        // other channel half of the same combiner, not a new stage.
        if (coIssue) _stage--;

        if (_stage >= Ps.MaxCombinerStages && opcode != Op.Xfc)
            Fail($"too many combiner stages (hardware has {Ps.MaxCombinerStages})");

        var o = new PsOperands();
        bool writesRgb = false, writesAlpha = false;

        int dstCount = NumDst(opcode);
        for (int i = 0; i < dstCount; i++)
        {
            uint token = code[pc++];
            DecodeDst(token, out o.Dst[i], out o.OutputMap[i], out o.Mask[i]);

            if ((o.Mask[i] & (Isa.WriteMask0 | Isa.WriteMask1 | Isa.WriteMask2)) != 0) writesRgb = true;
            if ((o.Mask[i] & Isa.WriteMask3) != 0) writesAlpha = true;
        }

        // xfc has no destination: its result is the pixel. It is still programmed
        // through the RGB selectors, and its constants live in the final combiner's
        // own slot rather than a stage's.
        bool isFinalCombiner = dstCount == 0;
        if (isFinalCombiner)
        {
            o.Dst[0] = Ps.RegZero;
            o.OutputMap[0] = 0;
            o.Mask[0] = Isa.WriteMask0 | Isa.WriteMask1 | Isa.WriteMask2;
            writesRgb = true;
        }

        int srcCount = NumSrc(opcode);
        for (int i = 0; i < srcCount; i++)
            DecodeSrc(code[pc++], i, o, isFinalCombiner);

        if (writesRgb)
            PixelInstructions.Emit(opcode, _psd, _stage, PixelInstructions.Colour, o);

        if (writesAlpha)
            PixelInstructions.Emit(opcode, _psd, _stage, PixelInstructions.Alpha, o);

        if (opcode != Op.Xfc) _stage++;
    }

    private static void DecodeDst(uint token, out uint reg, out uint outputMap, out uint mask)
    {
        uint type = (token & Isa.RegTypeMask) >> Isa.RegTypeShift;
        uint offset = token & Isa.RegNumMask;
        uint shift = ((token & Isa.DstShiftMask) >> Isa.DstShiftShift) & 0xF;
        uint dstMod = (token & Isa.DstModMask) >> Isa.DstModShift;

        outputMap = Ps.ShiftAndBiasToMap(shift, dstMod);
        reg = Ps.TypeOffsetToCombinerReg[type][offset];
        mask = token & Isa.WriteMaskAll;
    }

    private void DecodeSrc(uint token, int i, PsOperands o, bool isFinalCombiner)
    {
        uint type = (token & Isa.RegTypeMask) >> Isa.RegTypeShift;
        uint regnum = token & Isa.RegNumMask;

        o.Swizzle[i] = token & Isa.SwizzleMask;

        if (((uint)RegFile.Const << Isa.RegTypeShift) == (token & Isa.RegTypeMask))
            regnum = MapConstant(regnum, isFinalCombiner);

        uint mod = (token & Isa.SrcModMask) >> Isa.SrcModShift;
        o.Src[i] = Ps.TypeOffsetToCombinerReg[type][regnum];

        // A DX8 pixel shader clamps its inputs to [0,1], so an unmodified source is
        // unsigned there; an Xbox xps source is signed.
        o.InputMod[i] = mod == 0 && _legacy ? Ps.UnsignedIdentity : Ps.D3DModToNvMod[mod];

        // The final combiner has no signed inputs, so SIGNED_IDENTITY -- which is
        // what an unmodified source decodes to -- has to fall back to unsigned.
        if (isFinalCombiner && o.InputMod[i] == Ps.SignedIdentity)
            o.InputMod[i] = Ps.UnsignedIdentity;
    }

    /// <summary>
    /// A stage sees only two constants, C0 and C1, so each D3D constant a stage
    /// references is assigned one of those slots and the reference is rewritten.
    /// The mapping is recorded so the runtime can push the right D3D constant into
    /// the right slot.
    /// </summary>
    private uint MapConstant(uint offset, bool isFinalCombiner)
    {
        if (offset >= Ps.MaxConstants)
            Fail("invalid constant source register");

        int slotOwner = isFinalCombiner ? 8 : _stage;

        for (int slot = 0; slot < 2; slot++)
        {
            if (_constMapping[slotOwner, slot] != Ps.ConstantUnused &&
                _constMapping[slotOwner, slot] != offset)
            {
                continue;
            }

            _constMapping[slotOwner, slot] = offset;

            if (isFinalCombiner)
            {
                if (slot == 0) _psd.FinalCombinerConstant0 = _d3dConstants[offset];
                else _psd.FinalCombinerConstant1 = _d3dConstants[offset];
            }
            else
            {
                if (slot == 0) _psd.Constant0[_stage] = _d3dConstants[offset];
                else _psd.Constant1[_stage] = _d3dConstants[offset];
            }

            return (uint)slot;   // c0 or c1
        }

        Fail(isFinalCombiner
            ? "more than 2 constants used in final combiner"
            : $"more than 2 constants used in stage {_stage}");
        return 0;
    }

    /// <summary>
    /// Texture instructions configure a texture unit rather than a combiner stage.
    /// The source register's modifier is reused as the dot-mapping selector, which
    /// is why '_bx2' and friends mean something different here.
    /// </summary>
    private void Texture(Op opcode, IReadOnlyList<uint> code, ref int pc,
                         uint[] texMode, uint[] otherReg, uint[] dotMap)
    {
        int index = opcode - Op.TexCoord;

        uint dstToken = code[pc++];
        uint dstOffset = dstToken & Isa.RegNumMask;

        int argCount = opcode switch
        {
            Op.TexM3x2Depth => 2,
            Op.TexBrdf => 1,
            _ => NumTexArgs[index],
        };

        uint srcOffset = 0;
        uint srcMod = 0;

        if (argCount >= 2)
        {
            uint srcToken = code[pc++];
            srcOffset = srcToken & Isa.RegNumMask;
            srcMod = srcToken & Isa.SrcModMask;
        }

        if (argCount >= 3) pc++;

        if (dstOffset >= Ps.MaxShaderStages)
            Fail("invalid texture destination register");

        texMode[dstOffset] = opcode switch
        {
            Op.TexM3x2Depth => Ps.TexDotZw,
            Op.TexBrdf => Ps.TexBrdf,
            _ => Ps.D3DOpToTexMode[index],
        };

        otherReg[dstOffset] = srcOffset;

        dotMap[dstOffset] = (srcMod >> Isa.SrcModShift) switch
        {
            0 => 0x00,  // none      -> ZERO_TO_ONE
            4 => 0x01,  // _bx2/sign -> MINUS1_TO_1_D3D
            1 => 0x02,  // negate    -> MINUS1_TO_1_GL
            2 => 0x03,  // bias      -> MINUS1_TO_1
            3 => 0x04,  // biasneg   -> HILO_1
            5 => 0x05,  // signneg   -> HILO_HEMISPHERE_D3D
            6 => 0x06,  // comp      -> HILO_HEMISPHERE_GL
            7 => 0x07,  // sat       -> HILO_HEMISPHERE
            _ => 0x00,
        };
    }
}
