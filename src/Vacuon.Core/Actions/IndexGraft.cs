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

    /// <summary>What every folder in the graft is read with.</summary>
    private static readonly EnumerationOptions Children = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Plants <paramref name="path"/> and everything under it into <paramref name="index"/>.
    /// </summary>
    public static GraftResult AddTree(VolumeIndex index, string path) =>
        AddTree(index, path, FileIdentity.RecordNumberOf);

    /// <param name="recordOf">
    /// Where record numbers come from. The file system, always, outside the tests — which
    /// hand in small numbers so the index they build does not have to be the size of a real
    /// MFT to hold them.
    /// </param>
    internal static GraftResult AddTree(VolumeIndex index, string path, Func<string, long> recordOf)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (string.IsNullOrEmpty(path)) return new GraftResult(0, false);
        if (index.Strategy != ScanStrategy.Mft) return new GraftResult(0, false);

        string full = Path.GetFullPath(path);

        // A copy to another volume changes nothing on this one.
        string? root = Path.GetPathRoot(full);
        if (root is null || !root.StartsWith(index.Volume.Root, StringComparison.OrdinalIgnoreCase))
            return new GraftResult(0, false);

        FileSystemInfo top = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!top.Exists) return new GraftResult(0, false);

        string? parentPath = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parentPath)) return new GraftResult(0, false);

        // The destination folder itself may be younger than the scan — a copy into a folder
        // created on the spot. MoveTarget adopts the whole chain, or gives up.
        int parent = MoveTarget.Locate(index, parentPath);
        if (parent < 0) return new GraftResult(0, false);

        // The one lookup by path in the whole graft. Everything below it is matched by name
        // against the folder it sits in — see Plant for why that matters.
        int known = index.FindEntry(full);

        int added = 0;
        bool complete = Plant(index, top, parent, known, recordOf, ref added);

        return new GraftResult(added, complete);
    }

    /// <summary>One entry and, when it is a folder, everything inside it.</summary>
    /// <param name="known">The entry the index already holds for this item, or -1 when it holds none.</param>
    private static bool Plant(VolumeIndex index, FileSystemInfo item, int parent, int known,
                              Func<string, long> recordOf, ref int added)
    {
        if (added >= MaxEntries) return false;

        int entry = known >= 0 ? known : Adopt(index, item, parent, recordOf);
        if (entry < 0) return false;

        added++;
        if (item is not DirectoryInfo folder) return true;

        // ⚠️ Matched by name against a table read once per folder, never with FindEntry per
        // child. FindEntry walks the child index, and every AddFile drops that index — so a
        // lookup per child rebuilt it per child, across the whole volume: 55 ms a rebuild on
        // 3.7 M records. Measured on a fresh scan of a real C:, robocopy copied 5,000 files
        // in 1.6 s and the graft then held the window for 258 s; it now takes 0.18 s. A
        // folder adopted just now has nothing below it in the index to find, so it gets no
        // table at all.
        Dictionary<string, int>? existing = known >= 0 ? ChildrenOf(index, entry) : null;

        bool complete = true;

        // ⚠️ The disk is not holding still for this. A folder that was there when its parent
        // was read can be gone, or renamed, by the time it is opened — and an enumerator that
        // cannot open its folder throws. That exception used to travel all the way out of the
        // copy and take the app with it: crash.log on my machine, 9 September, a
        // DirectoryNotFoundException for the "backend" folder of a project I had just copied,
        // thrown from here. That folder is a plain folder today, in a project renamed since,
        // so the likeliest story is that it moved while the old graft spent minutes on the
        // copy. A folder that cannot be read is now a graft that is incomplete, which the
        // caller already says out loud.
        try
        {
            // FileSystemInfo, not paths: the enumerator has already read each entry's size,
            // times and attributes, and asking for them again was a second call per file.
            foreach (FileSystemInfo child in folder.EnumerateFileSystemInfos("*", Children))
            {
                int childKnown = existing is not null && existing.TryGetValue(child.Name, out int found) ? found : -1;
                if (!Plant(index, child, entry, childKnown, recordOf, ref added)) complete = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return complete;
    }

    /// <summary>The entries the index holds directly under a folder, by name.</summary>
    private static Dictionary<string, int> ChildrenOf(VolumeIndex index, int folder)
    {
        var children = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (int child in index.GetChildren(folder))
        {
            if (index.Entries[child].IsInUse) children[index.GetName(child).ToString()] = child;
        }

        return children;
    }

    /// <summary>A new entry for something the index has never seen, at its real record number.</summary>
    private static int Adopt(VolumeIndex index, FileSystemInfo item, int parent, Func<string, long> recordOf)
    {
        long record = recordOf(item.FullName);
        if (record <= 0 || record >= index.Entries.Length) return -1;

        if (!ClaimRecord(index, (int)record, recordOf)) return -1;

        ReadOnlySpan<char> name = item.Name;
        if (name.Length == 0) return -1;

        if (item is not FileInfo file) return index.AddDirectory((int)record, parent, name);

        return index.AddFile((int)record, parent, name, file.Length,
                             AllocatedFor(file.Length, (int)index.Volume.BytesPerCluster),
                             file.LastWriteTimeUtc, file.CreationTimeUtc,
                             (file.Attributes & FileAttributes.Hidden) != 0,
                             (file.Attributes & FileAttributes.System) != 0);
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
    private static bool ClaimRecord(VolumeIndex index, int record, Func<string, long> recordOf, int depth = 0)
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
        long current = recordOf(occupant);
        if (current == record) return false;

        // Read before the entry is freed. The occupant's path was built from this very
        // chain, so if it still exists under another record its folder is this entry —
        // and knowing that spares a FindEntry, which would rebuild the child index once
        // per displaced file.
        int folder = (int)index.Entries[record].ParentIndex;

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
        if (depth < 2) Refile(index, occupant, folder, current, recordOf, depth + 1);

        return true;
    }

    /// <summary>Puts a file that moved records back into the index, at the record it has now.</summary>
    private static void Refile(VolumeIndex index, string path, int parent, long record,
                               Func<string, long> recordOf, int depth)
    {
        if (record <= 0 || record >= index.Entries.Length) return;
        if (parent < 0 || parent >= index.Entries.Length || !index.Entries[parent].IsInUse) return;
        if (!ClaimRecord(index, (int)record, recordOf, depth)) return;

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
