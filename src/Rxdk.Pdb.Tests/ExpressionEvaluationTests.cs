using Rxdk.Pdb;
using Rxdk.Pdb.Symbols;
using Rxdk.Pdb.Tpi;

namespace Rxdk.Pdb.Tests;

/// <summary>
/// Covers the managed type/expression engine that replaced dbghelp: the <see cref="ExpressionPath"/>
/// parser, <see cref="TypeSystem.TryFindMember"/> member resolution, and the <see cref="TypeEvaluator"/>
/// accessor walk (member, array index, pointer deref) against the checked-in PDBs.
/// </summary>
public sealed class ExpressionEvaluationTests
{
    private static string MiniPdb => Path.Combine(AppContext.BaseDirectory, "TestData", "MiniLocals.pdb");
    private static string TrianglePdb => Path.Combine(AppContext.BaseDirectory, "TestData", "TriangleXDK.pdb");

    private static LocalVariable Local(PdbImage pdb, string function, string name) =>
        pdb.Symbols.EnumerateFunctions().Single(f => f.FunctionName == function).Locals.Single(l => l.Name == name);

    // --- ExpressionPath parser ------------------------------------------------------------------

    [Fact]
    public void Parse_BareIdentifier_HasNoAccessors()
    {
        Assert.True(ExpressionPath.TryParse("hr", out var baseName, out var accessors));
        Assert.Equal("hr", baseName);
        Assert.Empty(accessors);
    }

    [Fact]
    public void Parse_MemberArrowAndIndex_ChainInOrder()
    {
        Assert.True(ExpressionPath.TryParse("a.b->c[7]", out var baseName, out var accessors));
        Assert.Equal("a", baseName);
        Assert.Equal(3, accessors.Count);
        Assert.Equal(new Accessor(AccessorKind.Member, "b", 0), accessors[0]);
        Assert.Equal(new Accessor(AccessorKind.Arrow, "c", 0), accessors[1]);
        Assert.Equal(new Accessor(AccessorKind.Index, "", 7), accessors[2]);
    }

