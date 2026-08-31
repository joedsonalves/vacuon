using System.Diagnostics;
using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M8, F4.6 — video near-duplicates.
/// <para>
/// The numbers these tests guard were measured before they were chosen, on synthetic footage
/// built with ffmpeg. One clip taken down a whole ladder of re-encodes — 720p to 480p, 360p,
/// 240p and 180p, each worse than the last — stayed within 7 bits of 64 of the original at
/// every step; unrelated footage sat between 42 and 44. The threshold sits in the middle of a
/// gap that wide, which is why none of these tests are about the threshold itself.
/// </para>
/// </summary>
public class VideoSimilarityTests
{
    private static VideoFingerprint Print(TimeSpan duration, params ulong[] frames) =>
        new(duration, 640, 480, frames);

    private static VideoFingerprint Print(double seconds, params ulong[] frames) =>
        Print(TimeSpan.FromSeconds(seconds), frames);

    [Fact]
    public void IdenticalFingerprintsAreTheSameVideo()
    {
        var a = Print(20, 0x0102030405060708, 0x1122334455667788, 0xAABBCCDDEEFF0011);

        Assert.Equal(0, VideoSimilarity.Distance(a, a));
        Assert.True(VideoSimilarity.AreSimilar(a, a));
    }

    [Fact]
    public void TheWorstFrameDecidesRatherThanTheAverage()
    {
        // This is the shared-opening case, and the reason an average is not used. Four frames
        // that agree perfectly must not carry a fifth that plainly does not: that is exactly
        // how two different films with the same title sequence end up in one group.
        var a = Print(20, 0, 0, 0, 0, 0);
        var b = Print(20, 0, 0, 0, 0, ulong.MaxValue);

        Assert.Equal(64, VideoSimilarity.Distance(a, b));
        Assert.False(VideoSimilarity.AreSimilar(a, b));
    }

    [Fact]
    public void VideosOfDifferentLengthAreNotCompared()
    {
        // The counterpart of the aspect-ratio gate that fixed the picture finder: a cheap
        // property that refuses whole populations before a single pixel is weighed.
        var a = Print(20, 1, 2, 3);
        var b = Print(600, 1, 2, 3);

        Assert.Null(VideoSimilarity.Distance(a, b));
        Assert.False(VideoSimilarity.AreSimilar(a, b));
    }

    [Theory]
    [InlineData(20.0, 20.4, true)]    // container rounding
    [InlineData(20.0, 21.5, false)]
    [InlineData(3600.0, 3630.0, true)]  // a percent of an hour
    [InlineData(3600.0, 3700.0, false)]
    [InlineData(10.0, 10.9, true)]      // the one-second floor covers short clips
    public void DurationToleranceIsAFloorAndAPercentage(double left, double right, bool expected)
    {
        Assert.Equal(expected, VideoSimilarity.DurationsMatch(
            TimeSpan.FromSeconds(left), TimeSpan.FromSeconds(right)));
    }

    [Fact]
    public void AFingerprintStandingOnTooFewFramesIsNotUsed()
    {
        // One or two frames is the single-thumbnail problem again, wearing more machinery.
        var thin = Print(20, 0x1234);
        var full = Print(20, 0x1234, 0x1234, 0x1234);

        Assert.False(thin.IsUsable);
        Assert.Null(VideoSimilarity.Distance(thin, full));
        Assert.False(VideoSimilarity.AreSimilar(thin, full));
    }

    [Fact]
    public void ComparingUsesOnlyTheFramesBothSidesHave()
    {
        var five = Print(20, 1, 1, 1, 1, 1);
        var three = Print(20, 1, 1, 1);

        Assert.Equal(0, VideoSimilarity.Distance(five, three));
    }

