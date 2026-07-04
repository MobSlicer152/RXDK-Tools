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
        var label = Describe(type, address, memory, out var expandable);
        variables.Append(name, label, expandable, expandBase);
    }

    /// <summary>
    /// Renders a resolved value for a single row: an aggregate/array shows a type summary and is
    /// marked expandable; a scalar/pointer is formatted from its bytes. Modifiers (const/volatile) are
    /// peeled first so a const struct or pointer is recognized rather than shown as a raw dword.
    /// </summary>
    private string Describe(PdbType type, nuint address, KitMemoryAccess memory, out bool expandable)
    {
        type = _pdb.Types.Peel(type.TypeIndex);
        switch (type.Kind)
        {
            case PdbTypeKind.Array:
            {
                var elem = type.ReferentType != 0 ? _pdb.Types.Resolve(type.ReferentType) : null;
                expandable = type.ElementCount > 0;
                return elem?.Name is { Length: > 0 } en ? $"{en}[{type.ElementCount}]" : $"array[{type.ElementCount}]";
            }

            case PdbTypeKind.Struct:
            case PdbTypeKind.Class:
            case PdbTypeKind.Union:
                expandable = type.Members.Count > 0;
                return type.Name is { Length: > 0 } tn ? tn : $"{{{type.ByteSize} bytes}}";

            default:
                expandable = false;
                return FormatScalar(type, address, memory);
        }
    }

    /// <summary>
    /// Evaluates a watch/hover expression: a bare symbol, a register, or a member/index/deref chain
    /// (<c>a.b</c>, <c>p-&gt;field</c>, <c>arr[2]</c>) over the current frame's locals or the program's
    /// globals. This is the managed replacement for dbghelp's expression engine and works on every
    /// platform. Returns false with a short <paramref name="error"/> token on any failure.
    /// </summary>
    internal bool TryEvaluate(string expression, ref XbdmContext context, KitMemoryAccess memory, out string value, out string? error)
    {
        value = string.Empty;
        error = null;

        if (!ExpressionPath.TryParse(expression, out var baseName, out var accessors))
        {
            error = "badExpression";
            return false;
        }

        // A bare register name (EAX, ESP, ...) with no member/index chain reads straight from context.
        if (accessors.Count == 0 && TryReadRegister(baseName, ref context, out var register))
        {
            value = $"0x{register:x8}";
            return true;
        }

        if (!TryResolveBase(baseName, ref context, out var address, out var typeIndex))
        {
            error = "symbolNotFound";
            return false;
        }

        var evaluator = new TypeEvaluator(_pdb.Types, a => memory.ReadDword((nuint)a));
        if (!evaluator.TryWalk(address, typeIndex, accessors, out var finalAddress, out var finalType, out var walkError))
        {
            error = walkError ?? "evalFailed";
            return false;
        }

        value = Describe(finalType, (nuint)finalAddress, memory, out _);
        return true;
    }

    /// <summary>Resolves a base symbol name to its address and type: a frame local first, then a global.</summary>
    private bool TryResolveBase(string name, ref XbdmContext context, out nuint address, out uint typeIndex)
    {
        address = 0;
        typeIndex = 0;

        var frame = FindFrame(context.Eip);
        if (frame is not null)
        {
            var local = frame.Locals.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                        ?? frame.Locals.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (local is not null)
            {
                address = (nuint)((long)context.Ebp + local.FrameOffset);
                typeIndex = local.TypeIndex;
                return true;
            }
        }

        foreach (var global in _pdb.EnumerateGlobals())
        {
            if (global.IsPublic)
                continue;
            if (!string.Equals(global.Name, name, StringComparison.Ordinal) &&
                !string.Equals(CleanGlobalName(global.Name), name, StringComparison.Ordinal))
                continue;
            if (!TryGlobalAddress(global, out address))
                continue;
            typeIndex = global.TypeIndex;
            return true;
        }

        return false;
    }

    private static bool TryReadRegister(string name, ref XbdmContext context, out uint value)
    {
        switch (name.ToUpperInvariant())
        {
            case "EAX": value = context.Eax; return true;
            case "EBX": value = context.Ebx; return true;
            case "ECX": value = context.Ecx; return true;
            case "EDX": value = context.Edx; return true;
            case "ESI": value = context.Esi; return true;
            case "EDI": value = context.Edi; return true;
            case "EBP": value = context.Ebp; return true;
            case "ESP": value = context.Esp; return true;
            case "EIP": value = context.Eip; return true;
            case "EFLAGS": value = context.EFlags; return true;
            default: value = 0; return false;
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
