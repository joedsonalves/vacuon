using System.Globalization;
using Vacuon.Core.Localization;

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
    /// <summary>A file the tool could not copy. Named here, and named again by the retry.</summary>
    Error,
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

    /// <summary>Error lines only: the Win32 code, 32 for a file somebody else has open.</summary>
    public int ErrorCode { get; init; }
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
        // Before the summary check, because an error line carries a clock and a clock
        // carries colons, and the summary test keys on a colon.
        if (TryParseError(line, out string failedPath, out int errorCode))
            return new RobocopyLine(RobocopyLineKind.Error, 0, failedPath, 0) { ErrorCode = errorCode };

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
    /// The path robocopy named in an error line, so the window can say <em>which</em> files
    /// did not make it instead of only how many.
    /// <para>
    /// Measured against the real tool, on a machine whose Windows is Portuguese. The line is
    /// <c>2026/08/31 03:34:56 ERROR 32 (0x00000020) Copying File C:\folder\locked.bin</c>,
    /// and the sentence underneath it — the one that explains the code — arrived translated
    /// while this line did not.
    /// </para>
    /// <para>
    /// ⚠️ Nothing here reads the word <c>ERROR</c> or the verb after the code. Both are words,
    /// and words are what this file has already been burned by: the same run printed
    /// <c>Bytes :</c> in English and <c>Ended : segunda-feira</c> in Portuguese. The anchor is
    /// <c>(0x</c> followed by a closing bracket, which is punctuation, plus a rooted path
    /// after it. A retry names the same file again, so the caller has to fold duplicates.
    /// </para>
    /// </summary>
    public static bool TryParseError(string line, out string path, out int code)
    {
        path = string.Empty;
        code = 0;

        int hex = line.IndexOf("(0x", StringComparison.Ordinal);
        if (hex < 0) return false;

        int close = line.IndexOf(')', hex);
        if (close < 0) return false;

        // The decimal code sits immediately in front of the bracket: "ERROR 32 (0x...)".
        ReadOnlySpan<char> head = line.AsSpan(0, hex).TrimEnd();
        int space = head.LastIndexOf(' ');
        if (space >= 0)
        {
            int.TryParse(head[(space + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
        }

        path = RootedTail(line.AsSpan(close + 1));
        return path.Length > 0;
    }

    /// <summary>
    /// The first rooted path in what is left of the line, drive letter or UNC. The words in
    /// front of it are translated; a path never is.
    /// </summary>
    private static string RootedTail(ReadOnlySpan<char> tail)
    {
        for (int i = 0; i + 2 < tail.Length; i++)
        {
            bool drive = char.IsLetter(tail[i]) && tail[i + 1] == ':' && tail[i + 2] == '\\';
            bool unc = tail[i] == '\\' && tail[i + 1] == '\\' && char.IsLetterOrDigit(tail[i + 2]);

            if (drive || unc) return tail[i..].Trim().ToString();
        }

        return string.Empty;
    }

    /// <summary>
    /// What the exit bitmask means, in a sentence somebody can read.
    /// <para>
    /// ⚠️ The number itself does not appear, and that is the point. This used to return
    /// <c>robocopy: files copied, some items could not be copied (9)</c>, and the 9 is a
    /// bitmask — 1 for "files copied" plus 8 for "some failed". On screen, at the end of a
    /// copy, it reads as a count of nine failed files. It was read that way. A figure the
    /// app never measured as a quantity has no business being printed like one, and the name
    /// of the tool doing the work is not the person's business either.
    /// </para>
    /// </summary>
    public static string Describe(int exitCode)
    {
        if (exitCode < 0 || exitCode == 16) return L.T("transfer.itemFatal");

        return (exitCode & 8) != 0
            ? L.T("transfer.itemSomeFailed")
            : L.T("transfer.itemFailed");
    }
}
