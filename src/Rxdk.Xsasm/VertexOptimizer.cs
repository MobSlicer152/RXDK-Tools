namespace Rxdk.Xsasm;

/// <summary>
/// Port of api.cpp's XGOptimizeVertexShader pipeline (Phase 2). It runs in place
/// on the microcode the translator produced -- the C++ D3DVertexShaderProgram is
/// a <see cref="List{VsInstruction}"/> here, and the D3DVsInstruction fields map
/// 1:1 (lowercase C++ -> PascalCase). See VERTEX_BACKEND_SPEC.md.
///
/// STATUS: the fixed-point driver, the shared helper layer, and the first pass
/// (PeepholePairOutputMasks) are ported and golden-neutral (they leave the
/// unoptimized `source+2` goldens unchanged). The three heavy classes
/// (DeadCodeStripper, Renamer, Reorderer) and the pairers (PeepholeOptimize,
/// PeepholePair1/2) are stubbed no-ops pending their ports -- the `-N` goldens
/// need them and stay red until then.
/// </summary>
internal static partial class VertexOptimizer
{
    // Opcode / mux / swizzle constants, aliased to VsInstruction's for brevity.
    private const uint MAC_NOP = VsInstruction.MacNop, MAC_MOV = VsInstruction.MacMov,
                       MAC_ADD = VsInstruction.MacAdd, MAC_ARL = VsInstruction.MacArl;
    private const uint ILU_MOV = VsInstruction.IluMov;
    private const uint MX_R = VsInstruction.MuxR, MX_V = VsInstruction.MuxV, MX_C = VsInstruction.MuxC;
    private const uint CSW_X = 0, CSW_Y = 1, CSW_Z = 2, CSW_W = 3;
    private const uint OM_MAC = VsInstruction.OmMac, OM_ILU = VsInstruction.OmIlu;

    // --- driver: XGOptimizeVertexShader (api.cpp:6609) -----------------------

    /// <summary>
    /// Run the optimizer in place. Defaults match the assembler's shipping config
    /// (optimize + globalOptimize), which is what produced the goldens.
    /// </summary>
    // The Renamer is ported (below) but not yet enabled. It is coupled to the
    // Reorderer -- the driver runs them in sequence and the goldens capture their
    // combined effect -- and on its own it diverges on at least one shader
    // (billbrd: our RemapVRegs succeeds and reassigns where retail's fails and
    // leaves the code unchanged). Flip this on together with the Reorderer port,
    // then resolve the remaining remap divergence against the goldens.
    private const bool EnableRenamer = false;

    public static void Optimize(List<VsInstruction> program, bool stateShader = false,
                                bool optimize = true, bool globalOptimize = true)
    {
        int preOptimizationLength;
        do
        {
            preOptimizationLength = program.Count;

            if (optimize)       PeepholePairOutputMasks(program);
            if (globalOptimize) new DeadCodeStripper(stateShader).Run(program);
            if (globalOptimize && EnableRenamer) new Renamer().Run(program);
            if (globalOptimize) Reorderer(program);
            if (globalOptimize) PeepholeOptimize(program, stateShader);
            if (optimize)       PeepholePair1(program);
            if (globalOptimize) PeepholePair2(program);

            // Shrinking can expose more dead code, which enables more pairing,
            // so loop until a full pass changes nothing.
        } while (globalOptimize && program.Count < preOptimizationLength);
    }

    // --- component-use tables (api.cpp:254-383) ------------------------------
    // Indexed by opcode value (the source comments mislabel the rows, but the
    // array position IS the opcode -- verified against kMacUsesA's ordering).

    private static readonly bool[] kMacUsesA =
        { false, true, true, true, true, true, true, true, true, true, true, true, true, true, false, false };
    private static readonly bool[] kMacUsesB =
        { false, false, true, false, true, true, true, true, true, true, true, true, true, false, false, false };
    private static readonly bool[] kMacUsesC =
        { false, false, false, true, true, false, false, false, false, false, false, false, false, false, false, false };

    // [opcode, slot] where slot 0=A, 1=B, 2=C.
    private static readonly byte[,] kMacFixed =
    {
        {0,0,0},{0,0,0},{0,0,0},{0,0,0},{0,0,0},{0xe,0xe,0},{0xe,0xf,0},{0xf,0xf,0},
        {0x6,0x5,0},{0,0,0},{0,0,0},{0,0,0},{0,0,0},{0x8,0,0},{0,0,0},{0,0,0},
    };
    private static readonly byte[,] kMacCol =
    {
        {0,0,0},{0xf,0,0},{0xf,0xf,0},{0xf,0,0xf},{0xf,0xf,0xf},{0,0,0},{0,0,0},{0,0,0},
        {0,0,0},{0xf,0xf,0},{0xf,0xf,0},{0xf,0xf,0},{0xf,0xf,0},{0,0,0},{0,0,0},{0,0,0},
    };
    private static readonly byte[,] kIluFixed =
    {
        {0,0,0},{0,0,0},{0,0,0x8},{0,0,0x8},{0,0,0x8},{0,0,0x8},{0,0,0x8},{0,0,0xd},
    };
    private static readonly byte[,] kIluCol =
    {
        {0,0,0},{0,0,0xf},{0,0,0},{0,0,0},{0,0,0},{0,0,0},{0,0,0},{0,0,0},
    };

    // --- register output masks (api.cpp:393, 6237) ---------------------------

    private struct OutPair { public byte M; public byte R; public bool Used; }

    private static void ExpandRegisterOutputMasks(OutPair[] masks, VsInstruction a)
    {
        masks[0].M = (byte)a.Rwm;
        masks[0].R = (byte)a.Rw;
        masks[0].Used = a.Rwm != 0 && a.Mac != 0;
        masks[1].M = (byte)a.Swm;
        masks[1].R = (byte)(a.Ilu != 0 ? (a.Mac != 0 ? 1u : a.Rw) : 7u);   // r7 = don't-care
        masks[1].Used = a.Swm != 0 && a.Ilu != 0;
    }

    // --- InputOutputDependency (api.cpp:5465) --------------------------------

    /// <summary>Does b depend on an output of a?</summary>
    private static bool InputOutputDependency(VsInstruction a, VsInstruction b)
    {
        var outMasks = new OutPair[2];
        ExpandRegisterOutputMasks(outMasks, a);

        bool aWritesToConst = a.Owm != 0 && (a.Oc & 0x100) == 0;
        bool aWritesToOut   = a.Owm != 0 && (a.Oc & 0x100) != 0;
        bool bUsesA = kMacUsesA[b.Mac];
        bool bUsesB = kMacUsesB[b.Mac];
        bool bUsesC = b.Ilu != 0 || kMacUsesC[b.Mac];
        uint baMask = bUsesA ? Read4(b.Axs, b.Ays, b.Azs, b.Aws) : 0;
        uint bbMask = bUsesB ? Read4(b.Bxs, b.Bys, b.Bzs, b.Bws) : 0;
        uint bcMask = bUsesC ? Read4(b.Cxs, b.Cys, b.Czs, b.Cws) : 0;
        bool bBUsesConst = (bUsesA && b.Amx == MX_C)
                        || (bUsesB && b.Bmx == MX_C)
                        || (bUsesC && b.Cmx == MX_C);

        for (int i = 0; i < 2; i++)
        {
            if (!outMasks[i].Used) continue;
            if (bUsesA && b.Amx == MX_R && b.Arr == outMasks[i].R && (baMask & outMasks[i].M) != 0) return true;
            if (bUsesB && b.Bmx == MX_R && b.Brr == outMasks[i].R && (bbMask & outMasks[i].M) != 0) return true;
            if (bUsesC && b.Cmx == MX_R && b.Crr == outMasks[i].R && (bcMask & outMasks[i].M) != 0) return true;
        }
        if (aWritesToConst && bBUsesConst && (b.Ca == a.Oc || b.Cin != 0)) return true;
        if (aWritesToOut && a.Oc == 0x100)   // o[oPos] written, read back from r12
        {
            if (bUsesA && b.Amx == MX_R && b.Arr == 12 && (baMask & a.Owm) != 0) return true;
            if (bUsesB && b.Bmx == MX_R && b.Brr == 12 && (bbMask & a.Owm) != 0) return true;
            if (bUsesC && b.Cmx == MX_R && b.Crr == 12 && (bcMask & a.Owm) != 0) return true;
        }
        if (a.Mac == MAC_ARL && bBUsesConst && b.Cin != 0) return true;
        return false;
    }

