using System.Text;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Telling the parts of a hex dump apart, so colour can answer what the wall of digits cannot.
/// </summary>
public class HexSpansTests
{
    private static string Dump(params byte[] bytes) => FilePreview.Hex(bytes);

    private static List<HexSpan> Of(string dump, HexKind kind) =>
        [.. HexSpans.Classify(dump).Where(s => s.Kind == kind)];

    [Fact]
    public void TheAddressColumnIsFound()
    {
        string dump = Dump(1, 2, 3);

        HexSpan offset = Assert.Single(Of(dump, HexKind.Offset));

        Assert.Equal(0, offset.Start);
        Assert.Equal(8, offset.Length);
        Assert.Equal("00000000", dump.Substring(offset.Start, offset.Length));
    }

    [Fact]
    public void ZeroBytesAreTheirOwnKind()
    {
        // Padding is most of what a dump shows, and it is the part worth dimming: a run of
        // zeros is a shape, not information.
        string dump = Dump(0x00, 0x41, 0x00, 0x00);

        Assert.Equal(3, Of(dump, HexKind.Zero).Count);
    }

    [Fact]
    public void APrintableByteIsMarkedInTheHexColumnToo()
    {
        // 0x41 is 'A'. Colouring it in the hex column is what lets somebody see a stretch of
        // text without moving their eye to the right-hand column and back.
        string dump = Dump(0x41, 0x42, 0x01);

        List<HexSpan> printable = Of(dump, HexKind.Printable);

        Assert.Equal(2, printable.Count);
        Assert.Equal("41", dump.Substring(printable[0].Start, 2));
        Assert.Equal("42", dump.Substring(printable[1].Start, 2));
    }

    [Fact]
    public void ANonPrintableByteThatIsNotZeroIsAnother()
    {
        string dump = Dump(0xFF, 0x00, 0x41);

        HexSpan other = Assert.Single(Of(dump, HexKind.Other));

        Assert.Equal("FF", dump.Substring(other.Start, 2));
    }

    [Fact]
    public void TheReadableColumnIsSeparatedFromTheDots()
    {
        string dump = Dump([.. Encoding.ASCII.GetBytes("ABC"), 0x00, 0x01,
                            .. Encoding.ASCII.GetBytes("DE")]);

        List<HexSpan> ascii = Of(dump, HexKind.Ascii);
        List<HexSpan> dots = Of(dump, HexKind.Placeholder);

        Assert.Equal("ABC", dump.Substring(ascii[0].Start, ascii[0].Length));
        Assert.Equal("..", dump.Substring(dots[0].Start, dots[0].Length));
        Assert.Equal("DE", dump.Substring(ascii[1].Start, ascii[1].Length));
    }

    [Fact]
    public void AShortLastLineDoesNotSwallowThePadding()
    {
        // ⚠️ A line with fewer than sixteen bytes is padded with spaces. Reading those as
        // byte columns would produce spans over nothing and push the readable column off.
        string dump = Dump(0x41, 0x42);

        Assert.Equal(2, HexSpans.Classify(dump).Count(s => s.Kind is HexKind.Zero
                                                               or HexKind.Printable
                                                               or HexKind.Other));
    }

    [Fact]
    public void EverySpanStaysInsideTheText()
    {
        // A span past the end is an exception in the renderer, on a screen whose whole job is
        // to look at files nobody trusts.
        var bytes = new byte[300];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;

        string dump = Dump(bytes);

        foreach (HexSpan span in HexSpans.Classify(dump))
        {
            Assert.True(span.Start >= 0);
            Assert.True(span.Start + span.Length <= dump.Length);
            Assert.True(span.Length > 0);
        }
    }

    [Fact]
    public void SpansNeverOverlap()
    {
        var bytes = new byte[64];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 3);

        List<HexSpan> spans = HexSpans.Classify(Dump(bytes));

        for (int i = 1; i < spans.Count; i++)
            Assert.True(spans[i].Start >= spans[i - 1].Start + spans[i - 1].Length);
    }

    [Fact]
    public void AnEmptyDumpClassifiesToNothing()
    {
        Assert.Empty(HexSpans.Classify(string.Empty));
    }

    [Fact]
    public void EveryLineOfALongDumpGetsAnAddress()
    {
        var bytes = new byte[16 * 5];
        string dump = Dump(bytes);

        Assert.Equal(5, Of(dump, HexKind.Offset).Count);
    }

    // ================= drawing only the lines on screen (F6.19) =================

    [Fact]
    public void LineStartsFindsEveryLine()
    {
        string dump = Dump([.. Enumerable.Range(0, 64).Select(i => (byte)i)]);

        int[] starts = HexSpans.LineStarts(dump);

        Assert.Equal(4, starts.Length);
        Assert.Equal(0, starts[0]);

        // Every start but the first sits immediately after a newline, and nowhere else.
        for (int i = 1; i < starts.Length; i++) Assert.Equal(NEWLINE, dump[starts[i] - 1]);
    }

    [Fact]
    public void LineStartsHandlesTheShortLastLine()
    {
        // Seventeen bytes: one full line and one of a single byte. A renderer working out
        // where line two begins by multiplying would still be right here, and wrong about
        // where line two ENDS — so the ends are what this checks.
        string dump = Dump([.. Enumerable.Range(0, 17).Select(i => (byte)i)]);

        int[] starts = HexSpans.LineStarts(dump);

        Assert.Equal(2, starts.Length);

        string second = dump[starts[1]..HexSpans.LineEnd(dump, starts, 1)];

        Assert.StartsWith("00000010", second);
        Assert.DoesNotContain(NEWLINE, second);
    }

    [Fact]
    public void LineStartsOfNothingIsNothing()
    {
        Assert.Empty(HexSpans.LineStarts(string.Empty));
    }

    /// <summary>
    /// ⚠️ The invariant the drawing depends on: classifying one line at a time has to give
    /// exactly what classifying the whole dump gives.
    /// </summary>
    /// <remarks>
    /// The preview draws a band of lines and the byte editor draws another band of the same
    /// dump. If these two ever disagreed, the same file would be coloured differently
    /// depending on which screen it was looked at from, and neither would look broken.
    /// </remarks>
    [Fact]
    public void LineByLineAgreesWithTheWholeDump()
    {
        var bytes = new byte[600];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7);

        string dump = Dump(bytes);

        List<HexSpan> whole = HexSpans.Classify(dump);

        var piecemeal = new List<HexSpan>();
        int[] starts = HexSpans.LineStarts(dump);

        for (int i = 0; i < starts.Length; i++)
        {
            HexSpans.Line(dump, starts[i], HexSpans.LineEnd(dump, starts, i), piecemeal);
        }

        Assert.Equal(whole, piecemeal);
    }

    private const char NEWLINE = '\n';
}
