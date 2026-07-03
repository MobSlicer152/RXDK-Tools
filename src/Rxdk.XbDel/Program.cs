using System.CommandLine;
using System.CommandLine.Invocation;
using Rxdk.XbFile;

namespace Rxdk.XbDel;

internal static class Program
{
    private static readonly Option<string?> XboxOption = new(["-x", "/x", "--xbox"])
    {
        Description = "Xbox hostname or IP address.",
    };

    private static readonly Option<bool> ForceOption = new(["/f", "-f"]) { Description = "Force delete of read-only files." };
    private static readonly Option<bool> RecursiveOption = new(["/r", "-r"]) { Description = "Recursive delete of directories." };
    private static readonly Option<bool> VerboseOption = new(["/v", "-v"]) { Description = "Print each deleted file." };
    private static readonly Option<bool> QuietOption = new(["/q", "-q"]) { Description = "Always exit 0, even if a file could not be deleted." };

    private static readonly Argument<string[]> PathsArgument = new("paths")
    {
        Description = "Xbox file(s) to delete (xE:\\..., may contain wildcards).",
        Arity = ArgumentArity.OneOrMore,
    };

    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("Delete one or more files from the Xbox target system.")
        {
            PathsArgument,
            XboxOption,
            ForceOption,
            RecursiveOption,
            VerboseOption,
            QuietOption,
        };

        root.SetHandler(Execute);

        try
        {
            return await root.InvokeAsync(XbLegacyArgv.ForXbDel(args));
        }
        catch (XbFileException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void Execute(InvocationContext context)
    {
        var parse = context.ParseResult;
        var quiet = parse.GetValueForOption(QuietOption);
        try
        {
            var paths = parse.GetValueForArgument(PathsArgument) ?? Array.Empty<string>();
            if (paths.Length < 1)
                throw new XbFileException("At least one file to delete is required.");

            var parsed = paths.Select(XbPath.Parse).ToList();
            var xbox = parse.GetValueForOption(XboxOption);
            using var session = XbConsoleSession.Connect(xbox);

            var options = new XbDelOptions
            {
                Force = parse.GetValueForOption(ForceOption),
                Recursive = parse.GetValueForOption(RecursiveOption),
                Verbose = parse.GetValueForOption(VerboseOption),
            };
            var service = new XbDelService(options, session);

            var anyFailed = false;
            foreach (var path in parsed)
            {
                try
                {
                    service.Execute(path);
                }
                catch (XbFileException ex)
                {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    anyFailed = true;
                }
            }

            context.ExitCode = anyFailed && !quiet ? 1 : 0;
        }
        catch (XbFileException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            context.ExitCode = quiet ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            context.ExitCode = quiet ? 0 : 1;
        }
    }
}
