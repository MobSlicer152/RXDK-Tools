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
| Shipped `.rdf`/`.xpr` pairs | 375 of 439 byte-identical, 0 build failures |

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

## Tools, and how they ship

| Tool | Project | Ships as |
| --- | --- | --- |
| `bundler` | `src/Rxdk.Bundler` | `tools/bundler` in the managed bundle |
| `skinbld` | `src/Rxdk.SkinBld` | `tools/skinbld` in the managed bundle |

Both are in `RXDKTools.sln`, so CI's `msbuild RXDKTools.sln` builds them, and both
are listed in `scripts/publish-managed-cli-tools.{ps1,sh}`, so every platform's
`rxdk-managed-<rid>.zip` release asset carries them. `skinbld` takes a
`ProjectReference` on `Rxdk.Bundler` — it delegates image compilation rather than
duplicating the codec, so a bundler fix changes skin output too.

`RXDK-VS20XX`'s `HostToolsInstaller.RequiredHostTools` does **not** list
`skinbld` yet, because nothing in the build invokes it. Add it there in the same
change that wires the skin step in, otherwise every existing install will
consider itself incomplete before a release exists that contains the tool.

## Reproducing

Build the two tools first — Debug is fine and is what the commands below assume:

```powershell
cd D:\Git\RXDK-Tools
dotnet build src\Rxdk.SkinBld\Rxdk.SkinBld.csproj      # builds Rxdk.Bundler too
```

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

**Alpha merge — the two tools disagree on which channel.** The merge loop copies
one byte of the loaded `0xAARRGGBB` alpha pixel into the destination alpha, and
`bundler.exe` and `skinbld.exe` pick a *different* byte:

* `bundler.exe` takes **byte 3** (the X/alpha channel). A 24-bit BMP loads as
  `X8R8G8B8` with byte 3 = 0, so **every 24-bit `AlphaSource` yields alpha 0** —
  the alpha art is effectively ignored. Confirmed byte-exact against the shipped
  sample `.xpr` files: `Input\Lightgun` merges to alpha 0 and now matches to the
  byte, and `PlayField`, `PaintEffect`, `Fire`, `PolynomialTextureMaps`,
  `Marketplace`, the three `loadsaveresource` bundles, `Gamepad` and `XPRViewer`
  all collapse to nothing but their DXT/resample colour tail.
* `skinbld.exe` takes **byte 0** (the **blue** channel), so the skin's grayscale
  alpha art survives. This is why the shared skin matched all along.

The leaked `basetexture.cpp` writes `dwAlpha = (*pAlphaBits) << 24`, i.e. blue —
so the *leaked bundler source is skinbld's behaviour, not the shipped
`bundler.exe`'s*. The two shipped binaries genuinely diverge here. The shared
codec keeps bundler's byte-3 merge by default; `Rxdk.SkinBld.XprBuilder` opts
into the blue-channel merge via `Bundler.AlphaFromBlueChannel = true`. Both
paths still bring alpha and colour to the larger of the two source dimensions
before merging, then resize to a power of two.

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

