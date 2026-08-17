# skinbld / bundler byte-parity — state of play

Goal: `Rxdk.SkinBld` and `Rxdk.Bundler` produce `.uix` and `.xpr` files
**byte-for-byte identical** to the Xbox 5849 SDK's Win32 `skinbld.exe` /
`bundler.exe`, so the RXDK build needs no Windows-only binaries.

Both tools build and run cross-platform (.NET 8, no Win32 dependencies).

## Where things stand

| Artifact | Result |
| --- | --- |
| `sk_res.h` | identical to the committed copy (only the embedded input path differs) |
| Shared default skin | 5334 differing bytes of 2,066,512 (0.26%) |
| `UIXKeyboard.uix` | 10 differing bytes of 2,519,286 |
| Shipped `.rdf`/`.xpr` pairs | 370 of 439 byte-identical, 0 build failures |

Everything except DXT **colour** halves now matches in both skins: object and
property tables, all nine localised string tables, audio, the section directory,
every resource header, every DXT **alpha** half, and every uncompressed texture.

## Ground truth

Five samples share one skin built from `XDKSamples/Common/uix/default.inx`, and
they are byte-identical to each other (SHA-256 begins `E4177319`):

```
XDKSamples/Networking/{UIXAuth,UIXFriends,UIXPlayers,UIXPlugin,SimpleVoice}/Media/*.uix
XDKSamples/Networking/UIXKeyboard/Media/UIXKeyboard.uix   # from its own .inx
```

