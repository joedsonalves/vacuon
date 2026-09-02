namespace Vacuon.Core.Preview;

/// <summary>What a stretch of a hex dump is, so the view can colour it.</summary>
public enum HexKind
{
    /// <summary>The address column on the left.</summary>
    Offset,
    /// <summary>A byte that is zero. Padding, and most of a sparse file.</summary>
    Zero,
    /// <summary>A byte that would be a printable character.</summary>
    Printable,
    /// <summary>Any other byte.</summary>
    Other,
    /// <summary>The readable column on the right.</summary>
    Ascii,
    /// <summary>A dot standing in for a byte that has no character.</summary>
    Placeholder,
}

/// <summary>A stretch of the dump, and what it is.</summary>
public readonly record struct HexSpan(int Start, int Length, HexKind Kind);

/// <summary>
/// Splits a hex dump into the parts worth telling apart by colour.
/// <para>
/// A dump is a wall of two-character groups, and every one of them looks the same. What a
/// person is actually doing with it is finding the shape of a file: where the header ends,
/// where a run of padding starts, which stretch is text. Colour answers those at a glance and
/// the wall answers none of them.
/// </para>
/// <para>
/// Positions only, no brushes: the same split the rest of the project uses, so this is
/// testable without a window. See <see cref="SyntaxSpans"/> for the other half of the rule.
/// </para>
/// </summary>
public static class HexSpans
{
    /// <summary>Width of the address column produced by <see cref="FilePreview.Hex"/>.</summary>
    private const int OffsetWidth = 8;

    /// <summary>Where the byte columns begin: eight for the address plus two spaces.</summary>
    private const int BytesStart = OffsetWidth + 2;

    /// <summary>
    /// Classifies the dump line by line.
    /// </summary>
    /// <remarks>
    /// ⚠️ Reads the <b>rendered text</b> rather than the bytes it came from. The dump is what
    /// is on screen, and a classifier working from the original bytes would have to reproduce
    /// the exact column arithmetic of the formatter to line up — two descriptions of one
    /// layout, which drift apart the first time either is touched.
    /// </remarks>
    public static List<HexSpan> Classify(string dump)
    {
        ArgumentNullException.ThrowIfNull(dump);

        var spans = new List<HexSpan>(dump.Length / 24);
        int lineStart = 0;

        while (lineStart < dump.Length)
        {
            int lineEnd = dump.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = dump.Length;

            Line(dump, lineStart, lineEnd, spans);

            lineStart = lineEnd + 1;
        }

        return spans;
    }

    /// <summary>
    /// Classifies one line of the dump, appending to <paramref name="spans"/>.
    /// </summary>
    /// <remarks>
    /// Offsets are absolute, into <paramref name="dump"/>, not relative to the line. That is
    /// what lets a renderer classify only the lines it is about to draw without cutting them
    /// out of the dump first — the same positions it would have got from
    /// <see cref="Classify(string)"/>, so the two can never disagree about a line.
    /// </remarks>
    public static void Line(string dump, int start, int end, List<HexSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(spans);

        if (end - start < BytesStart) return;

        spans.Add(new HexSpan(start, OffsetWidth, HexKind.Offset));

        int at = start + BytesStart;
        int bytes = 0;

        // Sixteen groups of two, with a gap after the eighth. Anything that does not look
        // like a pair of hex digits ends the byte columns — including the run of spaces a
        // short last line pads with.
        while (bytes < 16 && at + 1 < end)
        {
            if (!IsHex(dump[at]) || !IsHex(dump[at + 1])) break;

            int value = (Value(dump[at]) << 4) | Value(dump[at + 1]);

            spans.Add(new HexSpan(at, 2, value == 0
                ? HexKind.Zero
                : value is >= 0x20 and <= 0x7E ? HexKind.Printable : HexKind.Other));

            at += 3;
            if (bytes == 7) at++;

            bytes++;
        }

        // What is left of the line is the readable column, with dots where a byte had no
        // character. The dots are dimmed so the readable stretches stand out from them.
        int text = end - start >= BytesStart ? TextStart(dump, start, end) : end;

        for (int i = text; i < end; i++)
        {
            bool dot = dump[i] == '.';
            int run = i;

            while (run < end && (dump[run] == '.') == dot) run++;

            spans.Add(new HexSpan(i, run - i, dot ? HexKind.Placeholder : HexKind.Ascii));
            i = run - 1;
        }
    }

    /// <summary>
    /// The character offset at which each line of the dump begins.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Found by scanning, never by multiplying.</b> The lines a dump is made of happen
    /// to be a fixed width, and computing <c>line * 78</c> would be a second description of
    /// the formatter's layout living apart from the formatter — the drift this whole class
    /// avoids by reading the rendered text. It is also wrong on the last line, which is short
    /// whenever the file does not end on a sixteen-byte boundary.
    /// </remarks>
    public static int[] LineStarts(string dump)
    {
        ArgumentNullException.ThrowIfNull(dump);

        if (dump.Length == 0) return [];

        var starts = new List<int>(dump.Length / 78 + 1);
        int at = 0;

        while (at < dump.Length)
        {
            starts.Add(at);

            int next = dump.IndexOf('\n', at);
            if (next < 0) break;

            at = next + 1;
        }

        return [.. starts];
    }

    /// <summary>
    /// Where line <paramref name="line"/> ends: the newline is not part of it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The last line is the one that catches people, this author included.</b> Every
    /// other line ends one character before the next one starts, but the last has no next —
    /// and a dump ends with a newline, so taking the end of the string would hand the line
    /// its own line break. <see cref="Classify(string)"/> never sees that character, so a
    /// renderer that did would colour the last line of a file differently from the classifier
    /// and nothing would look wrong. Both ask this instead.
    /// </remarks>
    public static int LineEnd(string dump, int[] starts, int line)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(starts);

        if (line + 1 < starts.Length) return starts[line + 1] - 1;

        int end = dump.Length;
        if (end > 0 && dump[end - 1] == NewLine) end--;

        return end;
    }

    private const char NewLine = '\n';

    /// <summary>
    /// Where the readable column starts: after the last run of two or more spaces.
    /// </summary>
    private static int TextStart(string dump, int start, int end)
    {
        for (int i = end - 1; i > start + BytesStart; i--)
            if (dump[i - 1] == ' ' && dump[i] != ' ' && dump[i - 2] == ' ') return i;

        return end;
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static int Value(char c) =>
        c <= '9' ? c - '0' : (char.ToUpperInvariant(c) - 'A') + 10;
}
