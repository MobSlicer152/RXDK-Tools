// skinbld — Xbox UIX skin compiler.
//
// Cross-platform .NET port of the XDK "Xbox Skin Builder" tool. Reads skin
// description files (.inx) and produces the .uix skin the UIX runtime loads,
// packing any images through the bundler.
//
// Usage: skinbld [/header] [/pre] [/rdf] input1.inx [input2.inx ...] output_file

using System.Runtime.InteropServices;
using System.Text;

namespace Rxdk.SkinBld;

internal static class Program
{
    private const int Failed = 1;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Xbox Skin Builder Tool - Version 1.00.5849");
        Console.WriteLine("Copyright (c) 2000-2004  Microsoft Corporation.  All rights reserved.");
        Console.WriteLine();

        var header = false;
        var pre = false;
        var keepRdf = false;
        var files = new List<string>();

        foreach (var arg in args)
        {
            if (arg.StartsWith('/') || arg.StartsWith('-'))
            {
                switch (arg[1..].ToLowerInvariant())
                {
                    case "header": header = true; break;
                    case "pre": pre = true; break;
                    case "rdf": keepRdf = true; break;
                    case "help" or "?": PrintUsage(); return Failed;
                    default:
                        Error($"Unknown option '{arg}'");
                        return Failed;
                }
            }
            else
            {
                files.Add(arg);
            }
        }

        if (files.Count < 2)
        {
            PrintUsage();
            return Failed;
        }

        var inputs = files[..^1];
        var output = files[^1];

        try
        {
            var parsed = new List<InxSection>();
            foreach (var input in inputs)
                parsed.AddRange(InxParser.Parse(input));

            var schema = SkinSchema.Load();
            var skin = new SkinCompiler(schema).Compile(parsed);

            var workDirectory = Directory.GetCurrentDirectory();
            var xprBuilder = new XprBuilder(keepRdf, quiet: !keepRdf);
            foreach (var section in skin.Sections)
                section.Xpr = xprBuilder.Build(section, workDirectory);

            File.WriteAllBytes(output, UixWriter.Write(skin));

            if (pre)
            {
                var path = Path.Combine(workDirectory, "sk_pre.inx");
                Console.WriteLine($"SKINBLD Pre: {path}");
                File.WriteAllText(path, PreWriter.Build(parsed), new UnicodeEncoding(false, true));
            }

            if (header)
            {
                var path = Path.Combine(workDirectory, "sk_res.h");
                Console.WriteLine($"SKINBLD Header: {path}");
                File.WriteAllText(path, HeaderWriter.Build(skin, inputs.Select(ReportPath)), Encoding.ASCII);
            }

            return 0;
        }
        catch (SkinBldException ex)
        {
            Error(ex.Message);
            return Failed;
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return Failed;
        }
    }

    private static void Error(string message) =>
        Console.Error.WriteLine($"SKINBLD : error : {message}");

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: skinbld [/header] [/pre] [/rdf] input1.inx [input2.inx ...] output_file");
        Console.Error.WriteLine();
        Console.Error.WriteLine("/header: Generate a C/C++ header file with resource IDs");
        Console.Error.WriteLine("/pre:    Generate a merged input file for reference");
        Console.Error.WriteLine("/rdf:    Keep temporary .RDF files and show Bundler output");
    }

    /// <summary>
    /// The header records the input's full path. The original tool shortens it
    /// through the Win32 8.3 form, which is reproduced where it exists.
    /// </summary>
    private static string ReportPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return full;

        var buffer = new StringBuilder(320);
        var length = GetShortPathNameW(full, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string path, StringBuilder shortPath, int capacity);
}
