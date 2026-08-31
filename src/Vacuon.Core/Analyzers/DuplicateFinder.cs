using System.Security.Cryptography;
using Vacuon.Core.Index;

namespace Vacuon.Core.Analyzers;

/// <summary>One file in a duplicate group.</summary>
public sealed record DuplicateFile(
    int EntryIndex,
    string Path,
    long Bytes,
    long BytesOnDisk,
    DateTime LastWrite,
    int NameCount)
{
    /// <summary>
    /// True when the file is reachable under more than one name.
    /// <para>
    /// Deleting one name of a hardlinked file frees nothing: NTFS charges the content once
    /// and only releases it when the last name goes. Counting such a copy as recoverable is
    /// how a duplicate finder promises gigabytes that never arrive.
    /// </para>
    /// </summary>
    public bool IsHardLinked => NameCount > 1;
}

/// <summary>
/// A set of files with identical content.
/// <para>
/// One of them is the <see cref="Keeper"/>, always, and it is not in
/// <see cref="Redundant"/>. That is structural rather than a rule the UI is asked to
/// remember: there is no way to hold this object and get back a list containing every copy,
/// so "delete all of them" is not a state the model can represent.
/// </para>
/// </summary>
public sealed class DuplicateGroup
{
    internal DuplicateGroup(long bytes, IReadOnlyList<DuplicateFile> files, DuplicateFile keeper)
    {
        Bytes = bytes;
        Files = files;
        Keeper = keeper;

        var redundant = new List<DuplicateFile>(files.Count - 1);
        long recoverable = 0;

        foreach (DuplicateFile file in files)
        {
            if (ReferenceEquals(file, keeper)) continue;
            redundant.Add(file);

            // Only what the disk would actually give back.
            if (!file.IsHardLinked) recoverable += file.BytesOnDisk;
        }

        Redundant = redundant;
        RecoverableBytes = recoverable;
    }

    /// <summary>Logical size shared by every file in the group.</summary>
    public long Bytes { get; }

    public IReadOnlyList<DuplicateFile> Files { get; }

    /// <summary>The copy that stays. Never null, and never part of <see cref="Redundant"/>.</summary>
    public DuplicateFile Keeper { get; }

    public IReadOnlyList<DuplicateFile> Redundant { get; }

    /// <summary>
    /// What removing every redundant copy would really free, hardlinked copies excluded.
    /// </summary>
    public long RecoverableBytes { get; }

    public int CopyCount => Files.Count;
}

/// <summary>How to pick the copy that stays.</summary>
public enum KeepPreference
{
    /// <summary>The one written first. The original, in the usual case.</summary>
    Oldest,
    /// <summary>The one written last.</summary>
    Newest,
    /// <summary>The shallowest path, then the shortest name.</summary>
    ShallowestPath,
}

public sealed record DuplicateOptions
{
    /// <summary>Files smaller than this are ignored. Defaults to 4 KiB.</summary>
    public long MinimumBytes { get; init; } = 4096;

    public KeepPreference Keep { get; init; } = KeepPreference.Oldest;

    /// <summary>
    /// Compare the surviving candidates byte by byte instead of trusting the full hash.
    /// <para>
    /// Off by default. A SHA-256 collision between two files that also share a size has
    /// never been produced by accident, but the PRD asks for the option and it costs a
    /// second read, so it is the caller's call rather than a decision made for them.
    /// </para>
    /// </summary>
    public bool VerifyByteForByte { get; init; }
}

public sealed record DuplicateReport(
    IReadOnlyList<DuplicateGroup> Groups,
    long BytesRead,
    int FilesHashed,
    int UnreadableFiles)
{
    public int GroupCount => Groups.Count;

    /// <summary>Total the disk would give back, hardlinked copies already excluded.</summary>
    public long RecoverableBytes
    {
        get
        {
            long total = 0;
            foreach (DuplicateGroup group in Groups) total += group.RecoverableBytes;
            return total;
        }
    }

    /// <summary>
    /// Copies that are identical but whose removal frees nothing, because the content is
    /// reachable under another name. Reported separately so the headline figure stays true.
    /// </summary>
    public int HardLinkedCopies
    {
        get
        {
            int count = 0;

            foreach (DuplicateGroup group in Groups)
                foreach (DuplicateFile file in group.Redundant)
                    if (file.IsHardLinked) count++;

            return count;
        }
    }
}

