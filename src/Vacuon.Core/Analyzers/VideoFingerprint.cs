using System.Runtime.Versioning;
using Vacuon.Core.Preview;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Analyzers;

/// <summary>
/// What a video looks like, as a handful of frame hashes taken across its running time.
/// </summary>
/// <param name="FrameHashes">
/// One dHash per sampled frame, in order. Frames too flat to fingerprint are <b>absent</b>
/// rather than zero — see <see cref="VideoFingerprint.Of"/>.
/// </param>
public sealed record VideoFingerprint(
    TimeSpan Duration,
    int Width,
    int Height,
    ulong[] FrameHashes)
{
    public bool IsUsable => FrameHashes.Length >= VideoSimilarity.MinimumFrames;
}

/// <summary>
/// Groups videos that are the same footage: re-encodes, resizes, copies under another name.
/// <para>
/// A single picture cannot decide this. The shell's thumbnail gives one representative frame,
/// and two unrelated films that open on a dark title card fingerprint identically from it.
/// Sampling across the running time is what separates "the same video, re-encoded" from
/// "both start black".
/// </para>
/// <para>
/// <b>Measured on synthetic footage before any of the numbers below were chosen.</b> The same
/// clip re-encoded at half the resolution and much lower quality came back at distance
/// <b>0</b> on every frame; unrelated footage sat at <b>30 to 37</b> out of 64. Two clips
/// sharing a two-second opening and nothing else measured 32 — because the samples are taken
/// from the middle of the running time, where a video is actually itself.
/// </para>
/// <para>
/// <b>What it does not see.</b> dHash reads luminance, so a re-grade or a hue shift is
/// invisible to it — the same clip recoloured measured 0, which is arguably right and worth
/// knowing. A re-edit, a different cut of the same source, will not group: the sampled moments
/// no longer line up, and that is the intended answer rather than a shortcoming to paper over.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoSimilarity
{
    /// <summary>
    /// How many moments are sampled. Five is what the measurements were taken with: enough
    /// that a shared opening cannot carry a match, few enough that a library of thousands
    /// stays inside a coffee break at roughly 50 ms a file.
    /// </summary>
    public const int SampleCount = 5;

    /// <summary>
    /// Fewer usable frames than this and the video gets no fingerprint at all.
    /// <para>
    /// A fingerprint standing on one or two frames is the single-thumbnail problem again,
    /// wearing more machinery.
    /// </para>
    /// </summary>
    public const int MinimumFrames = 3;

    /// <summary>
    /// How far apart two corresponding frames may be, in bits of 64.
    /// <para>
    /// Ten, against measurements where true matches came in at 0 to 5 and unrelated footage
    /// at 30 or more. The gap is wide enough that the exact figure hardly matters, which is
    /// the point: a threshold picked from the middle of a gap that big is not the thing
    /// holding the result together.
    /// </para>
    /// </summary>
    public const int FrameThreshold = 10;

    /// <summary>
    /// Videos shorter than this are not fingerprinted. Sampling five moments out of three
    /// seconds returns five views of the same instant.
    /// </summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads a video and fingerprints it. Returns null when it cannot be read or is too
    /// featureless to fingerprint honestly.
    /// </summary>
    public static VideoFingerprint? Of(string path, MediaInfo? probed = null)
    {
        MediaInfo info = probed ?? MediaProbe.Read(path);

        if (info.Duration is not { } duration || duration < MinimumDuration) return null;

        VideoReadResult read = VideoFrames.Read(path, duration, SampleCount);

        if (read.Frames.Count == 0) return null;

        var hashes = new List<ulong>(read.Frames.Count);

        foreach (VideoFrame frame in read.Frames)
        {
            // A frame too flat to fingerprint contributes nothing, and is left out rather
            // than recorded as zero. Zero is a hash value like any other: a run of them
            // would make every dark video identical to every other, which is exactly the
            // false positive the contrast gate exists to prevent.
            ulong? hash = PerceptualHash.Compute(
                new ThumbnailBitmap(frame.Width, frame.Height, frame.Bgra32, IsContentThumbnail: true));

            if (hash is not null) hashes.Add(hash.Value);
        }

        if (hashes.Count < MinimumFrames) return null;

        return new VideoFingerprint(duration, (int)(info.Width ?? 0), (int)(info.Height ?? 0), [.. hashes]);
    }

    /// <summary>
    /// How far apart two videos are, as the worst of their corresponding frames — or null
    /// when they are not comparable at all.
    /// <para>
    /// The <b>worst</b> frame, not the average. An average lets four matching frames carry
    /// one that plainly does not, which is how two films that share an opening sequence end
    /// up in the same group.
    /// </para>
    /// </summary>
    public static int? Distance(VideoFingerprint left, VideoFingerprint right)
    {
        if (!left.IsUsable || !right.IsUsable) return null;
        if (!DurationsMatch(left.Duration, right.Duration)) return null;

        int count = Math.Min(left.FrameHashes.Length, right.FrameHashes.Length);
        int worst = 0;

        for (int i = 0; i < count; i++)
        {
            int distance = PerceptualHash.Distance(left.FrameHashes[i], right.FrameHashes[i]);
            if (distance > worst) worst = distance;
        }

        return worst;
    }

    public static bool AreSimilar(VideoFingerprint left, VideoFingerprint right) =>
        Distance(left, right) is { } distance && distance <= FrameThreshold;

    /// <summary>
    /// Whether two running times are close enough to be the same footage.
    /// <para>
    /// This is the counterpart of the aspect-ratio gate that fixed the picture finder: a cheap
    /// property that costs nothing to compare and refuses whole populations of near-misses
    /// before any pixel is weighed. Two videos of visibly different length are not the same
    /// video, whatever their frames look like.
    /// </para>
    /// <para>
    /// One second of slack, or one percent of the longer, whichever is more — containers
    /// round, and a re-encode can land a frame either side of the original's last one.
    /// </para>
    /// </summary>
    public static bool DurationsMatch(TimeSpan left, TimeSpan right)
    {
        double longer = Math.Max(left.TotalSeconds, right.TotalSeconds);
        double tolerance = Math.Max(1.0, longer * 0.01);

        return Math.Abs(left.TotalSeconds - right.TotalSeconds) <= tolerance;
    }

    /// <summary>Whether the extension is one this can attempt at all.</summary>
    public static bool IsVideo(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0) return false;

        ReadOnlySpan<char> extension = fileName[(dot + 1)..];

        foreach (string known in Extensions)
        {
            if (extension.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static readonly string[] Extensions =
    [
        "mp4", "m4v", "mov", "mkv", "avi", "wmv", "webm", "mpg", "mpeg", "ts", "m2ts", "flv", "3gp",
    ];
}
