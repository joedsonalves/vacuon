using Vacuon.Core.Index;

namespace Vacuon.Core.Analyzers;

/// <summary>How one folder changed between two scans.</summary>
public sealed record FolderChange(string Folder, long BeforeBytes, long AfterBytes, int FileDelta)
{
    public long ByteDelta => AfterBytes - BeforeBytes;

    public bool Grew => ByteDelta > 0;
    public bool Appeared => BeforeBytes == 0 && AfterBytes > 0;
    public bool Vanished => AfterBytes == 0 && BeforeBytes > 0;
}

public sealed record SnapshotComparison(
    DateTime BeforeTakenAtUtc,
    DateTime AfterTakenAtUtc,
    IReadOnlyList<FolderChange> Changes,
    long BeforeBytes,
    long AfterBytes,
    int BeforeFiles,
    int AfterFiles)
{
    public TimeSpan Elapsed => AfterTakenAtUtc - BeforeTakenAtUtc;

    public long ByteDelta => AfterBytes - BeforeBytes;
    public int FileDelta => AfterFiles - BeforeFiles;

    /// <summary>Folders that grew, biggest first.</summary>
    public IReadOnlyList<FolderChange> Grew
    {
        get
        {
            var grew = new List<FolderChange>();
            foreach (FolderChange change in Changes) if (change.Grew) grew.Add(change);

            grew.Sort(static (a, b) => b.ByteDelta.CompareTo(a.ByteDelta));
            return grew;
        }
    }

    /// <summary>Folders that shrank, biggest drop first.</summary>
    public IReadOnlyList<FolderChange> Shrank
    {
        get
        {
            var shrank = new List<FolderChange>();
            foreach (FolderChange change in Changes) if (change.ByteDelta < 0) shrank.Add(change);

            shrank.Sort(static (a, b) => a.ByteDelta.CompareTo(b.ByteDelta));
            return shrank;
        }
    }
}

/// <summary>
/// What changed between two scans of the same volume.
/// <para>
/// This is the answer to "my disk had eighty gigabytes free last week and has twenty now, and
/// I have not done anything". A single scan can only rank what is big; comparing two says what
/// <b>moved</b>, which is a different and usually more useful question — the ninety-gigabyte
/// folder that was already there last month is not the culprit.
/// </para>
/// <para>
/// <b>Folders, not files.</b> Comparing file by file across two scans means matching identity
/// across renames, moves and MFT record reuse, and getting that wrong produces a list where
/// the same file is reported both deleted and created. Folder totals do not have that problem
/// and answer the question people actually ask, which is <i>where</i> the space went.
/// </para>
/// <para>
/// The direction is kept. A volume that gained space is as worth seeing as one that lost it,
/// and reporting only growth would hide a cleanup that worked.
/// </para>
/// </summary>
public static class SnapshotDiff
{
    /// <summary>
    /// Folders that moved by less than this are left out. Every folder on a live volume
    /// changes by a few kilobytes; a list that includes all of them buries the answer.
    /// </summary>
    public const long MinimumDelta = 10L * 1024 * 1024;

    public static SnapshotComparison Compare(LoadedSnapshot before, LoadedSnapshot after,
                                             long minimumDelta = MinimumDelta)
    {
        Dictionary<string, (long Bytes, int Files)> first = FolderTotals(before.Index);
        Dictionary<string, (long Bytes, int Files)> second = FolderTotals(after.Index);

        var changes = new List<FolderChange>();

        foreach ((string folder, (long bytes, int files)) in second)
        {
            first.TryGetValue(folder, out (long Bytes, int Files) then);

            if (Math.Abs(bytes - then.Bytes) < minimumDelta) continue;

            changes.Add(new FolderChange(folder, then.Bytes, bytes, files - then.Files));
        }

        // Folders that are gone entirely appear in the first scan and not the second, so the
        // loop above never sees them. Leaving them out would report a deletion as nothing
        // having happened, which is the same failure as a monitor calling a lost interval quiet.
        foreach ((string folder, (long bytes, int files)) in first)
        {
            if (second.ContainsKey(folder)) continue;
            if (bytes < minimumDelta) continue;

            changes.Add(new FolderChange(folder, bytes, 0, -files));
        }

        changes.Sort(static (a, b) => Math.Abs(b.ByteDelta).CompareTo(Math.Abs(a.ByteDelta)));

        return new SnapshotComparison(
            before.TakenAtUtc, after.TakenAtUtc, changes,
            Total(first), Total(second), Files(first), Files(second));
    }

    /// <summary>
    /// Bytes and file counts per folder, counting only what sits directly in each one.
    /// <para>
    /// Direct contents, not the whole subtree. Rolled-up totals make a change appear at every
    /// level above where it happened, so the answer to "where did it go" becomes a chain of
    /// parents ending at the drive letter.
    /// </para>
    /// </summary>
    private static Dictionary<string, (long Bytes, int Files)> FolderTotals(VolumeIndex index)
    {
        var byIndex = new Dictionary<int, (long Bytes, int Files)>();

        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;

            int parent = (int)entry.ParentIndex;

            byIndex.TryGetValue(parent, out (long Bytes, int Files) current);
            byIndex[parent] = (current.Bytes + entry.LogicalSize, current.Files + 1);
        }

        // Keyed by path, because record numbers are reused: a folder deleted and another
        // created can land on the same MFT record between two scans, and matching by number
        // would report one folder's contents as the other's growth.
        var byPath = new Dictionary<string, (long Bytes, int Files)>(StringComparer.OrdinalIgnoreCase);

        foreach ((int folder, (long bytes, int files)) in byIndex)
        {
            string path = index.GetFullPath(folder);
            if (path.Length == 0) continue;

            byPath.TryGetValue(path, out (long Bytes, int Files) current);
            byPath[path] = (current.Bytes + bytes, current.Files + files);
        }

        return byPath;
    }

    private static long Total(Dictionary<string, (long Bytes, int Files)> totals)
    {
        long sum = 0;
        foreach ((_, (long bytes, _)) in totals) sum += bytes;
        return sum;
    }

    private static int Files(Dictionary<string, (long Bytes, int Files)> totals)
    {
        int sum = 0;
        foreach ((_, (_, int files)) in totals) sum += files;
        return sum;
    }
}
