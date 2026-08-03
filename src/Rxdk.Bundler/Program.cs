// bundler — Xbox Packed Resource (.xpr) compiler.
//
// Cross-platform .NET port of the XDK "Bundler" tool. Reads a .rdf resource
// description file and produces the packed .xpr plus a companion Resource.h.
//
// Usage: bundler [options] <file.rdf | file.tga|bmp|...>
//   -o <file>   output .xpr path (overrides out_packedresource)
//   -h <file>   output header .h path (overrides out_header)
//   -p <prefix> prefix for the header #defines (overrides out_prefix)
//   -e <file>   error-log path (overrides out_error)
//   -q          quiet

namespace Rxdk.Bundler;

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

        try
        {
            var bundler = new Bundler();
            bundler.Initialize(args);
            bundler.Process();
            return 0;
        }
        catch (BundlerException ex)
        {
            Console.Error.WriteLine($"Bundler : error : {ex.Message}");
            return kFailed;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Bundler : error : {ex.Message}");
            return kFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bundler : error : {ex.Message}");
            return kFailed;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("bundler - Xbox Packed Resource (.xpr) compiler");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: bundler [options] <file.rdf | image-file>");
        Console.Error.WriteLine("  -o <file>   output .xpr path");
        Console.Error.WriteLine("  -h <file>   output header (.h) path");
        Console.Error.WriteLine("  -p <prefix> prefix for header #defines");
        Console.Error.WriteLine("  -e <file>   error-log path");
        Console.Error.WriteLine("  -q          quiet");
    }
}
