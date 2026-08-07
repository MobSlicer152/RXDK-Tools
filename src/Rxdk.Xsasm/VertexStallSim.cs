namespace Rxdk.Xsasm;

/// <summary>
/// TLEngineSim (api.cpp:1948) -- a cycle-accurate model of the NV2A vertex
/// pipeline's read-after-write stalls, with the per-register-component scoreboard,
/// bypass paths and shadow-stalls. The Reorderer and PeepholeOptimize use it to
/// decide whether reordering an instruction reduces stalls, so it must match
/// retail exactly for their scheduling decisions to reproduce the goldens.
///
/// Ordinary (and screen-space) vertex shaders run the multithreaded model
/// (float cycles, the big RegScoreboard); vertex STATE shaders run the
/// single-threaded model (integer cycles, STRegScoreboard). Ported as a nested
/// type so it shares VertexOptimizer's component-use tables and helpers.
///
/// NOT yet wired in or golden-verified -- the Reorderer that drives it is still
/// stubbed. It compiles and is exercised only once the Reorderer lands.
/// </summary>
internal static partial class VertexOptimizer
{
    // |a - b| < e   (api.cpp:108)
    private static bool Epsilon(float a, float b, float e) => a - b < e && a - b > -e;

    // ComputeEffectiveReadMask(pI, channel) (api.cpp:5916) -- reuse Read4.
    private static byte EffectiveReadMask(VsInstruction a, int channel) => channel switch
    {
        0 => (byte)Read4(a.Axs, a.Ays, a.Azs, a.Aws),
        1 => (byte)Read4(a.Bxs, a.Bys, a.Bzs, a.Bws),
        2 => (byte)Read4(a.Cxs, a.Cys, a.Czs, a.Cws),
        _ => 0,
    };

    // StripMacInstruction (api.cpp:5584) -- reduce a paired instruction to its ilu.
    private static void StripMacInstruction(VsInstruction pI)
    {
        if (pI.Mac == 0) return;
        if (pI.Swm != 0) { pI.Rw = 1; pI.Rwm = pI.Swm; }
        else { pI.Rw = 0; pI.Rwm = 0; }
        pI.Mac = 0;
        pI.Amx = MX_V; pI.Arr = 0; pI.Ane = 0; pI.Axs = 0; pI.Ays = 0; pI.Azs = 0; pI.Aws = 0;
        pI.Bmx = MX_V; pI.Brr = 0; pI.Bne = 0; pI.Bxs = 0; pI.Bys = 0; pI.Bzs = 0; pI.Bws = 0;
    }

    // Stall categories (api.cpp:1913). Char values are load-bearing only as
    // distinct tags the scoreboard switches on.
    private enum StallType
    {
        None = ' ', RegInit = 'A', Bypass = 'B', Standard = 'C', Shadow = 'D',
        StandardAndShadow = 'E', BypassAndShadow = 'F', StandardAndBypShad = 'G',
        Arl = 'H', ArlShadow = 'I', XOutputsIsXCycles = 'J', SingleThreaded = 'K',
        SmallShader = 'L',
    }

    private enum OpType { ALU, MLU, ILU, ARL, Other }

    private sealed class VertexStallSim
    {
        private const int MAC_ADD_ = 3, MAC_MAD_ = 4, MAC_ARL_ = 13;

        private bool _multiThreaded;
        private float _currentCycle;
        private readonly int[] _c = new int[2];          // constant-bank write cycles (state shaders)
        private readonly bool[] _outRegWritten = new bool[16];
        private readonly STRegScoreboard _st = new();
        private readonly RegScoreboard _sb = new();

        public void Initialize(bool isVertexShader, int numInst)
        {
            _multiThreaded = isVertexShader;
            Clear(numInst);
        }

        private void Clear(int numInst)
        {
            _c[0] = _c[1] = -6;
            for (int i = 0; i < 16; i++) _outRegWritten[i] = false;
            _st.Clear();
            _sb.Clear(numInst);
            _currentCycle = 0;
        }

        public bool IsStall(VsInstruction pI) => CalculateStall(pI, out _) > 0;

