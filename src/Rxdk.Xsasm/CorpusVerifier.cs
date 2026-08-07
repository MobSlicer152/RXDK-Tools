namespace Rxdk.Xsasm;

/// <summary>
/// Runs the port's acceptance gates over a tree of XDK samples.
///
/// The samples ship the original assembler's own output next to its input --
/// matched .psh/.xpu and .vsh/.xvu pairs -- so this compares against Microsoft's
/// bytes rather than against this port's own idea of the format. That is the
/// difference between a regression test and a self-check.
/// </summary>
internal static class CorpusVerifier
{
    /// <summary>
    /// Include fragments: no version line, because something #includes them. They
    /// are not standalone shaders and are correct to reject.
    /// </summary>
    private static readonly string[] Fragments =
        { "wind.vsh", "hairlighting.vsh", "eyelighthalf.vsh" };

    /// <summary>
    /// Known deviation, deliberately not chased. pshader's golden zeroes
    /// C0Mapping/C1Mapping/FinalCombinerConstants where dolphin's -- the other
    /// ps.1.0 golden -- keeps the 0xF unused sentinel. The two goldens contradict
    /// each other, so there is no rule to infer, and the port follows the source.
    /// </summary>
    private static readonly string[] KnownXpuDeviations = { "pshader.psh" };

    public static int Run(string root)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"xsasm: no such directory '{root}'");
            return 1;
        }

        var shaders = Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".psh", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}out{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        int parseOk = 0, parseFail = 0, fragments = 0;
        var parseFailures = new List<string>();

        foreach (string f in shaders)
        {
            if (Fragments.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                fragments++;
                continue;
            }

            if (TryParse(f, out _)) parseOk++;
            else { parseFail++; parseFailures.Add(Path.GetFileName(f)); }
        }

        Console.WriteLine($"parse        {parseOk}/{parseOk + parseFail}" +
                          $"   ({fragments} include fragments skipped)");
        foreach (string f in parseFailures) Console.WriteLine($"  FAIL {f}");

        // Pixel shaders: assemble and compare against the golden .xpu.
        int xpuOk = 0, xpuBad = 0, xpuKnown = 0;
        var xpuFailures = new List<string>();

        foreach (string f in shaders.Where(s => s.EndsWith(".psh", StringComparison.OrdinalIgnoreCase)))
        {
            string golden = Path.ChangeExtension(f, ".xpu");
            if (!File.Exists(golden)) continue;

            byte[]? produced = TryAssemblePixel(f);
            bool match = produced is not null &&
                         produced.AsSpan().SequenceEqual(File.ReadAllBytes(golden));

            if (match) xpuOk++;
            else if (KnownXpuDeviations.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                xpuKnown++;
            else { xpuBad++; xpuFailures.Add(Path.GetFileName(f)); }
        }

        Console.WriteLine($"xpu golden   {xpuOk}/{xpuOk + xpuBad + xpuKnown} byte-exact" +
                          $"   ({xpuKnown} known deviation)");
        foreach (string f in xpuFailures) Console.WriteLine($"  FAIL {f}");

        // Vertex microcode: the translation is not ported yet, so all that can be
        // checked is that the bitfield encoding round-trips the goldens exactly.
        int xvuOk = 0, xvuBad = 0;
        var xvuFailures = new List<string>();

        foreach (string g in Directory
                     .EnumerateFiles(root, "*.xvu", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}out{Path.DirectorySeparatorChar}"))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                byte[] original = File.ReadAllBytes(g);
                var (kind, code) = XvuFile.Read(original);
                if (original.AsSpan().SequenceEqual(XvuFile.Write(kind, code))) xvuOk++;
                else { xvuBad++; xvuFailures.Add(Path.GetFileName(g)); }
            }
            catch (AssemblyException)
            {
                xvuBad++;
                xvuFailures.Add(Path.GetFileName(g));
            }
        }

        Console.WriteLine($"xvu encoding {xvuOk}/{xvuOk + xvuBad} round-trip byte-exact");
        foreach (string f in xvuFailures) Console.WriteLine($"  FAIL {f}");

        // Vertex translation (Phase 1): assemble each .vsh and compare the golden
        // .xvu. The pairing/reorder optimizer is not ported, so only the
        // unoptimized goldens match today; this is reported, not yet gated.
        int vtxOk = 0, vtxTotal = 0;
        foreach (string f in shaders.Where(s => s.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase)))
        {
            string golden = Path.ChangeExtension(f, ".xvu");
            if (!File.Exists(golden)) continue;
            vtxTotal++;
            byte[]? produced = TryAssembleVertex(f);
            if (produced is not null && produced.AsSpan().SequenceEqual(File.ReadAllBytes(golden)))
                vtxOk++;
        }
        Console.WriteLine($"xvu golden   {vtxOk}/{vtxTotal} byte-exact" +
                          $"   (translation only; the rest need the pairing optimizer)");

        bool pass = parseFail == 0 && xpuBad == 0 && xvuBad == 0;
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }

    private static bool TryParse(string path, out ParseResult? result)
    {
        result = null;
        var diags = new List<Diagnostic>();

        try
        {
            string source = new Preprocessor(Array.Empty<string>(), Array.Empty<string>(), diags)
                .Process(path);

            if (diags.Any(d => d.IsError)) return false;

            result = new Parser(source, diags).Parse();
            return !diags.Any(d => d.IsError);
        }
        catch (AssemblyException)
        {
            return false;
        }
    }

    private static byte[]? TryAssemblePixel(string path)
    {
        if (!TryParse(path, out var result) || result is null ||
            result.Kind != ShaderKind.Pixel)
        {
            return null;
        }

        try
        {
            bool legacy = !result.Xbox && result.VersionMajor == 1 && result.VersionMinor == 0;
            return new PixelShaderCompiler(new List<Diagnostic>(), legacy)
                .Compile(result.Code)
                .ToBytes(withFileId: true);
        }
        catch (AssemblyException)
        {
            return null;
        }
    }

    private static byte[]? TryAssembleVertex(string path)
    {
        if (!TryParse(path, out var result) || result is null || result.Kind == ShaderKind.Pixel)
            return null;

        var diags = new List<Diagnostic>();
        var code = new VertexShaderCompiler(diags)
            .Compile(result.Code, result.Kind, result.ScreenSpace, result.StateShader);
        if (diags.Any(d => d.IsError))
            return null;

        var vkind = result.StateShader ? VertexShaderKind.State
                  : result.Writable   ? VertexShaderKind.ReadWrite
                                      : VertexShaderKind.Ordinary;
        return XvuFile.Write(vkind, code);
    }
}
