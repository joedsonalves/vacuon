using System.IO.Enumeration;

namespace Vacuon.Core.Actions;

/// <summary>
/// Junctions, symbolic links and mount points: entries that stand for another place.
/// <para>
/// ⚠️ Anything that walks a tree to change it has to stop at these, and robocopy does not.
/// Measured on 5 October 2026: a <c>/MIR</c> purge of a folder holding a junction erased every
/// file in the folder the junction pointed at, and one aimed at a junction emptied the folder
/// it stood for. <c>/XJ</c> did not stop it, and neither did <c>/SJ /SL</c> — those switches
/// shape the copy, and the purge never reads them.
/// </para>
/// <para>
/// A link is a door, not a room: removing one removes the door. The app makes doors itself —
/// moving a folder to another drive can leave a junction where it was — so a folder about to
/// be deleted is likelier than most to have one inside.
/// </para>
/// </summary>
public static class Links
{
    /// <summary>
    /// Whether the entry at this path is a link itself. What it points at, or whether that
    /// still exists, does not enter into it.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            // Reads the entry, not its target: a link answers with its own attributes.
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0) return false;

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every folder link below <paramref name="folder"/> — junction, directory symlink or
    /// mount point — found without entering any of them.
    /// <para>
    /// Only folders: a file symlink cannot be walked through, and the purge, measured, removes
    /// one without touching the file it names. Other reparse points are not doors either — a
    /// OneDrive folder carries one and is still a folder with its own files in it, so the walk
    /// goes on into it.
    /// </para>
    /// </summary>
    /// <exception cref="IOException">
    /// The walk could not finish. Nobody can then say there is no link in the part it missed,
    /// and the caller has to treat that as "there may be".
    /// </exception>
    public static List<string> FoldersBelow(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // Hidden and system entries count: Windows hides most of the links it makes.
            AttributesToSkip = 0,
            // A folder this process cannot list is one robocopy, started by this process,
            // cannot list either — nothing inside it can be walked through by it.
            IgnoreInaccessible = true,
        };

        var walk = new FileSystemEnumerable<string>(
            folder,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => entry.IsDirectory && IsLink(ref entry),
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => !IsLink(ref entry),
        };

        return [.. walk];
    }

    /// <summary>
    /// Removes a link and only the link. What it points at is not touched.
    /// </summary>
    /// <returns>Whether the link is gone.</returns>
    public static bool Remove(string link)
    {
        try
        {
            // RemoveDirectory takes a junction or a directory symlink away by itself, whatever
            // is behind it — or nothing, for one whose target is gone — and the non-recursive
            // Directory.Delete is exactly that one call. DeleteFile does the same for a file
            // symlink. Which one is read off the link, not off what it points at.
            if ((File.GetAttributes(link) & FileAttributes.Directory) != 0) Directory.Delete(link);
            else File.Delete(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !Path.Exists(link);
        }

        return !Path.Exists(link);
    }

    /// <summary>The same question for an entry already read from the disk.</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.Exists
                && (info.Attributes & FileAttributes.ReparsePoint) != 0
                && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The same question for an entry a walk is standing on. Only a reparse point is opened
    /// to ask, so a walk over ordinary files pays nothing for it.
    /// </summary>
    internal static bool IsLink(ref FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0
        && entry.ToFileSystemInfo().LinkTarget is not null;
}