        public float CalculateStall(VsInstruction pI, out StallType reason) => CalculateRealStall(pI, out reason);

        public float CalculateFinalStall(out StallType reason)
        {
            if (_multiThreaded)
            {
                float numRegsWritten = 0, stall = 0;
                reason = StallType.None;
                for (int i = 0; i < 16; i++) if (_outRegWritten[i]) numRegsWritten += 1.0f;
                if (numRegsWritten > _currentCycle)
                {
                    stall = numRegsWritten - _currentCycle;
                    reason = StallType.XOutputsIsXCycles;
                }
                if (_currentCycle < 2.2f)
                {
                    float newStall = 2.2f - _currentCycle;
                    if (stall < newStall) { stall = newStall; reason = StallType.SmallShader; }
                }
                return stall;
            }
            reason = StallType.SingleThreaded;
            return 18.0f;
        }

        public void Do(VsInstruction pI, out float stall, out StallType reason)
        {
            reason = StallType.None;
            if (_multiThreaded)
            {
                float realStall = CalculateRealStall(pI, out reason);
                _currentCycle += realStall;
                if (realStall > 0.19f) _sb.StartNewInstruction();   // avoid a duplicate shadow
                DoOut(pI, reason);
                _currentCycle += 0.5f;
                stall = realStall;
            }
            else
            {
                float realStall = CalculateRealStall(pI, out reason);
                _currentCycle += realStall;
                STDoOut(pI);
                _currentCycle += 1;
                stall = realStall;
            }
        }

        // --- op classification (api.cpp:2070) ----------------------------------

        private static readonly OpType[] kMacOpType =
        {
            OpType.Other, OpType.MLU, OpType.MLU, OpType.ALU, OpType.ALU, OpType.ALU, OpType.ALU,
            OpType.ALU, OpType.MLU, OpType.MLU, OpType.MLU, OpType.MLU, OpType.MLU, OpType.Other,
            OpType.Other, OpType.Other,
        };

        private static OpType MacOpType(VsInstruction pI) =>
            pI.Ilu != 0 ? OpType.ALU : kMacOpType[pI.Mac];   // an active ILU defers the MLU output

        private static OpType ILUOpType(VsInstruction pI) => pI.Ilu != 0 ? OpType.ILU : OpType.Other;

        // --- stall computation (api.cpp:2113) ----------------------------------

        private float CalculateRealStall(VsInstruction pI, out StallType reason)
        {
            reason = StallType.None;
            if (_multiThreaded)
            {
                var useMasks = new byte[3];
                for (int i = 0; i < 3; i++) useMasks[i] = EffectiveReadMask(pI, i);

                float stall = AReady(pI, useMasks[0], _currentCycle, out StallType r);
                reason = r;
                float bready = BReady(pI, useMasks[1], _currentCycle, out r);
                if (bready > stall || reason == StallType.None) { reason = r; stall = bready; }
                float cready = CReady(pI, useMasks[2], _currentCycle, out r);
                if (cready > stall || reason == StallType.None) { reason = r; stall = cready; }
                return stall;
            }
            else
            {
                var useMasks = new byte[3];
                for (int i = 0; i < 3; i++) useMasks[i] = EffectiveReadMask(pI, i);
                int stall;
                for (stall = 0; stall < 5; stall += 1)   // never more than five cycles
                {
                    if (STAReady(pI, useMasks[0], (int)_currentCycle + stall)
                        && STBReady(pI, useMasks[1], (int)_currentCycle + stall)
                        && STCReady(pI, useMasks[2], (int)_currentCycle + stall))
                        break;
                }
                return stall;
            }
        }

        private bool STAReady(VsInstruction pI, byte m, int cyc) => STMuxReady(pI, pI.Amx, pI.Arr, pI.Axs, pI.Ays, pI.Azs, pI.Aws, 0, m, cyc);
        private bool STBReady(VsInstruction pI, byte m, int cyc) => STMuxReady(pI, pI.Bmx, pI.Brr, pI.Bxs, pI.Bys, pI.Bzs, pI.Bws, 1, m, cyc);
        private bool STCReady(VsInstruction pI, byte m, int cyc) => STMuxReady(pI, pI.Cmx, pI.Crr, pI.Cxs, pI.Cys, pI.Czs, pI.Cws, 2, m, cyc);