    private static uint Read4(uint xs, uint ys, uint zs, uint ws) =>
        (1u << (int)(3 - xs)) | (1u << (int)(3 - ys)) | (1u << (int)(3 - zs)) | (1u << (int)(3 - ws));

    // --- effective swizzles (api.cpp:5880-5924) ------------------------------

    private static void ComputePostSwizzleUseMasks(VsInstruction pI, byte[] results)
    {
        uint macMask = pI.Rwm | (pI.Om == OM_MAC ? pI.Owm : 0);
        uint iluMask = pI.Swm | (pI.Om == OM_ILU ? pI.Owm : 0);
        for (int a = 0; a < 3; a++)
        {
            results[a] = (byte)(
                  kMacFixed[pI.Mac, a]
                | kIluFixed[pI.Ilu, a]
                | (macMask & kMacCol[pI.Mac, a])
                | (iluMask & kIluCol[pI.Ilu, a]));
        }
    }

    private static void ComputeEffectiveSwizzle(byte mask, uint xs, uint ys, uint zs, uint ws, sbyte[] sw, int off)
    {
        sw[off + 0] = (mask & 1) != 0 ? (sbyte)ws : (sbyte)-1;   // where do we read W from?
        sw[off + 1] = (mask & 2) != 0 ? (sbyte)zs : (sbyte)-1;
        sw[off + 2] = (mask & 4) != 0 ? (sbyte)ys : (sbyte)-1;
        sw[off + 3] = (mask & 8) != 0 ? (sbyte)xs : (sbyte)-1;
    }

    private static void ComputeEffectiveSwizzles(VsInstruction a, sbyte[] sw)
    {
        var masks = new byte[3];
        ComputePostSwizzleUseMasks(a, masks);
        ComputeEffectiveSwizzle(masks[0], a.Axs, a.Ays, a.Azs, a.Aws, sw, 0);
        ComputeEffectiveSwizzle(masks[1], a.Bxs, a.Bys, a.Bzs, a.Bws, sw, 4);
        ComputeEffectiveSwizzle(masks[2], a.Cxs, a.Cys, a.Czs, a.Cws, sw, 8);
    }

    // --- swizzle canonicalization (api.cpp:5932-5989) ------------------------

    private static bool CanUseXYZW(sbyte[] run, int off)
    {
        ReadOnlySpan<sbyte> kXYZW = stackalloc sbyte[] { (sbyte)CSW_W, (sbyte)CSW_Z, (sbyte)CSW_Y, (sbyte)CSW_X };
        for (int i = 0; i < 4; i++)
            if (run[off + i] != -1 && run[off + i] != kXYZW[i]) return false;
        return true;
    }

    private static sbyte PickDefault(sbyte[] run, int off)
    {
        if (CanUseXYZW(run, off)) return -1;
        for (int i = 0; i < 4; i++)
            if (run[off + i] != -1) return run[off + i];
        return -1;   // shouldn't get here
    }

    private static uint ChooseBestSwizzle(sbyte swizzle, sbyte defaultSw, uint defaultDefault)
    {
        if (swizzle == -1) swizzle = defaultSw;
        if (swizzle == -1) return defaultDefault;
        return (uint)swizzle;
    }

    private static void SetSwizzles(VsInstruction pI, sbyte[] sw)
    {
        sbyte d = PickDefault(sw, 0);
        pI.Aws = ChooseBestSwizzle(sw[0], d, CSW_W);
        pI.Azs = ChooseBestSwizzle(sw[1], d, CSW_Z);
        pI.Ays = ChooseBestSwizzle(sw[2], d, CSW_Y);
        pI.Axs = ChooseBestSwizzle(sw[3], d, CSW_X);

        d = PickDefault(sw, 4);
        pI.Bws = ChooseBestSwizzle(sw[4], d, CSW_W);
        pI.Bzs = ChooseBestSwizzle(sw[5], d, CSW_Z);
        pI.Bys = ChooseBestSwizzle(sw[6], d, CSW_Y);
        pI.Bxs = ChooseBestSwizzle(sw[7], d, CSW_X);

        d = PickDefault(sw, 8);
        pI.Cws = ChooseBestSwizzle(sw[8], d, CSW_W);
        pI.Czs = ChooseBestSwizzle(sw[9], d, CSW_Z);
        pI.Cys = ChooseBestSwizzle(sw[10], d, CSW_Y);
        pI.Cxs = ChooseBestSwizzle(sw[11], d, CSW_X);
    }

    private static bool MergeSwizzles(VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        var aSw = new sbyte[12];
        var bSw = new sbyte[12];
        var abSw = new sbyte[12];
        ComputeEffectiveSwizzles(a, aSw);
        ComputeEffectiveSwizzles(b, bSw);
        for (int i = 0; i < 12; i++)
        {
            sbyte az = aSw[i], bz = bSw[i];
            if (az != -1 && bz != -1 && az != bz) return false;   // incompatible
            abSw[i] = az != -1 ? az : bz;
        }
        SetSwizzles(pair, abSw);
        return true;
    }

    // --- source comparison / const helpers (api.cpp:5770-5872) ---------------

    private static bool SourcesEqual(uint aMx, uint aRr, uint aNe, VsInstruction a,
                                     uint bMx, uint bRr, uint bNe, VsInstruction b) =>
        aMx == bMx && aNe == bNe &&
        ((aMx == MX_R && aRr == bRr)
         || (aMx == MX_V && a.Va == b.Va)
         || (aMx == MX_C && a.Ca == b.Ca && a.Cin == b.Cin));

    private static bool NullSwizzle(uint rwm, uint ne, uint xs, uint ys, uint zs, uint ws) =>
        ne == 0
        && ((rwm & 1) == 0 || ws == CSW_W)
        && ((rwm & 2) == 0 || zs == CSW_Z)
        && ((rwm & 4) == 0 || ys == CSW_Y)
        && ((rwm & 8) == 0 || xs == CSW_X);

    private static bool ReadsFromConst(VsInstruction pI) =>
        (kMacUsesA[pI.Mac] && pI.Amx == MX_C)
        || (kMacUsesB[pI.Mac] && pI.Bmx == MX_C)
        || (kMacUsesC[pI.Mac] && pI.Cmx == MX_C)
        || (pI.Ilu != 0 && pI.Cmx == MX_C);

    private static bool CompatableConstUsage(VsInstruction pA, VsInstruction pB)
    {
        if (pA.Ca == pB.Ca && pA.Cin == pB.Cin) return true;
        if (!(ReadsFromConst(pA) && ReadsFromConst(pB))) return true;
        return false;
    }

    // --- PairableMasks family (api.cpp:5711-6072) ----------------------------

