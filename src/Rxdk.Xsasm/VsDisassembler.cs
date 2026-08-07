using System.Text;

namespace Rxdk.Xsasm;

/// <summary>
/// Renders one NV2A microcode instruction as readable assembly, a port of
/// api.cpp's InstructionDisassembler. Its purpose is debugging the vertex back
/// end: byte-exact divergences between our optimizer and the shipped goldens are
/// far easier to localize as a diff of disassembled instructions than of raw
/// 16-byte records. One microcode instruction can drive up to five sub-ops (a
/// mac register write, a mac output write, an ilu register write, an ilu output
/// write); they are joined with " + " on a single line so instructions line up
/// one-per-line for diffing.
/// </summary>
internal static class VsDisassembler
{
    private static readonly string[] IluOps = { "nop", "mov", "rcp", "rcc", "rsq", "expp", "logp", "lit" };
    private static readonly string[] MacOps =
        { "nop", "mov", "mul", "add", "mad", "dp3", "dph", "dp4", "dst", "min", "max", "slt", "sge", "mov", "??e", "??f" };
    private static readonly byte[] IluArgMask = { 0, 4, 4, 4, 4, 4, 4, 4 };
    private static readonly byte[] MacArgMask = { 0, 1, 3, 5, 7, 3, 3, 3, 3, 3, 3, 3, 3, 1, 0, 0 };
    private static readonly bool[] ExpandX = { false, false, true, true, true, true, true, false };
    private static readonly string[] OutNames =
        { "oPos", "o1?", "o2?", "oD0", "oD1", "oFog", "oPts", "oB0", "oB1", "oT0", "oT1", "oT2", "oT3", "???" };
    private static readonly string[] WriteMasks =
        { ".null", ".w", ".z", ".zw", ".y", ".yw", ".yz", ".yzw", ".x", ".xw", ".xz", ".xzw", ".xy", ".xyw", ".xyz", "", "error" };

    public static string Disassemble(VsInstruction pI)
    {
        var parts = new List<string>();
        if (pI.Mac == 0 && pI.Ilu == 0) parts.Add(One(pI, false, false));
        if (pI.Mac != 0 && (pI.Rwm != 0 || pI.Mac == VsInstruction.MacArl)) parts.Add(One(pI, false, false));
        if (pI.Mac != 0 && pI.Owm != 0 && pI.Om == VsInstruction.OmMac) parts.Add(One(pI, false, true));
        if (pI.Ilu != 0 && pI.Swm != 0) parts.Add(One(pI, true, false));
        if (pI.Ilu != 0 && pI.Owm != 0 && pI.Om == VsInstruction.OmIlu) parts.Add(One(pI, true, true));

        string s = parts.Count == 0 ? "nop" : string.Join(" + ", parts);
        if (pI.Eos != 0) s += "  // end";
        return s;
    }

    private static string One(VsInstruction pI, bool doIlu, bool doOwm)
    {
        var sb = new StringBuilder();
        sb.Append(doIlu ? IluOps[pI.Ilu] : MacOps[pI.Mac]).Append(' ');
        sb.Append(ParseOut(pI, doIlu, doOwm));

        byte argMask = doIlu ? IluArgMask[pI.Ilu] : MacArgMask[pI.Mac];
        bool expandX = doIlu && ExpandX[pI.Ilu];
        if ((argMask & 1) != 0) sb.Append(Mux(pI.Amx, pI.Arr, pI.Aws, pI.Azs, pI.Ays, pI.Axs, pI.Ane, pI, false));
        if ((argMask & 2) != 0) sb.Append(Mux(pI.Bmx, pI.Brr, pI.Bws, pI.Bzs, pI.Bys, pI.Bxs, pI.Bne, pI, false));
        if ((argMask & 4) != 0) sb.Append(Mux(pI.Cmx, pI.Crr, pI.Cws, pI.Czs, pI.Cys, pI.Cxs, pI.Cne, pI, expandX));
        return sb.ToString();
    }

    private static string ParseOut(VsInstruction pI, bool doIlu, bool doOwm)
    {
        var sb = new StringBuilder();
        if (!doIlu && !doOwm && pI.Rwm != 0) sb.Append($"r{pI.Rw}{Wm(pI.Rwm)}");
        if (!doIlu && pI.Mac == VsInstruction.MacArl) sb.Append("a0.x");
        if (doIlu && !doOwm && pI.Swm != 0)
        {
            uint iluRw = (pI.Mac != 0 && pI.Ilu != 0) ? 1u : pI.Rw;   // paired ilu writes r1
            sb.Append($"r{iluRw}{Wm(pI.Swm)}");
        }
        if (doOwm && pI.Owm != 0 && (pI.Om == VsInstruction.OmIlu) == doIlu)
        {
            bool ocOutput = (pI.Oc & 0x100) != 0;
            uint ocIndex = pI.Oc & 0xff;
            if (ocOutput)
            {
                if (ocIndex > 12) ocIndex = 13;
                sb.Append($"{OutNames[ocIndex]}{Wm(pI.Owm)}");
            }
            else sb.Append($"c{(int)ocIndex - 96}{Wm(pI.Owm)}");
        }
        return sb.ToString();
    }

