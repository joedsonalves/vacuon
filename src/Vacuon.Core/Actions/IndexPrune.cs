using System.Runtime.InteropServices;
using Vacuon.Core.Index;

namespace Vacuon.Core.Actions;

/// <summary>
/// Takes out of the index what an operation removed from a folder it did not finish with.
/// <para>
/// A permanent delete of a folder is a purge and then the removal of what is left. When one
/// file inside is held open, the purge takes everything else and the folder stays — so the
/// item is a failure, and the index used to keep the whole folder, every file the disk had
/// just lost included. The list went on showing them, the totals went on counting them, and
/// the only way to find out was to click one.
/// </para>
/// <para>
/// The disk decides, entry by entry, and a folder that is gone takes its whole subtree with
/// it without a question per file below it.
/// </para>
/// </summary>
public static class IndexPrune
{
    /// <summary>
    /// Frees <paramref name="entry"/>, or whatever below it is no longer on the disk.
    /// </summary>
    /// <returns>What left the index, measured from the entries themselves.</returns>
    public static Removal Vanished(VolumeIndex index, int entry)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (entry < 0 || entry >= index.Entries.Length || !index.Entries[entry].IsInUse) return default;

        var gone = new List<int>();
        var pending = new Stack<int>();
        pending.Push(entry);

        // Read in full before anything is freed: the walk goes through the child index, and
        // freeing drops it.
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            string path = index.GetFullPath(current);
            if (path.Length == 0) continue;

            if (IsGone(path))
            {
                gone.Add(current);
                continue;
            }

            if (!index.Entries[current].IsDirectory) continue;

            foreach (int child in index.GetChildren(current))
                if (index.Entries[child].IsInUse) pending.Push(child);
        }

        return gone.Count == 0 ? default : index.MarkDeleted(CollectionsMarshal.AsSpan(gone));
    }

    /// <summary>
    /// True only when the file system says the path does not exist.
    /// <para>
    /// ⚠️ Not <c>File.Exists</c>, which answers false for a file it was refused a look at, and
    /// would have the index forget something that is still sitting on the disk. Only "not
    /// found" counts as gone; anything else leaves the entry where it is.
    /// </para>
    /// </summary>
    private static bool IsGone(string path)
    {
        try
        {
            File.GetAttributes(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