    private static bool PairableMasks1(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        pair = null!;
        if (a.Mac != b.Mac || a.Ilu != b.Ilu) return false;

        bool aSame = a.Amx == b.Amx && a.Ane == b.Ane && a.Arr == b.Arr
            && a.Aws == b.Aws && a.Axs == b.Axs && a.Ays == b.Ays && a.Azs == b.Azs;
        bool bSame = a.Bmx == b.Bmx && a.Bne == b.Bne && a.Brr == b.Brr
            && a.Bws == b.Bws && a.Bxs == b.Bxs && a.Bys == b.Bys && a.Bzs == b.Bzs;
        bool cSame = a.Cmx == b.Cmx && a.Cne == b.Cne && a.Crr == b.Crr
            && a.Cws == b.Cws && a.Cxs == b.Cxs && a.Cys == b.Cys && a.Czs == b.Czs;
        if (!(aSame && bSame && cSame)) return false;
        if (!(a.Ca == b.Ca && a.Va == b.Va && a.Cin == b.Cin)) return false;
        if (InputOutputDependency(a, b)) return false;

        bool aUsesReg = a.Rwm != 0 || a.Swm != 0;
        bool bUsesReg = b.Rwm != 0 || b.Swm != 0;
        if ((aUsesReg && bUsesReg) || (a.Owm != 0 && b.Owm != 0)) return false;

        pair = a.Clone();
        pair.Rwm |= b.Rwm;
        pair.Swm |= b.Swm;
        if (bUsesReg && !aUsesReg) pair.Rw = b.Rw;
        pair.Owm |= b.Owm;
        if (b.Owm != 0 && a.Owm == 0) { pair.Oc = b.Oc; pair.Om = b.Om; }
        return true;
    }

    private static bool PairableMasks2(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        pair = null!;
        bool pairable = a.Owm == 0
            && a.Rwm != 0
            && b.Mac == MAC_MOV
            && b.Ilu == 0
            && b.Amx == MX_R
            && b.Arr == a.Rw
            && NullSwizzle(b.Owm, b.Ane, b.Axs, b.Ays, b.Azs, b.Aws)
            && b.Om == OM_MAC
            && (b.Owm & ~a.Rwm) == 0      // b only reads what a wrote
            && b.Owm != 0
            && b.Rwm == 0
            && b.Swm == 0
            && CompatableConstUsage(a, b);
        if (!pairable) return false;

        pair = a.Clone();
        pair.Owm = b.Owm;
        pair.Om = b.Om;
        pair.Oc = b.Oc;
        return true;
    }

    private static bool PairableMasks(out VsInstruction pair, VsInstruction a, VsInstruction b) =>
        PairableMasks1(out pair, a, b) || PairableMasks2(out pair, a, b);

    // PairableMasks3 (api.cpp:5991): same op, same sources, compatible outputs,
    // merged swizzles -- the globalOptimize form PairableMasks1 defers to.
    private static bool PairableMasks3(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        pair = null!;
        if (a.Mac != b.Mac || a.Ilu != b.Ilu) return false;
        if (!(SourcesEqual(a.Amx, a.Arr, a.Ane, a, b.Amx, b.Arr, b.Ane, b)
           && SourcesEqual(a.Bmx, a.Brr, a.Bne, a, b.Bmx, b.Brr, b.Bne, b)
           && SourcesEqual(a.Cmx, a.Crr, a.Cne, a, b.Cmx, b.Crr, b.Cne, b))) return false;
        if (InputOutputDependency(a, b)) return false;

        bool aUsesReg = a.Rwm != 0 || a.Swm != 0;
        bool bUsesReg = b.Rwm != 0 || b.Swm != 0;
        if (aUsesReg && bUsesReg && a.Rw != b.Rw) return false;
        if (a.Owm != 0 && b.Owm != 0 && a.Oc != b.Oc) return false;

        pair = a.Clone();
        if (!MergeSwizzles(pair, a, b)) return false;
        pair.Rwm |= b.Rwm;
        pair.Swm |= b.Swm;
        if (bUsesReg && !aUsesReg) pair.Rw = b.Rw;
        pair.Owm |= b.Owm;
        if (b.Owm != 0 && a.Owm == 0) { pair.Oc = b.Oc; pair.Om = b.Om; }
        return true;
    }

    // --- register output mask merge (api.cpp:6247-6306) ----------------------

    private static bool SetRegisterOutputMasks(VsInstruction a, OutPair[] masks)
    {
        // If both mac and ilu write registers, mac can't use r1 and ilu must.
        if (masks[0].Used && masks[1].Used && (masks[0].R == 1 || masks[1].R != 1))
            return false;
        a.Rwm = masks[0].M;
        a.Swm = masks[1].M;
        a.Rw = masks[0].Used ? masks[0].R : masks[1].R;
        return true;
    }

    private static bool MergeRegisterOutputMasks(VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        var ma = new OutPair[2];
        var mb = new OutPair[2];
        var mp = new OutPair[2];
        ExpandRegisterOutputMasks(ma, a);
        ExpandRegisterOutputMasks(mb, b);
        for (int i = 0; i < 2; i++)
        {
            mp[i] = ma[i];
            if (ma[i].Used && mb[i].Used)
            {
                if (ma[i].R != mb[i].R) return false;
                if (ma[i].M != mb[i].M) return false;
            }
            if (mb[i].Used) mp[i] = mb[i];
        }
        if (pair.Mac != 0 && pair.Ilu != 0)
        {
            if (mp[1].Used && mp[1].R != 1) return false;   // ilu must use r1
            if (mp[0].Used && mp[0].R == 1) return false;   // mac must not use r1
        }
        return SetRegisterOutputMasks(pair, mp);
    }

    // --- operand-swap / IMV conversion (api.cpp:5518-5581) -------------------

    private static bool SwapAC(out VsInstruction pOut, VsInstruction pIn)
    {
        pOut = null!;
        if (!(pIn.Mac == MAC_ADD && pIn.Ilu == 0)) return false;
        pOut = pIn.Clone();
        pOut.Amx = pIn.Cmx; pOut.Ane = pIn.Cne; pOut.Arr = pIn.Crr;
        pOut.Axs = pIn.Cxs; pOut.Ays = pIn.Cys; pOut.Azs = pIn.Czs; pOut.Aws = pIn.Cws;
        pOut.Cmx = pIn.Amx; pOut.Cne = pIn.Ane; pOut.Crr = pIn.Arr;
        pOut.Cxs = pIn.Axs; pOut.Cys = pIn.Ays; pOut.Czs = pIn.Azs; pOut.Cws = pIn.Aws;
        return true;
    }

    private static bool ConvertToImv(out VsInstruction ucode, VsInstruction mov)
    {
        ucode = null!;
        if (mov.Mac != MAC_MOV || mov.Ilu != 0) return false;
        ucode = new VsInstruction
        {
            Mac = MAC_NOP, Ilu = ILU_MOV,
            Ca = mov.Ca, Va = mov.Va,
            Ane = 0, Axs = CSW_X, Ays = CSW_Y, Azs = CSW_Z, Aws = CSW_W, Amx = MX_R, Arr = 0,
            Bne = 0, Bxs = CSW_X, Bys = CSW_Y, Bzs = CSW_Z, Bws = CSW_W, Bmx = MX_R, Brr = 0,
            Cne = mov.Ane, Cxs = mov.Axs, Cys = mov.Ays, Czs = mov.Azs, Cws = mov.Aws,
            Cmx = mov.Amx, Crr = mov.Arr,
            Rw = mov.Rw, Rwm = 0,
            Oc = mov.Oc, Om = mov.Owm != 0 ? OM_ILU : OM_MAC,
            Eos = mov.Eos, Cin = mov.Cin, Swm = mov.Rwm, Owm = mov.Owm,
        };
        return true;
    }

    // --- Uses predicates (api.cpp:6104-6234) ---------------------------------

    private static bool MacUsesMX(VsInstruction a, uint mx) =>
        (kMacUsesA[a.Mac] && a.Amx == mx) || (kMacUsesB[a.Mac] && a.Bmx == mx)
        || (kMacUsesC[a.Mac] && a.Cmx == mx);
    private static bool UsesMX(VsInstruction a, uint mx) =>
        MacUsesMX(a, mx) || (a.Ilu != 0 && a.Cmx == mx);
    private static bool UsesCA(VsInstruction a) => UsesMX(a, MX_C);
    private static bool UsesVA(VsInstruction a) => UsesMX(a, MX_V);
    private static bool UsesA(VsInstruction a) => kMacUsesA[a.Mac];
    private static bool UsesB(VsInstruction a) => kMacUsesB[a.Mac];
    private static bool UsesC(VsInstruction a) => kMacUsesC[a.Mac] || a.Ilu != 0;

