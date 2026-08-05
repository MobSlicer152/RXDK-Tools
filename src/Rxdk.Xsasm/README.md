# xsasm — the Xbox shader assembler, managed port

The XDK ships `xsasm.exe`, a Win32 PE wrapping `xgraphics.lib`'s `shadeasm`. That
makes authoring shaders a Windows-only step. This is a port to managed code so the
SDK works on Linux and macOS too.

Source of truth is the leak's own assembler (`private/windows/directx/dxg/`,
`xgraphics/shadeasm` + `tools/xsasm`), which RXDK-Libs already compiles for the
Xbox target as part of libxgraphics. This port follows that code rather than
reverse-engineering the output format.

## Status

**Front end: complete.**

- `Lexer.cs` — tokenizer, matching `CD3DXAssembler::Token()`. Handles `;` and `//`
  comments, `#line` tracking, `#pragma screenspace`, and the two context-sensitive
  rules: `c-3` lexes as one identifier (negative constant register), and a `.` only
  begins a float *after* the version token, or `ps.1.1` would lex as one number.
- `Opcodes.cs` — opcode spellings and arity, per shader kind, plus the `_x2`/`_bx2`/
  `_bias`/`_d2` destination modifiers.
- `Registers.cs` — register names, write masks, swizzles, source modifiers.
- `Parser.cs` — recursive descent over `shadeasm.y`, carrying the semantic actions
  from `CD3DXAssembler::Production()`. Emits the D3D8 token stream.

Verified by parsing every shader in the XDK sample corpus: **85 of 115 clean**, and
all 30 remaining failures are accounted for (below) rather than unexplained.

**Combiner layer: complete** (`PixelCombiners.cs`). The NV2A `PS_*` encodings and
the four tables that map D3D8 onto them — source modifier to input mapping, its
inversion, register file/number to combiner register, and texture opcode to
addressing mode — all transcribed from `d3d8types.h` and `pixelshader.cpp`.

Worth knowing when reading it: there is no literal "1" register. Constants are
spelled as the ZERO register put through an input mapping, so `1` is `invert(0)`
and `-1` is `expand(0)`. And `PS_CHANNEL_RGB` and `PS_CHANNEL_BLUE` are both 0 —
which one a value means depends on whether it feeds the RGB or the alpha combiner.

**Pixel back end: working, 14 of the 15 goldens byte-exact.** `xsasm shader.psh`
writes a `.xpu`. `PixelInstructions.cs` holds the per-instruction combiner
lowering, `PixelShaderCompiler.cs` the driver.

**Vertex back end: encoding done, translation not.** `VertexMicrocode.cs` has the
128-bit instruction layout and the `.xvu` container, verified by round-tripping
all 53 goldens byte-exact (`xsasm --verify-xvu file.xvu`). What remains is the
translation from the D3D8 token stream to microcode, plus `api.cpp`'s MAC/ILU
pairing optimiser. `.vsh` input is rejected rather than half-assembled.

The `.xvu` container: two characters (`'x'` plus `' '` ordinary / `'w'`
read/write / `'s'` state), a WORD instruction count, then 16 bytes per
instruction. The leading DWORD reads as `0x2078` only because that is `'x'`
followed by a space.

An instruction drives two units at once — the MAC (mul/add/mad/dp3/dp4/min/max/
slt/sge/arl) and the ILU (rcp/rsq/exp/log/lit) — over three shared operand slots
A, B, C. That is what "pairing" means: two source operations fit in one
instruction when one is a MAC op and the other an ILU op. Reproducing the
goldens therefore needs the optimiser, not just the translation, since it decides
which operations get paired.

**Preprocessor: complete** (`Preprocessor.cs`). `#include` with `-I` search paths,
`#define`/`#undef` with object-like substitution, `#ifdef`/`#ifndef`/`#else`/
`#endif`, `#pragma` passed through, and `#line` emission so errors point at the
original source. `-P` skips it, matching the original's `/P`.

It also implements the NVASM-style `macro name params` / `endm` facility, which
is a real feature of `preprocessor.cpp` and not a `#define` — it spans lines and
takes arguments, referenced in the body as `%param`.

Corpus: **112 of 112 shaders parse.** The three files that do not (`wind.vsh`,
`hairlighting.vsh`, `eyelighthalf.vsh`) carry no version line because they are
`#include` fragments, not standalone shaders — failing on them is correct.

## The verification gate

The corpus carries the original assembler's own output next to its input: **15
matched `.psh`/`.xpu` pairs and 52 matched `.vsh`/`.xvu` pairs**. Those are the
acceptance test — a back end is done when it reproduces them byte for byte, not
when it produces something plausible.

## Three 5849-vs-leak differences the goldens forced out

The leak is January 2002; the XDK is 5849. Each of these was found by a golden
refusing to match, and resolved from the goldens rather than by guessing.

**`_hemi` exists.** `HQBumpShader.psh` uses a bare `t1_hemi`, which the leak's
`DecodeRegister` rejects — it knows only `_hemi1`.`_hemi3` and `_hl`. Its
encoding was read back out of the golden: `texm3x2tex t3, t1_hemi` lands as
`dotMap[3] = 0x7` (`HILO_HEMISPHERE`), the `D3DSPSM_SAT` row, making it an alias
of `_hemi3`. The same file's `t0_bx2` lands as `0x1`, confirming the table lines
up independently.

**`lrp` no longer forces its interpolant unsigned.** The leak unconditionally
rewrites a non-unsigned first input to `UNSIGNED_IDENTITY`. Fire's golden keeps
`SIGNED_IDENTITY`. Dropping the force cannot affect the D slot either way, since
the inversion table sends both signed and unsigned identity to `UNSIGNED_INVERT`.

**`ps.1.0` lowers differently from `ps.1.1`** — and the boundary is the version,
not "stock DX8 vs Xbox": `ps.1.1` (Glass, sky) lowers exactly like `xps`. In
`ps.1.0` an unmodified source decodes to `UNSIGNED_IDENTITY` rather than
`SIGNED_IDENTITY` (a DX8 pixel shader clamps inputs to [0,1]), and the
texture-mode adjust global flag stays clear. Both `ps.1.0` goldens agree on both
points.

## The one golden that does not match

`PixelShader/pshader.psh` (`ps.1.0`) differs in `C0Mapping`, `C1Mapping` and
`FinalCombinerConstants`: its golden writes zeros where the assembler writes the
0xF "unused" sentinel. The other `ps.1.0` golden, `dolphin`, keeps the sentinel —
so the two disagree with each other, and neither the leak source nor any rule
derivable from two conflicting samples explains it. The port follows the source,
which is unconditional here, and `dolphin` matches.

Left as a known deviation rather than special-cased to make a number go up: a
rule invented to fit one sample and contradicted by another is not a rule. Note
the affected fields are constant *mappings*, used by `SetPixelShaderConstant` —
neither shader references a constant at all, so nothing reads them here.
