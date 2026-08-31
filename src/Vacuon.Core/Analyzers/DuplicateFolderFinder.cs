using System.Security.Cryptography;
using System.Text;
using Vacuon.Core.Index;

namespace Vacuon.Core.Analyzers;

/// <summary>One folder that has an identical twin somewhere else.</summary>
public sealed record DuplicateFolder(int EntryIndex, string Path, int FileCount, long Bytes, DateTime Modified);

/// <summary>
/// A set of folders whose contents are identical, file for file.
/// </summary>
public sealed class DuplicateFolderGroup
{
    public DuplicateFolderGroup(IReadOnlyList<DuplicateFolder> folders, KeepPreference keep)
    {
        Folders = folders;

        Keeper = keep switch
        {
            KeepPreference.Newest => folders.MaxBy(f => f.Modified)!,
            KeepPreference.ShallowestPath => folders.MinBy(f => f.Path.Count(c => c == '\\'))!,
            _ => folders.MinBy(f => f.Modified)!,
        };

        Redundant = [.. folders.Where(f => !ReferenceEquals(f, Keeper))];
        Bytes = folders[0].Bytes;
        FileCount = folders[0].FileCount;
        RecoverableBytes = Bytes * Redundant.Count;
    }

    public IReadOnlyList<DuplicateFolder> Folders { get; }

    /// <summary>The copy that stays. Never appears in <see cref="Redundant"/>.</summary>
    public DuplicateFolder Keeper { get; }

    public IReadOnlyList<DuplicateFolder> Redundant { get; }

    /// <summary>Bytes one copy holds.</summary>
    public long Bytes { get; }

    public int FileCount { get; }

    public long RecoverableBytes { get; }

    public int CopyCount => Folders.Count;
}

public sealed record DuplicateFolderReport(
    IReadOnlyList<DuplicateFolderGroup> Groups,
    int FoldersConsidered,
    int FoldersHashed,
    long BytesRead)
{
    public int GroupCount => Groups.Count;

    public long RecoverableBytes
    {
        get
        {
            long total = 0;
            foreach (DuplicateFolderGroup group in Groups) total += group.RecoverableBytes;
            return total;
        }
    }
}

/// <summary>
/// Whole folders that are identical, rather than the four thousand files inside them
/// (PRD F4.8).
/// <para>
/// The answer to a backup taken twice. Finding the files one by one is correct and useless:
/// nobody works through four thousand rows to conclude that one folder should go. What is
/// wanted is the folder.
/// </para>
/// <para>
/// ⚠️ <b>Identical means identical, and one extra file breaks it.</b> Two folders are the
/// same only when the set of relative paths matches exactly and every file's content matches
/// its counterpart's. A folder with one more file in it is not a duplicate of the other —
/// it <em>contains</em> the other, which is a different question and not this one.
/// </para>
/// <para>
/// ⚠️ <b>The name of a file matters here, unlike in the file search.</b> Two folders holding
/// the same bytes under different names are not the same folder: a program opening
/// <c>config.json</c> in one of them would find nothing in the other. It is the tree that is
/// being compared, and a tree is its paths as much as its contents.
/// </para>
/// </summary>
public static class DuplicateFolderFinder
{
    /// <summary>Folders smaller than this are not worth reporting as duplicates of each other.</summary>
    public const long MinimumBytes = 1024 * 1024;

    /// <summary>
    /// A folder has to hold at least one file to be worth comparing.
    /// <para>
    /// ⚠️ It was two, on the reasoning that a folder holding one file is really just that
    /// file and the file search covers it. The tests said otherwise, and they were right:
    /// <c>raiz\meio\fundo\arquivo.bin</c> duplicated is three levels of folder somebody
    /// wants to remove in one go, and reporting the file instead answers a smaller question.
    /// The byte minimum is what keeps the noise out.
    /// </para>
    /// </summary>
    public const int MinimumFiles = 1;

    public static DuplicateFolderReport Find(VolumeIndex index,
                                             long minimumBytes = MinimumBytes,
                                             KeepPreference keep = KeepPreference.Oldest,
                                             IProgress<DuplicateProgress>? progress = null,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        index.BuildSubtreeSizes();

        // Stage 1, and it costs nothing: two folders can only be identical if they hold the
        // same number of files and the same number of bytes. Both come from the index.
        var byShape = new Dictionary<(int Files, long Bytes), List<int>>();
        int considered = 0;

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry entry = ref index.Entries[i];
            if (!entry.IsInUse || !entry.IsDirectory) continue;
            if (i == index.RootIndex) continue;

            long bytes = index.GetSubtreeSize(i);
            if (bytes < minimumBytes) continue;

            int files = CountFiles(index, i);
            if (files < MinimumFiles) continue;

            considered++;

            var shape = (files, bytes);
            if (!byShape.TryGetValue(shape, out List<int>? bucket)) byShape[shape] = bucket = [];
            bucket.Add(i);
        }