/// <summary>
/// Finds files with identical content, in four stages, reading as little as possible.
/// <para>
/// Stage 1 is free: every size is already in the index, so grouping by size touches no
/// disk at all. Only survivors reach stage 2 (first 8 KiB), stage 3 (last 8 KiB) and
/// finally stage 4, the whole file.
/// </para>
/// <para>
/// <b>Stage 1 filters far less than it looks like it should</b>, and the sampled stages are
/// not an optimisation on top of it — they are where the work actually happens. Measured on
/// a real 2.49 M file volume: 1.63 M files fall under the 4 KiB minimum, and of the 857 k
/// left, <b>769 k still share a size with something else</b>. Sizes cluster hard, because
/// the things that fill a disk are produced by the same encoders, compilers and installers.
/// So stage 1 costs 58 ms and rules out about a tenth of the eligible files; what it really
/// buys is the shape of the problem — 16 KiB sampled per candidate instead of the 102 GiB
/// those candidates hold.
/// </para>
/// <para>
/// The verdict is always the full-file hash. The sampled stages exist to avoid reading
/// terabytes, never to decide anything: two different 9 GB renders from the same camera
/// share a size, a header and a tail, and calling them identical on that evidence is
/// exactly the false positive this milestone is not allowed to produce.
/// </para>
/// <para>
/// SHA-256 rather than the BLAKE3 the PRD names. BLAKE3 has no implementation in the base
/// class library, and the alternative was a third-party dependency in the one project that
/// has none. In the sampled stages the cost is the seek, not the hash.
/// </para>
/// </summary>
public sealed class DuplicateFinder
{
    private const int SampleBytes = 8 * 1024;

    /// <summary>How much is asked of the file system at a time when a whole file is read.</summary>
    private const int ReadBufferBytes = 1024 * 1024;

    /// <summary>Files at or below this size are read once and hashed whole.</summary>
    private const int SmallFileCutoff = SampleBytes * 2;

    /// <summary>
    /// Runs stage 1 alone and reports what the rest would cost.
    /// <para>
    /// Free, because every size is already in the index, and worth surfacing before the
    /// reading starts: on a real 2.49 M file volume this comes back with 769 k candidates
    /// holding 102 GiB. A "find duplicates" button that quietly starts reading that is a
    /// button nobody can consent to.
    /// </para>
    /// </summary>
    public DuplicateScope Scope(VolumeIndex index, DuplicateOptions? options = null)
    {
        options ??= new DuplicateOptions();

        List<List<int>> candidates = Stage1(index, options, out int files, out int candidateFiles);

        long candidateBytes = 0;
        foreach (List<int> bucket in candidates)
            candidateBytes += index.Entries[bucket[0]].LogicalSize * bucket.Count;

        return new DuplicateScope(files, candidateFiles, candidates.Count, candidateBytes);
    }

    /// <summary>Groups by size using only the index. Touches no file content.</summary>
    private static List<List<int>> Stage1(VolumeIndex index, DuplicateOptions options,
                                          out int eligibleFiles, out int candidateFiles)
    {
        var bySize = new Dictionary<long, List<int>>();

        FileEntry[] entries = index.Entries;
        eligibleFiles = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;

            // ⚠️ A file that lives in the cloud is not read here, and this is not a nicety.
            // OneDrive's Files On-Demand leaves a placeholder that reports its full size and
            // holds almost nothing; opening it to read makes Windows fetch the whole thing.
            // A duplicate search over a synced folder would quietly download every file it
            // touched — an app whose entire job is freeing space, filling the disk instead,
            // and over somebody's connection.
            if ((entry.Flags & EntryFlags.CloudPlaceholder) != 0) continue;

            long size = entry.LogicalSize;
            if (size < options.MinimumBytes) continue;

            eligibleFiles++;

            if (!bySize.TryGetValue(size, out List<int>? bucket))
                bySize[size] = bucket = [];

            bucket.Add(i);
        }

