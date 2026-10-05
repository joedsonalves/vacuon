using System.IO.Enumeration;
using System.Runtime.Versioning;
using Vacuon.Core.Localization;
using Vacuon.Core.Safety;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

public enum CompressOutcome
{
    /// <summary>The attribute was set and the file system re-wrote the file smaller.</summary>
    Compressed,
    /// <summary>The attribute was cleared and the file is stored whole again.</summary>
    Decompressed,
    /// <summary>Already in the state that was asked for.</summary>
    Unchanged,
    /// <summary>Refused before anything was tried: a protected path, or a link. Never attempted.</summary>
    Blocked,
    NotFound,
    /// <summary>The volume does not do this, or the file cannot be opened for it.</summary>
    Failed,
}

/// <summary>
/// One file or folder, and what compressing it actually returned.
/// </summary>
/// <param name="Before">Bytes on disk before. Not the length — the clusters.</param>
/// <param name="After">Bytes on disk after.</param>
public readonly record struct CompressResult(
    string Path,
    CompressOutcome Outcome,
    long Before,
    long After,
    string? Message = null)
{
    public bool Succeeded => Outcome is CompressOutcome.Compressed or CompressOutcome.Decompressed;

    /// <summary>
    /// What came back. Negative when compressing made it bigger, which happens on data that
    /// is already compressed and is reported rather than hidden.
    /// </summary>
    public long Freed => Before - After;
}

/// <summary>
/// Turns NTFS compression on or off for something already on disk (PRD F7.11).
/// <para>
/// The companion to the detector that finds candidates: logs, text and code compress by half
/// or better and are read rarely enough that the processor cost never shows. Space without
/// deleting anything — the file is still there, still opens, still has the same length.
/// </para>
/// <para>
/// ⚠️ <b>The gain is measured, never the catalogue's guess.</b> The candidate list carries an
/// assumed ratio per kind of file, which is a number from somebody else's disk. This reads
/// the clusters before and the clusters after, through <c>GetCompressedFileSize</c>, and
/// reports the difference — including when the difference is negative, which is what already
/// compressed data does.
/// </para>
/// <para>
/// ⚠️ On a folder the attribute governs what is <b>written next</b>; it does not reach the
/// files already inside. So a folder is set and then its files are set one by one, and the
/// figure reported is the sum of what those files gave back.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class CompressionService
{
    public static CompressResult Compress(string path) => Apply(path, compress: true);

    public static CompressResult Decompress(string path) => Apply(path, compress: false);

    private static CompressResult Apply(string path, bool compress)
    {
        if (string.IsNullOrWhiteSpace(path)) return new CompressResult(path ?? string.Empty, CompressOutcome.NotFound, 0, 0);

        string full = Path.GetFullPath(path);

        ProtectionVerdict guard = ProtectedPaths.Check(full);
        if (guard.IsProtected)
            return new CompressResult(full, CompressOutcome.Blocked, 0, 0, MoveService.Describe(guard.Reason));

        bool isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
            return new CompressResult(full, CompressOutcome.NotFound, 0, 0);

        return isDirectory ? ApplyToTree(full, compress) : ApplyToFile(full, compress);
    }

    private static CompressResult ApplyToFile(string path, bool compress)
    {
        long before = Kernel32.CompressedSizeOf(path);

        bool already = IsCompressed(path);
        if (already == compress) return new CompressResult(path, CompressOutcome.Unchanged, before, before);

        if (!Kernel32.SetCompression(path, compress, isDirectory: false))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return new CompressResult(path, CompressOutcome.Failed, before, before, $"FSCTL_SET_COMPRESSION: {error}");
        }

        long after = Kernel32.CompressedSizeOf(path);

        return new CompressResult(path,
            compress ? CompressOutcome.Compressed : CompressOutcome.Decompressed,
            before, after);
    }

    /// <summary>
    /// The folder, then everything already inside it.
    /// <para>
    /// Setting the attribute on a folder only decides how files written into it <em>later</em>
    /// are stored. A folder set and left would report a saving of zero and be perfectly
    /// truthful about it, having done nothing to the gigabytes already there.
    /// </para>
    /// </summary>
    private static CompressResult ApplyToTree(string root, bool compress)
    {
        // ⚠️ A link is a door, not a room — see Links. Set on one, the attribute lands on the
        // folder it stands for, wherever that is, and the walk goes on through it. The index
        // shows the link here and none of what is behind it, so the gain reported would be
        // that other folder's. Compressing that one is a choice made by selecting it.
        if (Links.IsLink(root))
        {
            return new CompressResult(root, CompressOutcome.Blocked, 0, 0,
                                      L.T("compress.isLink", new DirectoryInfo(root).LinkTarget ?? string.Empty));
        }

        Kernel32.SetCompression(root, compress, isDirectory: true);

        long before = 0;
        long after = 0;
        int touched = 0;
        string? firstError = null;

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };

        // Measured on 5 October 2026: this walk went through every junction in the tree, so
        // a folder holding one compressed a folder somewhere else — on another drive, if that
        // is where the link led — and counted what that gave back as this folder's saving. A
        // file symlink was the same door one level down: compressing it compresses the file it
        // names. Links are now neither entered nor touched.
        var walk = new FileSystemEnumerable<(string Path, bool IsDirectory)>(
            root,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !Links.IsLink(ref entry),
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => !Links.IsLink(ref entry),
        };

        using IEnumerator<(string Path, bool IsDirectory)> entries = walk.GetEnumerator();

        while (true)
        {
            // ⚠️ The disk does not hold still for this. A folder that goes away between being
            // listed and being opened throws out of the walk — and out of here it used to reach
            // the window's async handler, which takes the app down with it.
            try
            {
                if (!entries.MoveNext()) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstError ??= ex.Message;
                break;
            }

            (string path, bool isDirectory) = entries.Current;

            // Every path, not just the root: the protected list applies to what is being touched.
            if (ProtectedPaths.Check(path).IsProtected) continue;

            if (isDirectory)
            {
                Kernel32.SetCompression(path, compress, isDirectory: true);
                continue;
            }

            CompressResult one = ApplyToFile(path, compress);

            before += one.Before;
            after += one.After;

            if (one.Succeeded) touched++;
            else if (one.Outcome == CompressOutcome.Failed) firstError ??= one.Message;
        }

        return new CompressResult(root,
            touched > 0
                ? compress ? CompressOutcome.Compressed : CompressOutcome.Decompressed
                : CompressOutcome.Unchanged,
            before, after, firstError);
    }

    /// <summary>Whether NTFS is already storing this one compressed.</summary>
    public static bool IsCompressed(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Compressed) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