        var candidates = new List<List<int>>();
        foreach (KeyValuePair<(int, long), List<int>> pair in byShape)
            if (pair.Value.Count > 1) candidates.Add(pair.Value);

        int total = 0;
        foreach (List<int> bucket in candidates) total += bucket.Count;

        progress?.Report(new DuplicateProgress(0, total, 0));

        var groups = new List<DuplicateFolderGroup>();
        int hashed = 0;
        long bytesRead = 0;

        foreach (List<int> bucket in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bySignature = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            foreach (int folder in bucket)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? signature = SignatureOf(index.GetFullPath(folder), ref bytesRead, cancellationToken);
                hashed++;

                if (signature is null) continue;

                if (!bySignature.TryGetValue(signature, out List<int>? same)) bySignature[signature] = same = [];
                same.Add(folder);
            }

            foreach (List<int> same in bySignature.Values)
            {
                if (same.Count < 2) continue;

                var folders = new List<DuplicateFolder>(same.Count);

                foreach (int entry in same)
                {
                    folders.Add(new DuplicateFolder(entry, index.GetFullPath(entry),
                                                    CountFiles(index, entry), index.GetSubtreeSize(entry),
                                                    index.Entries[entry].LastWrite));
                }

                groups.Add(new DuplicateFolderGroup(folders, keep));
            }

            progress?.Report(new DuplicateProgress(hashed, total, groups.Count));
        }

        groups = DropNested(groups);
        groups.Sort((a, b) => b.RecoverableBytes.CompareTo(a.RecoverableBytes));

        return new DuplicateFolderReport(groups, considered, hashed, bytesRead);
    }

    /// <summary>
    /// The signature of a tree: every relative path with the hash of what is in it.
    /// <para>
    /// Sorted, because two identical folders can be walked in different orders and a
    /// signature that depended on the order would call them different. Null when anything
    /// inside could not be read — a folder that is partly unreadable is a folder nobody may
    /// call a duplicate.
    /// </para>
    /// </summary>
    public static string? SignatureOf(string folder, ref long bytesRead, CancellationToken cancellationToken)
    {
        if (folder.Length == 0 || !Directory.Exists(folder)) return null;

        var lines = new List<string>();
        string root = folder.TrimEnd('\\');

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relative = file[root.Length..].TrimStart('\\');

                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                              1024 * 1024, FileOptions.SequentialScan);

                bytesRead += stream.Length;

                // The name goes into the signature beside the content, because a tree is its
                // paths as much as its bytes.
                lines.Add(relative.ToLowerInvariant() + "|" + Convert.ToHexString(SHA256.HashData(stream)));
            }

            // Empty folders count: two trees that differ only by an empty directory are not
            // the same tree, and nothing above would have noticed.
            foreach (string sub in Directory.EnumerateDirectories(root, "*", options))
                lines.Add(sub[root.Length..].TrimStart('\\').ToLowerInvariant() + "|dir");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lines.Sort(StringComparer.Ordinal);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    /// <summary>
    /// Drops groups whose folders all sit inside folders of another group.
    /// <para>
    /// Two identical trees have identical subtrees all the way down, so without this the
    /// report says the same thing once per level — and the deepest, least useful phrasing
    /// of it is the one somebody scrolls to first.
    /// </para>
    /// </summary>
    private static List<DuplicateFolderGroup> DropNested(List<DuplicateFolderGroup> groups)
    {
        var kept = new List<DuplicateFolderGroup>(groups.Count);

        // Biggest first, so a parent is always considered before its children.
        groups.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        var covered = new List<string>();

        foreach (DuplicateFolderGroup group in groups)
        {
            bool inside = group.Folders.All(f => covered.Any(
                c => f.Path.StartsWith(c, StringComparison.OrdinalIgnoreCase)));

            if (inside) continue;

            kept.Add(group);
            foreach (DuplicateFolder folder in group.Folders) covered.Add(folder.Path.TrimEnd('\\') + "\\");
        }

        return kept;
    }

    private static int CountFiles(VolumeIndex index, int folder)
    {
        int count = 0;
        var pending = new Stack<int>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            int current = pending.Pop();

            foreach (int child in index.GetChildren(current))
            {
                if (index.Entries[child].IsDirectory) pending.Push(child);
                else count++;
            }
        }

        return count;
    }
}