        private float AReady(VsInstruction pI, byte m, float cyc, out StallType r) => MuxReady(pI, pI.Amx, pI.Arr, 0, m, cyc, out r);
        private float BReady(VsInstruction pI, byte m, float cyc, out StallType r) => MuxReady(pI, pI.Bmx, pI.Brr, 1, m, cyc, out r);
        private float CReady(VsInstruction pI, byte m, float cyc, out StallType r) => MuxReady(pI, pI.Cmx, pI.Crr, 2, m, cyc, out r);

        private static bool Bank(uint address) => ((address >> 2) & 1) != 0;

        private bool STMuxReady(VsInstruction pI, uint mx, uint rr, uint xs, uint ys, uint zs, uint ws, byte muxIndex, byte useMask, int cycle)
        {
            switch (mx)
            {
                case MX_R: return STRegReady(pI, muxIndex, (byte)rr, useMask, cycle);
                case MX_V: return true;
                case MX_C: return cycle - _c[Bank(pI.Crr) ? 1 : 0] >= 6;
                default: return true;
            }
        }

        private float MuxReady(VsInstruction pI, uint mx, uint rr, byte muxIndex, byte useMask, float cycle, out StallType reason)
        {
            reason = StallType.None;
            switch (mx)
            {
                case MX_R: return RegReady(pI, muxIndex, (byte)rr, useMask, cycle, out reason);
                case MX_V: return 0.0f;
                case MX_C:
                    if (pI.Cin != 0) return _sb.Ready(13, 0, false, out reason);   // reading c[a0.x] right after writing a0.x
                    return 0.0f;
                default: return 0.0f;
            }
        }

        private bool STRegReady(VsInstruction pI, byte muxIndex, byte rr, byte useMask, int cycle)
        {
            for (int c = 0; c < 4; c++)
            {
                byte cMask = (byte)(1 << c);
                if ((useMask & cMask) == 0) continue;
                if (_st.Ready(rr, c, cycle)) continue;
                if (_st.TakeBypass(pI, muxIndex, rr, cMask, cycle)) continue;
                return false;
            }
            return true;
        }

        private float RegReady(VsInstruction pI, byte muxIndex, byte rr, byte useMask, float cycle, out StallType reason)
        {
            reason = StallType.None;
            float stall = 0.0f;
            for (int c = 0; c < 4; c++)
            {
                byte cMask = (byte)(1 << c);
                if ((useMask & cMask) == 0) continue;
                bool isAluCInput = muxIndex == 2 && pI.Ilu == 0;
                float whenReady = _sb.Ready(rr, c, isAluCInput, out StallType tempReason);
                if (stall <= whenReady) { stall = whenReady; reason = tempReason; }
            }
            return stall;
        }

        // --- commit (api.cpp:2205, 2244) ---------------------------------------

        private void STDoOut(VsInstruction pI)
        {
            if (pI.Rwm != 0) _st.Start(MacOpType(pI), (byte)pI.Rw, (byte)pI.Rwm, (int)_currentCycle);
            if (pI.Swm != 0)
            {
                uint iluRw = (pI.Mac != 0 && pI.Ilu != 0) ? 1u : pI.Rw;
                _st.Start(ILUOpType(pI), (byte)iluRw, (byte)pI.Swm, (int)_currentCycle);
            }
            if (pI.Owm != 0)
            {
                bool ocOutput = (pI.Oc & 0x100) != 0;
                uint ocIndex = pI.Oc & 0xff;
                if (ocOutput)
                {
                    if (ocIndex == 0)   // oPos shadowed as r12
                    {
                        if (pI.Om == 0) _st.Start(MacOpType(pI), 12, (byte)pI.Owm, (int)_currentCycle);
                        else _st.Start(ILUOpType(pI), 12, (byte)pI.Owm, (int)_currentCycle);
                    }
                }
                else if (ocIndex < 192) _c[Bank(ocIndex) ? 1 : 0] = 1;   // (C++ assigns bool true == 1)
            }
        }

