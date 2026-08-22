using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Core.Preview;

namespace Vacuon.Core.Analyzers;

/// <summary>One video that shares its footage with others.</summary>
public sealed record SimilarVideo(
    int EntryIndex,
    string Path,
    long SizeBytes,
    VideoFingerprint Fingerprint)
{
    public TimeSpan Duration => Fingerprint.Duration;

    public string? ResolutionLabel => Fingerprint.Height <= 0 ? null : $"{Fingerprint.Height}p";
}

/// <summary>
/// Videos that are the same footage.
/// <para>
/// The one that stays is the biggest, which on video is very nearly always the least
/// compressed — the same rule the picture finder uses, for the same reason: of two copies of
/// something, the one worth keeping is the one that has lost the least.
/// </para>
/// </summary>
public sealed class VideoGroup
{
    public VideoGroup(IReadOnlyList<SimilarVideo> videos)
    {
        Videos = videos;

        SimilarVideo keeper = videos[0];
        foreach (SimilarVideo video in videos)
            if (video.SizeBytes > keeper.SizeBytes) keeper = video;

        Keeper = keeper;

        var others = new List<SimilarVideo>(videos.Count - 1);
        long recoverable = 0;

        foreach (SimilarVideo video in videos)
        {
            if (ReferenceEquals(video, keeper)) continue;

            others.Add(video);
            recoverable += video.SizeBytes;
        }

        Others = others;
        RecoverableBytes = recoverable;
    }

    public IReadOnlyList<SimilarVideo> Videos { get; }

    /// <summary>The copy that stays. It is never in <see cref="Others"/>.</summary>
    public SimilarVideo Keeper { get; }

    /// <summary>Everything else in the group. Deleting all of these still leaves one.</summary>
    public IReadOnlyList<SimilarVideo> Others { get; }

    public long RecoverableBytes { get; }

    /// <summary>How far the furthest member sits from the keeper, in bits of 64.</summary>
    public int Spread
    {
        get
        {
            int worst = 0;

            foreach (SimilarVideo video in Others)
            {
                int? distance = VideoSimilarity.Distance(Keeper.Fingerprint, video.Fingerprint);
                if (distance is { } d && d > worst) worst = d;
            }

            return worst;
        }
    }
}

public sealed record VideoSimilarReport(
    IReadOnlyList<VideoGroup> Groups,
    int Fingerprinted,
    int Unreadable,
    int TooShort,
    int FromCache,
    bool Cancelled)
{
    public long RecoverableBytes
    {
        get
        {
            long total = 0;
            foreach (VideoGroup group in Groups) total += group.RecoverableBytes;
            return total;
        }
    }
}

public readonly record struct VideoScope(int Candidates, long CandidateBytes);

public sealed record VideoDuplicateOptions
{
    public int Threshold { get; init; } = VideoSimilarity.FrameThreshold;

    /// <summary>
    /// Videos smaller than this are ignored. Four mebibytes is a few seconds of anything
    /// worth reclaiming space over, and it keeps sprite sheets and animated stingers — the
    /// video equivalent of the card faces that forced a size floor on pictures — out.
    /// </summary>
    public long MinimumBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>Whether to read and write the on-disk fingerprint cache.</summary>
    public bool UseCache { get; init; } = true;
}