    private static string Mux(uint mx, uint rr, uint ws, uint zs, uint ys, uint xs, uint ne, VsInstruction pI, bool expandX)
    {
        const string s = "xyzw";
        const string m = "?rvc";
        var sb = new StringBuilder(", ");
        if (ne != 0) sb.Append('-');
        sb.Append(mx == 1 ? "r" : m[(int)mx].ToString());
        switch (mx)
        {
            case 1: sb.Append(rr); break;
            case 2: sb.Append(pI.Va); break;
            case 3:
                int userReg = (int)pI.Ca - 96;
                if (pI.Cin != 0)
                {
                    sb.Append("[a0.x");
                    if (userReg < 0) sb.Append(userReg);
                    else if (userReg > 0) sb.Append($"+{userReg}");
                    sb.Append(']');
                }
                else sb.Append(userReg);
                break;
            default: sb.Append("error"); break;
        }

        if (expandX) { ys = zs = ws = xs; }   // microcode scalar back to .x
        if (xs == 0 && ys == 1 && zs == 2 && ws == 3) { }                        // identity: no suffix
        else if (xs == ys && ys == zs && zs == ws) sb.Append($".{s[(int)xs]}");
        else if (ys == zs && zs == ws) sb.Append($".{s[(int)xs]}{s[(int)ys]}");
        else if (zs == ws) sb.Append($".{s[(int)xs]}{s[(int)ys]}{s[(int)zs]}");
        else sb.Append($".{s[(int)xs]}{s[(int)ys]}{s[(int)zs]}{s[(int)ws]}");
        return sb.ToString();
    }

    private static string Wm(uint mask) => WriteMasks[mask > 15 ? 16 : mask];

    // --- CLI ------------------------------------------------------------------

    /// <summary>
    /// `--disasm A [B]`: list A's instructions, or (with B) diff the two
    /// instruction-by-instruction. Each arg is a .xvu (read directly) or a .vsh
    /// (assembled through the vertex compiler first).
    /// </summary>
    public static int RunCli(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            Console.Error.WriteLine("xsasm --disasm <file.xvu|file.vsh> [file2]");
            return 1;
        }

        List<string> a;
        try { a = Load(files[0]); }
        catch (Exception e) { Console.Error.WriteLine($"xsasm: {files[0]}: {e.Message}"); return 1; }

        if (files.Count == 1)
        {
            for (int i = 0; i < a.Count; i++) Console.WriteLine($"{i,3}: {a[i]}");
            return 0;
        }

        List<string> b;
        try { b = Load(files[1]); }
        catch (Exception e) { Console.Error.WriteLine($"xsasm: {files[1]}: {e.Message}"); return 1; }

        Console.WriteLine($"--- {Path.GetFileName(files[0])} ({a.Count})   +++ {Path.GetFileName(files[1])} ({b.Count})");
        int n = Math.Max(a.Count, b.Count), diffs = 0;
        for (int i = 0; i < n; i++)
        {
            string la = i < a.Count ? a[i] : "<none>";
            string lb = i < b.Count ? b[i] : "<none>";
            bool same = la == lb;
            if (!same) diffs++;
            Console.WriteLine($"{(same ? " " : "*")} {i,3}: {la,-40} | {lb}");
        }
        Console.WriteLine(diffs == 0 ? "identical" : $"{diffs} instruction(s) differ");
        return diffs == 0 ? 0 : 1;
    }

    private static List<string> Load(string path)
    {
        List<VsInstruction> code;
        if (path.EndsWith(".xvu", StringComparison.OrdinalIgnoreCase))
        {
            code = XvuFile.Read(File.ReadAllBytes(path)).Code;
        }
        else
        {
            var diags = new List<Diagnostic>();
            string src = new Preprocessor(Array.Empty<string>(), Array.Empty<string>(), diags).Process(path);
            var result = new Parser(src, diags).Parse();
            if (diags.Any(d => d.IsError)) throw new AssemblyException("parse failed");
            code = new VertexShaderCompiler(diags).Compile(
                result.Code, result.Kind, result.ScreenSpace, result.StateShader);
        }
        return code.Select(Disassemble).ToList();
    }
}