        private void DoOut(VsInstruction pI, StallType reason)
        {
            _sb.StartNewInstruction();

            if (pI.Rwm != 0) _sb.Start(MacOpType(pI), (byte)pI.Rw, (byte)pI.Rwm, _currentCycle, reason);
            if (pI.Swm != 0)
            {
                uint iluRw = (pI.Mac != 0 && pI.Ilu != 0) ? 1u : pI.Rw;
                _sb.Start(ILUOpType(pI), (byte)iluRw, (byte)pI.Swm, _currentCycle, reason);
            }
            if (pI.Owm != 0)
            {
                bool ocOutput = (pI.Oc & 0x100) != 0;
                uint ocIndex = pI.Oc & 0xff;
                if (ocOutput)
                {
                    _outRegWritten[ocIndex] = true;
                    if (ocIndex == 0)   // oPos shadowed as r12
                    {
                        if (pI.Om == 0) _sb.Start(MacOpType(pI), 12, (byte)pI.Owm, _currentCycle, reason);
                        else _sb.Start(ILUOpType(pI), 12, (byte)pI.Owm, _currentCycle, reason);
                    }
                }
            }

            if (pI.Mac == MAC_ARL_) _sb.Start(OpType.ARL, 13, 1, _currentCycle, reason);   // 0.16-cycle arl stall
        }

        // --- single-threaded scoreboard (api.cpp:2384) -------------------------

        private sealed class STRegScoreboard
        {
            private readonly int[,] _resultCycle = new int[13, 4];
            private readonly OpType[,] _resultOpType = new OpType[13, 4];

            public void Clear()
            {
                for (int i = 0; i < 13; i++)
                    for (int j = 0; j < 4; j++) { _resultCycle[i, j] = -6; _resultOpType[i, j] = OpType.Other; }
            }

            public void Start(OpType opType, byte r, byte useMask, int cycle)
            {
                for (int c = 0; c < 4; c++)
                    if (((1 << c) & useMask) != 0) { _resultCycle[r, c] = cycle + 6; _resultOpType[r, c] = opType; }
            }

            public bool Ready(byte r, int c, int cycle) => _resultCycle[r, c] <= cycle;

            public bool TakeBypass(VsInstruction pI, uint muxIndex, byte r, byte useMask, int cycle)
            {
                bool take = false;
                for (int c = 0; c < 4; c++)
                {
                    if (((1 << c) & useMask) == 0) continue;
                    if (cycle != _resultCycle[r, c] - 3) return false;      // not the bypass cycle
                    if (_resultOpType[r, c] == OpType.MLU) { take = true; continue; }
                    if (muxIndex != 2) return false;                        // other bypasses are C-of-ADD/MAD only
                    if (pI.Mac != MAC_ADD_ && pI.Mac != MAC_MAD_) return false;
                    if (pI.Ilu != 0)
                    {
                        var test = pI.Clone();
                        StripMacInstruction(test);
                        var useMasks = new byte[3];
                        ComputePostSwizzleUseMasks(test, useMasks);
                        if ((useMasks[2] & (1 << c)) != 0) return false;    // ilu still needs this component
                    }
                    if (_resultOpType[r, c] == OpType.ALU && muxIndex == 2) { take = true; continue; }
                    if (_resultOpType[r, c] == OpType.ILU && muxIndex == 2) { take = true; continue; }
                    return false;
                }
                return take;
            }
        }

        // --- multithreaded scoreboard (api.cpp:2463) ---------------------------

        private sealed class RegScoreboard
        {
            private readonly float[,] _cyc = new float[14, 4];
            private readonly float[,] _cycAlu = new float[14, 4];
            private readonly float[,] _next = new float[14, 4];
            private readonly float[,] _nextAlu = new float[14, 4];
            private readonly StallType[,] _reason = new StallType[14, 4];
            private readonly StallType[,] _reasonAlu = new StallType[14, 4];
            private readonly StallType[,] _nextReason = new StallType[14, 4];
            private readonly StallType[,] _nextReasonAlu = new StallType[14, 4];
            private int _numCycles;

