using Vacuon.Core.Index;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Analyzers;

/// <summary>One folder worth compressing, and what it would probably give back.</summary>
public sealed record CompressionCandidate(
    string Folder,
    int FileCount,
    long Bytes,
    string Category,
    double AssumedRatio)
{
    /// <summary>
    /// What NTFS compression would probably reclaim.
    /// <para>
    /// <b>Probably.</b> This is the one number in the report that was not measured, and it
    /// carries a name that says so: it is bytes times a ratio typical of the file type, not a
    /// trial compression of these particular files. Whoever reads it is told as much.
    /// </para>
    /// </summary>
    public long EstimatedSaving => (long)(Bytes * AssumedRatio);
}

public sealed record CompressionReport(
    IReadOnlyList<CompressionCandidate> Candidates,
    int AlreadyCompressed,
    long AlreadyCompressedBytes)
{
    public long EstimatedSaving
    {
        get
        {
            long total = 0;
            foreach (CompressionCandidate candidate in Candidates) total += candidate.EstimatedSaving;
            return total;
        }
    }
}

/// <summary>
/// Folders full of files NTFS would compress well, that are not compressed.
/// <para>
/// The point is that this reclaims space <b>without deleting anything</b>. Everything else in
/// this application asks someone to give a file up; compression asks them to give up a little
/// CPU on read. For a folder of logs, source, or CSV exports that nobody opens twice a day,
/// that is close to free.
/// </para>
/// <para>
/// <b>The saving is estimated and labelled estimated.</b> Measuring it honestly would mean
/// compressing the files to find out, which is the operation itself — so the number is bytes
/// times a ratio typical of the type, and both the property name and the interface say so.
/// The alternative, printing a confident figure derived from a table, is precisely the habit
/// that once had this app report 758 GiB on a 476 GiB volume.
/// </para>
/// <para>
/// <b>Nothing here compresses anything.</b> It reports; the act is <c>compact.exe</c> or the
/// folder's own properties dialog, run by a person who read the estimate.
/// </para>
/// </summary>
public static class CompressionCandidates
{
    /// <summary>
    /// Folders holding less than this are not worth the entry. Compressing a folder that
    /// gives back four megabytes is work for nothing.
    /// </summary>
    public const long MinimumFolderBytes = 50L * 1024 * 1024;

    /// <summary>
    /// How well each category typically compresses.
    /// <para>
    /// Anything already compressed by its own format — video, pictures, archives, installers
    /// — is <b>absent</b> rather than listed with a small ratio. NTFS compression on a JPEG
    /// costs CPU on every read and gives back nothing, and offering it with an honest 2%
    /// beside it would still be offering it.
    /// </para>
    /// </summary>
    private static readonly (string Category, double Ratio)[] Ratios =
    [
        (FileCategories.Log, 0.75),
        (FileCategories.Code, 0.65),
        (FileCategories.Document, 0.45),
        (FileCategories.Database, 0.55),
        (FileCategories.Build, 0.55),
        (FileCategories.Executable, 0.35),
    ];

    public static double? RatioFor(string category)
    {
        foreach ((string name, double ratio) in Ratios)
            if (name == category) return ratio;

        return null;
    }

    public static CompressionReport Find(VolumeIndex index, long minimumFolderBytes = MinimumFolderBytes)
    {
        // Folder index to what it holds, per category. A folder is only a candidate when one
        // category dominates it: compressing a mixed folder of video and logs spends the
        // whole cost on the video and reclaims what the logs would have given anyway.
        var byFolder = new Dictionary<int, Dictionary<string, (int Count, long Bytes)>>();

        int alreadyCompressed = 0;
        long alreadyCompressedBytes = 0;

        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;

            if ((entry.Flags & EntryFlags.Compressed) != 0)
            {
                alreadyCompressed++;
                alreadyCompressedBytes += entry.LogicalSize;
                continue;
            }

            // Sparse and encrypted files cannot be NTFS-compressed at all, and a cloud
            // placeholder has no local bytes to compress — offering any of them would be
            // offering something Windows will refuse.
            if ((entry.Flags & (EntryFlags.Sparse | EntryFlags.Encrypted | EntryFlags.CloudPlaceholder)) != 0)
                continue;

            string category = FileCategories.Of(index.GetName(i));
            if (RatioFor(category) is null) continue;

            int parent = (int)entry.ParentIndex;

            if (!byFolder.TryGetValue(parent, out Dictionary<string, (int, long)>? counts))
                byFolder[parent] = counts = [];

            counts.TryGetValue(category, out (int Count, long Bytes) current);
            counts[category] = (current.Count + 1, current.Bytes + entry.LogicalSize);
        }

        var candidates = new List<CompressionCandidate>();

        foreach ((int folder, Dictionary<string, (int Count, long Bytes)> counts) in byFolder)
        {
            string best = string.Empty;
            long bestBytes = 0;
            int bestCount = 0;

            foreach ((string category, (int count, long bytes)) in counts)
            {
                if (bytes <= bestBytes) continue;

                best = category;
                bestBytes = bytes;
                bestCount = count;
            }

            if (bestBytes < minimumFolderBytes) continue;

            string path = index.GetFullPath(folder);
            if (path.Length == 0) continue;

            // Windows compresses its own files as it sees fit, and a folder under it is not
            // this app's to suggest touching. The same list that governs every other action.
            if (ProtectedPaths.Check(path).IsProtected) continue;

            candidates.Add(new CompressionCandidate(path, bestCount, bestBytes, best, RatioFor(best)!.Value));
        }

        candidates.Sort(static (a, b) => b.EstimatedSaving.CompareTo(a.EstimatedSaving));

        return new CompressionReport(candidates, alreadyCompressed, alreadyCompressedBytes);
    }
}
