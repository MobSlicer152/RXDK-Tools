using Rxdk.Xsasm;

// xsasm -- the Xbox shader assembler, ported to managed code so shaders can be
// authored on Linux and macOS as well as Windows. The original is a Win32 PE
// wrapping xgraphics.lib's shadeasm.
//
// Front end (lexer, parser, register/opcode/swizzle encoding -> D3D8 token
// stream) is complete. The back ends that lower that stream to hardware form --
// a D3DPIXELSHADERDEF for pixel shaders, NV2A microcode for vertex shaders --
// are still being ported, so this currently exposes the front end only.

if (args.Length == 0 || args.Contains("/?") || args.Contains("--help"))
{
    Console.Error.WriteLine("""
        Xbox Shader Assembler (RXDK managed port)

        usage: xsasm [--tokens] sourcefile

          --tokens    Print the D3D8 token stream the front end produces.

        Assembling to .xpu/.xvu is not wired up yet -- the pixel and vertex
        back ends are still being ported.
        """);
    return args.Length == 0 ? 1 : 0;
}

// Round-trips a .xvu through the microcode bitfield encoder. Any packing error
// shows up as a byte difference, which is the only way to be sure the 128-bit
// layout is right before anything starts generating it.
if (args.Contains("--verify-xvu"))
{
    string xvu = args.First(a => a.EndsWith(".xvu", StringComparison.OrdinalIgnoreCase));
    byte[] original = File.ReadAllBytes(xvu);
    var (kind, code) = XvuFile.Read(original);
    byte[] repacked = XvuFile.Write(kind, code);

    if (original.AsSpan().SequenceEqual(repacked))
    {
        Console.WriteLine($"OK   {Path.GetFileName(xvu)}  ({code.Count} instructions, {kind})");
        return 0;
    }

    int at = 0;
    while (at < original.Length && original[at] == repacked[at]) at++;
    Console.WriteLine($"FAIL {Path.GetFileName(xvu)}  first difference at byte {at}");
    return 1;
}

bool dumpTokens = args.Contains("--tokens");
string? path = args.FirstOrDefault(a => !a.StartsWith('-'));

if (path is null)
{
    Console.Error.WriteLine("xsasm: no input file");
    return 1;
}

if (!File.Exists(path))
{
    Console.Error.WriteLine($"xsasm: cannot open '{path}'");
    return 1;
}

string source = File.ReadAllText(path);
var diags = new List<Diagnostic>();

ParseResult result;
try
{
    result = new Parser(source, diags).Parse();
}
catch (AssemblyException ex)
{
    Console.Error.WriteLine($"{path} : error: {ex.Message}");
    return 1;
}

foreach (var d in diags)
    Console.Error.WriteLine(d with { File = d.File.Length == 0 ? path : d.File });

if (diags.Any(d => d.IsError))
    return 1;

if (!dumpTokens)
{
    if (result.Kind != ShaderKind.Pixel)
    {
        Console.Error.WriteLine("xsasm: the vertex back end is not ported yet");
        return 1;
    }

    PixelShaderDef psd;
    try
    {
        // The boundary is ps.1.0, not "not xps": ps.1.1 shaders (Glass, sky) lower
        // exactly like xps in the goldens, while ps.1.0 (dolphin, pshader) does not.
        bool legacy = !result.Xbox && result.VersionMajor == 1 && result.VersionMinor == 0;
        psd = new PixelShaderCompiler(diags, legacy).Compile(result.Code);
    }
    catch (AssemblyException ex)
    {
        Console.Error.WriteLine($"{path} : error: {ex.Message}");
        return 1;
    }

    // Default output name matches the original: <source>.xpu for a pixel shader.
    string outPath = args.SkipWhile(a => a != "-o").Skip(1).FirstOrDefault()
                     ?? Path.ChangeExtension(path, ".xpu");

    File.WriteAllBytes(outPath, psd.ToBytes(withFileId: true));
    return 0;
}

{
    string kind = result.Kind == ShaderKind.Pixel ? "pixel" : "vertex";
    string flavour = result.Xbox ? "xbox" : "dx8";
    Console.WriteLine($"; {kind} shader ({flavour}) {result.VersionMajor}.{result.VersionMinor}" +
                      (result.ScreenSpace ? " screenspace" : ""));

    foreach (var (reg, v) in result.Constants.OrderBy(c => c.Key))
        Console.WriteLine($"; def c{reg} = {v[0]}, {v[1]}, {v[2]}, {v[3]}");

    foreach (uint t in result.Code)
        Console.WriteLine($"0x{t:x8}");
}

return 0;
