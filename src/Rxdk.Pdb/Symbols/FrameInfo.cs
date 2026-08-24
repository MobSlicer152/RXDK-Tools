namespace Rxdk.Pdb.Symbols;

/// <summary>
/// The frame register a local's <see cref="LocalVariable.FrameOffset"/> is measured from.
/// x86 -O0 with a frame pointer uses <see cref="Ebp"/> for everything, but functions that
/// realign the stack for aligned locals (e.g. 16-byte D3D/XG matrices via SSE) describe their
/// locals relative to <see cref="VFrame"/> -- the realigned frame base -- and their `this`
/// via a register-relative range that also names VFRAME. <see cref="Esp"/> appears in
/// frame-pointer-omitted functions. The consumer maps each to a concrete address from the
/// thread context (see <c>ManagedSymbols.ResolveLocalAddress</c>).
/// </summary>
public enum FrameBase
{
    Ebp,
    Esp,
    VFrame,
}

/// <summary>
/// A local variable located relative to a frame register (<see cref="Base"/>).
/// <see cref="FrameOffset"/> is added to that register's value to get the variable's address;
/// <see cref="TypeIndex"/> resolves against the TPI type system for size/shape.
/// </summary>
public sealed record LocalVariable(
    string Name, uint TypeIndex, long FrameOffset, bool IsParameter, FrameBase Base = FrameBase.Ebp);

/// <summary>
/// The function whose code contains a queried RVA, plus its frame-relative locals. This is the
/// managed replacement for dbghelp's (broken) locals enumeration on Zig/LLVM PDBs.
/// </summary>
public sealed class FrameInfo
{
    public required string FunctionName { get; init; }
    public required uint FunctionRva { get; init; }
    public required uint CodeSize { get; init; }
    public required IReadOnlyList<LocalVariable> Locals { get; init; }

    /// <summary>
    /// Bytes of callee-saved registers pushed after the frame pointer (from S_FRAMEPROC). Needed
    /// to reconstruct <see cref="FrameBase.VFrame"/> from EBP: VFRAME = (EBP - this) &amp; ~0xF.
    /// </summary>
    public uint CalleeSavedBytes { get; init; }
}