            public void Clear(int numCycles)
            {
                for (int i = 0; i < 13; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        _cyc[i, j] = 2.5f; _cycAlu[i, j] = 2.5f;      // first-read (uninitialized) stall
                        _next[i, j] = 1.5f; _nextAlu[i, j] = 1.5f;    // write-then-read-next stall
                        _reason[i, j] = StallType.RegInit; _reasonAlu[i, j] = StallType.RegInit;
                        _nextReason[i, j] = StallType.RegInit; _nextReasonAlu[i, j] = StallType.RegInit;
                    }
                _cyc[13, 0] = 0.0f;
                _reason[13, 0] = StallType.None;
                _numCycles = numCycles;
            }

            public void StartNewInstruction()
            {
                _cyc[13, 0] = 0.0f;
                _reason[13, 0] = StallType.None;
                for (int i = 0; i < 14; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        _cyc[i, j] = _next[i, j];
                        _reason[i, j] = _nextReason[i, j];
                        _cycAlu[i, j] = _nextAlu[i, j];
                        _reasonAlu[i, j] = _nextReasonAlu[i, j];
                        _next[i, j] = 0.0f; _nextAlu[i, j] = 0.0f;
                        _nextReason[i, j] = StallType.None; _nextReasonAlu[i, j] = StallType.None;
                    }
            }

