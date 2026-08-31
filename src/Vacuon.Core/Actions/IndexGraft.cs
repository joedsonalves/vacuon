using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

/// <summary>What planting a tree into a live index managed to do.</summary>
public readonly record struct GraftResult(int Added, bool Complete)
{
    /// <summary>Nothing was planted, so whatever the caller did is not on screen yet.</summary>
    public bool IsEmpty => Added == 0;
}

/// <summary>
/// Puts something that has just appeared on disk into the index that is already on screen,
/// without rescanning the volume.
/// <para>
/// A copy writes files the scan has never heard of, so the list, the tree and the totals
/// went on showing the volume as it was before — the copied folder simply was not there
/// until the next full scan. Walking the few hundred entries that were just created costs
/// milliseconds, against seconds for the whole volume, and the two halves of the screen
/// agree again straight away.
/// </para>
/// <para>
/// ⚠️ Every entry goes in <b>at its real MFT record number</b>, read from the file system
/// itself, never at an invented slot. That is what keeps a later journal delta about that
/// record landing on this entry instead of on a stranger, and it is why this refuses to do
/// anything at all outside an MFT scan: in a walk the entry numbers are positions in that
/// walk and a record number means nothing.
/// </para>
/// </summary>
public static class IndexGraft
{
    /// <summary>
    /// Where this stops and lets the next scan sort it out.
    /// <para>
    /// One handle is opened per entry to read its record number, so the cost is real, and a
    /// copy of a source tree with a hundred thousand small files would be a visible freeze
    /// on a window that has just finished telling somebody the copy was done. Past this the
    /// graft reports itself incomplete and the caller says the list is behind the disk —
    /// which is the truth, and better than a list that is behind without saying so.
    /// </para>
    /// </summary>
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Plants <paramref name="path"/> and everything under it into <paramref name="index"/>.
    /// </summary>
    public static GraftResult AddTree(VolumeIndex index, string path)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (string.IsNullOrEmpty(path)) return new GraftResult(0, false);
        if (index.Strategy != ScanStrategy.Mft) return new GraftResult(0, false);

        string full = Path.GetFullPath(path);

        // A copy to another volume changes nothing on this one.
        string? root = Path.GetPathRoot(full);
        if (root is null || !root.StartsWith(index.Volume.Root, StringComparison.OrdinalIgnoreCase))
            return new GraftResult(0, false);