    [Theory]
    [InlineData("holiday.mp4", true)]
    [InlineData("render.MKV", true)]
    [InlineData("clip.webm", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("song.mp3", false)]
    [InlineData("noextension", false)]
    [InlineData("archive.mp4.bak", false)]
    public void OnlyVideoExtensionsAreAttempted(string name, bool expected)
    {
        Assert.Equal(expected, VideoSimilarity.IsVideo(name));
    }

    [Fact]
    public void DistanceIsSymmetric()
    {
        var a = Print(20, 0x00FF00FF00FF00FF, 0x0F0F0F0F0F0F0F0F, 0x3333333333333333);
        var b = Print(20, 0x00FF00FF00FF00F0, 0x0F0F0F0F0F0F0F00, 0x3333333333333330);

        Assert.Equal(VideoSimilarity.Distance(a, b), VideoSimilarity.Distance(b, a));
    }
}

/// <summary>
/// The decoding half, against files that actually exist.
/// <para>
/// These build a few seconds of their own footage with ffmpeg and are skipped when it is not
/// installed. The clips are deliberately small: what they check does not depend on size, and
/// the whole suite runs in under half a minute. A skip is visible in the run; quietly passing
/// with nothing to check is the failure mode this project has been bitten by before, and is
/// not on offer.
/// </para>
/// </summary>
public class VideoDecodingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vacuon-vid-{Guid.NewGuid():N}");

    private static readonly string? Ffmpeg = FindFfmpeg();

    private const string NeedsFfmpeg = "ffmpeg is not on PATH, so there is no footage to decode.";

    private static string? FindFfmpeg()
    {
        string? paths = Environment.GetEnvironmentVariable("PATH");
        if (paths is null) return null;

        foreach (string directory in paths.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            try
            {
                string candidate = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry is not this test's problem */ }
        }

        return null;
    }

    /// <summary>
    /// Runs ffmpeg and waits for it.
    /// <para>
    /// The stderr is read, not merely redirected. ffmpeg writes its progress there, and a
    /// redirected pipe nobody drains fills up and blocks the process forever — which looks
    /// from the outside like a feature that does not work, because the file it was writing
    /// never finishes.
    /// </para>
    /// </summary>
    private static void Run(string arguments)
    {
        var info = new ProcessStartInfo(Ffmpeg!)
        {
            Arguments = "-hide_banner -loglevel error -nostats " + arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(info)!;

        string errors = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(120_000))
            throw new TimeoutException("ffmpeg did not finish: " + errors);
    }

    private string Make(string name, string filter, int seconds = 7)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);

        Run($"-y -f lavfi -i \"{filter}\" -t {seconds} -c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p \"{path}\"");