/// <summary>
/// Finds videos that are the same footage: re-encodes, resizes, copies under another name.
/// <para>
/// Structurally apart from the picture finder rather than folded into it, because the
/// evidence is different in kind. A picture is one fingerprint; a video is several, and two
/// videos are only comparable when their running times agree. Sharing the plumbing would have
/// meant a hash field that means one thing for pictures and another for videos, and grouping
/// that has to ask which it is holding.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoDuplicateFinder
{
    /// <summary>
    /// Counts what a run would read, without reading it.
    /// <para>
    /// Worth showing for the same reason the picture scope is: decoding frames out of every
    /// video on a disk is minutes, and a button that starts that with no number beside it is
    /// a button nobody agreed to.
    /// </para>
    /// </summary>
    public VideoScope Scope(VolumeIndex index, VideoDuplicateOptions? options = null)
    {
        options ??= new VideoDuplicateOptions();

        int candidates = 0;
        long bytes = 0;

        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;
            if (entry.LogicalSize < options.MinimumBytes) continue;
            if (!VideoSimilarity.IsVideo(index.GetName(i))) continue;

            candidates++;
            bytes += entry.LogicalSize;
        }

        return new VideoScope(candidates, bytes);
    }

    public VideoSimilarReport Find(VolumeIndex index,
                                   VideoDuplicateOptions? options = null,
                                   IProgress<DuplicateProgress>? progress = null,
                                   CancellationToken cancellationToken = default)
    {
        options ??= new VideoDuplicateOptions();

        var candidates = new List<int>();
        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;
            if (entry.LogicalSize < options.MinimumBytes) continue;
            if (!VideoSimilarity.IsVideo(index.GetName(i))) continue;

            candidates.Add(i);
        }

        progress?.Report(new DuplicateProgress(0, candidates.Count, 0));

        FingerprintCache? cache = options.UseCache ? new FingerprintCache() : null;

        var prints = new List<SimilarVideo>(candidates.Count);
        int unreadable = 0, tooShort = 0, fromCache = 0;
        bool cancelled = false;

        for (int i = 0; i < candidates.Count; i++)
        {
            // Stopping keeps what was read. Grouping the partial set costs milliseconds, and
            // offering a stop button that then shows nothing is worse than not offering one.
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            int entry = candidates[i];
            string path = index.GetFullPath(entry);
            if (path.Length == 0) continue;

            VideoFingerprint? print = Fingerprint(path, cache, ref fromCache, ref unreadable, ref tooShort);

            if (print is not null)
                prints.Add(new SimilarVideo(entry, path, index.Entries[entry].LogicalSize, print));

            // Every fourth: decoding five frames is slow enough that a coarser interval
            // leaves the bar looking stuck.
            if ((i & 3) == 0) progress?.Report(new DuplicateProgress(i, candidates.Count, 0));
        }

        cache?.Save();

        List<VideoGroup> groups = Group(prints, options.Threshold);
        groups.Sort(static (a, b) => b.RecoverableBytes.CompareTo(a.RecoverableBytes));

        return new VideoSimilarReport(groups, prints.Count, unreadable, tooShort, fromCache, cancelled);
    }

    private static VideoFingerprint? Fingerprint(string path, FingerprintCache? cache,
                                                 ref int fromCache, ref int unreadable, ref int tooShort)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(path);
            if (!info.Exists) { unreadable++; return null; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            unreadable++;
            return null;
        }

        // Probed even on a cache hit. Reading the property store is milliseconds — the cost
        // this cache exists to avoid is decoding frames, not asking Windows how big the
        // video is, and serving a cached entry without it would drop the resolution the
        // screen shows beside each copy.
        MediaInfo media = MediaProbe.Read(path);

        if (media.Duration is not { } duration || duration < VideoSimilarity.MinimumDuration)
        {
            tooShort++;
            return null;
        }

        if (cache?.Get(path, info.Length, info.LastWriteTimeUtc, out TimeSpan cachedDuration) is { } hashes
            && hashes.Length >= VideoSimilarity.MinimumFrames)
        {
            fromCache++;

            return new VideoFingerprint(
                cachedDuration == TimeSpan.Zero ? duration : cachedDuration,
                (int)(media.Width ?? 0), (int)(media.Height ?? 0), hashes);
        }

        VideoFingerprint? print = VideoSimilarity.Of(path, media);

        if (print is null)
        {
            // No frames, or none with enough contrast to fingerprint. Counted rather than
            // dropped: a report that only says how many were read makes the rest look
            // examined and unique.
            unreadable++;
            return null;
        }

        cache?.Put(path, info.Length, info.LastWriteTimeUtc, print.FrameHashes, print.Duration);

        return print;
    }

    /// <summary>
    /// Clusters fingerprints, re-checking every member against the keeper.
    /// <para>
    /// Against the <b>keeper</b>, not the seed — the correction that came out of the picture
    /// finder, where a group once reported members "14 bits apart" under a threshold of 10
    /// because the measurements had been taken from different centres.
    /// </para>
    /// </summary>
    private static List<VideoGroup> Group(List<SimilarVideo> videos, int threshold)
    {
        var groups = new List<VideoGroup>();
        var taken = new bool[videos.Count];

        for (int i = 0; i < videos.Count; i++)
        {
            if (taken[i]) continue;

            // Indices, not the records themselves. SimilarVideo is a record, so looking one
            // up by value would compare whole fingerprints to find a position already known.
            var members = new List<int> { i };
            taken[i] = true;

            for (int j = i + 1; j < videos.Count; j++)
            {
                if (taken[j]) continue;

                int? distance = VideoSimilarity.Distance(videos[i].Fingerprint, videos[j].Fingerprint);
                if (distance is null || distance > threshold) continue;

                members.Add(j);
                taken[j] = true;
            }

            if (members.Count < 2)
            {
                if (members.Count == 1) taken[i] = true;   // alone, and not worth revisiting
                continue;
            }

            // The copy that stays is the biggest, and the re-check measures from it rather
            // than from wherever the group happened to start. That correction came out of
            // the picture finder, where a group once reported members 14 bits apart under a
            // threshold of 10 because the distances had been taken from different centres.
            int keeper = members[0];
            foreach (int m in members)
                if (videos[m].SizeBytes > videos[keeper].SizeBytes) keeper = m;

            var confirmed = new List<SimilarVideo> { videos[keeper] };

            foreach (int m in members)
            {
                if (m == keeper) continue;

                int? distance = VideoSimilarity.Distance(videos[keeper].Fingerprint, videos[m].Fingerprint);

                if (distance is { } d && d <= threshold) confirmed.Add(videos[m]);
                else taken[m] = false;   // free it to seed or join a group of its own
            }

            if (confirmed.Count >= 2) groups.Add(new VideoGroup(confirmed));
        }

        return groups;
    }
}
