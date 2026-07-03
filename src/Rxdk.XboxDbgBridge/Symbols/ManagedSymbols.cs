using Rxdk.Pdb;
using Rxdk.Pdb.Symbols;
using Rxdk.Pdb.Tpi;
using Rxdk.Xbdm;

namespace Rxdk.XboxDbgBridge.Symbols;

/// <summary>
/// Locals emission backed by the pure-managed <see cref="PdbImage"/> reader instead of dbghelp.
/// dbghelp cannot interpret the modern S_LOCAL / S_DEFRANGE_FRAMEPOINTER_REL records that Zig/LLVM
/// emit (it reports a def-range's code span as the variable size, so a 4-byte HRESULT shows as
/// array[64]); the managed reader resolves the real type/size/frame-offset. Falls back to dbghelp
/// via <see cref="SymbolService"/> only when this path yields nothing.
/// </summary>
internal sealed class ManagedSymbols
{
    private readonly PdbImage _pdb;
    private readonly nuint _moduleBase;

    internal ManagedSymbols(PdbImage pdb, nuint moduleBase)
    {
        _pdb = pdb;
        _moduleBase = moduleBase;
    }

    /// <summary>Emits the current frame's locals; returns false if no frame was found at EIP.</summary>
    internal bool EmitLocals(ref XbdmContext context, VariableJson variables, KitMemoryAccess memory)
    {
        var frame = FindFrame(context.Eip);
        if (frame is null)
            return false;

        var emitted = false;
        foreach (var local in frame.Locals)
        {
            if (variables.IsFull)
                break;
            if (IsHidden(local.Name) || variables.WasEmitted(local.Name))
                continue;

            var address = (nuint)((long)context.Ebp + local.FrameOffset);
            EmitValue(local.Name, local.TypeIndex, address, memory, variables, expandBase: local.Name);
            emitted = true;
        }

        return emitted;
    }

    /// <summary>
    /// Emits the program's global-scope data symbols (globals + file-statics), formatting each at its
    /// absolute kit address (moduleBase + RVA). Compiler-generated helpers and publics are skipped.
    /// <paramref name="maxTier"/> caps how far past the title's own mutable globals to include
    /// (0 = title .data only, 1 = + title const tables, 2 = + linked-library globals); it is driven
    /// by the extension's "Globals visibility" toggle. Returns false when the PDB has no usable data
    /// globals (so the map fallback can take over).
    /// </summary>
    internal bool EmitGlobals(VariableJson variables, KitMemoryAccess memory, int maxVars, int maxTier)
    {
        if (_moduleBase == 0)
            return false;

        // Rank the program's globals so the Globals pane shows the most useful set. A title links
        // hundreds of library globals (D3D::*, CRT stdio, ...) plus compile-time const lookup tables
        // from system headers (e.g. D3DTEXTUREDIRECTENCODE, emitted into the title's own .obj but
        // living in read-only .rdata). What the user actually debugs is mutable program state:
        //   tier 0 = title compiland + writable section (.data/.bss) — the real globals
        //   tier 1 = title compiland, any section                    — include title's const tables
        //   tier 2 = everything                                      — last-resort, library globals
        // The toggle sets maxTier; we show every tier up to it, but relax upward if that would leave
        // the pane empty (e.g. a title with no writable globals) so there's always something to see.
        var candidates = new List<(GlobalSymbol Global, nuint Address, int Tier)>();
        foreach (var global in _pdb.EnumerateGlobals())
        {
            if (global.IsPublic || IsHiddenGlobal(global.Name))
                continue;
            var rva = _pdb.Dbi.SectionOffsetToRva(global.Section, (int)global.Offset);
            if (rva == 0)
                continue;
            var address = _moduleBase + (nuint)rva;
            var module = _pdb.Dbi.FindModuleByRva(rva);
            var isTitle = module is not null && !IsLibraryObject(module.ObjectFileName);
            var tier = isTitle ? (IsWritableSection(global.Section) ? 0 : 1) : 2;
            candidates.Add((global, address, tier));
        }

        var showTier = Math.Clamp(maxTier, 0, 2);
        while (showTier < 2 && !candidates.Exists(c => c.Tier <= showTier))
            showTier++;

        var emitted = false;
        foreach (var (global, address, tier) in candidates)
        {
            if (variables.IsFull || variables.Count >= maxVars)
                break;
            if (tier > showTier)
                continue;

            var display = CleanGlobalName(global.Name);
            if (variables.WasEmitted(display))
                continue;

            // Display the readable name but expand under the raw symbol name, since TryEmitMembers
            // matches globals by their raw stream name.
            EmitValue(display, global.TypeIndex, address, memory, variables, expandBase: global.Name);
            emitted = true;
        }

        return emitted;
    }

