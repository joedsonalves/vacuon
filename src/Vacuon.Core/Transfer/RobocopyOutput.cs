using System.Globalization;

namespace Vacuon.Core.Transfer;

public enum RobocopyLineKind
{
    /// <summary>Banner, separator, blank — nothing to learn from.</summary>
    Ignored,
    /// <summary>A file was written at the destination.</summary>
    File,
    /// <summary>A file was removed from the destination — the <c>/MIR</c> purge.</summary>
    Extra,
    /// <summary>Progress inside the file currently open.</summary>
    Percent,
    /// <summary>One numeric row of the closing table: Dirs, then Files, then Bytes.</summary>
    SummaryRow,
}

public readonly record struct RobocopyLine(
    RobocopyLineKind Kind,
    long Bytes,
    string Path,
    int Percent)
{
    /// <summary>Summary rows only: the FAILED column.</summary>
    public long Failed { get; init; }

    /// <summary>Summary rows only: the Extras column, which is what a purge removed.</summary>
    public long Extras { get; init; }
}

/// <summary>
/// Reads robocopy's console output.
/// <para>
/// The shapes here were taken from robocopy's actual output on this machine, not from
/// memory — run with <c>/BYTES /NJH /NDL</c> a file line is five tab-separated fields whose
/// last is the full source path and whose second-to-last is the size:
/// </para>
/// <code>
/// \t    New File  \t\t  200000\tC:\folder\file1.bin
/// \t  *EXTRA File \t\t  150000\tC:\folder\sub\gone.bin
/// </code>
/// <para>
/// Progress inside a file arrives on its own as <c>  0%  </c> / <c>100%  </c>, separated by
/// carriage returns so that a console would overwrite in place.
/// </para>
/// <para>
/// ⚠️ Nothing here matches on the class words. <c>New File</c> is English on this machine
/// while the same run signed off with <c>Ended : domingo, 30 de agosto de 2026</c>, so the
/// words are localised on some installs and the layout is not. Structure is what gets
/// matched: a tab-separated line ending in a rooted path, with digits in front of it.
/// </para>
/// </summary>
public static class RobocopyOutput
{
    /// <summary>Robocopy is the rare tool whose success is a bitmask, and 8 is where it turns bad.</summary>
    public const int FirstFailingExitCode = 8;

    public static bool Succeeded(int exitCode) => exitCode >= 0 && exitCode < FirstFailingExitCode;

    public static RobocopyLine Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new RobocopyLine(RobocopyLineKind.Ignored, 0, string.Empty, 0);

        if (TryParsePercent(line, out int percent))
            return new RobocopyLine(RobocopyLineKind.Percent, 0, string.Empty, percent);

        // A summary row is recognised here but not interpreted: which quantity it counts
        // depends on how many rows came before it, and a single line cannot know that. The
        // runner keeps that count — see FileTransferService.
        if (TryParseSummaryRow(line, out long copied, out long failed, out long extras))
            return new RobocopyLine(RobocopyLineKind.SummaryRow, copied, string.Empty, 0)
            {
                Failed = failed,
                Extras = extras,
            };

        return ParseFileLine(line);
    }

    private static RobocopyLine Ignored => new(RobocopyLineKind.Ignored, 0, string.Empty, 0);

    private static RobocopyLine ParseFileLine(string line)
    {
        string[] fields = line.Split('\t');
        if (fields.Length < 3) return Ignored;

        string path = fields[^1];

        // Robocopy overwrites its own progress in place, so a file line can arrive with the
        // percentages that followed it still attached behind a carriage return:
        // "...\plain.bin\r  0%  \r100%  ". The path is everything before the first one.
        int overwrite = path.IndexOf('\r');
        if (overwrite >= 0) path = path[..overwrite];

        path = path.Trim();

        // A rooted path is the only thing that makes this a file line. Directory rows carry
        // a trailing separator and a size of -1, and they are not files.
        if (path.Length < 3 || path.EndsWith('\\')) return Ignored;
        if (!System.IO.Path.IsPathRooted(path)) return Ignored;

        long bytes = -1;
        for (int i = fields.Length - 2; i >= 0; i--)
        {
            string candidate = fields[i].Trim();
            if (candidate.Length == 0) continue;

            if (long.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                && parsed >= 0)
            {
                bytes = parsed;
            }

            break;
        }

        if (bytes < 0) return Ignored;

        // The purge marker is the one token robocopy does not translate: it is punctuation.
        bool extra = line.Contains("*EXTRA", StringComparison.Ordinal);

        return new RobocopyLine(extra ? RobocopyLineKind.Extra : RobocopyLineKind.File, bytes, path, 0);
    }

    private static bool TryParsePercent(string line, out int percent)
    {
        percent = 0;

        ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[^1] != '%') return false;

        ReadOnlySpan<char> number = trimmed[..^1];

        // Robocopy prints tenths on slower copies ("12.3%"), whole numbers on fast ones.
        int dot = number.IndexOfAny('.', ',');
        if (dot >= 0) number = number[..dot];

        if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return false;
        if (value is < 0 or > 100) return false;

        percent = value;
        return true;
    }

    /// <summary>
    /// One numeric row of the closing table, whose six columns are Total, Copied, Skipped,
    /// Mismatch, FAILED and Extras.
    /// <para>
    /// The row label is localised, so nothing here reads it. What is fixed is the shape —
    /// a colon followed by exactly six integers — and the order the three rows come in.
    /// Telling Bytes from Files is therefore the caller's job, by counting rows, and this
    /// method deliberately refuses to guess it from the magnitudes.
    /// </para>
    /// </summary>
    public static bool TryParseSummaryRow(string line, out long copied, out long failed, out long extras)
    {
        copied = 0;
        failed = 0;
        extras = 0;

        int colon = line.IndexOf(':');
        if (colon < 0) return false;

        string[] parts = line[(colon + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 6) return false;

        var numbers = new long[6];
        for (int i = 0; i < 6; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        copied = numbers[1];
        failed = numbers[4];
        extras = numbers[5];
        return true;
    }

    /// <summary>
    /// A plain-language reading of the exit bitmask, for a report that has to say what
    /// happened rather than print a number.
    /// </summary>
    public static string Describe(int exitCode)
    {
        if (exitCode < 0) return $"robocopy exited abnormally ({exitCode})";
        if (exitCode == 16) return "robocopy reported a fatal error (16)";

        var flags = new List<string>(4);
        if ((exitCode & 1) != 0) flags.Add("files copied");
        if ((exitCode & 2) != 0) flags.Add("extra items at the destination");
        if ((exitCode & 4) != 0) flags.Add("mismatched items");
        if ((exitCode & 8) != 0) flags.Add("some items could not be copied");

        return flags.Count == 0
            ? "robocopy found nothing to do (0)"
            : $"robocopy: {string.Join(", ", flags)} ({exitCode})";
    }
}
