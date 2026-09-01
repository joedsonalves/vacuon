using System.Globalization;
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
/// <summary>What a folder search would have to read, worked out before it starts.</summary>
public readonly record struct DuplicateFolderScope(int FoldersConsidered, int Candidates, long CandidateBytes);

public static class DuplicateFolderFinder
{
    /// <summary>
    /// Runs the two free stages and stops, so the cost of the third can be shown before
    /// anybody commits to it.
    /// <para>
    /// Measured on a real C: with 3,66 M entries: 27.169 folders considered, 11.176 of them
    /// still candidates after stage 1 holding 420 GiB, and 7.491 holding 110 GiB after
    /// stage 2 — about 5 s of CPU, no disk. The figure it returns is the one after both,
    /// because that is what the read will actually be.
    /// </para>
    /// </summary>
    public static DuplicateFolderScope Scope(VolumeIndex index, long minimumBytes = MinimumBytes,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        List<List<int>> buckets = Buckets(index, minimumBytes, out int considered, cancellationToken);

        int candidates = 0;
        long bytes = 0;

        foreach (List<int> bucket in buckets)
        {
            candidates += bucket.Count;
            foreach (int folder in bucket) bytes += index.GetSubtreeSize(folder);
        }

        return new DuplicateFolderScope(considered, candidates, bytes);
    }

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

        List<List<int>> buckets = Buckets(index, minimumBytes, out int considered, cancellationToken);

        int total = 0;
        foreach (List<int> bucket in buckets) total += bucket.Count;

        progress?.Report(new DuplicateProgress(0, total, 0));

