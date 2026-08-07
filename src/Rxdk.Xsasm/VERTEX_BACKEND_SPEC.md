# NV2A vertex-shader back end — byte-exact port reference

## ⚠️ THE CHECKED-IN `.xvu` GOLDENS ARE AN UNRELIABLE ORACLE — use `xsasm.exe /O1`

The whole optimizer is ported. The catch we discovered: **many of the shipped
`.xvu` files do not match what the 5849 `xsasm.exe` produces** — they are stale
(built by an earlier assembler, or with different flags). Proof: for `matinv`,
our optimizer output is **byte-exact** to `xsasm.exe` (5849) yet differs from the
checked-in `matinv.xvu`; and `5849-xsasm != checked-in golden` for `billbrd` too.
So the "sim-precision regressions" earlier were not bugs — the sim is right, the
checked-in golden was wrong.

**The authoritative oracle is the retail assembler itself:**
`D:\Git\RXDK\POC\XDKSetup5849.17\XDK\xbox\bin\xsasm.exe`. Our port implements the
**`/O1`** ("old", peephole/reorder/rename) optimizer — `xsasm.exe` default is
`/O1 /O2` keep-best, so compare against `xsasm /O1`. Its `/l` listing prints
retail's per-instruction stalls (the `TLEngineSim` reference trace); e.g. matinv
instr 4 `mul r0` stalls **5.00**, matching ours exactly.

**Corpus result vs `xsasm /O1`: MATCH 47 / DIFFER 0 / ERR 30 of 77 `.vsh`.** The
optimizer is now **byte-exact to the retail assembler for every shader it can
assemble** — the full pipeline (PeepholePairOutputMasks, DeadCodeStripper, Renamer,
Reorderer/TLEngineSim, PeepholeOptimize, PeepholePair1/2) reproduces `/O1` exactly.
The 30 err are the remaining translator gaps (`frc`/`exp`/`log` macros, co-issue
source) — the shaders that don't assemble yet, unrelated to the optimizer.

Getting from 36 to 47 took two fixes: (1) `EnableRenamer` had silently reverted to
`const false`, so the renamer never ran (`billbrd`/`brdf`/... need it); (2) a
`VRegInfo.Sw` init bug — the swizzle map for register components OUTSIDE a vreg's
write mask must be identity (a rename passes unwritten components straight
through), not zero, else an unused select like the W of a source feeding an
`.xyz`-only op mis-canonicalized to `.xyzx` (that one fixed the last 8). Both the
Reorderer and Renamer are now ON by default (`XSASM_NO_OPT` to disable).

---

Status (against the STALE checked-in goldens, which under-count): `--verify-corpus`
reports **xvu golden 11/52** with the scheduler gated off; enabling reorder+rename
moves it to ~19 but "regresses" the two stale goldens above. Ignore that metric in
favor of the `xsasm /O1` comparison.

`VertexOptimizer.cs` ports the `XGOptimizeVertexShader` fixed-point driver and,
so far, these passes byte-exact:
- `PeepholePairOutputMasks` (`PairableMasks1/2`) — 6075
- `DeadCodeStripper` — the backward-liveness class, 2766
- `PeepholePair1`/`PeepholePair2` — the co-issue pairers, 6479/6507, on top of the
  full pairing layer (`ForcedPair`/`ForcedPair2`, `Pairable`, `SequentialPairable`,
  `PairableMulAdd`, `PairableMasks3`, `MergeRegisterOutputMasks`, `SwapAC`,
  `ConvertToImv`, the swizzle-merge canonicalization, `InputOutputDependency`).

`Renamer` (4383) is **ported but gated off** (`EnableRenamer = false`). It is
coupled to the `Reorderer` — the driver runs them in sequence and the goldens
capture their combined effect — so enabling it alone regresses at least one
shader: on `billbrd` the golden keeps the authored registers, i.e. retail's
`RemapVRegs` *fired but failed* (leaving the code unchanged), whereas our port's
`RemapVRegs` succeeds and reassigns. Enable it together with the Reorderer and
resolve that remap-failure divergence against the goldens then.

`TLEngineSim` (1948) — the cycle-accurate NV2A vertex-pipeline stall model, with
its single-threaded (`STRegScoreboard`) and multithreaded (`RegScoreboard`,
float cycles + shadow/bypass) scoreboards — is ported in `VertexStallSim.cs` and
`PeepholeOptimize` (5641) is wired on top of it (ADD-arg-swap when it lowers the
modelled stall). Enabling it held 11/52 (retail's PeepholeOptimize is a no-op on
those shaders, and so is ours), which sanity-checks the sim's `CalculateStall`
path. The sim's exact stall VALUES are only fully exercised once the Reorderer
uses them.

