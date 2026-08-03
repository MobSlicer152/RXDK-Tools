// xactbld - Xbox Audio Content Tool (XACT) project compiler.
//
// Cross-platform .NET port of the XDK "xactbld"/filegen tool. Reads an .xap XACT
// project and produces the generated C header (XactSounds.h), the wave bank (.xwb)
// and the sound bank (.xsb) - the byte layouts the ported XACT runtime (libs/libxact)
// loads at runtime.
//
// Usage: xactbld [options] <file.xap>
//   -q          quiet

namespace Rxdk.XactBld;

internal static class Program
{
    public static int Main(string[] args)
    {
        const int kFailed = 10;

        if (args.Length == 0 || args.Any(a => a is "-?" or "/?" or "--help"))
        {
            PrintUsage();
            return kFailed;
        }

        bool quiet = false;
        string? xap = null;
        foreach (var a in args)
        {
            if (a is "-q" or "/q" or "-Q" or "/Q") { quiet = true; continue; }
            if (a.Length > 0 && (a[0] == '-' || a[0] == '/'))
            {
                Console.Error.WriteLine($"xactbld : error : bad option: {a}");
                return kFailed;
            }
            xap = a;
        }

        if (xap == null)
        {
            PrintUsage();
            return kFailed;
        }

        try
        {
            Action<string>? log = quiet ? null : Console.Out.WriteLine;
            var compiler = new XactCompiler(xap, log);
            compiler.Run();
            return 0;
        }
        catch (XactBldException ex)
        {
            Console.Error.WriteLine($"xactbld : error : {ex.Message}");
            return kFailed;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"xactbld : error : {ex.Message}");
            return kFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"xactbld : error : {ex.Message}");
            return kFailed;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("xactbld - Xbox Audio Content Tool (XACT) project compiler");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: xactbld [options] <file.xap>");
        Console.Error.WriteLine("  -q          quiet");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Reads an .xap project and writes the header (XactSounds.h),");
        Console.Error.WriteLine("the wave bank (.xwb) and the sound bank (.xsb) named inside it.");
    }
}