    // --- PairableMulAdd (api.cpp:6126): MUL then ADD to the same reg -> MAD ---

    private static bool PairableMulAdd(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        pair = null!;
        if (!(a.Mac == VsInstruction.MacMul && a.Ilu == 0
              && b.Mac == MAC_ADD && b.Ilu == 0
              && a.Rwm != 0 && a.Owm == 0
              && b.Rwm == a.Rwm && b.Rw == a.Rw
              && (b.Owm & ~b.Rwm) == 0)) return false;

        bool bUsesAOpr = b.Amx == MX_R && b.Arr == a.Rw;
        bool bUsesCOpr = b.Cmx == MX_R && b.Crr == a.Rw;
        if (bUsesAOpr == bUsesCOpr) return false;                 // exactly one
        if (bUsesAOpr && !NullSwizzle(b.Rwm, b.Ane, b.Axs, b.Ays, b.Azs, b.Aws)) return false;
        if (bUsesCOpr && !NullSwizzle(b.Rwm, b.Cne, b.Cxs, b.Cys, b.Czs, b.Cws)) return false;

        bool aVA = UsesVA(a), bVA = UsesVA(b), aCA = UsesCA(a), bCA = UsesCA(b);
        if (aVA && bVA && a.Va != b.Va) return false;
        if (aCA && bCA && a.Ca != b.Ca) return false;

        pair = a.Clone();
        pair.Mac = VsInstruction.MacMad;
        if (bVA) pair.Va = b.Va;
        if (bCA) pair.Ca = b.Ca;
        if (bUsesAOpr)   // the register being MAC'd is A, so the added operand is C
        {
            pair.Cmx = b.Cmx; pair.Cne = b.Cne; pair.Crr = b.Crr;
            pair.Cxs = b.Cxs; pair.Cys = b.Cys; pair.Czs = b.Czs; pair.Cws = b.Cws;
        }
        if (bUsesCOpr)   // the register being MAC'd is C, so the added operand is A
        {
            pair.Cmx = b.Amx; pair.Cne = b.Ane; pair.Crr = b.Arr;
            pair.Cxs = b.Axs; pair.Cys = b.Ays; pair.Czs = b.Azs; pair.Cws = b.Aws;
        }
        pair.Om = b.Om; pair.Owm = b.Owm; pair.Oc = b.Oc;
        return true;
    }

    // --- ForcedPair / Pairable (api.cpp:6309-6475, 5614) ---------------------

    private static bool ForcedPair2(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        pair = a.Clone();
        if (b.Mac != 0) { if (a.Mac != 0 && a.Mac != b.Mac) return false; pair.Mac = b.Mac; }
        if (b.Ilu != 0) { if (a.Ilu != 0 && a.Ilu != b.Ilu) return false; pair.Ilu = b.Ilu; }

        if (UsesA(b))
        {
            if (UsesA(a))
            {
                if (b.Amx != a.Amx) return false;
                if (b.Ane != a.Ane) return false;
                if (b.Amx == MX_R && b.Arr != a.Arr) return false;
            }
            pair.Amx = b.Amx; pair.Ane = b.Ane; pair.Arr = b.Arr;
        }
        if (UsesB(b))
        {
            if (UsesB(a))
            {
                if (b.Bmx != a.Bmx) return false;
                if (b.Bne != a.Bne) return false;
                if (b.Bmx == MX_R && b.Brr != a.Brr) return false;
            }
            pair.Bmx = b.Bmx; pair.Bne = b.Bne; pair.Brr = b.Brr;
        }
        if (UsesC(b))
        {
            if (UsesC(a))
            {
                if (b.Cmx != a.Cmx) return false;
                if (b.Cne != a.Cne) return false;
                if (b.Cmx == MX_R && b.Crr != a.Crr) return false;
            }
            pair.Cmx = b.Cmx; pair.Cne = b.Cne; pair.Crr = b.Crr;
        }

        if (!MergeSwizzles(pair, a, b)) return false;

        if (UsesCA(b))
        {
            if (UsesCA(a) && (a.Ca != b.Ca || a.Cin != b.Cin)) return false;
            pair.Ca = b.Ca; pair.Cin = b.Cin;
        }
        if (UsesVA(b))
        {
            if (UsesVA(a) && a.Va != b.Va) return false;
            pair.Va = b.Va;
        }
        if (b.Owm != 0)
        {
            if (a.Owm != 0 && (b.Om != a.Om || b.Owm != a.Owm || b.Oc != a.Oc)) return false;
            pair.Owm = b.Owm; pair.Om = b.Om; pair.Oc = b.Oc;
        }
        return MergeRegisterOutputMasks(pair, a, b);
    }

    private static bool ForcedPair(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        if (ForcedPair2(out pair, a, b)) return true;
        if (ConvertToImv(out var da, a) && ForcedPair2(out pair, da, b)) return true;
        if (ConvertToImv(out var db, b) && ForcedPair2(out pair, a, db)) return true;
        pair = null!;
        return false;
    }

    private static bool Pairable(out VsInstruction pair, VsInstruction a, VsInstruction b)
    {
        pair = null!;
        if (InputOutputDependency(a, b)) return false;
        if (ForcedPair(out pair, a, b)) return true;
        // ADD operands commute -- try with A/C swapped.
        if (SwapAC(out var ta, a)) { if (ForcedPair(out pair, ta, b)) return true; }
        else if (SwapAC(out var tb, b)) { if (ForcedPair(out pair, a, tb)) return true; }
        return false;
    }

    private static bool SequentialPairable(out VsInstruction pair, VsInstruction a, VsInstruction b) =>
        Pairable(out pair, a, b) || PairableMasks(out pair, a, b)
        || PairableMulAdd(out pair, a, b) || PairableMasks3(out pair, a, b);

    // --- Pass 1: PeepholePairOutputMasks (api.cpp:6075) ----------------------

    private static void PeepholePairOutputMasks(List<VsInstruction> program)
    {
        int outPC = 0;
        for (int pc = 0; pc < program.Count; pc++, outPC++)
        {
            if (pc < program.Count - 1 && PairableMasks(out var pair, program[pc], program[pc + 1]))
            {
                program[outPC] = pair;
                pc++;
            }
            else
            {
                program[outPC] = program[pc];
            }
        }
        if (outPC < program.Count)
            program.RemoveRange(outPC, program.Count - outPC);
    }

    // --- stubbed passes (TODO: port from api.cpp) ----------------------------

    // Register_t enumeration (microcodeformat.h) -- one flat address space the
    // stripper's liveness tables are indexed by.
    private const int REG_V0 = 0, REG_V15 = 15, REG_O0 = 16, REG_oPos = 16, REG_O15 = 31,
                      REG_C0 = 32, REG_R0 = 224, REG_R1 = 225, REG_R11 = 235, REG_R12 = 236,
                      REG_R15 = 239, REG_ARL = 240, REG_ZER = 241;

    /// <summary>
    /// DeadCodeStripper (api.cpp:2766). Walks the program backward tracking, per
    /// register component, whether a later instruction reads it before it is next
    /// written; narrows each write to only the live components and drops
    /// instructions whose every output is dead. Stateful, so it is an instance.
    /// </summary>
    private sealed class DeadCodeStripper
    {
        private struct RegMask { public int Reg; public byte Mask; }
        private sealed class OutRegMaskSet
        {
            public bool OutIsMac;
            public RegMask O, R /* mac/ARL */, S /* ilu */;
            public byte CombinedWriteMask() => (byte)(O.Mask | R.Mask | S.Mask);
            public byte MacWriteMask() => (byte)(R.Mask | (OutIsMac ? O.Mask : 0));
            public byte IluWriteMask() => (byte)(S.Mask | (!OutIsMac ? O.Mask : 0));
        }
        private sealed class InRegMaskSet { public RegMask A, B, C, A0; }

        private readonly byte[] _regUsed = new byte[256];
        private readonly byte[] _regLastWritten = new byte[256];
        private byte _anyPRRead;
        private readonly bool _stateShader;

