namespace Rxdk.Xsasm;

/// <summary>
/// Reorderer (api.cpp:3477) -- the instruction scheduler. Walks the program
/// through the stall sim; when an instruction stalls, it pulls a later,
/// independent, non-stalling instruction forward into the stall slot, and it
/// pulls independent instructions forward to co-issue-pair with the current one
/// (a pair also saves a slot). Dependencies are tracked with RegSet, a
/// per-register-component written/read set. Does NOT rename registers.
/// </summary>
internal static partial class VertexOptimizer
{
    private sealed class Reorderer
    {
        private List<VsInstruction> _ucode = null!;
        private readonly VertexStallSim _sim = new();
        private readonly bool _isVertexShader;

        public Reorderer(bool isVertexShader) => _isVertexShader = isVertexShader;

        public void Run(List<VsInstruction> ucode)
        {
            _ucode = ucode;
            _sim.Initialize(_isVertexShader, ucode.Count);
            bool tryPairingAgain = false;

            for (int pc = 0; pc < _ucode.Count - 1; pc++)
            {
                VsInstruction pI = _ucode[pc];
                if (_sim.IsStall(pI) && FindInstruction(pc, out int pc2a))
                    MoveInstruction(pc, pc2a);

                if (FindPairableInstruction(pc, out int pc3, out VsInstruction pair))
                {
                    bool moved = false;
                    if (_sim.IsStall(pair) && FindInstruction(pc, out int pc2b))
                    {
                        MoveInstruction(pc, pc2b);
                        tryPairingAgain = true;
                        moved = true;
                    }
                    if (!moved)
                        PairInstructions(pc, pc3, pair);
                }

                _sim.Do(_ucode[pc], out _, out _);
            }

            if (tryPairingAgain)
            {
                _sim.Initialize(_isVertexShader, _ucode.Count);
                for (int pc = 0; pc < _ucode.Count - 1; pc++)
                    if (FindPairableInstruction(pc, out int pc2, out VsInstruction pair))
                        PairInstructions(pc, pc2, pair);
            }
        }

        // --- list edits (api.cpp:3853, 3892) -----------------------------------

        private void MoveInstruction(int pc, int pc2)
        {
            VsInstruction temp = _ucode[pc2];
            if (pc2 < pc)
                for (int i = pc2; i < pc; i++) _ucode[i] = _ucode[i + 1];   // toward the end
            else
                for (int i = pc2; i > pc; i--) _ucode[i] = _ucode[i - 1];   // toward the beginning
            _ucode[pc] = temp;
        }

        private void PairInstructions(int pc, int pc3, VsInstruction pair)
        {
            _ucode[pc] = pair;
            _ucode.RemoveAt(pc3);   // drop the now-folded instruction (pc3 > pc)
        }

        // --- candidate search (api.cpp:3924) -----------------------------------

        private bool FindInstruction(int pc, out int foundPc)
        {
            var r = new RegSet();
            r.DirtyOut(_ucode[pc]);
            for (int pc2 = pc + 1; pc2 < _ucode.Count; pc2++)
            {
                if (!r.IsReadDirty(_ucode[pc2])                 // (b) no read of the region's outputs
                    && !_sim.IsStall(_ucode[pc2])               // (a) does not itself stall
                    && OKToMoveConditionC(r, pc, pc2))          // (c) no write conflict
                {
                    foundPc = pc2;
                    return true;
                }
                r.DirtyOut(_ucode[pc2]);
            }
            foundPc = -1;
            return false;
        }

        private bool OKToMoveConditionC(RegSet r, int pc, int pc2)
        {
            var r2 = new RegSet();
            r2.DirtyOut(_ucode[pc2]);
            if (r.DirtyConflict(r2)) return false;              // pc2-write vs region-write
            for (int pc3 = pc; pc3 < pc2; pc3++)
                if (r2.IsReadDirty(_ucode[pc3])) return false;  // pc2-write vs region-read
            return true;
        }

        private bool FindPairableInstruction(int pc, out int foundPc, out VsInstruction pair)
        {
            var r = new RegSet();
            r.DirtyOut(_ucode[pc]);
            for (int pc2 = pc + 1; pc2 < _ucode.Count; pc2++)
            {
                if (!r.IsReadDirty(_ucode[pc2])
                    && Pairable(out pair, _ucode[pc], _ucode[pc2])
                    && OKToMoveConditionC(r, pc, pc2))
                {
                    foundPc = pc2;
                    return true;
                }
                r.DirtyOut(_ucode[pc2]);
            }
            foundPc = -1;
            pair = null!;
            return false;
        }

        // --- RegSet: which register components an instruction range touches -----

        private sealed class RegSet
        {
            private readonly byte[] _r = new byte[16];    // temp regs (r12 = oPos shadow)
            private readonly byte[] _c = new byte[192];   // constant regs
            private readonly byte[] _o = new byte[16];    // output regs
            private bool _a0x;
            private byte _anyCReg;