`Reorderer` (3477) — the instruction scheduler (`RegSet` dirty-tracking +
`FindInstruction`/`MoveInstruction`/`PairInstructions`, driven by
`TLEngineSim.IsStall`) — is ported in `VertexReorderer.cs` but **gated**
(`XSASM_ENABLE_REORDERER`, default off). Enabling it moves **11 → 19/52**: it
preserves 9 of the original 11 and adds 10, but **regresses 2** — `billbrd`
(multithreaded) and `matinv` (single-threaded state shader). Both are the same
symptom: our sim flags a stall + a bypass-movable candidate and reorders
independent instructions where retail leaves program order (the `matinv` diff
shows it moving instr 8's `mad r0` down to slot 10). So the scheduler logic is
right; the divergence is **cycle-precision in `TLEngineSim`'s stall/bypass
detection** on those two shaders.

Investigated (`XSASM_DBG_REORDER` traces reorder decisions; `--disasm` shows the
result): on matinv (single-threaded state shader) our sim stalls `mul r0` (instr
4) **5 cycles**, and that cascades -- at instr 8 `mad r0` stalls 2 and `mad r1`
(instr 9) lands on its MLU-bypass cycle, so our reorderer pulls it forward; retail
leaves program order. matinv *was* globally optimized in retail (our
full-minus-reorderer matches its golden, so dead-code/peephole ran) and retail's
reorderer was a no-op on it, so ours is simply over-eager. Every faithful-reading
path leads back to "retail should reorder too," which means the true single-
threaded stall values differ from ours in a way that isn't visible from the port
alone. **Blocked on a reference trace** of retail's per-instruction stalls (build
xgraphics.lib's debug `Print`, or the Xbox vertex-shader-processor stall
whitepaper the code cites). Fix that + the Renamer/billbrd remap, then flip both
gates on for a clean 19+/52. The pixel back
end, `VsInstruction` encoding, and `.xvu` container remain green (parse 112/112,
xpu 14/15, xvu 53/53 round-trip).

The authoritative C++ spec is `RXDK-Libs/libs/libxgraphics/shadeasm/api.cpp`
(the assembler RXDK compiles for the Xbox target — the C++ IS the specification;
do not reverse-engineer the formats). Token constants:
`shared/include/d3d8types.h`. Flags/opcodes: `shared/include/xgraphics.h`,
`microcodeformat.h`.

## Scope finding (decisive for sequencing)

The shipped goldens are a MIX. Counting instructions (`.xvu` size = 4 + 16·N):
- `imagemap.vsh` 5 → `.xvu` 7  (= 5 + 2)  — **translation only, no optimization**
- `alphafog.vsh` 16 → 18       (= 16 + 2) — **translation only**
- `bumpmap.vsh` 26 → 24        (= 26 + 2 − 4) — pairing/dead-code ran

So the `+2` goldens (the screen-space postfix and nothing else) are matchable
with **`D3DTokensToUCode` + the postfix alone**, before any optimizer. That is
the first milestone: implement the translation, wire the verifier to assemble
`.vsh` and compare golden `.xvu` (exactly as the pixel path does `.psh`→`.xpu`),
and turn those goldens green. The optimizer (which the `−N` goldens need) is a
separate, later phase.

## Phase 1 — `D3DTokensToUCode` (api.cpp:7755-8278)

Token stream in (`result.Code` from the parser; version DWORD then instruction
tokens, `D3DSIO_END` terminated), `List<VsInstruction>` out. Per-instruction
defaults before dispatch: `mac=NOP, ilu=NOP`, all swizzles identity
(`axs=CSW_X…aws=CSW_W`, same b,c), all mux `MX_V(2)`, `rw=7, rwm=0, oc=0x1ff,
om=OM_MAC, eos=0, cin=0, swm=0, owm=0`, all `ne/rr/ca/va=0`.

Dispatch sets `inst.mac`/`inst.ilu` plus two locals:
- `outputs`: 0 none, 1 MAC out, 2 ILU out, **3 ARL** (mov→a0; token has a dst but
  hardware writes none — parse then discard the dst token, set outputs=0).
- `inputs`: **a SLOT BITMASK** (bit0=A, bit1=B, bit2=C), never a count. MOV=1,
  ADD/SUB=5(A|C), MUL/DP*/MIN/MAX/SGE/SLT/DST/DPH=3(A|B), MAD=7, ILU ops=4(C).

Op table and every trap: see the checklist below. Matrix macros
(`M4x4/M4x3/M3x4/M3x3/M3x2`) expand to runs of `dp4`/`dp3` via a fall-through
cascade (order is load-bearing) that builds a synthetic token array and calls
`D3DTokensToUCode` **recursively**, then splices. `frc`/`exp`/`log` similarly
expand and need a free temp (frc keys on the Y component, exp/log on Z).