        return path;
    }

    [SkippableFact]
    public void TheSameFootageReEncodedSmallerIsRecognised()
    {
        Skip.If(Ffmpeg is null, NeedsFfmpeg);

        string original = Make("a.mp4", "testsrc=size=320x240:rate=25");

        string shrunk = Path.Combine(_directory, "a-small.mp4");
        Run($"-y -i \"{original}\" -c:v libx264 -preset ultrafast -crf 34 -vf scale=160:120 \"{shrunk}\"");

        VideoFingerprint? a = VideoSimilarity.Of(original);
        VideoFingerprint? b = VideoSimilarity.Of(shrunk);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(VideoSimilarity.AreSimilar(a!, b!),
            $"half the resolution and much worse quality measured {VideoSimilarity.Distance(a!, b!)} bits apart");
    }

    [SkippableFact]
    public void UnrelatedFootageIsNotGrouped()
    {
        Skip.If(Ffmpeg is null, NeedsFfmpeg);

        VideoFingerprint? a = VideoSimilarity.Of(Make("t.mp4", "testsrc=size=320x240:rate=25"));
        VideoFingerprint? b = VideoSimilarity.Of(Make("s.mp4", "smptebars=size=320x240:rate=25"));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.False(VideoSimilarity.AreSimilar(a!, b!));
    }

    [SkippableFact]
    public void ANearlyBlackVideoGetsNoFingerprintAtAll()
    {
        // The false positive this whole design is arranged around. Two dark videos share no
        // content, and every flat frame hashes the same way — so a fingerprint built on
        // nothing must not exist, rather than exist and match everything else that is dark.
        Skip.If(Ffmpeg is null, NeedsFfmpeg);

        Assert.Null(VideoSimilarity.Of(Make("dark.mp4", "color=c=black:size=320x240:rate=25")));
    }

    [SkippableFact]
    public void SharingAnOpeningIsNotSharingAVideo()
    {
        Skip.If(Ffmpeg is null, NeedsFfmpeg);

        string a = Path.Combine(_directory, "intro-a.mp4");
        string b = Path.Combine(_directory, "intro-b.mp4");

        Concat("smptebars=size=320x240:rate=25:duration=1", "testsrc=size=320x240:rate=25:duration=6", a);
        Concat("smptebars=size=320x240:rate=25:duration=1", "mandelbrot=size=320x240:rate=25", b);

        VideoFingerprint? left = VideoSimilarity.Of(a);
        VideoFingerprint? right = VideoSimilarity.Of(b);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.False(VideoSimilarity.AreSimilar(left!, right!));
    }

    private void Concat(string first, string second, string output)
    {
        Directory.CreateDirectory(_directory);

        Run($"-y -f lavfi -i \"{first}\" -f lavfi -i \"{second}\" -t 7 " +
            $"-filter_complex \"[0:v][1:v]concat=n=2:v=1\" -c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p \"{output}\"");
    }

    [SkippableFact]
    public void AWidthWhoseRowsNeedPaddingDecodesTheSameAsOneThatDoesNot()
    {
        // The regression this exists for. A row of BGRA is width*4 bytes, but the buffer pads
        // each row out to an alignment boundary: 854*4 = 3416 is not a multiple of 64, while
        // 1280*4 = 5120 is. Reading the padded one at the unpadded pitch shears every row a
        // little further than the last, and the frame comes back as diagonal stripes.
        //
        // It did not look like a decoding bug from the numbers. It looked like heavy quality
        // loss defeating the fingerprint, because the copies that broke were the smaller ones.
        // Writing three frames out and looking at them settled it in seconds.
        Skip.If(Ffmpeg is null, NeedsFfmpeg);

        string source = Make("wide.mp4", "testsrc2=size=1280x720:rate=25");

        string padded = Path.Combine(_directory, "padded.mp4");    // 854 wide: not aligned
        string aligned = Path.Combine(_directory, "aligned.mp4");  // 640 wide: aligned

        Run($"-y -i \"{source}\" -vf scale=854:480 -c:v libx264 -preset ultrafast -crf 28 \"{padded}\"");
        Run($"-y -i \"{source}\" -vf scale=640:360 -c:v libx264 -preset ultrafast -crf 28 \"{aligned}\"");

        VideoFingerprint? a = VideoSimilarity.Of(padded);
        VideoFingerprint? b = VideoSimilarity.Of(aligned);

        Assert.NotNull(a);
        Assert.NotNull(b);

        int? distance = VideoSimilarity.Distance(a!, b!);

        Assert.True(distance <= VideoSimilarity.FrameThreshold,
            $"two downscales of one clip measured {distance} bits apart; a padded row pitch " +
            "read at the unpadded width shears the frame and costs about half the bits");
    }

    [Fact]
    public void AFileThatIsNotAVideoYieldsNothingRatherThanThrowing()
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, "not-really.mp4");
        File.WriteAllText(path, "this is text wearing a video's extension");

        Assert.Null(VideoSimilarity.Of(path));
    }

    [Fact]
    public void AMissingFileYieldsNothingRatherThanThrowing()
    {
        Assert.Null(VideoSimilarity.Of(Path.Combine(_directory, "was-never-there.mp4")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* the decoder may still hold a handle; the temp folder survives */ }

        GC.SuppressFinalize(this);
    }
}
