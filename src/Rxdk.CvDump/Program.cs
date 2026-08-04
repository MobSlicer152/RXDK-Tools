using Rxdk.Pdb.Cv;

// cvdump — recover ground-truth C type layouts (struct/union/enum offsets, sizes, enum values)
// from the CodeView .debug$T sections in a COFF static library (.lib). Used to read the exact ABI
// a prebuilt XDK library was compiled against, so the RXDK source libs can be reconciled to it.
//
//   cvdump <lib> [nameFilter...]
//
// With no filter, every named aggregate/enum is dumped. Filters are case-insensitive substrings.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: cvdump <path-to.lib> [nameFilter ...]");
    return 2;
}

var libPath = args[0];
if (!File.Exists(libPath))
{
    Console.Error.WriteLine($"cvdump: not found: {libPath}");
    return 2;
}

var filters = args.Skip(1).ToArray();

try
{
    var types = LibTypeDump.Extract(libPath, filters);
    if (types.Count == 0)
    {
        Console.Error.WriteLine(filters.Length > 0
            ? $"cvdump: no types matched [{string.Join(", ", filters)}] in {Path.GetFileName(libPath)}"
            : $"cvdump: no CodeView type records found in {Path.GetFileName(libPath)}");
        return 1;
    }

    Console.WriteLine($"// {types.Count} type(s) recovered from {Path.GetFileName(libPath)}");
    Console.WriteLine();
    foreach (var t in types)
    {
        Console.WriteLine(t.Rendered);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"cvdump: {ex.Message}");
    return 1;
}
