namespace Vacuon.Core.Transfer;

/// <summary>
/// Builds robocopy's argument list.
/// <para>
/// A list, never a command string. Every path here comes from a disk the app did not choose
/// the names on, and quoting one by hand is how a folder called <c>Séries "2024"</c> becomes
/// three arguments. <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> hands each
/// element over already separated, so there is no quoting to get wrong.
/// </para>
/// <para>
/// No shell is involved either: the app starts <c>robocopy.exe</c> itself, so there is no
/// console window to hide and none to flash. That is not a setting that can drift — there is
/// no <c>cmd</c> in the chain to open one.
/// </para>
/// </summary>
public static class RobocopyArguments
{
    /// <summary>
    /// Switches every run shares.
    /// <para>
    /// <c>/R:1 /W:1</c> because robocopy's own default is a million retries thirty seconds
    /// apart — a locked file would hold a transfer window open for the better part of a
    /// year. <c>/BYTES</c> so the sizes on each line are bytes and not a rounded "1.2 m"
    /// that cannot be added up. <c>/NJH</c> and <c>/NDL</c> drop the header and the
    /// directory rows, leaving the file rows the progress is counted from — the closing
    /// summary is kept on purpose, because it is the second, independent reading the total
    /// gets checked against.
    /// </para>
    /// </summary>
    private static readonly string[] Common = ["/R:1", "/W:1", "/BYTES", "/NJH", "/NDL"];

    public static List<string> Copy(string sourceFolder, string destinationFolder, string? singleFile, int threads)
    {
        var args = new List<string> { sourceFolder, destinationFolder };

        if (!string.IsNullOrEmpty(singleFile)) args.Add(singleFile);
        else args.Add("/E");   // subdirectories, empty ones included

        args.AddRange(Common);
        AddThreads(args, threads, singleFile is not null);
        return args;
    }

    /// <summary>
    /// A move across volumes: copy, then drop the source. <c>/MOVE</c> covers directories as
    /// well as files, which <c>/MOV</c> does not.
    /// </summary>
    public static List<string> Move(string sourceFolder, string destinationFolder, string? singleFile, int threads)
    {
        List<string> args = Copy(sourceFolder, destinationFolder, singleFile, threads);
        args.Add("/MOVE");
        return args;
    }

    /// <summary>
    /// Empties a folder by mirroring an empty one onto it.
    /// <para>
    /// This is robocopy's fast delete, and it is quick for the reason the plain one is slow:
    /// it walks the tree once with many threads instead of recursing a directory at a time.
    /// The folder itself survives the mirror, emptied — removing it is one more call, and
    /// the caller's job.
    /// </para>
    /// <para>
    /// ⚠️ <c>/MIR</c> pointed at the wrong folder erases it. Nothing in this class checks
    /// that; <see cref="FileTransferService"/> does, before the process is ever started.
    /// </para>
    /// </summary>
    public static List<string> Purge(string emptySourceFolder, string targetFolder, int threads)
    {
        var args = new List<string> { emptySourceFolder, targetFolder, "/MIR" };
        args.AddRange(Common);
        AddThreads(args, threads, singleFile: false);
        return args;
    }

    /// <summary>
    /// <c>/MT</c> is worth having for a tree of many files and worth nothing for one file,
    /// where the single thread is also what keeps the per-file percentage meaningful: with
    /// several threads open at once those percentages belong to whichever file each thread
    /// happens to be on, and reading them as one number would be inventing a figure.
    /// </summary>
    private static void AddThreads(List<string> args, int threads, bool singleFile)
    {
        if (singleFile || threads <= 1) return;
        args.Add($"/MT:{Math.Clamp(threads, 2, 128)}");
    }
}