### Traps (from the api.cpp read; all must be reproduced for byte-exact output)
1. `inputs` is a slot bitmask (1/2/4 = A/B/C), not a count.
2. Write masks stored **bit-reversed** (x=8,y=4,z=2,w=1):
   `mask2 = ((m&1)<<3)|((m&2)<<1)|((m&4)>>1)|((m&8)>>3)`. Dependency-code read
   masks use `1<<(3-select)`.
3. `sub` = `MAC_ADD` with `cne` preset, combined with source negate via `^=` on
   the C slot (so a source-negated `sub` XORs correctly).
4. `mov`→a0 becomes `MAC_ARL`, decided by the DESTINATION register (regtype
   `D3DSPR_ADDR`, regnum 0); its dst token is parsed then discarded.
5. Matrix macros: fall-through case order; op/iRepeat table (M4x4→DP4×4,
   M3x4→DP3×4, M4x3→DP4×3, M3x3→DP3×3, M3x2→DP3×2); rows whose dst component is
   masked out are skipped; input2 const reg = `pTokens[3]+j`; recursion+splice.
6. FRC free-temp search keys on Y, EXP/LOG on Z; only `.xy`/`.y` masks valid for
   FRC; broadcast-swizzle construction `(sw)|(sw<<2)|(sw<<4)|(sw<<6)`.
7. Scalar ILU (RCP/RCC/RSQ/EXP/LOG — not LIT/MOV) forces all four C swizzles to
   the W-select.
8. Absent operand slots default to `MX_V`(v0), identity swizzle.
9. `oc`: bit 8 (0x100) distinguishes output regs from constant regs; 0x100=oPos.
10. Constant renumber `MapDX8ToUcode`: DX8 index (−96..95) → 0..191, handling the
    12-bit two's-complement negative form (`if(reg<=95) reg+=96; else reg = 96 -
    (0xfff & (~reg+1))`).
11. `GetEnumeratedReg`/`BASE_REG` map D3D regtype → internal `Register_t`
    (REG_V0=0, REG_O0=16, REG_C0=32, REG_R0=224, REG_ARL=240).

Register/opcode/bitfield tables (`Register_t`, `MAC_*`/`ILU_*`, the
`kMacUsesA/B/C` and component-use tables) are transcribed in the port notes;
`microcodeformat.h` is the source of truth. The `VsInstruction` packing
(`WordY/Z/W`, crr straddling the W/Z boundary) already exists in
`VertexMicrocode.cs` and is golden-verified — the translation only has to fill
the fields.

## Phase 1 driver — `InstructionsToMicrocode` postfix (api.cpp:6838-7008)

After translation, unless the shader is screenspace or a vertex-state shader,
append the fixed **2-instruction screen-space postfix**:
```
{0x00000000,0x0647401b,0xc4361bff,0x1078e800}, {0x00000000,0x0087601b,0xc400286c,0x3070e800}
```
(decode each via the existing `VsInstruction.Unpack`; force `eos=0` first). Then
`length>136` → error; set `eos=1` on the last instruction.

## Phase 2 — the optimizer (api.cpp:6609-6763, the `−N` goldens)

`XGOptimizeVertexShader` is a fixed-point loop (`while length keeps shrinking`)
running these passes IN ORDER: PeepholePairOutputMasks (`PairableMasks`),
DeadCodeStripper, Renamer, Reorderer, PeepholeOptimize (ADD-arg-swap stall opt,
NOT the pairer), PeepholePair1 (`Pairable`), PeepholePair2 (`SequentialPairable`).
The MAC/ILU slot-sharing happens in the `Pairable*`/`ForcedPair2` merges, guided
by `InputOutputDependency`, `MergeSwizzles`→`SetSwizzles` (whose
`PickDefault`/`ChooseBestSwizzle`/`CanUseXYZW` canonicalization determines the
exact emitted swizzle bits), and `MergeRegisterOutputMasks`. `PairableMulAdd`
fuses MUL+ADD→MAD. `InputsConflict_MAC_ILU`/`OutputsConflict_MAC_ILU` are DEAD
CODE (never called) — skip them. The `SASM_USE_V1/V2_OPTIMIZER` and
`PACKMATRIX_*` flags are inert; goldens come from the single
`optimize`/`globalOptimize` pipeline. Matching Phase 2 byte-exact requires
reproducing Microsoft's dead-code stripper, register renamer, and instruction
scheduler exactly — the demanding part.

## Verification

Extend `CorpusVerifier`: for each `.vsh` with a golden `.xvu`, assemble and
compare bytes (mirror the `.psh`→`.xpu` block). Milestone 1 = every `+2` golden
byte-exact with translation only. Milestone 2 = the `−N` goldens once the
optimizer lands. The 3 include-fragment `.vsh` (`wind`, `hairlighting`,
`eyelighthalf`) have no version line and are correctly skipped.
