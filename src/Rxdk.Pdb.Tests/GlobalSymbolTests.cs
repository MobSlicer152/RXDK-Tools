using Rxdk.Pdb;
using Rxdk.Pdb.Tpi;

namespace Rxdk.Pdb.Tests;

public sealed class GlobalSymbolTests
{
    private static string TrianglePdb => Path.Combine(AppContext.BaseDirectory, "TestData", "TriangleXDK.pdb");
    private static string MiniPdb => Path.Combine(AppContext.BaseDirectory, "TestData", "MiniLocals.pdb");

    [Fact]
    public void Dbi_ExposesSymbolRecordStream()
    {
        var dbi = PdbImage.OpenFile(TrianglePdb).Dbi;
        Assert.True(dbi.SymbolRecordStreamIndex >= 0, "symbol-record stream index should be present");
    }

    [Fact]
    public void EnumerateGlobals_YieldsDataSymbols()
    {
        var globals = PdbImage.OpenFile(TrianglePdb).EnumerateGlobals().ToList();

        // Real data globals (not just publics) must be present, each with a section:offset location.
        var data = globals.Where(g => !g.IsPublic).ToList();
        Assert.NotEmpty(data);
        Assert.All(data, g =>
        {
            Assert.False(string.IsNullOrEmpty(g.Name));
            Assert.NotEqual(0, g.Section);
        });
    }

    [Fact]
    public void EnumerateGlobals_ResolvesRvaAndType()
    {
        var pdb = PdbImage.OpenFile(TrianglePdb);

        // g_pd3dDevice is the sample's D3D device pointer — a 4-byte pointer global with a real RVA.
        var device = pdb.EnumerateGlobals().Single(g => g.Name == "g_pd3dDevice");
        Assert.False(device.IsPublic);
        Assert.NotEqual(0u, device.TypeIndex);

        var rva = pdb.Dbi.SectionOffsetToRva(device.Section, (int)device.Offset);
        Assert.True(rva > 0, "device global should map to a non-zero RVA");

        var type = pdb.Types.Resolve(device.TypeIndex);
        Assert.Equal(PdbTypeKind.Pointer, type.Kind);
        Assert.Equal(4u, type.ByteSize);
    }

    [Fact]
    public void EnumerateGlobals_ResolvesAggregateGlobalWithMembers()
    {
        var pdb = PdbImage.OpenFile(TrianglePdb);

        // qwTime is a _LARGE_INTEGER union global — an aggregate the Globals pane can expand.
        var qwTime = pdb.EnumerateGlobals().Single(g => g.Name == "qwTime");
        var type = pdb.Types.Resolve(qwTime.TypeIndex);
        Assert.True(type.IsAggregate || type.Kind == PdbTypeKind.Union);
        Assert.True(type.ByteSize > 0);
    }

    [Fact]
    public void Globals_ResolveToOwningModule_ForTitleFiltering()
    {
        // The bridge's Globals pane restricts to the title's own compiland by mapping each global's
        // RVA back to its module and checking whether the module's object file is a .lib. Lock that
        // data path: some non-public global must resolve to a module that exposes an object-file name.
        var pdb = PdbImage.OpenFile(TrianglePdb);

        var resolvedToModule = 0;
        foreach (var g in pdb.EnumerateGlobals())
        {
            if (g.IsPublic)
                continue;
            var rva = pdb.Dbi.SectionOffsetToRva(g.Section, (int)g.Offset);
            if (rva == 0)
                continue;
            var module = pdb.Dbi.FindModuleByRva(rva);
            if (module is not null)
            {
                Assert.NotNull(module.ObjectFileName);
                resolvedToModule++;
            }
        }

        Assert.True(resolvedToModule > 0, "at least one data global should map back to a module");
    }

    [Fact]
    public void EnumerateGlobals_PublicsCarryNoType()
    {
        // MiniLocals.c declares no globals, so its symbol-record stream holds only publics
        // (the linker's _main/_add), which have a location but no type index.
        var publics = PdbImage.OpenFile(MiniPdb).EnumerateGlobals().Where(g => g.IsPublic).ToList();
        Assert.NotEmpty(publics);
        Assert.All(publics, g => Assert.Equal(0u, g.TypeIndex));
    }
}
