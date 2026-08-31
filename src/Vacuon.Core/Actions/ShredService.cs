using System.Runtime.Versioning;
using System.Security.Cryptography;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Actions;

/// <summary>Why overwriting this particular file would not do what somebody expects.</summary>
[Flags]
public enum ShredDoubt
{
    None = 0,

    /// <summary>
    /// The volume reports no seek penalty, so it is solid state. Wear levelling writes the
    /// new bytes to different cells and leaves the old ones addressable by the drive but not
    /// by the operating system.
    /// </summary>
    SolidState = 1 << 0,

    /// <summary>Compressed or sparse: writing over it allocates elsewhere and abandons the old clusters.</summary>
    MovesWhenWritten = 1 << 1,

    /// <summary>Small enough to live inside its MFT record, where a file write does not reach.</summary>
    MaybeResident = 1 << 2,

    /// <summary>A shadow copy of the volume already holds a version of it.</summary>
    ShadowCopies = 1 << 3,
}

public enum ShredOutcome
{
    /// <summary>The bytes were overwritten and the file was removed.</summary>
    Shredded,
    Blocked,
    NotFound,
    Failed,
}

/// <summary>What one shred did, and what it does not promise.</summary>
public readonly record struct ShredResult(
    string Path,
    ShredOutcome Outcome,
    long Bytes,
    ShredDoubt Doubt,
    string? Message = null)
{
    public bool Succeeded => Outcome == ShredOutcome.Shredded;

    /// <summary>True when the overwrite happened but cannot be claimed to have erased anything.</summary>
    public bool IsUncertain => Doubt != ShredDoubt.None;
}

/// <summary>
/// Overwrites a file's bytes before removing it (PRD F7.6).
/// <para>
/// ⚠️ <b>This is the feature with the most honest thing to say and the least ability to
/// deliver it.</b> Overwriting works on a spinning disk, where a sector rewritten is that
/// sector rewritten. On an SSD it does not: wear levelling puts the new bytes in different
/// cells and the old ones stay on the drive, unaddressable by the operating system and
/// perfectly readable by the controller. The correct answer on solid state is whole-volume
/// encryption from the start, or the drive's own secure erase — not this.
/// </para>
/// <para>
/// So this reports the doubts it has rather than a comforting "securely deleted". The volume
/// already tells the app whether it has a seek penalty, so the SSD case is <b>detected</b>,
/// not guessed at, and said in the result. Three other cases are checked too: a compressed
/// or sparse file writes to fresh clusters and abandons the old ones; a small file lives
/// inside its own MFT record where a file-level write never reaches; and a shadow copy may
/// hold a version of the file that nothing here touches.
/// </para>
/// <para>
/// ⚠️ <b>One pass, on purpose.</b> The thirty-five passes people quote are Gutmann's, and
/// they are about the encoding of MFM drives from 1996. On anything made since, a single
/// pass of random bytes is exactly as unrecoverable as thirty-five — and a routine that ran
/// thirty-five would take thirty-five times as long while implying a certainty it does not
/// have.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShredService
{
    /// <summary>Below this a file may be resident in its MFT record rather than in clusters.</summary>
    public const long ResidentCeiling = 900;

    private const int ChunkBytes = 1024 * 1024;

    /// <summary>
    /// Overwrites <paramref name="path"/> with random bytes and deletes it.
    /// </summary>
    /// <param name="volumeIsSolidState">
    /// Whether the volume reports no seek penalty. The caller knows this from the scan, and
    /// passing it in keeps this from having to open the device to find out.
    /// </param>
    /// <param name="hasShadowCopies">Whether the volume holds shadow copies.</param>
    public static ShredResult Shred(string path, bool volumeIsSolidState, bool hasShadowCopies = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ShredResult(path ?? string.Empty, ShredOutcome.NotFound, 0, ShredDoubt.None);

        string full = Path.GetFullPath(path);

        ProtectionVerdict guard = ProtectedPaths.Check(full);
        if (guard.IsProtected)
            return new ShredResult(full, ShredOutcome.Blocked, 0, ShredDoubt.None, MoveService.Describe(guard.Reason));

        if (Directory.Exists(full))
            return new ShredResult(full, ShredOutcome.Blocked, 0, ShredDoubt.None, "a folder is not a stream of bytes");

        if (!File.Exists(full)) return new ShredResult(full, ShredOutcome.NotFound, 0, ShredDoubt.None);

        var info = new FileInfo(full);
        long length = info.Length;

        ShredDoubt doubt = DoubtsAbout(info, volumeIsSolidState, hasShadowCopies);

        try
        {
            // Read-only, hidden and system come off first: a write to a read-only file fails
            // and would leave the file whole and the caller told it had been shredded.
            if ((info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(full, FileAttributes.Normal);

            Overwrite(full, length);
            File.Delete(full);

            if (File.Exists(full))
                return new ShredResult(full, ShredOutcome.Failed, length, doubt, "the file is still there");

            return new ShredResult(full, ShredOutcome.Shredded, length, doubt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return new ShredResult(full, ShredOutcome.Failed, length, doubt, ex.Message);
        }
    }

    /// <summary>
    /// Everything about this file that makes "the bytes are gone" an overstatement.
    /// </summary>
    public static ShredDoubt DoubtsAbout(FileInfo info, bool volumeIsSolidState, bool hasShadowCopies)
    {
        ShredDoubt doubt = ShredDoubt.None;

        if (volumeIsSolidState) doubt |= ShredDoubt.SolidState;
        if (hasShadowCopies) doubt |= ShredDoubt.ShadowCopies;

        FileAttributes attributes = info.Attributes;

        // Compressed and sparse files are not written in place: NTFS allocates for the new
        // contents and lets the old clusters go, still holding what was there.
        if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Encrypted)) != 0)
            doubt |= ShredDoubt.MovesWhenWritten;

        // A small file lives inside its own MFT record. A write through the file API changes
        // the record, but the previous record content is not overwritten cluster by cluster.
        if (info.Length <= ResidentCeiling) doubt |= ShredDoubt.MaybeResident;

        return doubt;
    }

    /// <summary>
    /// One pass of cryptographic random over the whole length, flushed to the device.
    /// <para>
    /// ⚠️ <c>FileOptions.WriteThrough</c> is not decoration. Without it the writes sit in the
    /// cache and the delete that follows can beat them to the disk — the file would be gone
    /// and its old clusters never touched, which is the failure that looks exactly like
    /// success.
    /// </para>
    /// </summary>
    private static void Overwrite(string path, long length)
    {
        if (length <= 0) return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                                          ChunkBytes, FileOptions.WriteThrough);

        byte[] noise = new byte[(int)Math.Min(length, ChunkBytes)];
        long written = 0;

        while (written < length)
        {
            RandomNumberGenerator.Fill(noise);

            int take = (int)Math.Min(noise.Length, length - written);
            stream.Write(noise, 0, take);
            written += take;
        }

        stream.Flush(flushToDisk: true);
    }
}