    [Fact]
    public void Parse_ToleratesWhitespaceAndHexIndex()
    {
        Assert.True(ExpressionPath.TryParse(" p -> field [0x10] ", out var baseName, out var accessors));
        Assert.Equal("p", baseName);
        Assert.Equal(new Accessor(AccessorKind.Arrow, "field", 0), accessors[0]);
        Assert.Equal(16, accessors[1].Index);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".x")]        // no base
    [InlineData("a.")]        // dangling member
    [InlineData("a->")]       // dangling arrow
    [InlineData("a[")]        // unterminated index
    [InlineData("a[b]")]      // non-numeric index
    [InlineData("a[-1]")]     // negative index
    public void Parse_RejectsMalformedExpressions(string expression)
    {
        Assert.False(ExpressionPath.TryParse(expression, out _, out _));
    }

    // --- TypeSystem.TryFindMember --------------------------------------------------------------

    [Fact]
    public void TryFindMember_ResolvesStructFieldsAtTheirOffsets()
    {
        var pdb = PdbImage.OpenFile(MiniPdb);
        var point = Local(pdb, "main", "p").TypeIndex;

        Assert.True(pdb.Types.TryFindMember(point, "x", out var xOffset, out var xType));
        Assert.Equal(0u, xOffset);
        Assert.Equal(4u, pdb.Types.SizeOf(xType));

        Assert.True(pdb.Types.TryFindMember(point, "y", out var yOffset, out _));
        Assert.Equal(4u, yOffset);

        Assert.False(pdb.Types.TryFindMember(point, "nope", out _, out _));
    }

    [Fact]
    public void TryFindMember_IsCaseInsensitiveFallback()
    {
        var pdb = PdbImage.OpenFile(MiniPdb);
        var point = Local(pdb, "main", "p").TypeIndex;

        Assert.True(pdb.Types.TryFindMember(point, "X", out var offset, out _));
        Assert.Equal(0u, offset);
    }

    [Fact]
    public void TryFindMember_NonAggregate_ReturnsFalse()
    {
        var pdb = PdbImage.OpenFile(MiniPdb);
        var hr = Local(pdb, "main", "hr").TypeIndex; // a scalar (long)
        Assert.False(pdb.Types.TryFindMember(hr, "anything", out _, out _));
    }

    // --- TypeEvaluator walk --------------------------------------------------------------------

    [Fact]
    public void Walk_StructMember_AddsFieldOffset()
    {
        var pdb = PdbImage.OpenFile(MiniPdb);
        var point = Local(pdb, "main", "p").TypeIndex;
        var eval = new TypeEvaluator(pdb.Types, _ => null);

        ExpressionPath.TryParse("p.y", out _, out var accessors);
        Assert.True(eval.TryWalk(0x2000, point, accessors, out var address, out var type, out var error));
        Assert.Null(error);
        Assert.Equal(0x2004u, (uint)address);
        Assert.Equal(4u, type.ByteSize);
        Assert.Equal(PdbTypeKind.Primitive, type.Kind);
    }

    [Fact]
    public void Walk_ArrayIndex_AddsElementStride()
    {
        var pdb = PdbImage.OpenFile(MiniPdb);
        var arr = Local(pdb, "main", "arr").TypeIndex; // int[4]
        var eval = new TypeEvaluator(pdb.Types, _ => null);

        ExpressionPath.TryParse("arr[2]", out _, out var accessors);
        Assert.True(eval.TryWalk(0x3000, arr, accessors, out var address, out var type, out _));
        Assert.Equal(0x3008u, (uint)address); // 2 * sizeof(int)
        Assert.Equal(4u, type.ByteSize);
    }

    [Fact]
    public void Walk_PointerIndex_DereferencesThroughMemory()
    {
        var pdb = PdbImage.OpenFile(TrianglePdb);
        var device = pdb.EnumerateGlobals().Single(g => g.Name == "g_pd3dDevice");
        var pointerType = pdb.Types.Resolve(device.TypeIndex);
        Assert.Equal(PdbTypeKind.Pointer, pointerType.Kind);

        const ulong pointerSlot = 0x4000;
        const uint pointee = 0x1234;
        var memory = new Dictionary<ulong, uint> { [pointerSlot] = pointee };
        var eval = new TypeEvaluator(pdb.Types, a => memory.TryGetValue(a, out var v) ? v : (uint?)null);

        ExpressionPath.TryParse("dev[0]", out _, out var accessors);
        Assert.True(eval.TryWalk(pointerSlot, device.TypeIndex, accessors, out var address, out _, out _));
        Assert.Equal(pointee, (uint)address); // element 0 lives at the dereferenced pointer value
    }

    [Fact]
    public void Walk_PointerRead_FailingMemory_ReportsReadFailed()
    {
        var pdb = PdbImage.OpenFile(TrianglePdb);
        var device = pdb.EnumerateGlobals().Single(g => g.Name == "g_pd3dDevice");
        var eval = new TypeEvaluator(pdb.Types, _ => null); // every read fails

        ExpressionPath.TryParse("dev[0]", out _, out var accessors);
        Assert.False(eval.TryWalk(0x4000, device.TypeIndex, accessors, out _, out _, out var error));
        Assert.Equal("readFailed", error);
    }

    [Fact]
    public void Walk_MemberOnScalar_ReportsMemberNotFound()
    {
        var pdb = PdbImage.OpenFile(MiniPdb);
        var hr = Local(pdb, "main", "hr").TypeIndex; // scalar
        var eval = new TypeEvaluator(pdb.Types, _ => null);

        ExpressionPath.TryParse("hr.field", out _, out var accessors);
        Assert.False(eval.TryWalk(0x1000, hr, accessors, out _, out _, out var error));
        Assert.Equal("memberNotFound", error);
    }

    [Fact]
    public void Walk_ArrowThroughPointerToAggregate_ResolvesMember()
    {
        var pdb = PdbImage.OpenFile(TrianglePdb);

        // Find any pointer type in the program whose referent is an aggregate with a named member,
        // so `->member` exercises the deref-then-member path against real type data.
        (uint PointerType, uint Referent, PdbMember Member)? target = null;
        foreach (var index in pdb.Tpi.TypeIndices())
        {
            var t = pdb.Types.Resolve(index);
            if (t.Kind != PdbTypeKind.Pointer || t.ReferentType == 0)
                continue;
            var referent = pdb.Types.Peel(t.ReferentType);
            var member = referent.Members.FirstOrDefault(m => !string.IsNullOrEmpty(m.Name));
            if (referent.IsAggregate && member is not null)
            {
                target = (index, t.ReferentType, member);
                break;
            }
        }

        Assert.True(target is not null, "expected at least one pointer-to-aggregate type in TriangleXDK.pdb");
        var (pointerType, _, expected) = target!.Value;

        const ulong pointerSlot = 0x5000;
        const uint structAddr = 0x6000;
        var memory = new Dictionary<ulong, uint> { [pointerSlot] = structAddr };
        var eval = new TypeEvaluator(pdb.Types, a => memory.TryGetValue(a, out var v) ? v : (uint?)null);

        var accessors = new[] { new Accessor(AccessorKind.Arrow, expected.Name, 0) };
        Assert.True(eval.TryWalk(pointerSlot, pointerType, accessors, out var address, out _, out var error));
        Assert.Null(error);
        Assert.Equal(structAddr + (uint)expected.Offset, (uint)address);
    }
}