        bool isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full)) return new GraftResult(0, false);

        string? parentPath = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parentPath)) return new GraftResult(0, false);

        // The destination folder itself may be younger than the scan — a copy into a folder
        // created on the spot. MoveTarget adopts the whole chain, or gives up.
        int parent = MoveTarget.Locate(index, parentPath);
        if (parent < 0) return new GraftResult(0, false);

        int added = 0;
        bool complete = Plant(index, full, parent, isDirectory, ref added);

        return new GraftResult(added, complete);
    }

    /// <summary>One entry and, when it is a folder, everything inside it.</summary>
    private static bool Plant(VolumeIndex index, string path, int parent, bool isDirectory, ref int added)
    {
        if (added >= MaxEntries) return false;

        int entry = Adopt(index, path, parent, isDirectory);
        if (entry < 0) return false;

        added++;
        if (!isDirectory) return true;

        // Enumerated with attributes in hand, so telling a folder from a file costs nothing
        // extra — the entry the enumerator already read carries them.
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };

        bool complete = true;

        foreach (string child in Directory.EnumerateFileSystemEntries(path, "*", options))
        {
            bool childIsDirectory = Directory.Exists(child);
            if (!Plant(index, child, entry, childIsDirectory, ref added)) complete = false;
        }

        return complete;
    }

    /// <summary>
    /// The entry for a path: the one already there, or a new one at its real record number.
    /// </summary>
    private static int Adopt(VolumeIndex index, string path, int parent, bool isDirectory)
    {
        int found = index.FindEntry(path);
        if (found >= 0) return found;

        long record = FileIdentity.RecordNumberOf(path);
        if (record <= 0 || record >= index.Entries.Length) return -1;

        if (!ClaimRecord(index, (int)record)) return -1;

        ReadOnlySpan<char> name = Path.GetFileName(path.AsSpan().TrimEnd('\\'));
        if (name.Length == 0) return -1;

        if (isDirectory) return index.AddDirectory((int)record, parent, name);

        var info = new FileInfo(path);

        return index.AddFile((int)record, parent, name, info.Length,
                             AllocatedFor(info.Length, (int)index.Volume.BytesPerCluster),
                             info.LastWriteTimeUtc, info.CreationTimeUtc,
                             (info.Attributes & FileAttributes.Hidden) != 0,
                             (info.Attributes & FileAttributes.System) != 0);
    }

    /// <summary>
    /// Frees a record whose occupant is not on the disk any more, so the new owner can have it.
    /// <para>
    /// ⚠️ NTFS recycles MFT records, and it recycles them <b>fast</b>: measured here, a folder
    /// created seconds after a scan of a real C: landed on the record of a snapshot file this
    /// very app had written and deleted. So "the slot is taken" is not the rare disagreement
    /// it looks like — it is the ordinary case, and refusing on it meant nothing new was ever
    /// adopted.
    /// </para>
    /// <para>
    /// The disk decides, and it decides on evidence: the entry sitting in the slot is asked
    /// for its path, and only if nothing is there any more is it marked deleted — through the
    /// same <c>MarkDeleted</c> a real deletion uses, so the volume totals follow. An occupant
    /// that <b>does</b> still exist is a genuine disagreement between index and disk, and this
    /// walks away from it rather than papering over it with a plausible-looking entry.
    /// </para>
    /// </summary>
    private static bool ClaimRecord(VolumeIndex index, int record, int depth = 0)
    {
        if (!index.Entries[record].IsInUse) return true;

        // A directory carries its whole subtree, and MarkDeleted takes the subtree with it.
        // Reclaiming one to make room for a new folder could drop thousands of entries that
        // are perfectly real. Not worth the room: the graft gives up on this one instead.
        if (index.Entries[record].IsDirectory) return false;

        string occupant = index.GetFullPath(record);
        if (occupant.Length == 0) return false;

        // ⚠️ Not "does something with that name still exist" — "is that thing still this
        // record". Measured on a real C:, this app's own snapshot file is the one that keeps
        // turning up in the way: it is rewritten under the same name every scan, so the path
        // is always there while the record behind it changes. Asking the file system which
        // record that path is now is the only question with an answer.
        long current = FileIdentity.RecordNumberOf(occupant);
        if (current == record) return false;

        index.MarkDeleted(record);
        if (index.Entries[record].IsInUse) return false;

        // And the occupant goes back where it really lives now. Without this the volume
        // total quietly loses its bytes — measured at 397 MB gone off the total of a real
        // C: the first time this ran, because the file in the way was a 397 MB snapshot
        // that had simply been rewritten. An index that shrinks by a third of a gigabyte
        // without anything being deleted is exactly the sort of number this app is not
        // allowed to show.
        // Depth-limited: the file being put back can itself land on a stale record, and
        // that record's occupant on another. Two levels covers what a real disk produced
        // here; deeper than that the bytes wait for the next scan rather than the graft
        // walking a chain of its own making.
        if (depth < 2) Refile(index, occupant, current, depth + 1);

        return true;
    }

    /// <summary>Puts a file that moved records back into the index, at the record it has now.</summary>
    private static void Refile(VolumeIndex index, string path, long record, int depth)
    {
        if (record <= 0 || record >= index.Entries.Length) return;
        if (!ClaimRecord(index, (int)record, depth)) return;

        string? parentPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parentPath)) return;

        int parent = index.FindEntry(parentPath);
        if (parent < 0) return;

        var info = new FileInfo(path);
        if (!info.Exists) return;

        index.AddFile((int)record, parent, Path.GetFileName(path.AsSpan()), info.Length,
                      AllocatedFor(info.Length, (int)index.Volume.BytesPerCluster),
                      info.LastWriteTimeUtc, info.CreationTimeUtc,
                      (info.Attributes & FileAttributes.Hidden) != 0,
                      (info.Attributes & FileAttributes.System) != 0);
    }

    /// <summary>
    /// Bytes on disk, rounded up to a whole cluster.
    /// <para>
    /// ⚠️ This is a <b>computed</b> figure, not a measured one, and it is the only place in
    /// the graft where that is true. A copied file is a plain file: not compressed, not
    /// sparse, not resident in its own record, so its clusters are its length rounded up.
    /// The one thing that must not happen is a freshly copied file reporting zero on disk
    /// and quietly shrinking the volume's own total.
    /// </para>
    /// </summary>
    private static long AllocatedFor(long length, int clusterSize)
    {
        if (clusterSize <= 0) return length;

        long clusters = (length + clusterSize - 1) / clusterSize;
        return clusters * clusterSize;
    }
}