    /// <summary>Expands one aggregate local (array elements or struct members) by name.</summary>
    internal bool TryEmitMembers(string baseName, ref XbdmContext context, VariableJson variables, KitMemoryAccess memory)
    {
        var frame = FindFrame(context.Eip);
        var local = frame?.Locals.FirstOrDefault(l => l.Name == baseName);
        if (local is not null)
        {
            var address = (nuint)((long)context.Ebp + local.FrameOffset);
            EmitChildren(local.TypeIndex, address, memory, variables);
            return variables.Count > 0;
        }

        // No matching local: the name may be a global aggregate (struct/array) being expanded.
        foreach (var global in _pdb.EnumerateGlobals())
        {
            if (global.IsPublic || global.Name != baseName)
                continue;
            if (!TryGlobalAddress(global, out var address))
                continue;

            EmitChildren(global.TypeIndex, address, memory, variables);
            return variables.Count > 0;
        }

        return false;
    }

    private bool TryGlobalAddress(GlobalSymbol global, out nuint address)
    {
        address = 0;
        var rva = _pdb.Dbi.SectionOffsetToRva(global.Section, (int)global.Offset);
        if (rva == 0)
            return false;
        address = _moduleBase + (nuint)rva;
        return true;
    }

    // A module contributed by a static library records the .lib as its object file; the title's own
    // compiland records its .obj/.o. That distinguishes program globals from linked-in library ones.
    private static bool IsLibraryObject(string objectFile) =>
        objectFile.EndsWith(".lib", StringComparison.OrdinalIgnoreCase);

    // IMAGE_SCN_MEM_WRITE — set on .data/.bss (mutable program state), clear on .rdata (compile-time
    // constants). Mutable globals are what a debugger's Globals pane is for; const tables are noise.
    private const uint ImageScnMemWrite = 0x80000000;

    private bool IsWritableSection(ushort section)
    {
        var sections = _pdb.Dbi.Sections;
        if (section == 0 || section > sections.Count)
            return false;
        return (sections[section - 1].Characteristics & ImageScnMemWrite) != 0;
    }

    // File-static / anonymous-namespace globals carry a `anonymous namespace':: qualifier in their
    // symbol name; strip it for display (kept only as a scope marker, not part of the variable name).
    private static string CleanGlobalName(string name)
    {
        const string anon = "`anonymous namespace'::";
        return name.StartsWith(anon, StringComparison.Ordinal) ? name[anon.Length..] : name;
    }

    private FrameInfo? FindFrame(uint eip)
    {
        if (_moduleBase == 0 || eip < _moduleBase)
            return null;
        return _pdb.FindFrame(eip - (uint)_moduleBase); // RVA = EIP - module base (image base == PDB base)
    }

