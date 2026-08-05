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

**Back ends: not yet ported.** The D3D8 token stream still has to be lowered to
hardware form — a `D3DPIXELSHADERDEF` for pixel shaders (`pixelshader.cpp`'s
`CompilePixelShaderToUCode` plus its per-instruction combiner handlers), NV2A
microcode plus the MAC/ILU pairing optimiser for vertex shaders (`api.cpp`).
`PixelShaderDef.cs` has the 60-DWORD container and the `PSB0` file tag; nothing
fills it in yet. Until that lands, this tool only exposes `--tokens`.

**Preprocessor: not yet ported.** 29 of the 30 parse failures are `#include` or
`#ifdef` — a separate pass in the original too (`xsasm /P` skips it,
`/p` runs only it). `preprocessor.cpp` is the source.

## The verification gate

The corpus carries the original assembler's own output next to its input: **15
matched `.psh`/`.xpu` pairs and 52 matched `.vsh`/`.xvu` pairs**. Those are the
acceptance test — a back end is done when it reproduces them byte for byte, not
when it produces something plausible.

## A 5849-vs-leak gap found while testing

`HighQualityBumpMapping/HQBumpShader.psh` uses a bare `t1_hemi` source modifier.
The leak's `DecodeRegister` compares the whole suffix and knows only `_hemi1`,
`_hemi2`, `_hemi3` and `_hl` — so the January-2002 assembler would reject this
file. It nonetheless ships a golden `.xpu`, so 5849's `xsasm` accepts it.

Deliberately left failing rather than guessed at: once the pixel back end exists,
that golden `.xpu` states which modifier bits `_hemi` encodes to. Guessing now
would risk a plausible-but-wrong shader, which is worse than a clean error.