        public DeadCodeStripper(bool stateShader) => _stateShader = stateShader;

        public void Run(List<VsInstruction> program)
        {
            InitRegisters();
            var kept = new List<VsInstruction>();      // built back-to-front
            for (int i = program.Count - 1; i >= 0; i--)
                ProcessInstruction(program[i], kept);
            program.Clear();
            for (int i = kept.Count - 1; i >= 0; i--)  // reverse back to source order
                program.Add(kept[i]);
        }

        private void ProcessInstruction(VsInstruction pIn, List<VsInstruction> kept)
        {
            VsInstruction temp = pIn.Clone();
            var outm = new OutRegMaskSet();
            CalcOutputMasks(temp, outm);
            NarrowOutputMasks(temp, outm);
            UpdateCode(temp, outm);
            RemoveNullMoves(temp);

            if (!IsNOP(temp) && outm.CombinedWriteMask() != 0)
            {
                var inm = new InRegMaskSet();
                CalcInputMasks(temp, outm, inm);
                // Moving backward, record outputs before inputs so a register used
                // as both keeps read-after-write correct.
                RecordOutputMasks(temp, outm);
                RecordInputMasks(temp, inm);
                if (temp.Ilu != 0 || temp.Mac != 0)
                    kept.Add(temp);
            }
        }

        // --- null-move detection -----------------------------------------------

        private static bool IsMacMovRegNOP(VsInstruction c) =>
            c.Mac == MAC_MOV && c.Amx == MX_R && c.Rwm != 0 && c.Arr == c.Rw && c.Ane == 0
            && ((c.Rwm & 8) == 0 || c.Axs == CSW_X) && ((c.Rwm & 4) == 0 || c.Ays == CSW_Y)
            && ((c.Rwm & 2) == 0 || c.Azs == CSW_Z) && ((c.Rwm & 1) == 0 || c.Aws == CSW_W);

        private static bool IsMacMovConstNOP(VsInstruction c) =>
            c.Mac == MAC_MOV && c.Amx == MX_C && c.Cin == 0
            && c.Owm != 0 && c.Om == OM_MAC && (c.Oc & 0x100) == 0 && (c.Oc & 0xff) == c.Ca && c.Ane == 0
            && ((c.Owm & 8) == 0 || c.Axs == CSW_X) && ((c.Owm & 4) == 0 || c.Ays == CSW_Y)
            && ((c.Owm & 2) == 0 || c.Azs == CSW_Z) && ((c.Owm & 1) == 0 || c.Aws == CSW_W);

        private static bool IsIluMovRegNOP(VsInstruction c) =>
            c.Ilu == ILU_MOV && c.Cmx == MX_R && c.Swm != 0
            && ((c.Crr == c.Rw && c.Mac == 0) || (c.Mac != 0 && c.Crr == 1)) && c.Cne == 0
            && ((c.Swm & 8) == 0 || c.Cxs == CSW_X) && ((c.Swm & 4) == 0 || c.Cys == CSW_Y)
            && ((c.Swm & 2) == 0 || c.Czs == CSW_Z) && ((c.Swm & 1) == 0 || c.Cws == CSW_W);

        private static bool IsIluMovConstNOP(VsInstruction c) =>
            c.Ilu == ILU_MOV && c.Cmx == MX_C && c.Cin == 0
            && c.Owm != 0 && c.Om == OM_ILU && (c.Oc & 0x100) == 0 && (c.Oc & 0xff) == c.Ca && c.Cne == 0
            && ((c.Owm & 8) == 0 || c.Cxs == CSW_X) && ((c.Owm & 4) == 0 || c.Cys == CSW_Y)
            && ((c.Owm & 2) == 0 || c.Czs == CSW_Z) && ((c.Owm & 1) == 0 || c.Cws == CSW_W);

        private static void RemoveNullMoves(VsInstruction c)
        {
            if (IsMacMovRegNOP(c)) c.Rwm = 0;
            if (IsMacMovConstNOP(c)) c.Owm = 0;
            if (IsIluMovRegNOP(c)) c.Swm = 0;
            if (IsIluMovConstNOP(c)) c.Owm = 0;
        }

        private static bool IsNOP(VsInstruction c)
        {
            bool macIsNOP = c.Mac == 0;
            bool iluIsNOP = c.Ilu == 0;
            if (!macIsNOP)
                macIsNOP = (!(c.Om == OM_MAC && c.Owm != 0) && IsMacMovRegNOP(c))
                        || (c.Rwm == 0 && IsMacMovConstNOP(c));
            if (macIsNOP && !iluIsNOP)
                iluIsNOP = (!(c.Om == OM_ILU && c.Owm != 0) && IsIluMovRegNOP(c))
                        || (c.Swm == 0 && IsIluMovConstNOP(c));
            return macIsNOP && iluIsNOP;
        }

        // --- output masks ------------------------------------------------------

        private static void CalcOutputMasks(VsInstruction c, OutRegMaskSet outm)
        {
            if (c.Mac != 0)
            {
                if (c.Mac == MAC_ARL) { outm.R.Mask = 8; outm.R.Reg = REG_ARL; }
                if (c.Rwm != 0) { outm.R.Mask = (byte)c.Rwm; outm.R.Reg = (int)(REG_R0 + c.Rw); }
                if (c.Om == OM_MAC && c.Owm != 0)
                {
                    outm.OutIsMac = true;
                    outm.O.Mask = (byte)c.Owm;
                    outm.O.Reg = (int)((c.Oc & 0x100) != 0 ? REG_O0 + (c.Oc & 0xff) : REG_C0 + (c.Oc & 0xff));
                }
            }
            if (c.Ilu != 0)
            {
                if (c.Om == OM_ILU && c.Owm != 0)
                {
                    outm.OutIsMac = false;
                    outm.O.Mask = (byte)c.Owm;
                    outm.O.Reg = (int)((c.Oc & 0x100) != 0 ? REG_O0 + (c.Oc & 0xff) : REG_C0 + (c.Oc & 0xff));
                }
                if (c.Swm != 0)
                {
                    outm.S.Mask = (byte)c.Swm;
                    outm.S.Reg = (int)(c.Mac != 0 ? REG_R1 : REG_R0 + c.Rw);
                }
            }
        }

        private static void UpdateCode(VsInstruction c, OutRegMaskSet outm)
        {
            bool oldMac = c.Mac != 0;
            if (c.Mac != 0)
            {
                if (outm.MacWriteMask() == 0)
                {
                    c.Mac = 0; c.Rwm = 0;
                    if (c.Om == OM_MAC) { c.Owm = 0; c.Oc = 0x1ff; }
                }
                else
                {
                    if (c.Om == OM_MAC && c.Owm != 0) c.Owm = outm.O.Mask;
                    if (c.Rwm != 0) c.Rwm = outm.R.Mask;
                }
            }
            if (c.Ilu != 0)
            {
                if (outm.IluWriteMask() == 0)
                {
                    c.Ilu = 0; c.Swm = 0;
                    if (c.Om == OM_ILU) { c.Owm = 0; c.Oc = 0x1ff; }
                }
                else
                {
                    if (c.Om == OM_ILU && c.Owm != 0) c.Owm = outm.O.Mask;
                    if (c.Swm != 0)
                    {
                        c.Swm = outm.S.Mask;
                        if (oldMac && c.Mac == 0) c.Rw = 1;   // now unpaired: ilu owns r1
                    }
                }
            }
        }

        // --- input masks -------------------------------------------------------

