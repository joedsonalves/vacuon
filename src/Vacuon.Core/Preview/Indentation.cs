namespace Vacuon.Core.Preview;

/// <summary>
/// How a file indents itself, worked out from the file rather than from a setting.
/// <para>
/// A person editing one line of somebody else's file should not have to know, or care, that
/// the project uses tabs. The editor reads the answer off the text it just opened, so the
/// line it inserts looks like the lines around it.
/// </para>
/// </summary>
public static class Indentation
{
    /// <summary>What to insert for one level, when the file gives no clue.</summary>
    public const string Default = "    ";

    /// <summary>
    /// The indent unit this text uses: a tab, or the run of spaces it steps by.
    /// </summary>
    /// <remarks>
    /// ⚠️ Decided by <b>the smallest step between consecutive lines</b>, not by the smallest
    /// indent found. A file whose deepest nesting is eight spaces might step by two, four or
    /// eight, and only the differences say which — the first indented line alone cannot.
    /// </remarks>
    public static string Detect(ReadOnlySpan<char> text)
    {
        int tabs = 0;
        int spaced = 0;
        int step = int.MaxValue;
        int previous = 0;

        foreach (Range range in Lines(text))
        {
            ReadOnlySpan<char> line = text[range];
            int width = 0;

            while (width < line.Length && (line[width] == ' ' || line[width] == '\t')) width++;

            if (width == 0)
            {
                // A line with no indent resets the comparison: the next indented line is a
                // first level, not a step down from something.
                previous = 0;
                continue;
            }

            if (line[0] == '\t') { tabs++; previous = 0; continue; }

            // A blank line that happens to hold spaces says nothing about nesting.
            if (width == line.Length) continue;

            spaced++;

            if (previous > 0 && width > previous) step = Math.Min(step, width - previous);
            else if (previous == 0) step = Math.Min(step, width);

            previous = width;
        }

        if (tabs > spaced) return "\t";
        if (spaced == 0 || step is int.MaxValue or <= 0) return Default;

        // Beyond eight the guess is worth less than the convention.
        return new string(' ', Math.Min(step, 8));
    }

    /// <summary>
    /// The whitespace that opens the line containing <paramref name="caret"/>.
    /// <para>
    /// This is what a new line copies, so pressing Enter inside a nested block lands the
    /// cursor under the line above instead of at column zero.
    /// </para>
    /// </summary>
    public static string LeadingWhitespaceAt(string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0) return string.Empty;

        caret = Math.Clamp(caret, 0, text.Length);

        int start = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        int at = start;

        // Never past the caret: copying indentation from beyond where the person is standing
        // would insert whitespace they cannot see the source of.
        while (at < caret && at < text.Length && (text[at] == ' ' || text[at] == '\t')) at++;

        return text[start..at];
    }

    private static List<Range> Lines(ReadOnlySpan<char> text)
    {
        var ranges = new List<Range>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            ranges.Add(new Range(start, end));
            start = i + 1;
        }

        if (start < text.Length) ranges.Add(new Range(start, text.Length));

        return ranges;
    }
}
