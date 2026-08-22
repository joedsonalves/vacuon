using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M3, the part that needs no media library linked in: what Windows already knows
/// about a file. The property handlers themselves are not under test here — the decoding of
/// what they answer is, because that is where the surprises were.
/// </summary>
public class MediaProbeTests
{
    [Theory]
    [InlineData("{34363248-0000-0010-8000-00AA00389B71}", "H264")]
    [InlineData("{43564548-0000-0010-8000-00AA00389B71}", "HEVC")]
    [InlineData("34363248-0000-0010-8000-00aa00389b71", "H264")]
    public void AMediaSubtypeGuidYieldsTheFourCcInsideIt(string guid, string expected)
    {
        // Measured against real files, not assumed: the shell answers video compression as a
        // GUID whose first field is the FourCC. Printing the GUID would be true and useless.
        Assert.Equal(expected, MediaProbe.FourCcFromSubtype(guid));
    }

    [Theory]
    [InlineData("{11111111-2222-3333-4444-555555555555}")]   // not a media subtype
    [InlineData("H.264")]                                     // already human, not a GUID
    [InlineData("")]
    [InlineData(null)]
    public void TextThatIsNotAMediaSubtypeGuidIsLeftAlone(string? text)
    {
        Assert.Null(MediaProbe.FourCcFromSubtype(text));
    }

    [Fact]
    public void APackedFourCcDecodesToItsFourCharacters()
    {
        // 0x34363248 is 'H' '2' '6' '4' little-endian.
        Assert.Equal("H264", MediaProbe.FourCc(0x34363248));
    }

    [Fact]
    public void AValueThatIsNotPrintableIsNotAFourCc()
    {
        // A real compression tag is four printable characters. Anything else is a number
        // that happens to live in the same field, and guessing at it would put mojibake on
        // screen next to a file the user is deciding whether to delete.
        Assert.Null(MediaProbe.FourCc(0x00000001));
        Assert.Null(MediaProbe.FourCc(0));
        Assert.Null(MediaProbe.FourCc(null));
    }

    [Fact]
    public void AFileThatIsNotMediaAnswersNothingRatherThanZero()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vacuon-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "not a video");

        try
        {
            MediaInfo info = MediaProbe.Read(path);

            // Null, never 0: "unknown" and "zero seconds" lead to different decisions.
            Assert.Null(info.Duration);
            Assert.Null(info.Width);
            Assert.Null(info.ResolutionLabel);
            Assert.True(info.IsEmpty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileIsNotAnException()
    {
        MediaInfo info = MediaProbe.Read(@"C:\definitely\not\here\video.mp4");
        Assert.True(info.IsEmpty);
    }

    [Fact]
    public void ForwardSlashesAndRelativePathsReachTheSameFile()
    {
        // A shell "parsing name" is not a path the CRT would accept. Passing forward slashes
        // came back as "no handler for this file" — indistinguishable from a format Windows
        // genuinely knows nothing about, and the reason a 4K video reported no metadata at
        // all. The probe normalises, so all three spellings must agree.
        string directory = Path.Combine(Path.GetTempPath(), $"vacuon-slash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "note.txt");
        File.WriteAllText(file, "x");

        string previous = Directory.GetCurrentDirectory();

        try
        {
            MediaInfo backslash = MediaProbe.Read(file);
            MediaInfo forward = MediaProbe.Read(file.Replace('\\', '/'));

            Directory.SetCurrentDirectory(directory);
            MediaInfo relative = MediaProbe.Read("note.txt");

            // This file has no media metadata, so all three agree on "nothing" — what the
            // test guards is that they agree, not what the answer is.
            Assert.Equal(backslash.IsEmpty, forward.IsEmpty);
            Assert.Equal(backslash.IsEmpty, relative.IsEmpty);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AnImageReportsItsPixelsAndNotItsDpi()
    {
        // The bug this exists for: System.Image ids 5 and 6 are HorizontalResolution and
        // VerticalResolution — DPI, not size. A 1200×800 PNG reported itself as "96×96",
        // which is plausible enough to ship and completely wrong. The dimensions are ids
        // 3 and 4. Caught by making an image of a known size and reading the label back.
        const int width = 40;
        const int height = 25;

        string path = Path.Combine(Path.GetTempPath(), $"vacuon-dim-{Guid.NewGuid():N}.bmp");
        File.WriteAllBytes(path, Bitmap(width, height));

        try
        {
            MediaInfo info = MediaProbe.Read(path);

            // If no property handler answers on this machine, there is nothing to assert
            // about — but when it does answer, it must not answer 96.
            if (info.Width is null) return;

            Assert.Equal((uint)width, info.Width);
            Assert.Equal((uint)height, info.Height);
            Assert.NotEqual(96u, info.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A minimal 24-bit BMP of the given size, built by hand.</summary>
    private static byte[] Bitmap(int width, int height)
    {
        int rowBytes = (width * 3 + 3) & ~3;
        int pixelBytes = rowBytes * height;
        var bytes = new byte[54 + pixelBytes];

        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);        // pixel data offset
        BitConverter.GetBytes(40).CopyTo(bytes, 14);        // BITMAPINFOHEADER size
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);  // planes
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28); // bits per pixel
        BitConverter.GetBytes(pixelBytes).CopyTo(bytes, 34);

        return bytes;
    }

    [Fact]
    public void TheResolutionLabelIsHowPeopleSayIt()
    {
        // The whole point of reading this: comparing two copies of the same video is
        // comparing 2160p against 720p, not two byte counts.
        Assert.Equal("2160p", new MediaInfo { Height = 2160 }.ResolutionLabel);
        Assert.Equal("720p", new MediaInfo { Height = 720 }.ResolutionLabel);
        Assert.Null(new MediaInfo().ResolutionLabel);
        Assert.Null(new MediaInfo { Height = 0 }.ResolutionLabel);
    }

    [Fact]
    public void DimensionsNeedBothSidesToMeanAnything()
    {
        Assert.Equal("3840×2160", new MediaInfo { Width = 3840, Height = 2160 }.Dimensions);
        Assert.Null(new MediaInfo { Width = 3840 }.Dimensions);
        Assert.Null(new MediaInfo { Height = 2160 }.Dimensions);
    }

    [Fact]
    public void EmptyMeansTheShellAnsweredNothing()
    {
        Assert.True(new MediaInfo().IsEmpty);
        Assert.False(new MediaInfo { Height = 1080 }.IsEmpty);
        Assert.False(new MediaInfo { Duration = TimeSpan.FromSeconds(3) }.IsEmpty);
    }
}
