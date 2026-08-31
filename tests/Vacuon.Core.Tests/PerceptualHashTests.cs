using Vacuon.Core.Analyzers;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M8. The promise is "the same photo at five resolutions ends up in one group",
/// so the tests build the same picture at different sizes and demand the fingerprints agree
/// — and build genuinely different pictures and demand they do not.
/// </summary>
public class PerceptualHashTests
{
    /// <summary>
    /// Renders a synthetic picture at any size. The pattern is deliberately smooth so that
    /// resampling it produces the same shape rather than the same pixels.
    /// </summary>
    private static ThumbnailBitmap Picture(int width, int height, int seed = 0, bool content = true)
    {
        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / width;
                double v = (double)y / height;

                // A diagonal ramp with a bright blob — enough structure for dHash to bite on.
                double value = 120 * (u + v) + 90 * Math.Exp(-40 * ((u - 0.3) * (u - 0.3) + (v - 0.6) * (v - 0.6)));
                if (seed > 0) value = 255 - value;   // a different picture, not a variation

                byte b = (byte)Math.Clamp(value, 0, 255);
                int offset = (y * width + x) * 4;

                pixels[offset] = b;
                pixels[offset + 1] = b;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        }

        return new ThumbnailBitmap(width, height, pixels, content);
    }

    [Fact]
    public void TheSamePictureAtFiveSizesFingerprintsTheSame()
    {
        // The whole point of the milestone: 4000 px and 200 px versions of one photo have
        // nothing in common byte for byte, and must still land together.
        ulong reference = PerceptualHash.Compute(Picture(400, 300))!.Value;

        foreach (int width in new[] { 1600, 800, 200, 96, 64 })
        {
            int height = width * 3 / 4;
            ulong hash = PerceptualHash.Compute(Picture(width, height))!.Value;

            int distance = PerceptualHash.Distance(reference, hash);

            Assert.True(distance <= PerceptualHash.DefaultThreshold,
                        $"{width}px differed by {distance} bits, over the threshold");
        }
    }

    [Fact]
    public void ADifferentPictureIsFarAway()
    {
        ulong a = PerceptualHash.Compute(Picture(400, 300))!.Value;
        ulong b = PerceptualHash.Compute(Picture(400, 300, seed: 1))!.Value;

        // Grouping two unrelated pictures is what gets somebody's photo deleted.
        Assert.True(PerceptualHash.Distance(a, b) > PerceptualHash.DefaultThreshold,
                    $"unrelated pictures only differed by {PerceptualHash.Distance(a, b)} bits");
    }

    [Fact]
    public void AnIconIsNeverFingerprinted()
    {
        // The loudest false positive available: every .docx on a machine shares one icon,
        // so hashing icons would report thousands of identical "pictures".
        Assert.Null(PerceptualHash.Compute(Picture(64, 64, content: false)));
    }

    [Fact]
    public void NothingInMeansNothingOut()
    {
        Assert.Null(PerceptualHash.Compute(null));
        Assert.Null(PerceptualHash.Compute(new ThumbnailBitmap(0, 0, [], true)));

        // A bitmap whose buffer is shorter than its dimensions claim would read past the
        // end; refusing is the only safe answer.
        Assert.Null(PerceptualHash.Compute(new ThumbnailBitmap(64, 64, new byte[16], true)));
    }

    [Fact]
    public void DistanceIsSymmetricAndZeroAgainstItself()
    {
        ulong hash = PerceptualHash.Compute(Picture(320, 240))!.Value;

        Assert.Equal(0, PerceptualHash.Distance(hash, hash));
        Assert.Equal(PerceptualHash.Distance(hash, 0), PerceptualHash.Distance(0, hash));
    }

    [Fact]
    public void AFlatImageIsRefusedRatherThanFingerprinted()
    {
        // Found on a real disk, and it was the worst result this feature produced: 110
        // unrelated images — a logo on white, a solid blue rectangle, two Dolby wordmarks,
        // a pile of screenshots — arrived in ONE group of "the same picture". They are all
        // nearly uniform, so every cell comparison lands the same way and they all hash
        // alike. A fingerprint built on nothing must not exist.
        var flat = new byte[64 * 64 * 4];
        Array.Fill(flat, (byte)200);

        Assert.Null(PerceptualHash.Compute(new ThumbnailBitmap(64, 64, flat, true)));
    }

    [Fact]
    public void ALogoOnWhiteStillGetsAFingerprint_AndThatIsTheGatesLimit()
    {
        // Measured rather than assumed, and it corrects what I first believed: a small dark
        // mark on white has a cell spread of about 12, comfortably over the gate. So the
        // contrast gate catches the genuinely uniform images and NOT logos.
        //
        // Written down because it is the honest boundary of this feature: two unrelated
        // logos of similar layout can still be grouped, which is why the screen shows the
        // thumbnails and the bit distance instead of just asserting a match.
        var pixels = new byte[64 * 64 * 4];
        Array.Fill(pixels, (byte)255);

        for (int y = 28; y < 34; y++)
        {
            for (int x = 28; x < 34; x++)
            {
                int offset = (y * 64 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
            }
        }

        Assert.NotNull(PerceptualHash.Compute(new ThumbnailBitmap(64, 64, pixels, true)));
    }

    [Fact]
    public void APictureWithRealStructureIsAccepted()
    {
        // The other side of the same gate: refusing flat images must not refuse photos.
        Assert.NotNull(PerceptualHash.Compute(Picture(320, 240)));
    }

    [Fact]
    public void ChannelsAreReadInBgraOrderWithLumaWeights()
    {
        // Half green, half blue. To the eye green is far brighter, so under luma weights
        // this has an edge down the middle and fingerprints. Read the channels in the wrong
        // order and the halves swap, which flips the comparison across that edge — so the
        // two orderings cannot produce the same answer.
        ThumbnailBitmap Split(bool greenLeft)
        {
            var pixels = new byte[64 * 64 * 4];

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int offset = (y * 64 + x) * 4;
                    bool green = (x < 32) == greenLeft;

                    if (green) pixels[offset + 1] = 255;   // G
                    else pixels[offset] = 255;             // B

                    pixels[offset + 3] = 255;
                }
            }

            return new ThumbnailBitmap(64, 64, pixels, true);
        }

        ulong? greenLeft = PerceptualHash.Compute(Split(greenLeft: true));
        ulong? greenRight = PerceptualHash.Compute(Split(greenLeft: false));

        Assert.NotNull(greenLeft);
        Assert.NotNull(greenRight);

        // Mirroring the picture must move the fingerprint; if green and blue weighed the
        // same, both halves would be equal and both hashes would be zero.
        Assert.NotEqual(greenLeft, greenRight);
    }
}

