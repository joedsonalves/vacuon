using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Turning an edited hex dump back into bytes, and refusing the edits that would break a file
/// silently.
/// </summary>
public class HexDumpTests
{
    private static byte[] Sample(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 7);
        return bytes;
    }

    [Fact]
    public void ADumpThatWasNotTouchedComesBackTheSame()
    {
        // The whole promise: render bytes, parse them back, get the same bytes.
        byte[] original = Sample(200);

        HexParse parsed = HexDump.Parse(FilePreview.Hex(original, int.MaxValue), original.Length);

        Assert.True(parsed.Succeeded);
        Assert.Equal(original, parsed.Bytes);
    }

    [Fact]
    public void AChangedByteComesBackChanged()
    {
        byte[] original = Sample(32);
        string dump = FilePreview.Hex(original, int.MaxValue);

        // The first byte is 00; make it FF.
        string edited = string.Concat(dump.AsSpan(0, 10), "FF", dump.AsSpan(12));

        HexParse parsed = HexDump.Parse(edited, original.Length);

        Assert.True(parsed.Succeeded);
        Assert.Equal(0xFF, parsed.Bytes[0]);
        Assert.Equal(original[1], parsed.Bytes[1]);
    }

    [Fact]
    public void TheReadableColumnIsNotInput()
    {
        // ⚠️ The right-hand column is produced FROM the bytes. Reading it back as input would
        // let a file be changed by typing into a view of itself, and would let the two halves
        // of one line disagree about what the file holds.
        byte[] original = [0x41, 0x42, 0x43];
        string dump = FilePreview.Hex(original, int.MaxValue);

        string edited = dump.Replace("ABC", "ZZZ", StringComparison.Ordinal);

        HexParse parsed = HexDump.Parse(edited, original.Length);

        Assert.True(parsed.Succeeded);
        Assert.Equal(original, parsed.Bytes);
    }

    [Fact]
    public void ARemovedByteIsRefused()
    {
        // ⚠️ Shrinking a file through a dump moves every offset after the edit. In an
        // executable that breaks it in a way that looks like nothing until it is run.
        byte[] original = Sample(32);
        string dump = FilePreview.Hex(original, int.MaxValue);

        string edited = string.Concat(dump.AsSpan(0, 10), dump.AsSpan(13));

        Assert.Equal(HexParseOutcome.LengthChanged, HexDump.Parse(edited, original.Length).Outcome);
    }

    [Fact]
    public void AnAddedByteIsRefused()
    {
        byte[] original = Sample(16);
        string dump = FilePreview.Hex(original, int.MaxValue);

        // ⚠️ A seventeenth byte on a line used to be dropped in silence, because the reader
        // stopped at sixteen and the count still came out right. Refused now, not trimmed.
        Assert.Equal(HexParseOutcome.BadDigit,
                     HexDump.Parse(dump.Replace("00000000  ", "00000000  AB ", StringComparison.Ordinal),
                                   original.Length).Outcome);
    }

    [Fact]
    public void ATypoInAByteIsRefusedWithItsLine()
    {
        byte[] original = Sample(48);
        string dump = FilePreview.Hex(original, int.MaxValue);

        // Three hex digits where two belong: read loosely it would be one byte plus rubbish.
        int second = dump.IndexOf('\n') + 1;
        string edited = string.Concat(dump.AsSpan(0, second + 10), "ABC", dump.AsSpan(second + 12));

        HexParse parsed = HexDump.Parse(edited, original.Length);

        Assert.Equal(HexParseOutcome.BadDigit, parsed.Outcome);
        Assert.Equal(2, parsed.Line);
    }

    [Fact]
    public void ANonHexCharacterInAByteColumnIsRefused()
    {
        byte[] original = Sample(16);
        string dump = FilePreview.Hex(original, int.MaxValue);

        string edited = string.Concat(dump.AsSpan(0, 10), "Z1", dump.AsSpan(12));

        Assert.Equal(HexParseOutcome.LengthChanged, HexDump.Parse(edited, original.Length).Outcome);
    }

    [Fact]
    public void LowerCaseDigitsAreAccepted()
    {
        byte[] original = [0xAB, 0xCD];
        string dump = FilePreview.Hex(original, int.MaxValue);

        HexParse parsed = HexDump.Parse(dump.ToLowerInvariant(), original.Length);

        Assert.True(parsed.Succeeded);
        Assert.Equal(original, parsed.Bytes);
    }

    [Fact]
    public void AShortLastLineRoundTrips()
    {
        // The padding of a partial line is spaces where bytes would be, and must not be read
        // as anything.
        byte[] original = Sample(19);

        HexParse parsed = HexDump.Parse(FilePreview.Hex(original, int.MaxValue), original.Length);

        Assert.True(parsed.Succeeded);
        Assert.Equal(original, parsed.Bytes);
    }

    [Fact]
    public void AnEmptyDumpParsesToNothing()
    {
        Assert.True(HexDump.Parse(string.Empty, 0).Succeeded);
        Assert.Equal(HexParseOutcome.LengthChanged, HexDump.Parse(string.Empty, 4).Outcome);
    }

    [Fact]
    public void EveryByteValueSurvives()
    {
        var original = new byte[256];
        for (int i = 0; i < 256; i++) original[i] = (byte)i;

        HexParse parsed = HexDump.Parse(FilePreview.Hex(original, int.MaxValue), original.Length);

        Assert.True(parsed.Succeeded);
        Assert.Equal(original, parsed.Bytes);
    }
}