            public void Start(OpType opType, byte r, byte useMask, float cycle, StallType reason)
            {
                if (r == 13)   // a0.x: smaller stall/shadow, handled apart
                {
                    float a0xStall = 0.17f;
                    StallType a0xReason = StallType.Arl;
                    if (reason is StallType.Standard or StallType.StandardAndShadow or StallType.StandardAndBypShad)
                    {
                        if (_numCycles > 10 && cycle > 2) a0xStall = 0.33f;
                        a0xReason = StallType.ArlShadow;   // (retail shadows this into a local; kept for fidelity)
                    }
                    _cyc[r, 0] = a0xStall; _reason[r, 0] = a0xReason;
                    _cycAlu[r, 0] = a0xStall; _reasonAlu[r, 0] = a0xReason;
                    _next[r, 0] = 0.0f; _nextReason[r, 0] = StallType.None;
                    _nextAlu[r, 0] = 0.0f; _nextReasonAlu[r, 0] = StallType.None;
                    return;
                }

                for (int c = 0; c < 4; c++)
                {
                    if (((1 << c) & useMask) == 0) continue;
                    switch (reason)
                    {
                        case StallType.RegInit:
                            _next[r, c] = 0.0f; _nextReason[r, c] = StallType.None;
                            _nextAlu[r, c] = 0.0f; _nextReasonAlu[r, c] = StallType.None;
                            _cyc[r, c] = 0.0f; _reason[r, c] = StallType.Bypass;
                            _cycAlu[r, c] = 0.0f; _reasonAlu[r, c] = StallType.Bypass;
                            break;

                        case StallType.None:
                        case StallType.Shadow:
                        case StallType.Arl:
                        case StallType.ArlShadow:
                            _next[r, c] = 0.0f; _nextReason[r, c] = StallType.None;
                            _nextAlu[r, c] = 0.0f; _nextReasonAlu[r, c] = StallType.None;
                            if (opType == OpType.MLU)
                            {
                                if (_reason[r, c] != StallType.RegInit && reason != StallType.RegInit)
                                {
                                    _cyc[r, c] = 0.0f; _cycAlu[r, c] = 0.0f;
                                    _reason[r, c] = StallType.Bypass; _reasonAlu[r, c] = StallType.Bypass;
                                }
                            }
                            else
                            {
                                if (_reason[r, c] != StallType.RegInit && reason != StallType.RegInit)
                                {
                                    _cyc[r, c] = 0.5f; _reason[r, c] = StallType.Standard;
                                    _cycAlu[r, c] = 0.0f; _reasonAlu[r, c] = StallType.Bypass;
                                }
                            }
                            break;

                        case StallType.Standard:
                        case StallType.StandardAndShadow:
                        case StallType.StandardAndBypShad:
                            {
                                float twoShadow = 0.4f;
                                if (_numCycles < 10 || cycle < 4) twoShadow = 0.2f;
                                _next[r, c] = twoShadow; _nextReason[r, c] = StallType.Shadow;
                                _nextAlu[r, c] = twoShadow; _nextReasonAlu[r, c] = StallType.Shadow;
                            }
                            if (opType == OpType.MLU)
                            {
                                if (_reason[r, c] != StallType.RegInit)
                                {
                                    if (Epsilon(cycle, 2, 0.3f) || Epsilon(cycle, 3, 0.3f) || Epsilon(cycle, 4, 0.3f) || (_numCycles >= 5 && _numCycles <= 9))
                                    {
                                        _cyc[r, c] = 0.2f; _reason[r, c] = StallType.BypassAndShadow;
                                        _cycAlu[r, c] = 0.2f; _reasonAlu[r, c] = StallType.BypassAndShadow;
                                    }
                                    else
                                    {
                                        _cyc[r, c] = 0.5f; _reason[r, c] = StallType.BypassAndShadow;
                                        _cycAlu[r, c] = 0.5f; _reasonAlu[r, c] = StallType.BypassAndShadow;
                                    }
                                }
                            }
                            else
                            {
                                if (_reason[r, c] != StallType.RegInit)
                                {
                                    if (Epsilon(cycle, 2, 0.3f) || Epsilon(cycle, 3, 0.3f) || Epsilon(cycle, 4, 0.3f) || (_numCycles >= 5 && _numCycles <= 9))
                                    {
                                        _cycAlu[r, c] = 0.2f; _reasonAlu[r, c] = StallType.BypassAndShadow;
                                    }
                                    else
                                    {
                                        _cycAlu[r, c] = 0.5f; _reasonAlu[r, c] = StallType.BypassAndShadow;
                                    }
                                    if (_numCycles < 10) { _cyc[r, c] = 0.5f; _reason[r, c] = StallType.Standard; }
                                    else if (Epsilon(cycle, 2, 0.3f) || Epsilon(cycle, 3, 0.3f)) { _cyc[r, c] = 1.3f; _reason[r, c] = StallType.StandardAndShadow; }
                                    else { _cyc[r, c] = 1.05f; _reason[r, c] = StallType.StandardAndShadow; }
                                }
                            }
                            break;

                        case StallType.Bypass:
                        case StallType.BypassAndShadow:
                            if (opType == OpType.MLU)
                            {
                                _next[r, c] = 0.0f; _nextReason[r, c] = StallType.None;
                                _nextAlu[r, c] = 0.0f; _nextReasonAlu[r, c] = StallType.None;
                                if (_reason[r, c] != StallType.RegInit)
                                {
                                    _cyc[r, c] = 0.0f; _reason[r, c] = StallType.Bypass;
                                    _cycAlu[r, c] = 0.0f; _reasonAlu[r, c] = StallType.Bypass;
                                }
                            }
                            else
                            {
                                _next[r, c] = 0.0f; _nextReason[r, c] = StallType.None;
                                _nextAlu[r, c] = 0.0f; _nextReasonAlu[r, c] = StallType.None;
                                if (_reason[r, c] != StallType.RegInit)
                                {
                                    if (Epsilon(cycle, 1, 0.3f)) { _cyc[r, c] = 0.6f; _reason[r, c] = StallType.StandardAndBypShad; }
                                    else if (Epsilon(cycle, 3, 0.3f) || Epsilon(cycle, 3.5f, 0.3f)) { _cyc[r, c] = 0.72f; _reason[r, c] = StallType.StandardAndBypShad; }
                                    else { _cyc[r, c] = 0.83f; _reason[r, c] = StallType.StandardAndBypShad; }
                                    _cycAlu[r, c] = 0.0f; _reasonAlu[r, c] = StallType.Bypass;
                                }
                            }
                            break;
                    }
                }
            }

            public float Ready(byte r, int c, bool isAluCInput, out StallType reason)
            {
                if (isAluCInput) { reason = _reasonAlu[r, c]; return _cycAlu[r, c]; }
                reason = _reason[r, c]; return _cyc[r, c];
            }
        }
    }
}
