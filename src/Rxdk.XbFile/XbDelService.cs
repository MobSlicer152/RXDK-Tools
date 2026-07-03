using Rxdk.Xbdm;
using Rxdk.Xbdm.Managed;

namespace Rxdk.XbFile;

public sealed class XbDelOptions
{
    /// <summary>/f -- clear the read-only attribute before deleting.</summary>
    public bool Force { get; init; }

    /// <summary>/r -- recurse into directories (delete their contents first).</summary>
    public bool Recursive { get; init; }

    /// <summary>/v -- print each deleted file.</summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// Delete files (and, with Recursive, directory trees) from the Xbox target,
/// mirroring the XDK's xbdel. Xbox paths only (xE:\..., xD:\..., etc.); the final
/// path component may contain wildcards.
/// </summary>
public sealed class XbDelService
{
    private readonly XbDelOptions _options;
    private readonly XbConsoleSession _session;

    public XbDelService(XbDelOptions options, XbConsoleSession session)
    {
        _options = options;
        _session = session;
    }

    /// <summary>Delete one path spec. Returns true if at least one entry was deleted.</summary>
    public bool Execute(XbPath path)
    {
        if (!path.IsXbox)
        {
            throw new XbFileException($"xbdel only deletes Xbox files (xE:\\...): {path.Original}");
        }

        var conn = _session.Connection;

        if (path.HasWildcard)
        {
            var deleted = 0;
            foreach (var entry in XbXboxFs.ListDirectory(conn, path).ToList())
            {
                if (entry.Name is "." or "..")
                {
                    continue;
                }
                if (!XbWildcard.IsMatch(path.Name, entry.Name))
                {
                    continue;
                }
                if (DeleteEntry(conn, path.WithName(entry.Name), entry))
                {
                    deleted++;
                }
            }
            if (deleted == 0)
            {
                throw new XbFileException($"No files matched: {path.Original}");
            }
            return true;
        }

        if (!XbXboxFs.TryGetAttributes(conn, path, out var attributes) || attributes is null)
        {
            throw new XbFileException($"File not found: {path.WirePath}");
        }

        return DeleteEntry(conn, path, attributes);
    }

    private bool DeleteEntry(XbdmConnection conn, XbPath path, XbdmDirEntry entry)
    {
        var isDirectory = XbXboxFs.IsDirectory(entry);

        if (isDirectory && !_options.Recursive)
        {
            throw new XbFileException($"Cannot delete a directory without /r: {path.WirePath}");
        }

        if (isDirectory)
        {
            // Delete the directory's contents first, then the directory itself.
            foreach (var child in XbXboxFs.ListDirectory(conn, ChildPath(path, "*")).ToList())
            {
                if (child.Name is "." or "..")
                {
                    continue;
                }
                DeleteEntry(conn, ChildPath(path, child.Name), child);
            }
        }

        ClearReadOnlyIfForced(conn, path, entry);
        conn.Delete(path.WirePath, isDirectory);
        if (_options.Verbose)
        {
            Console.WriteLine($"deleted {path.WirePath}");
        }
        return true;
    }

    private void ClearReadOnlyIfForced(XbdmConnection conn, XbPath path, XbdmDirEntry entry)
    {
        if ((entry.Attributes & XbdmConstants.AttrReadOnly) == 0)
        {
            return;
        }
        if (!_options.Force)
        {
            throw new XbFileException($"{path.WirePath} is read-only (use /f to force).");
        }
        conn.SetFileAttributes(path.WirePath, entry.Attributes & ~XbdmConstants.AttrReadOnly);
    }

    private static XbPath ChildPath(XbPath dir, string childName) =>
        XbPath.Parse("x" + dir.WirePath.TrimEnd('\\') + "\\" + childName);
}
