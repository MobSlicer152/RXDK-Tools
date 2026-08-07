# NV2A vertex-shader back end — byte-exact port reference

Status: **Phase 1 (translation) done and golden-verified. Phase 2 (optimizer):
framework + first pass landed; the heavy passes remain.** `VertexShaderCompiler.cs`
translates `.vsh` tokens to `VsInstruction`s + appends the screen-space postfix,
then runs `VertexOptimizer.Optimize`. `--verify-corpus` reports **xvu golden 11/52
byte-exact** — the 7 unoptimized goldens (source-length + 2), plus 3 that need
output-mask pairing + dead-code stripping, plus 1 that needs MAC+ILU co-issue.

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

**Still stubbed (no-op):** `Reorderer` (3477) and `PeepholeOptimize` (5641) —
both need `TLEngineSim` (1948), the NV2A vertex-pipeline stall model, to decide
scheduling. That sim is the gating prerequisite for the timing-driven half of the
pipeline. Most of the remaining ~41 `-N` goldens need rename + reorder together
(the ~2000-line demanding stretch). The pixel back
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