1. **Large bundler mismatches — RESOLVED (was the byte-3-vs-blue alpha merge).**
   Every one of the worst offenders was an `AlphaSource` texture whose alpha art
   is a 24-bit BMP; `bundler.exe` merges byte 3 (→ alpha 0), we merged blue. See
   the "Alpha merge" finding above. After the fix the sweep goes from **370 → 375
   byte-identical** (five newly exact: `Input\Lightgun`, `Graphics\PointSprites`,
   `Graphics\VolumeSprites`, and both `TechCert*\menuresource`) with **zero
   regressions**, and the offenders collapse to their DXT/resample colour tail:

   | Sample | Before | After |
   | --- | --- | --- |
   | `Graphics\PlayField\Resource.rdf` | 795219 | 3371 |
   | `Graphics\Fire` | 236340 | 242 |
   | `Graphics\PolynomialTextureMaps` | 196526 | 92 |
   | `Networking\Marketplace` | 147480 | 59 |
   | `Graphics\XPRViewer\textures.rdf` | 67310 | 1774 |
   | `Graphics\DynamicGamma` | 61451 | 18472 |
   | `Certification\TechCert*\loadsaveresource` (×3) | 30767 | 7 |
   | `Input\Lightgun` | 6958 | **0** |
   | `Input\Gamepad` | 9292 | 414 |
   | `Graphics\PaintEffect` | 3291 | 11 |

   The residuals are now the item-2 colour tail (DXT endpoints) plus the
   uncompressed-resample rounding tail (e.g. PlayField's remaining 3371 are 2919
   RGB bytes in the alpha-less `Football` texture and 423 in the `Grass` DXT1
   block, with no alpha bytes left). `DynamicGamma`'s 18472 is likewise spread
   across all four channels of its `X8R8G8B8` textures — a pure resample tail, not
   alpha. Full list in `reftest/bundler-sweep.txt`.

   Still-open small groups untouched by this fix: the repeated `99` bytes across
   the Dolphin variants / PerPixelLighting / PersistDisplay and `48` across
   DebugKeyboard / DebugMouse / AsyncWrite / SectionLoad (one shared fix should
   clear each group), and the three CJK fonts at 46–79 bytes each.
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

## Picking this up again

Start by re-establishing the baseline, so any change is measured against a number
you produced yourself rather than the table above:

```powershell
cd D:\Git\RXDK-Tools
dotnet build src\Rxdk.SkinBld\Rxdk.SkinBld.csproj
reftest\Sweep-Bundler.ps1 -ShowAll        # ~17 min, writes reftest\bundler-sweep.txt
```

`Sweep-Bundler.ps1` finds every `.rdf` with an `.xpr` beside it (or under
`Media\`), rebuilds it into `$env:TEMP\bundlersweep`, and reports differing byte
counts. Expect `439 pairs: 370 byte-identical`. It defaults to the Debug
`bundler.exe`; pass `-Bundler` to test a published build instead.

Then take the two open threads in this order — the big mismatches first, because
they are the ones likely to be a real bug rather than a rounding tie:

1. **A large mismatch, e.g. `Graphics\PlayField`.** Rebuild that one `.rdf` to a
   scratch path and localise the damage before reading any code:

   ```powershell
   $b = 'D:\Git\RXDK-Tools\src\Rxdk.Bundler\bin\Debug\net8.0\bundler.exe'
   cd D:\Git\RXDK-VS20XX\XDKSamples\Graphics\PlayField
   & $b -q -o $env:TEMP\ours.xpr -h $env:TEMP\ours.h -e $env:TEMP\ours.err Resource.rdf
   cd D:\Git\RXDK-Tools\reftest
   python xprpix.py Media\Resource.xpr $env:TEMP\ours.xpr   # which resource, which pixels
   python blockdump.py <ref> <ours> <offset>                # one DXT block, decoded
   ```

   The question to answer first is whether the resource *headers* agree. If they
   do, it is pixel data and belongs to the codec; if they do not, it is layout or
   a format decision and the answer is in the leaked `bundler.cpp`. Watch for a
   mismatch shared by several samples — the repeated byte counts in
   `bundler-sweep.txt` mean one fix clears a whole group.

2. **DXT colour endpoints.** `dxtsplit.py` isolates the colour half from the
   alpha half, and `triangle.py` / `triangle2.py` model the resample at chosen
   precisions — that pairing is what settled the `F2I` question, and it is the
   right instrument here too. Re-derive `allSame`'s `force4` against
   `s3_quant.cpp` before anything else, since it is a known guess.

Only then wire the skin step into the build (`XboxBuild.cs`, beside the `.rdf`
and `.xap` passes, plus `RequiredHostTools`) — doing it earlier means every
parity change has to be re-validated through the build as well as directly.

Do not run the bundler with default output paths anywhere inside the sample tree;
see the incident above.