The samples also ship **439 `.rdf` files with a Microsoft-built `.xpr` beside
them** (usually in `Media\`). That is by far the broadest corpus for the
bundler, and `reftest/Sweep-Bundler.ps1` rebuilds every one and compares it.
A full sweep takes about 17 minutes and writes `reftest/bundler-sweep.txt`.

Leaked originals used to settle behaviour questions:

```
D:\Git\xbox_leak_may_2020\xbox_leak_may_2020\xbox trunk\xbox\private\
    atg\tools\Bundler\                         # bundler.cpp, basetexture.cpp, CD3DX*
    windows\directx\dxg\xgraphics\dxtc\        # s3_quant.cpp, s3_intrf.cpp (DXT)
```

## Reproducing

```powershell
$sb = 'D:\Git\RXDK-Tools\src\Rxdk.SkinBld\bin\Debug\net8.0\skinbld.exe'

cd D:\Git\RXDK-VS20XX\XDKSamples\Common\uix
& $sb default.inx out.uix                 # add /header for sk_res.h

cd D:\Git\RXDK-Tools\reftest
python uixdiff.py <reference.uix> <ours.uix>    # per-section, per-texture byte counts
python dxtsplit.py <reference.uix> <ours.uix>   # DXT alpha half vs colour half
python rawpix.py  <reference.uix> <ours.uix>    # differing pixels, uncompressed only
powershell -File Sweep-Bundler.ps1              # all 439 .rdf/.xpr pairs
```

`reftest/` also holds `triangle.py` and `triangle2.py`, which model D3DX's
triangle-filter resample at selectable precisions — that is how the
float-to-int question below was settled — plus `uixnames.py`, `uixstr.py`,
`uixsect.py`, `uixtex.py`, `blockdump.py`, `xprpix.py`, `bmpinfo.py`.

## Findings that were expensive to reach

**Float-to-int conversion keeps register width.** `bundler.cpp` pins the x87
precision control to 24 bits (`_controlfp(_PC_24, _MCW_PC)`, with a comment
saying it is deliberate for bit-for-bit output), so every *stored* float matches
a C# `float` and the resample accumulation must be `float` throughout. But the
scale-and-dither expression handed to `F2I` — `pColors[i].g * 255.0f + fDither`
— never reaches memory, so it is **not** rounded to `float` before the
truncating `fistp`. Rounding it, as a straight transcription does, turns
`151.4999986` into exactly `151.5` and then into `152`.

Every disputed pixel sat precisely on a `.5` tie, and the reference broke those
ties in both directions (up on 14, down on 5) — that inconsistency is the tell.
Sweeping 24 combinations of which values are narrowed against the reference
pixels, exactly one model gives **0 of 9216 differing channels** across three
independent source images: narrow everything except the conversion expression.
`CD3DXCodec.F2I` therefore takes a `double`, and the scale literals in its
arguments are double literals.

**Resource-ID hash:** `h = h * 0x112 + upper(c) ^ 0xA563` over `"Section$Name"`,
with `Screen` hardcoded to `0x40001001`.

**Brace tokens in strings** are either an image name to insert as an icon
(`{IMG_A}`) or a character code written as a C integer literal — hex behind
`0x`, octal behind a leading zero, decimal otherwise (`{0x22b2}`, `{0400}`).
Treating every token as an icon is what broke the keyboard skin's strings.

**Alpha merge:** the original converts to A8R8G8B8 and merges alpha from the
alpha image's **blue** channel at the *source* size, then resizes to a power of
two. Alpha and colour must be brought to the larger of the two source
dimensions before merging.

**A same-size triangle Blt is not identity** — it is a 3-tap `[0.125, 0.75,
0.125]` blur. The original avoids it because `BltSame`/`BltCopy` are tried
before the filtered paths. Worth remembering before "simplifying" a resize.

**Texture descriptor:** the DMA channel bit is forced to legacy channel A to
match 5849 output.

## Known deviation from the original

`S3Tc`'s `allSame` carries a hand-added `force4` flag to stop DXT2-5 emitting
3-colour ramps. The leaked `allSame` has no such parameter — it receives
`nColors = block->inLevel` and infers the constraint. This was a guess made
before the leaked source was consulted and should be re-derived against
`s3_quant.cpp` line 1329 onward; it may be causing some of the residual.

## Incident to avoid repeating

The bundler used to ignore `-o`/`-h`/`-e`: the positional input argument
overwrote the explicit output paths, so it wrote next to the `.rdf`. Because
`.xpr` is gitignored, a sweep silently overwrote three shipped bundles under
`Graphics\Water\media` (restored from that sample's build output directory; two
of the three had been byte-identical anyway). Fixed, and `m_strPath` now follows
the *input* path so source images still resolve when `-o` points elsewhere.
`Sweep-Bundler.ps1` sends every output to a scratch directory. **Never let the
bundler use its default output paths while sweeping the sample tree.**

## Remaining work

1. **Large bundler mismatches** — too big to be rounding tie-breaks, so likely a
   real bug and the best next lead. Worst offenders by share of the file:

   | Sample | Differing |
   | --- | --- |
   | `Graphics\PlayField\Resource.rdf` | 795219 of 6469632 (12.3%) |
   | `Graphics\PolynomialTextureMaps` | 196526 of 2101248 (9.4%) |
   | `Input\Lightgun` | 6958 of 186368 (3.7%) |
   | `Graphics\Fire` | 236340 of 8740864 (2.7%) |
   | `Networking\Marketplace` | 147480 of 6490112 (2.3%) |
   | `Graphics\XPRViewer\textures.rdf` | 67310 of 4792320 (1.4%) |
   | `Graphics\DynamicGamma` | 61451 of 7692288 (0.8%) |
   | `Certification\TechCert*\loadsaveresource` | 30767 of 5474304 (0.6%) |

   Several counts repeat exactly across samples that share a `resource.rdf`
   (`99` bytes for the Dolphin variants, PerPixelLighting and PersistDisplay;
   `48` for DebugKeyboard, DebugMouse, AsyncWrite and SectionLoad), so a single
   fix should clear whole groups at once. The three CJK fonts differ by only
   46–79 bytes each. Full list in `reftest/bundler-sweep.txt`.
2. **DXT colour-endpoint tie-breaks** — the long tail, including all 5334 bytes
   in the default skin. Endpoints differ by a single 6-bit step (e.g. green 34
   vs 36), and some blocks agree on endpoints but disagree on indices. Inputs to
   the compressor are now provably exact, so this is purely quantiser decisions
   in `s3_quant.cpp`'s `search43Mult`/`roundMult`/`allSame`. Re-check the
   `force4` deviation above first, then apply the register-width model: the leak
   declares its variables `double`, and under `_PC_24` each operation rounds to
   24 bits, so a wide *operand* (a double literal) combined with a 24-bit
   *result* is the shape to look for.
3. **Wire `Rxdk.SkinBld` into the build / media-restore path** so no Win32 tool
   is needed.
4. Add a regression test that runs `Sweep-Bundler.ps1` and the two skin diffs,
   so the counts above are enforced rather than remembered.