    private void EmitValue(string name, uint typeIndex, nuint address, KitMemoryAccess memory, VariableJson variables, string expandBase)
    {
        var type = _pdb.Types.Resolve(typeIndex);
        switch (type.Kind)
        {
            case PdbTypeKind.Array:
            {
                var elem = type.ReferentType != 0 ? _pdb.Types.Resolve(type.ReferentType) : null;
                var label = elem?.Name is { Length: > 0 } en ? $"{en}[{type.ElementCount}]" : $"array[{type.ElementCount}]";
                variables.Append(name, label, expandable: type.ElementCount > 0, expandBase: expandBase);
                break;
            }

            case PdbTypeKind.Struct:
            case PdbTypeKind.Class:
            case PdbTypeKind.Union:
            {
                var label = type.Name is { Length: > 0 } tn ? tn : $"{{{type.ByteSize} bytes}}";
                variables.Append(name, label, expandable: type.Members.Count > 0, expandBase: expandBase);
                break;
            }

            default:
                variables.Append(name, FormatScalar(type, address, memory));
                break;
        }
    }

    private void EmitChildren(uint typeIndex, nuint address, KitMemoryAccess memory, VariableJson variables)
    {
        var type = _pdb.Types.Resolve(typeIndex);

        if (type.Kind == PdbTypeKind.Array && type.ElementCount > 0)
        {
            var elem = _pdb.Types.Resolve(type.ReferentType);
            var elemSize = elem.ByteSize == 0 ? 4u : elem.ByteSize;
            var count = Math.Min(type.ElementCount, 256u);
            for (var i = 0u; i < count && !variables.IsFull; i++)
                EmitValue($"[{i}]", type.ReferentType, address + (nuint)(i * elemSize), memory, variables, expandBase: $"[{i}]");
            return;
        }

        if (type.IsAggregate)
        {
            foreach (var member in type.Members)
            {
                if (variables.IsFull)
                    break;
                EmitValue(member.Name, member.TypeIndex, address + (nuint)member.Offset, memory, variables, expandBase: member.Name);
            }
        }
    }

    private static string FormatScalar(PdbType type, nuint address, KitMemoryAccess memory)
    {
        var low = memory.ReadDword(address);
        if (low is null)
            return "<unreadable>";
        var value = low.Value;

        if (type.IsFloatingPoint && type.ByteSize == 8)
        {
            var high = memory.ReadDword(address + 4) ?? 0;
            var bits = ((ulong)high << 32) | value;
            return $"{BitConverter.Int64BitsToDouble((long)bits):g}";
        }

        if (type.IsFloatingPoint)
            return $"{BitConverter.Int32BitsToSingle((int)value):g} (0x{value:x8})";

        if (type.Kind == PdbTypeKind.Pointer)
            return $"0x{value:x8}";

        if (type.ByteSize == 8)
        {
            var high = memory.ReadDword(address + 4) ?? 0;
            return $"0x{high:x8}{value:x8}";
        }

        value = type.ByteSize switch
        {
            1 => value & 0xFF,
            2 => value & 0xFFFF,
            _ => value,
        };

        var isUnsigned = type.Name is not null && type.Name.Contains("unsigned", StringComparison.Ordinal);
        return isUnsigned ? $"{value} (0x{value:x8})" : $"{(int)value} (0x{value:x8})";
    }

    // Skip compiler-generated / mangled helper locals to match the dbghelp path's filtering.
    private static bool IsHidden(string name) =>
        string.IsNullOrEmpty(name) || name.StartsWith("__", StringComparison.Ordinal);

    // Global symbol records carry more toolchain noise than locals: precompiled-header markers
    // (__@@_PchSym_), CRT section anchors (__xc_a/__xi_a), string-literal statics ($SG.../??_C),
    // and C++-mangled names. Filter those the way the map-file path filtered its own noise, so the
    // Globals pane shows the program's real variables.
    private static bool IsHiddenGlobal(string name) =>
        string.IsNullOrEmpty(name) ||
        name.StartsWith("__", StringComparison.Ordinal) ||
        name.StartsWith("$", StringComparison.Ordinal) ||
        name.StartsWith("?", StringComparison.Ordinal) ||
        name.StartsWith(".", StringComparison.Ordinal) ||
        name.Contains("_PchSym_", StringComparison.Ordinal);
}
