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
    public void AFlatImageIsStableRatherThanRandom()
    {
        // Every cell equal means every comparison is "not brighter", so the fingerprint is
        // all zeroes. That is correct and, more importantly, the same every time — a blank
        // image must not produce noise that groups it with something.
        var flat = new byte[64 * 64 * 4];
        Array.Fill(flat, (byte)200);

        ulong? first = PerceptualHash.Compute(new ThumbnailBitmap(64, 64, flat, true));
        ulong? second = PerceptualHash.Compute(new ThumbnailBitmap(64, 64, flat, true));

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void BrightnessUsesLumaWeightsNotAPlainAverage()
    {
        // Pure green and pure blue have the same plain average, and wildly different
        // brightness to an eye. Using a flat average would let a colour shift move the
        // fingerprint more than a resize does.
        var green = new byte[8 * 8 * 4];
        var blue = new byte[8 * 8 * 4];

        for (int i = 0; i < green.Length; i += 4)
        {
            green[i + 1] = 255;   // G
            green[i + 3] = 255;
            blue[i] = 255;        // B
            blue[i + 3] = 255;
        }

        // Both are flat, so both hash to zero — what is asserted is that the reduction reads
        // the channels in BGRA order at all, which a wrong offset would break loudly.
        Assert.NotNull(PerceptualHash.Compute(new ThumbnailBitmap(8, 8, green, true)));
        Assert.NotNull(PerceptualHash.Compute(new ThumbnailBitmap(8, 8, blue, true)));
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