        private static void CalcInputMasks(VsInstruction c, OutRegMaskSet outm, InRegMaskSet inm)
        {
            inm.A0.Reg = REG_ZER;
            byte aMask = 0, bMask = 0, cMask = 0;
            switch (c.Mac)
            {
                case 0: break;                                       // NOP
                case MAC_MOV: aMask = outm.MacWriteMask(); break;
                case VsInstruction.MacMul: case VsInstruction.MacMin: case VsInstruction.MacMax:
                case VsInstruction.MacSlt: case VsInstruction.MacSge:
                    aMask = bMask = outm.MacWriteMask(); break;
                case MAC_ADD: aMask = cMask = outm.MacWriteMask(); break;
                case VsInstruction.MacMad: aMask = bMask = cMask = outm.MacWriteMask(); break;
                case VsInstruction.MacDp3: aMask = 0xe; bMask = 0xe; break;
                case VsInstruction.MacDph: aMask = 0xe; bMask = 0xf; break;
                case VsInstruction.MacDp4: aMask = 0xf; bMask = 0xf; break;
                case VsInstruction.MacDst: aMask = 0x6; bMask = 0x5; break;
                case MAC_ARL: aMask = 0x8; break;
            }
            switch (c.Ilu)
            {
                case 0: break;
                case ILU_MOV: cMask |= outm.IluWriteMask(); break;
                case VsInstruction.IluRcp: case VsInstruction.IluRcc: case VsInstruction.IluRsq:
                case VsInstruction.IluExp: case VsInstruction.IluLog: cMask |= 0x8; break;
                case VsInstruction.IluLit: cMask |= 0xd; break;
            }

            inm.A.Mask = UnswizzleMask(aMask, c.Axs, c.Ays, c.Azs, c.Aws);
            inm.B.Mask = UnswizzleMask(bMask, c.Bxs, c.Bys, c.Bzs, c.Bws);
            inm.C.Mask = UnswizzleMask(cMask, c.Cxs, c.Cys, c.Czs, c.Cws);

            CalcInputReg(aMask, inm, ref inm.A.Reg, c, c.Arr, c.Amx);
            CalcInputReg(bMask, inm, ref inm.B.Reg, c, c.Brr, c.Bmx);
            CalcInputReg(cMask, inm, ref inm.C.Reg, c, c.Crr, c.Cmx);
        }

        private static byte UnswizzleMask(byte swizMask, uint xs, uint ys, uint zs, uint ws)
        {
            int useX = (swizMask & 8) >> 3, useY = (swizMask & 4) >> 2,
                useZ = (swizMask & 2) >> 1, useW = swizMask & 1;
            return (byte)(
                  (useX << (int)(3 - xs))
                | (useY << (int)(3 - ys))
                | (useZ << (int)(3 - zs))
                | (useW << (int)(3 - ws)));
        }

        private static void CalcInputReg(byte operationMask, InRegMaskSet inm, ref int reg,
                                         VsInstruction c, uint rr, uint mx)
        {
            if (operationMask != 0)
            {
                switch (mx)
                {
                    case MX_R: reg = (int)(REG_R0 + rr); break;
                    case MX_V: reg = (int)(REG_V0 + c.Va); break;
                    case MX_C:
                        reg = (int)(REG_C0 + c.Ca);
                        if (c.Cin != 0) { inm.A0.Reg = REG_ARL; inm.A0.Mask = 8; }
                        break;
                }
            }
            else reg = REG_ZER;
        }

        // --- liveness narrow / record ------------------------------------------

        private void NarrowOutputMasks(VsInstruction c, OutRegMaskSet outm)
        {
            if (c.Mac != 0)
            {
                NarrowOutputMask(ref outm.R);
                if (outm.OutIsMac) NarrowOutputMask(ref outm.O);
            }
            if (c.Ilu != 0)
            {
                NarrowOutputMask(ref outm.S);
                if (!outm.OutIsMac) NarrowOutputMask(ref outm.O);
            }
        }

        private void NarrowOutputMask(ref RegMask mask)
        {
            if (mask.Reg >= REG_C0 && mask.Reg < REG_C0 + 192)
                mask.Mask &= (byte)(_anyPRRead | (0xf & ~_regLastWritten[mask.Reg]));
            else
                mask.Mask &= (byte)(0xf & ~_regLastWritten[mask.Reg]);

            if ((mask.Reg >= REG_R0 && mask.Reg <= REG_R11) || mask.Reg == REG_ARL)
                mask.Mask &= (byte)(0xf & _regUsed[mask.Reg]);
        }

        private void RecordOutputMasks(VsInstruction c, OutRegMaskSet outm)
        {
            if (c.Mac != 0)
            {
                RecordOutputMask(outm.R);
                if (outm.OutIsMac) RecordOutputMask(outm.O);
            }
            if (c.Ilu != 0)
            {
                RecordOutputMask(outm.S);
                if (!outm.OutIsMac) RecordOutputMask(outm.O);
            }
        }

        private void RecordOutputMask(RegMask mask)
        {
            _regUsed[mask.Reg] |= mask.Mask;
            _regLastWritten[mask.Reg] |= mask.Mask;
            if (mask.Reg == REG_oPos)   // writing oPos also writes r12
            {
                _regUsed[REG_R12] |= mask.Mask;
                _regLastWritten[REG_R12] |= mask.Mask;
            }
        }

        private void RecordInputMasks(VsInstruction c, InRegMaskSet inm)
        {
            RecordInputMask(c, inm.A);
            RecordInputMask(c, inm.B);
            RecordInputMask(c, inm.C);
            RecordInputMask(c, inm.A0);
        }

        private void RecordInputMask(VsInstruction c, RegMask mask)
        {
            if (mask.Reg == REG_ZER) return;
            if (c.Cin != 0 && mask.Reg >= REG_C0 && mask.Reg < REG_C0 + 192)
            {
                _anyPRRead |= mask.Mask;   // indexed read could hit any c register
            }
            else
            {
                _regUsed[mask.Reg] |= mask.Mask;
                _regLastWritten[mask.Reg] &= (byte)~mask.Mask;
                if (mask.Reg == REG_R12)   // reading r12 counts as reading o[0]
                {
                    _regUsed[REG_oPos] |= mask.Mask;
                    _regLastWritten[REG_oPos] &= (byte)~mask.Mask;
                }
            }
        }

        private void InitRegisters()
        {
            Array.Clear(_regUsed);
            Array.Clear(_regLastWritten);
            if (_stateShader)   // non-plain-vertex: CPU can read const regs afterward
                for (int i = REG_C0; i < REG_C0 + 192; i++)
                    _regUsed[i] = 0xf;
            _anyPRRead = 0;
        }
    }

    /// <summary>
    /// Renamer (api.cpp:4383). Builds SSA-style Values (one per written register
    /// component) grouped into virtual registers by def-use, computes each vreg's
    /// live range, then reassigns physical temp registers (rotor-ordered, r1
    /// last, honoring the paired-ilu-needs-r1 / paired-mac-cant-be-r1 rules) and
    /// rewrites the code -- freeing register pressure so more instructions pair.
    /// If register assignment can't be satisfied, it leaves the code untouched.
    /// </summary>
    private sealed class Renamer
    {
        private sealed class PRegSet
        {
            public readonly int[][] In = { new int[4], new int[4], new int[4] };  // [abc][wzyx]
            public int Rw, Sw;
        }
        private sealed class ValueInfo { public int First, Last; public byte Reg, Mask; public int Owner, Next; }
        private sealed class VRegInfo
        {
            public int First, Last, HeadValue;
            public byte Mask, Reg, NewReg;
            public bool PrefersR1, RequiresR1, CantBeR1, FixedComponents;
            public readonly byte[] Sw = new byte[4];
            public void Reset()
            {
                First = Last = HeadValue = 0; Mask = Reg = NewReg = 0;
                PrefersR1 = RequiresR1 = CantBeR1 = FixedComponents = false;
                Array.Clear(Sw);
            }
        }

