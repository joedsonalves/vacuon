namespace Vacuon.Core.Preview;

/// <summary>Why a dump could or could not be turned back into bytes.</summary>
public enum HexParseOutcome
{
    Parsed,
    /// <summary>Something in a byte column is not a pair of hex digits.</summary>
    BadDigit,
    /// <summary>The dump no longer holds the same number of bytes it started with.</summary>
    LengthChanged,
}

public sealed record HexParse(HexParseOutcome Outcome, byte[] Bytes, int Line)
{
    public bool Succeeded => Outcome == HexParseOutcome.Parsed;
}

/// <summary>
/// Turning an edited hex dump back into bytes.
/// <para>
/// ⚠️ <b>Only the byte columns are read.</b> The address on the left and the readable column
/// on the right are produced <em>from</em> the bytes; treating them as input would let a file
/// be changed by typing in a column that is supposed to be a view of it, and would make two
/// halves of the same line disagree about what the file contains.
/// </para>
/// <para>
/// ⚠️ <b>The byte count may not change.</b> Editing bytes in place is one thing; growing or
/// shrinking a file through a dump is another, and in an executable it moves every offset
/// after the edit — which breaks the file in a way that looks like nothing until it is run.
/// A dump that no longer holds the same number of bytes is refused.
/// </para>
/// </summary>
public static class HexDump
{
    /// <summary>Where the byte columns begin: eight for the address plus two spaces.</summary>
    private const int BytesStart = 10;

    public static HexParse Parse(string dump, int expectedLength)
    {
        ArgumentNullException.ThrowIfNull(dump);

        var bytes = new List<byte>(expectedLength);
        int lineStart = 0;
        int line = 0;

        while (lineStart < dump.Length)
        {
            line++;

            int lineEnd = dump.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = dump.Length;

            int end = lineEnd > lineStart && dump[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;

            if (end - lineStart >= BytesStart && !ReadLine(dump, lineStart + BytesStart, end, bytes))
                return new HexParse(HexParseOutcome.BadDigit, [], line);

            lineStart = lineEnd + 1;
        }

        return bytes.Count == expectedLength
            ? new HexParse(HexParseOutcome.Parsed, [.. bytes], 0)
            : new HexParse(HexParseOutcome.LengthChanged, [], 0);
    }

    /// <summary>
    /// Reads up to sixteen byte columns from one line.
    /// </summary>
    /// <returns><c>false</c> when something that should be a byte is not one.</returns>
    private static bool ReadLine(string dump, int at, int end, List<byte> into)
    {
        int taken = 0;

        while (taken < 16)
        {
            // Two spaces where a byte would be is the padding of a short last line, and the
            // gap before the readable column. Either way there are no more bytes here.
            while (at < end && dump[at] == ' ') at++;

            if (at >= end) return true;

            // The readable column can hold anything, hex digits included, so the byte columns
            // end where their count does — not where the characters stop looking like hex.
            if (at + 1 >= end) return false;

            if (!IsHex(dump[at]) || !IsHex(dump[at + 1])) return true;

            // A group of three or more digits is a typo that would otherwise be read as one
            // byte plus the start of the readable column.
            if (at + 2 < end && IsHex(dump[at + 2])) return false;

            into.Add((byte)((Value(dump[at]) << 4) | Value(dump[at + 1])));

            at += 2;
            taken++;
        }

        // Sixteen taken, and still a byte column: the line grew. Told apart from the readable
        // column by the gap — the formatter puts two spaces before it, and a seventeenth byte
        // typed into the row has only one.
        if (at + 2 < end && dump[at] == ' ' && dump[at + 1] != ' '
            && IsHex(dump[at + 1]) && IsHex(dump[at + 2]))
        {
            return false;
        }

        return true;
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static int Value(char c) =>
        c <= '9' ? c - '0' : (char.ToUpperInvariant(c) - 'A') + 10;
}