        var candidates = new List<List<int>>();
        candidateFiles = 0;

        foreach (KeyValuePair<long, List<int>> pair in bySize)
        {
            if (pair.Value.Count < 2) continue;
            candidates.Add(pair.Value);
            candidateFiles += pair.Value.Count;
        }

        return candidates;
    }

    public DuplicateReport Find(VolumeIndex index,
                                DuplicateOptions? options = null,
                                IProgress<DuplicateProgress>? progress = null,
                                CancellationToken cancellationToken = default)
    {
        options ??= new DuplicateOptions();

        List<List<int>> candidates = Stage1(index, options, out _, out int candidateFiles);

        FileEntry[] entries = index.Entries;

        progress?.Report(new DuplicateProgress(0, candidateFiles, 0));

        // ---- stages 2 to 4 ----
        var groups = new List<DuplicateGroup>();
        var state = new ScanState();

        int done = 0;

        foreach (List<int> sameSize in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long size = entries[sameSize[0]].LogicalSize;

            foreach (List<int> confirmed in Confirm(index, sameSize, size, options, state, cancellationToken))
                groups.Add(Build(index, confirmed, size, options.Keep));

            done += sameSize.Count;
            progress?.Report(new DuplicateProgress(done, candidateFiles, groups.Count));
        }

        // Biggest recoverable first: that is the order someone acting on this wants.
        groups.Sort(static (a, b) => b.RecoverableBytes.CompareTo(a.RecoverableBytes));

        return new DuplicateReport(groups, state.BytesRead, state.FilesHashed, state.Unreadable);
    }

    /// <summary>Narrows one same-size bucket down to the sets that really match.</summary>
    private static List<List<int>> Confirm(VolumeIndex index, List<int> sameSize, long size,
                                           DuplicateOptions options, ScanState state,
                                           CancellationToken cancellationToken)
    {
        // A file this small is read whole by the first sample, so the sampled stages would
        // read it twice to learn nothing new.
        bool smallEnoughToHashWhole = size <= SmallFileCutoff;

        List<List<int>> buckets = [sameSize];

        if (!smallEnoughToHashWhole)
        {
            buckets = Split(index, buckets, size, Stage.Head, state, cancellationToken);
            buckets = Split(index, buckets, size, Stage.Tail, state, cancellationToken);
        }

        buckets = Split(index, buckets, size, Stage.Whole, state, cancellationToken);

        if (options.VerifyByteForByte)
            buckets = VerifyBytes(index, buckets, state, cancellationToken);

        return buckets;
    }

    private enum Stage { Head, Tail, Whole }

    /// <summary>
    /// Re-partitions each bucket by the hash of one stage, dropping singletons.
    /// <para>
    /// The files are hashed <b>several at a time</b>, and that is worth doing because this
    /// stage is not waiting on the processor. Measured on a real C:, reading and hashing the
    /// same 24.7 GiB: 677 MiB/s on one thread, 1,265 on four, 1,634 on eight, and nothing
    /// more at twelve. A single thread spends most of its life with one read outstanding
    /// against a device that will happily serve several.
    /// </para>
    /// <para>
    /// ⚠️ The work is flattened across <b>all</b> the buckets before it is handed out. A
    /// bucket is a group of files that share a size, and most of them hold two or three —
    /// parallelising inside one would leave nine threads idle.
    /// </para>
    /// <para>
    /// ⚠️ <b>The answer does not depend on the order the threads finish.</b> Hashes land in an
    /// array at the position their file had, and the grouping is then built by walking that
    /// array from the start, exactly as the sequential version walked the bucket. Which copy
    /// ends up first in a group decides nothing on its own — the keeper is chosen by age or
    /// depth — but a result that shuffled between runs would be a result nobody could check.
    /// </para>
    /// </summary>
    private static List<List<int>> Split(VolumeIndex index, List<List<int>> buckets, long size,
                                         Stage stage, ScanState state,
                                         CancellationToken cancellationToken)
    {
        // Paths first, on this thread. Building one walks the parent chain through the
        // shared entry array and name blob, and nothing here is going to be the place that
        // finds out whether that is safe from several threads at once.
        int total = 0;
        foreach (List<int> bucket in buckets) total += bucket.Count;

        if (total == 0) return [];

        var paths = new string[total];
        int at = 0;

        foreach (List<int> bucket in buckets)
            foreach (int entry in bucket)
                paths[at++] = index.GetFullPath(entry);

        var hashes = new string?[total];

        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = HashingThreads,
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, total, parallel, i => hashes[i] = Hash(paths[i], size, stage, state));

        var next = new List<List<int>>();
        at = 0;

        foreach (List<int> bucket in buckets)
        {
            var byHash = new Dictionary<string, List<int>>(bucket.Count, StringComparer.Ordinal);

            foreach (int entry in bucket)
            {
                string? hash = hashes[at++];
                if (hash is null) continue;   // unreadable: never grouped, never guessed at

                if (!byHash.TryGetValue(hash, out List<int>? same))
                    byHash[hash] = same = [];

                same.Add(entry);
            }

            foreach (List<int> same in byHash.Values)
                if (same.Count > 1) next.Add(same);
        }

        return next;
    }

    /// <summary>
    /// How many files are read at once.
    /// <para>
    /// Eight, because that is where the measurement stopped improving on the machine this
    /// was written on — twelve threads read no faster than eight, and a number chosen from
    /// the core count would have picked twelve. Capped by the core count anyway, so a
    /// two-core machine does not open eight files to fight over one drive.
    /// </para>
    /// </summary>
    private static readonly int HashingThreads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));

    private static string? Hash(string path, long size, Stage stage, ScanState state)
    {
        try
        {
            // ⚠️ The buffer size is load-bearing. With bufferSize: 0 the hash pulled from the
            // file in whatever small chunks it asked for, and the whole search ran at
            // 405 MiB/s against a device measured at 677 on one thread — a third of the
            // time spent on the round trip rather than the bytes. A megabyte at a time is
            // what closed that gap.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                              ReadBufferBytes, FileOptions.SequentialScan);

            using var sha = SHA256.Create();

            if (stage == Stage.Whole)
            {
                byte[] whole = sha.ComputeHash(stream);
                state.AddBytes(size);
                state.CountHashed();
                return Convert.ToHexString(whole);
            }

            var buffer = new byte[SampleBytes];

            if (stage == Stage.Tail)
            {
                long from = Math.Max(0, size - SampleBytes);
                stream.Seek(from, SeekOrigin.Begin);
            }

            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            state.AddBytes(read);

            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            // A file we cannot read is not a file we may call a duplicate.
            state.CountUnreadable();
            return null;
        }
    }

    /// <summary>
    /// Last resort for the paranoid: compares the survivors byte by byte.
    /// </summary>
    private static List<List<int>> VerifyBytes(VolumeIndex index, List<List<int>> buckets,
                                               ScanState state, CancellationToken cancellationToken)
    {
        var next = new List<List<int>>();

        foreach (List<int> bucket in buckets)
        {
            var confirmed = new List<int> { bucket[0] };
            string first = index.GetFullPath(bucket[0]);

            for (int i = 1; i < bucket.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (SameBytes(first, index.GetFullPath(bucket[i]), state))
                    confirmed.Add(bucket[i]);
            }

            if (confirmed.Count > 1) next.Add(confirmed);
        }

        return next;
    }

    private static bool SameBytes(string left, string right, ScanState state)
    {
        try
        {
            using var a = File.OpenRead(left);
            using var b = File.OpenRead(right);

            if (a.Length != b.Length) return false;

            var bufferA = new byte[64 * 1024];
            var bufferB = new byte[64 * 1024];

            while (true)
            {
                int readA = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                int readB = b.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);

                state.AddBytes(readA + readB);

                if (readA != readB) return false;
                if (readA == 0) return true;

                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.CountUnreadable();
            return false;
        }
    }

    private static DuplicateGroup Build(VolumeIndex index, List<int> entries, long size,
                                        KeepPreference keep)
    {
        var files = new List<DuplicateFile>(entries.Count);

        foreach (int entry in entries)
        {
            ref FileEntry e = ref index.Entries[entry];

            files.Add(new DuplicateFile(
                entry,
                index.GetFullPath(entry),
                size,
                index.GetSizeOnDisk(entry),
                e.LastWrite,
                e.HardLinkCount));
        }

        return new DuplicateGroup(size, files, Choose(files, keep));
    }

    /// <summary>
    /// Picks the copy that stays. A hardlinked copy is never chosen as the one to remove
    /// when an ordinary one is available, because removing it frees nothing.
    /// </summary>
    internal static DuplicateFile Choose(IReadOnlyList<DuplicateFile> files, KeepPreference keep)
    {
        DuplicateFile best = files[0];

        for (int i = 1; i < files.Count; i++)
        {
            if (Better(files[i], best, keep)) best = files[i];
        }

        return best;
    }

    private static bool Better(DuplicateFile candidate, DuplicateFile current, KeepPreference keep)
    {
        // Keeping the hardlinked copy is free; keeping an ordinary one instead would ask the
        // user to delete something that returns no space.
        if (candidate.IsHardLinked != current.IsHardLinked) return candidate.IsHardLinked;

        return keep switch
        {
            KeepPreference.Newest => candidate.LastWrite > current.LastWrite,
            KeepPreference.ShallowestPath => Depth(candidate.Path) < Depth(current.Path)
                || (Depth(candidate.Path) == Depth(current.Path)
                    && candidate.Path.Length < current.Path.Length),
            _ => candidate.LastWrite < current.LastWrite,
        };
    }

    private static int Depth(string path)
    {
        int depth = 0;
        foreach (char c in path) if (c == '\\') depth++;
        return depth;
    }

    /// <summary>
    /// The running tally, now written by several threads: every field moves through
    /// <see cref="Interlocked"/>. Bytes lost to a torn increment would be bytes the report
    /// claims it did not read.
    /// </summary>
    private sealed class ScanState
    {
        private long _bytesRead;
        private int _filesHashed;
        private int _unreadable;

        public long BytesRead => Interlocked.Read(ref _bytesRead);
        public int FilesHashed => Volatile.Read(ref _filesHashed);
        public int Unreadable => Volatile.Read(ref _unreadable);

        public void AddBytes(long bytes) => Interlocked.Add(ref _bytesRead, bytes);
        public void CountHashed() => Interlocked.Increment(ref _filesHashed);
        public void CountUnreadable() => Interlocked.Increment(ref _unreadable);
    }
}

public readonly record struct DuplicateProgress(int FilesDone, int FilesTotal, int GroupsFound);

/// <summary>
/// What stage 1 found, and therefore what stages 2 to 4 would cost.
/// <para>
/// <see cref="CandidateBytes"/> is the worst case — the total the candidates hold. The
/// sampled stages usually read a fraction of it, but the fraction is not knowable in
/// advance, and quoting a hopeful number before an operation that can take an hour is
/// exactly the kind of estimate this app does not make.
/// </para>
/// </summary>
public readonly record struct DuplicateScope(
    int EligibleFiles,
    int CandidateFiles,
    int SizeBuckets,
    long CandidateBytes);