public class NearDuplicateChoiceTests
{
    private static SimilarImage Image(string name, long bytes, uint? width = null, uint? height = null) =>
        new(0, name, bytes, 0, width, height);

    [Fact]
    public void TheVersionWithMostPixelsIsKept()
    {
        SimilarImage best = NearDuplicateFinder.Choose(
        [
            Image("small.jpg", 900_000, 1280, 720),
            Image("big.jpg", 400_000, 3840, 2160),
        ]);

        // Pixels, not bytes: a 4K frame as a good JPEG is smaller than a 720p PNG of the
        // same thing, and keeping the bigger file would throw away the better picture.
        Assert.Equal("big.jpg", best.Path);
    }

    [Fact]
    public void SamePixelsFallsBackToTheLargerFile()
    {
        SimilarImage best = NearDuplicateFinder.Choose(
        [
            Image("compressed.jpg", 200_000, 1920, 1080),
            Image("original.png", 2_000_000, 1920, 1080),
        ]);

        Assert.Equal("original.png", best.Path);
    }

    [Fact]
    public void KnownDimensionsBeatUnknownOnes()
    {
        SimilarImage best = NearDuplicateFinder.Choose(
        [
            Image("mystery.img", 5_000_000),
            Image("known.jpg", 100_000, 1920, 1080),
        ]);

        Assert.Equal("known.jpg", best.Path);
    }

    [Fact]
    public void TheThresholdIsTightBecauseRealVersionsLandAtZero()
    {
        // Measured, not chosen by feel: three versions of one photograph — 1440p PNG,
        // 1080p JPEG and a badly compressed 720p JPEG — all fingerprinted identically.
        // Nothing is lost by tightening, and every bit of slack costs false positives.
        Assert.True(PerceptualHash.DefaultThreshold <= 6,
                    "the threshold was widened; the measurements that set it are in the XML doc");
    }

    [Fact]
    public void TheSizeFloorIsHighEnoughToExcludeUiArt()
    {
        // The false positives were card faces and sprites of about 2 KiB, which a 64-bit
        // fingerprint cannot tell apart at all — twenty-four different cards had a minimum
        // distance of zero. The floor is what removes that population.
        Assert.True(new NearDuplicateOptions().MinimumBytes >= 256 * 1024,
                    "the size floor was lowered; small UI art comes back with it");
    }

    [Fact]
    public void WithNothingKnownTheBiggestFileWins()
    {
        SimilarImage best = NearDuplicateFinder.Choose(
        [
            Image("a.img", 100),
            Image("b.img", 900),
        ]);

        Assert.Equal("b.img", best.Path);
    }
}