        // Output components that can't be moved to another channel by swizzling.
        private static readonly byte[] kFixedIluOutputs = { 0, 0, 0, 0, 0, 0xf, 0xf, 0 };   // EXP, LOG
        private static readonly byte[] kFixedMacOutputs =
            { 0, 0, 0, 0, 0, 0, 0, 0, 0xf, 0, 0, 0, 0, 0, 0, 0 };                            // DST
        // Search order for a free temp: r1 is assigned last (it is the paired-ilu slot).
        private static readonly int[] kRegOrder = { 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private PRegSet[] _regSet = null!;
        private ValueInfo[] _valueInfo = null!;
        private int _valueCount;
        private VRegInfo[] _vregInfo = null!;
        private int _vregCount;
        private int _renameRegRotor;
        private List<VsInstruction> _ucode = null!;

        public void Run(List<VsInstruction> ucode)
        {
            if (EligableInstructions(ucode)) Rename(ucode);
        }

        private static bool EligableInstructions(List<VsInstruction> ucode)
        {
            foreach (var pI in ucode)
            {
                if (pI.Mac == 0 && pI.Ilu != 0 && pI.Swm != 0 && pI.Rw != 1) return true;
                // Also worth it if a MAC writes r1 -- frees r1 so movs pair more easily.
                if (pI.Mac != 0 && pI.Ilu == 0 && pI.Rwm != 0 && pI.Rw == 1) return true;
            }
            return false;
        }

        private void Rename(List<VsInstruction> ucode)
        {
            _ucode = ucode;
            _valueCount = 0; _vregCount = 0; _renameRegRotor = 0;
            int n = ucode.Count;
            _regSet = new PRegSet[n];
            for (int i = 0; i < n; i++) _regSet[i] = new PRegSet();
            int total = 2 * n + 1;   // <=2 values/vregs per instruction, index 0 reserved
            _valueInfo = new ValueInfo[total];
            for (int i = 0; i < total; i++) _valueInfo[i] = new ValueInfo();
            _vregInfo = new VRegInfo[total];
            for (int i = 0; i < total; i++) _vregInfo[i] = new VRegInfo();

            MapRegisterUse();
            if (RemapVRegs()) RemapCode();   // failed assignment => leave code unchanged
        }

        // --- def-use / lifetime mapping ----------------------------------------

        private void MapRegisterUse()
        {
            var currentReg = new int[16 * 4];   // [reg*4 + (wzyx=0..3)]
            _valueCount = 1;
            _vregCount = 1;
            for (int i = 0; i < _ucode.Count; i++)
            {
                var pI = _ucode[i];
                var inMasks = new byte[3];
                ComputePostSwizzleUseMasks(pI, inMasks);
                UpdateInput(inMasks[0], pI.Amx, pI.Arr, pI.Axs, pI.Ays, pI.Azs, pI.Aws, _regSet[i].In[0], currentReg, i);
                UpdateInput(inMasks[1], pI.Bmx, pI.Brr, pI.Bxs, pI.Bys, pI.Bzs, pI.Bws, _regSet[i].In[1], currentReg, i);
                UpdateInput(inMasks[2], pI.Cmx, pI.Crr, pI.Cxs, pI.Cys, pI.Czs, pI.Cws, _regSet[i].In[2], currentReg, i);
                if (pI.Rwm != 0)
                    _regSet[i].Rw = UpdateOutput((byte)pI.Rwm, pI.Rw, currentReg, i, true);
                if (pI.Swm != 0)
                {
                    uint reg = pI.Mac != 0 ? 1u : pI.Rw;   // any MAC forces swm to r1
                    _regSet[i].Sw = UpdateOutput((byte)pI.Swm, reg, currentReg, i, false);
                }
            }
        }

        private void UpdateInput(byte inMask, uint mx, uint rr, uint xs, uint ys, uint zs, uint ws,
                                 int[] prr, int[] currentReg, int pc)
        {
            if (inMask == 0 || mx != MX_R) return;
            int vbase = (int)rr * 4;
            for (int j = 0; j < 4; j++)
            {
                if ((inMask & (1 << j)) != 0)
                {
                    int source = j switch { 0 => (int)ws, 1 => (int)zs, 2 => (int)ys, _ => (int)xs };
                    int v2 = currentReg[vbase + (3 - source)];
                    _valueInfo[v2].Last = pc;
                    _vregInfo[_valueInfo[v2].Owner].Last = pc;
                    prr[j] = v2;
                }
                else prr[j] = 0;
            }

            // Combine virtual registers read together into the lowest-numbered one.
            int winner = 0;
            for (int i = 0; i < 4; i++)
            {
                int v = prr[i];
                if (v != 0) { int vreg = _valueInfo[v].Owner; if (winner == 0 || vreg < winner) winner = vreg; }
            }
            for (int i = 0; i < 4; i++)
            {
                int v = prr[i];
                if (v == 0) continue;
                int vreg = _valueInfo[v].Owner;
                if (winner == vreg) continue;
                var pWinner = _vregInfo[winner];
                var pLoser = _vregInfo[vreg];
                if (pLoser.First == 0) continue;   // already merged out

                // Merge the two value chains, keeping them sorted ascending.
                int winnerVal = pWinner.HeadValue, loserVal = pLoser.HeadValue, tailVal = 0;
                bool firstTime = true;
                while (winnerVal != 0 || loserVal != 0)
                {
                    if (winnerVal == 0 || (loserVal != 0 && loserVal < winnerVal))
                    {
                        if (firstTime) pWinner.HeadValue = loserVal;
                        if (tailVal != 0) _valueInfo[tailVal].Next = loserVal;
                        _valueInfo[loserVal].Owner = winner;
                        tailVal = loserVal;
                        loserVal = _valueInfo[loserVal].Next;
                    }
                    else
                    {
                        if (tailVal != 0) _valueInfo[tailVal].Next = winnerVal;
                        tailVal = winnerVal;
                        winnerVal = _valueInfo[winnerVal].Next;
                    }
                    firstTime = false;
                }
                if (tailVal != 0) _valueInfo[tailVal].Next = 0;

                pWinner.First = Math.Min(pWinner.First, pLoser.First);
                pWinner.Last = Math.Max(pWinner.Last, pLoser.Last);
                pWinner.Mask |= pLoser.Mask;
                pWinner.PrefersR1 |= pLoser.PrefersR1;
                pWinner.RequiresR1 |= pLoser.RequiresR1;
                pWinner.CantBeR1 |= pLoser.CantBeR1;
                pWinner.FixedComponents |= pLoser.FixedComponents;
                pLoser.Reset();
            }
        }

        private int UpdateOutput(byte outMask, uint rw, int[] currentReg, int pc, bool isMac)
        {
            int vid = _valueCount++;
            int vbase = (int)rw * 4;
            for (int j = 0; j < 4; j++)
                if ((outMask & (1 << j)) != 0) currentReg[vbase + j] = vid;
            int vr = _vregCount++;
            var pV = _valueInfo[vid];
            pV.First = pc; pV.Last = pc; pV.Reg = (byte)rw; pV.Mask = outMask; pV.Owner = vr;
            var pVr = _vregInfo[vr];
            pVr.First = pc; pVr.Last = pc; pVr.HeadValue = vid; pVr.Mask = outMask; pVr.Reg = (byte)rw;
            UpdateComponentUse(vr, pc, isMac);
            return vid;
        }

        private void UpdateComponentUse(int vreg, int pc, bool isMac)
        {
            var pI = _ucode[pc];
            var pVr = _vregInfo[vreg];
            uint ilu = pI.Ilu;
            if (!isMac && ilu != 0 && pI.Swm != 0)
            {
                pVr.PrefersR1 = true;
                if (pI.Mac != 0) pVr.RequiresR1 = true;
            }
            if (isMac && ilu != 0 && pI.Rwm != 0) pVr.CantBeR1 = true;
            pVr.FixedComponents = (kFixedIluOutputs[pI.Ilu] | kFixedMacOutputs[pI.Mac]) != 0;
        }

        // --- physical register reassignment ------------------------------------

        private bool RemapVRegs()
        {
            var regs = new int[64];
            for (int pc = 0; pc < _ucode.Count; pc++)
            {
                var pRegSet = _regSet[pc];
                if (!RemapVRegs2Start(pRegSet.Rw, regs, pc)) return false;
                if (!RemapVRegs2Start(pRegSet.Sw, regs, pc)) return false;
                for (int op = 0; op < 3; op++) RemapVRegs2End(pRegSet.In[op], regs, pc);
            }
            return true;
        }

        private bool RemapVRegs2Start(int v, int[] regs, int pc)
        {
            if (v == 0) return true;
            var pVr = _vregInfo[_valueInfo[v].Owner];
            if (pVr.First != pc) return true;   // only assign on first use
            int vreg = _valueInfo[v].Owner;
            if (pVr.PrefersR1 && !pVr.CantBeR1)
            {
                if (IsRegFree(regs, 1, vreg)) return AssignReg(regs, 1, vreg);
                if (pVr.RequiresR1) return false;
                return AssignFreeReg(regs, vreg, false);
            }
            return AssignFreeReg(regs, vreg, pVr.CantBeR1);
        }

        private bool IsRegFree(int[] regs, int reg, int vreg)
        {
            int cbase = reg * 4;
            var pVr = _vregInfo[vreg];
            int mask = pVr.Mask;
            if (pVr.FixedComponents)
            {
                for (int i = 0; i < 4; i++)
                    if ((mask & (1 << i)) != 0 && regs[cbase + i] != 0) return false;
                return true;
            }
            int free = 0, needed = 0;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) != 0) needed++;
                if (regs[cbase + i] == 0) free++;
            }
            return free >= needed;
        }

        private bool AssignReg(int[] regs, int reg, int vreg)
        {
            int cbase = reg * 4;
            var pVr = _vregInfo[vreg];
            int mask = pVr.Mask;
            bool trySwizzled = false;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                if (regs[cbase + i] != 0) { trySwizzled = true; break; }
                regs[cbase + i] = vreg;
                pVr.NewReg = (byte)reg;
                pVr.Sw[i] = (byte)(3 - i);
            }
            if (!trySwizzled) return true;
            if (pVr.FixedComponents) return false;

            int component = 0;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                while (component < 4 && regs[cbase + component] != 0) component++;
                if (component >= 4) return false;
                regs[cbase + component] = vreg;
                pVr.NewReg = (byte)reg;
                pVr.Sw[i] = (byte)(3 - component);
            }
            return true;
        }

        private bool AssignFreeReg(int[] regs, int vreg, bool cantBeR1)
        {
            // Rotate the starting register to scatter assignments (helps reordering).
            _renameRegRotor++;
            if (_renameRegRotor >= kRegOrder.Length) _renameRegRotor -= kRegOrder.Length;
            for (int i = 0; i < kRegOrder.Length; i++)
            {
                int i2 = _renameRegRotor + i;
                if (i2 >= kRegOrder.Length) i2 -= kRegOrder.Length;
                int reg = kRegOrder[i2];
                if (IsRegFree(regs, reg, vreg)) return AssignReg(regs, reg, vreg);
            }
            if (!cantBeR1 && IsRegFree(regs, 1, vreg)) return AssignReg(regs, 1, vreg);
            return false;
        }

        private void RemapVRegs2End(int[] inComps, int[] regs, int pc)
        {
            int vreg = 0;
            for (int c = 0; c < 4; c++) { int v = inComps[c]; if (v != 0) vreg = _valueInfo[v].Owner; }
            if (vreg == 0) return;
            var pVr = _vregInfo[vreg];
            if (pVr.Last != pc) return;   // free the register at its last use
            int mask = pVr.Mask;
            int rbase = pVr.NewReg * 4;
            for (int c = 0; c < 4; c++)
                if ((mask & (1 << c)) != 0) regs[rbase + (3 - pVr.Sw[c])] = 0;
        }

        // --- rewrite ------------------------------------------------------------

        private void RemapCode()
        {
            for (int i = 0; i < _ucode.Count; i++)
            {
                var pI = _ucode[i];
                var pRegSet = _regSet[i];
                for (int j = 0; j < 3; j++)
                {
                    int v = 0;
                    for (int k = 0; k < 4; k++) { int v2 = pRegSet.In[j][k]; if (v2 != 0) { v = v2; break; } }
                    if (v == 0) continue;
                    var pVr = _vregInfo[_valueInfo[v].Owner];
                    switch (j)
                    {
                        case 0:
                            pI.Arr = pVr.NewReg;
                            pI.Aws = pVr.Sw[3 - pI.Aws]; pI.Azs = pVr.Sw[3 - pI.Azs];
                            pI.Ays = pVr.Sw[3 - pI.Ays]; pI.Axs = pVr.Sw[3 - pI.Axs];
                            break;
                        case 1:
                            pI.Brr = pVr.NewReg;
                            pI.Bws = pVr.Sw[3 - pI.Bws]; pI.Bzs = pVr.Sw[3 - pI.Bzs];
                            pI.Bys = pVr.Sw[3 - pI.Bys]; pI.Bxs = pVr.Sw[3 - pI.Bxs];
                            break;
                        case 2:
                            pI.Crr = pVr.NewReg;
                            pI.Cws = pVr.Sw[3 - pI.Cws]; pI.Czs = pVr.Sw[3 - pI.Czs];
                            pI.Cys = pVr.Sw[3 - pI.Cys]; pI.Cxs = pVr.Sw[3 - pI.Cxs];
                            break;
                    }
                }
                if (pRegSet.Rw != 0)
                {
                    var pVr = _vregInfo[_valueInfo[pRegSet.Rw].Owner];
                    pI.Rw = pVr.NewReg;
                    pI.Rwm = CalcSwizzledWriteMask(pI.Rwm, pVr);
                }
                if (pRegSet.Sw != 0)
                {
                    var pVr = _vregInfo[_valueInfo[pRegSet.Sw].Owner];
                    if (pI.Mac == 0) pI.Rw = pVr.NewReg;
                    pI.Swm = CalcSwizzledWriteMask(pI.Swm, pVr);
                }
            }
        }

        private static uint CalcSwizzledWriteMask(uint mask, VRegInfo pVr)
        {
            uint result = 0;
            for (int c = 0; c < 4; c++)
                if ((mask & (1 << c)) != 0) result |= 1u << (3 - pVr.Sw[c]);
            return result;
        }
    }

    // Reorderer (api.cpp:3477) -- instruction scheduler.
    private static void Reorderer(List<VsInstruction> program) { }

    // PeepholeOptimize (api.cpp:5641): the second operand of an ADD does not
    // stall, so swapping an ADD's A and C is faster when it lowers the modelled
    // stall. Walks the program through the stall sim, adopting each beneficial swap.
    private static void PeepholeOptimize(List<VsInstruction> program, bool stateShader)
    {
        var sim = new VertexStallSim();
        sim.Initialize(!stateShader, program.Count);
        for (int pc = 0; pc < program.Count; pc++)
        {
            VsInstruction pI = program[pc];
            if (SwapAC(out var temp, pI)
                && sim.CalculateStall(pI, out _) > sim.CalculateStall(temp, out _))
            {
                program[pc] = temp;
                pI = temp;
            }
            sim.Do(pI, out _, out _);
        }
    }

    // Shared driver for the two pairers (api.cpp:6479, 6507): greedily fold each
    // instruction with the following ones while they keep pairing.
    private delegate bool PairFn(out VsInstruction pair, VsInstruction a, VsInstruction b);

    private static void PairSweep(List<VsInstruction> program, PairFn pairable)
    {
        int outPC = 0;
        for (int pc = 0; pc < program.Count; pc++, outPC++)
        {
            VsInstruction a = program[pc];
            while (pc < program.Count - 1 && pairable(out var pair, a, program[pc + 1]))
            {
                a = pair;
                pc++;
            }
            program[outPC] = a;
        }
        if (outPC < program.Count)
            program.RemoveRange(outPC, program.Count - outPC);
    }

    // PeepholePair1 (api.cpp:6479) -- Pairable/ForcedPair MAC+ILU merge.
    private static void PeepholePair1(List<VsInstruction> program) => PairSweep(program, Pairable);

    // PeepholePair2 (api.cpp:6507) -- SequentialPairable merge.
    private static void PeepholePair2(List<VsInstruction> program) => PairSweep(program, SequentialPairable);
}
