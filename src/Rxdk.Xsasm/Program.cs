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

if (dumpTokens)
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