            public bool DirtyConflict(RegSet o)
            {
                for (int i = 0; i < 13; i++) if ((_r[i] & o._r[i]) != 0) return true;   // sizeof(r) == 13
                for (int i = 0; i < 192; i++) if ((_c[i] & o._c[i]) != 0) return true;
                for (int i = 0; i < 16; i++) if ((_o[i] & o._o[i]) != 0) return true;
                return _a0x && o._a0x;
            }

            public void DirtyOut(VsInstruction pI)
            {
                if (pI.Mac == VsInstruction.MacArl) _a0x = true;
                if (pI.Rwm != 0 && pI.Rw <= 11) _r[pI.Rw] |= (byte)pI.Rwm;
                if (pI.Swm != 0)
                {
                    uint iluRw = (pI.Mac != 0 && pI.Ilu != 0) ? 1u : pI.Rw;
                    if (iluRw < 16) _r[iluRw] |= (byte)pI.Swm;
                }
                if (pI.Owm != 0)
                {
                    bool ocOutput = (pI.Oc & 0x100) != 0;
                    uint ocIndex = pI.Oc & 0xff;
                    if (ocOutput)
                    {
                        if (ocIndex < 16)
                        {
                            _o[ocIndex] = (byte)pI.Owm;
                            if (ocIndex == 0) _r[12] |= (byte)pI.Owm;   // oPos shadowed as r12
                        }
                    }
                    else if (ocIndex < 192)
                    {
                        _c[ocIndex] |= (byte)pI.Owm;
                        _anyCReg |= (byte)pI.Owm;
                    }
                }
            }

            public bool IsReadDirty(VsInstruction pI)
            {
                bool macDirty = pI.Mac != 0 && DoMac(pI);
                bool iluDirty = pI.Ilu != 0 && DoILU(pI);
                bool a0Dirty = _a0x && pI.Cin != 0;
                return macDirty || iluDirty || a0Dirty;
            }

            private bool ADirty(VsInstruction pI, byte used) =>
                MuxReadDirty(pI, pI.Amx, pI.Arr, pI.Axs, pI.Ays, pI.Azs, pI.Aws, used);
            private bool BDirty(VsInstruction pI, byte used) =>
                MuxReadDirty(pI, pI.Bmx, pI.Brr, pI.Bxs, pI.Bys, pI.Bzs, pI.Bws, used);
            private bool CDirty(VsInstruction pI, byte used) =>
                MuxReadDirty(pI, pI.Cmx, pI.Crr, pI.Cxs, pI.Cys, pI.Czs, pI.Cws, used);

            private bool MuxReadDirty(VsInstruction pI, uint mx, uint rr, uint xs, uint ys, uint zs, uint ws, byte usedMask)
            {
                byte um = 0;
                if ((usedMask & 8) != 0) um = (byte)(1 << (int)(3 - xs));
                if ((usedMask & 4) != 0) um |= (byte)(1 << (int)(3 - ys));
                if ((usedMask & 2) != 0) um |= (byte)(1 << (int)(3 - zs));
                if ((usedMask & 1) != 0) um |= (byte)(1 << (int)(3 - ws));

                switch (mx)
                {
                    case MX_R: return (_r[rr] & um) != 0;
                    case MX_V: return false;
                    case MX_C:
                        bool dirty = (_c[pI.Ca] & um) != 0;
                        if (pI.Cin != 0 && (_anyCReg & um) != 0) dirty = true;
                        return dirty;
                    default: return false;
                }
            }

            private bool DoMac(VsInstruction pI)
            {
                byte used = (byte)(pI.Rwm | pI.Owm);
                switch (pI.Mac)
                {
                    case VsInstruction.MacArl: return ADirty(pI, 8);
                    case VsInstruction.MacMov: return ADirty(pI, used);
                    case VsInstruction.MacAdd: return ADirty(pI, used) || CDirty(pI, used);
                    case VsInstruction.MacMad: return ADirty(pI, used) || BDirty(pI, used) || CDirty(pI, used);
                    case VsInstruction.MacMul: case VsInstruction.MacMin: case VsInstruction.MacMax:
                    case VsInstruction.MacSlt: case VsInstruction.MacSge:
                        return ADirty(pI, used) || BDirty(pI, used);
                    case VsInstruction.MacDp3: return ADirty(pI, 0xe) || BDirty(pI, 0xe);
                    case VsInstruction.MacDph: return ADirty(pI, 0xe) || BDirty(pI, 0xf);
                    case VsInstruction.MacDp4: return ADirty(pI, 0xf) || BDirty(pI, 0xf);
                    case VsInstruction.MacDst: return ADirty(pI, 0x6) || BDirty(pI, 0x5);
                    default: return false;   // NOP
                }
            }

            private bool DoILU(VsInstruction pI)
            {
                byte used = (byte)(pI.Swm | pI.Owm);
                switch (pI.Ilu)
                {
                    case VsInstruction.IluMov: return CDirty(pI, used);
                    case VsInstruction.IluRcp: case VsInstruction.IluRcc: case VsInstruction.IluRsq:
                    case VsInstruction.IluExp: case VsInstruction.IluLog: return CDirty(pI, 0x8);
                    case VsInstruction.IluLit: return CDirty(pI, 0xd);
                    default: return false;   // NOP
                }
            }
        }
    }
}