        return Read(index, buckets, total, considered, keep, progress, cancellationToken);
    }

    /// <summary>The two stages that cost nothing, shared by <see cref="Scope"/> and the search.</summary>
    private static List<List<int>> Buckets(VolumeIndex index, long minimumBytes, out int considered,
                                           CancellationToken cancellationToken)
    {
        index.BuildSubtreeSizes();

        // Stage 1, and it costs nothing: two folders can only be identical if they hold the
        // same number of files and the same number of bytes. Both come from the index.
        var byShape = new Dictionary<(int Files, long Bytes), List<int>>();
        considered = 0;

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

        // Stage 2, and it also costs nothing: the shape of the tree — every relative path
        // with the size of what is at it — read straight out of the index.
        //
        // ⚠️ This is where the run stops being unusable. Measured on a real C: with 3,66 M
        // entries: stage 1 leaves 11.176 candidate folders holding 420 GiB, and stage 2
        // takes 3,3 s of pure CPU to cut that to 7.491 folders and 110 GiB. Two folders
        // agreeing on count and total bytes is common — installers, node_modules, copies of
        // a project at different points — and almost none of them agree file by file.
        //
        // It trusts the index the same way stage 1 already does; it does not widen that
        // trust, it just asks the index a much sharper question before touching the disk.
        var buckets = new List<List<int>>();

        foreach (KeyValuePair<(int, long), List<int>> pair in byShape)
        {
            if (pair.Value.Count < 2) continue;

            var byTree = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            foreach (int folder in pair.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string shape = ShapeOf(index, folder);
                if (!byTree.TryGetValue(shape, out List<int>? same)) byTree[shape] = same = [];
                same.Add(folder);
            }

            foreach (List<int> same in byTree.Values)
                if (same.Count > 1) buckets.Add(same);
        }

        return buckets;
    }

    private static DuplicateFolderReport Read(VolumeIndex index, List<List<int>> buckets, int total,
                                              int considered, KeepPreference keep,
                                              IProgress<DuplicateProgress>? progress,
                                              CancellationToken cancellationToken)
    {
        // Stage 3 reads. Every folder's signature is independent of every other's, so they
        // are read at once rather than one after another, at the same ceiling the file
        // search measured for the same kind of work.
        //
        // The gain is not the processor — SHA-256 is not what this waits on. It is queue
        // depth: the candidates on a real C: hold 4,2 M files, most of them small, and one
        // thread spends its life with a single read outstanding against a device that will
        // serve several. Measured over 620 folders on each side, 5.179 MiB against 5.223:
        // <b>152,3 s on one thread and 80,7 s on eight</b>, so 34 MiB/s against 65.
        //
        // ⚠️ That comparison is easy to get wrong and was, twice, before it was right.
        // Candidate folders run from 1 MiB to tens of GiB, so handing each thread count a
        // different slice measures the slices: the first attempt gave the eight-thread arm
        // 9.236 MiB and the one-thread arm 5.999 and reported eight as the slower. What
        // works is interleaving blocks over one population with a ceiling on folder size,
        // so neither arm can win or lose on the luck of one enormous folder.
        var signatures = new string?[total];
        var flat = new List<int>(total);
        foreach (List<int> bucket in buckets) flat.AddRange(bucket);

        var state = new FolderScanState();
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = HashingThreads,
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, total, parallel, i =>
        {
            signatures[i] = SignatureOf(index.GetFullPath(flat[i]), state, cancellationToken);
            state.CountHashed();

            // Reported from inside the loop so a long run has a moving number rather than
            // one that jumps at the end of each bucket.
            progress?.Report(new DuplicateProgress(state.FoldersHashed, total, 0));
        });

        var groups = new List<DuplicateFolderGroup>();
        int hashed = state.FoldersHashed;
        long bytesRead = state.BytesRead;
        int at = 0;

        foreach (List<int> bucket in buckets)
        {
            var bySignature = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            foreach (int folder in bucket)
            {
                string? signature = signatures[at++];
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
        }

        progress?.Report(new DuplicateProgress(hashed, total, groups.Count));

        groups = DropNested(groups);
        groups.Sort((a, b) => b.RecoverableBytes.CompareTo(a.RecoverableBytes));

        return new DuplicateFolderReport(groups, considered, hashed, bytesRead);
    }

    /// <summary>
    /// How many folders are read at once in stage 3. Same ceiling as the file search.
    /// </summary>
    private static readonly int HashingThreads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));

    /// <summary>
    /// The shape of a tree, from the index alone: every relative path with the size at it.
    /// <para>
    /// Not a content signature and never treated as one — two files of the same size are not
    /// the same file, and stage 3 still reads every byte before anything is called a
    /// duplicate. What this rules out is folders that cannot possibly match, which on a real
    /// disk is most of them.
    /// </para>
    /// </summary>
    private static string ShapeOf(VolumeIndex index, int folder)
    {
        var lines = new List<string>();
        Shape(index, folder, string.Empty, lines);
        lines.Sort(StringComparer.Ordinal);

        // Hashed rather than joined: the biggest candidates here hold thousands of files,
        // and keeping every one of those strings alive as a dictionary key is megabytes of
        // nothing.
        using var sha = SHA256.Create();

        foreach (string line in lines)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static void Shape(VolumeIndex index, int folder, string prefix, List<string> lines)
    {
        foreach (int child in index.GetChildren(folder))
        {
            ref FileEntry entry = ref index.Entries[child];

            string name = index.GetName(child).ToString().ToLowerInvariant();
            string relative = prefix.Length == 0 ? name : prefix + "\\" + name;

            if (entry.IsDirectory)
            {
                lines.Add(relative + "|dir");
                Shape(index, child, relative, lines);
            }
            else
            {
                lines.Add(relative + "|" + entry.LogicalSize.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// What stage 3 counts while several threads read at once.
    /// <para>
    /// Through <see cref="Interlocked"/>, for the same reason the file search does it: a
    /// torn increment here is bytes missing from a number the report puts on screen.
    /// </para>
    /// </summary>
    internal sealed class FolderScanState
    {
        private long _bytesRead;
        private int _foldersHashed;

        public long BytesRead => Interlocked.Read(ref _bytesRead);
        public int FoldersHashed => Volatile.Read(ref _foldersHashed);

        public void AddBytes(long bytes) => Interlocked.Add(ref _bytesRead, bytes);
        public void CountHashed() => Interlocked.Increment(ref _foldersHashed);
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
    public static string? SignatureOf(string folder, CancellationToken cancellationToken) =>
        SignatureOf(folder, null, cancellationToken);

    internal static string? SignatureOf(string folder, FolderScanState? state,
                                        CancellationToken cancellationToken)
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

                state?.AddBytes(stream.Length);

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
