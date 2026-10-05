using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Safety;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

public enum LinkOutcome
{
    /// <summary>The copy is gone and its name now belongs to the keeper's bytes.</summary>
    Linked,
    /// <summary>Refused by <see cref="ProtectedPaths"/>. Never attempted.</summary>
    Blocked,
    /// <summary>One of the two is not there any more.</summary>
    NotFound,
    /// <summary>They already share one set of bytes, so there is nothing to free.</summary>
    AlreadyLinked,
    /// <summary>Different volumes. A hard link cannot cross one.</summary>
    DifferentVolumes,
    /// <summary>They are not the same file any more, whatever the search found earlier.</summary>
    ContentChanged,
    /// <summary>The link could not be made. The copy was put back exactly as it was.</summary>
    Failed,
}

/// <summary>What one replacement did.</summary>
public readonly record struct LinkResult(string Path, LinkOutcome Outcome, long BytesFreed, string? Message = null)
{
    public bool Succeeded => Outcome == LinkOutcome.Linked;
}

/// <summary>
/// Replaces a redundant copy with a second name for the copy that stays (PRD F4.4).
/// <para>
/// The space comes back and every path keeps working. Measured on a real volume: two
/// identical 200 MiB files, the copy replaced by a link, free space up by exactly 200 MiB,
/// the old path still opening and reading the same bytes and still reporting 200 MiB of
/// length. The link costs under a millisecond — nothing is copied, a name is added to a file
/// that is already there.
/// </para>
/// <para>
/// ⚠️ <b>This is not a shortcut, and the difference is the point.</b> A <c>.lnk</c> is a
/// little file that something has to know how to follow; a program opening the old path
/// would get that little file and fail. A hard link is another name on the same bytes: at
/// the level anything reads a file, the old path <em>is</em> the file. Nothing has to know.
/// </para>
/// <para>
/// ⚠️ <b>What genuinely changes.</b> Afterwards there is one file with two names, so writing
/// through either name changes what the other one reads — measured, one byte written through
/// the keeper and the other name's hash changed with it. Two independent copies of an
/// installer become one. For anything that gets edited, that is a different thing from what
/// was there before, and the confirmation says so before anybody agrees to it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class HardLinkService
{
    /// <summary>
    /// Points <paramref name="redundant"/> at the bytes of <paramref name="keeper"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ The order is what keeps this from being a way to lose a file. A hard link cannot be
    /// created over a name that exists, so the copy has to go first — and a delete followed
    /// by a link that fails is a file nobody has any more. So the copy is <b>renamed</b> to a
    /// name beside it, the link is made, and only a link that exists gets the rename thrown
    /// away. A link that fails puts the name back exactly where it was.
    /// </remarks>
    public static LinkResult Replace(string keeper, string redundant)
    {
        if (string.IsNullOrWhiteSpace(keeper) || string.IsNullOrWhiteSpace(redundant))
            return new LinkResult(redundant, LinkOutcome.NotFound, 0);

        string keeperFull = Path.GetFullPath(keeper);
        string copyFull = Path.GetFullPath(redundant);

        // Both ends, not just the one being removed: adding a name to a protected file is
        // still touching it, and the list has no override on either side.
        ProtectionVerdict guard = ProtectedPaths.Check(copyFull);
        if (guard.IsProtected) return new LinkResult(copyFull, LinkOutcome.Blocked, 0, MoveService.Describe(guard.Reason));

        guard = ProtectedPaths.Check(keeperFull);
        if (guard.IsProtected) return new LinkResult(copyFull, LinkOutcome.Blocked, 0, MoveService.Describe(guard.Reason));

        if (!File.Exists(keeperFull) || !File.Exists(copyFull))
            return new LinkResult(copyFull, LinkOutcome.NotFound, 0);

        if (string.Equals(keeperFull, copyFull, StringComparison.OrdinalIgnoreCase))
            return new LinkResult(copyFull, LinkOutcome.AlreadyLinked, 0);

        if (!string.Equals(Path.GetPathRoot(keeperFull), Path.GetPathRoot(copyFull),
                           StringComparison.OrdinalIgnoreCase))
        {
            return new LinkResult(copyFull, LinkOutcome.DifferentVolumes, 0);
        }

        // Already one file under two names: the bytes were freed by whoever did that, and
        // doing it again would free nothing and report that it had.
        long keeperRecord = FileIdentity.RecordNumberOf(keeperFull);
        long copyRecord = FileIdentity.RecordNumberOf(copyFull);
        if (keeperRecord > 0 && keeperRecord == copyRecord)
            return new LinkResult(copyFull, LinkOutcome.AlreadyLinked, 0);

        // ⚠️ Read both again, now. The search hashed them minutes or hours ago, and this
        // step does not move a file aside — it replaces its contents with somebody else's,
        // and there is no undo for that. A plan is not evidence about the present.
        if (!SameContent(keeperFull, copyFull))
            return new LinkResult(copyFull, LinkOutcome.ContentChanged, 0);

        string aside = copyFull + ".vacuon-link-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            File.Move(copyFull, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LinkResult(copyFull, LinkOutcome.Failed, 0, ex.Message);
        }

        if (!Kernel32.CreateHardLink(copyFull, keeperFull, 0))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            // Back exactly where it was. Anything else here loses somebody's file.
            try { File.Move(aside, copyFull); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new LinkResult(copyFull, LinkOutcome.Failed, 0,
                                      L.T("link.strandedCopy", aside));
            }

            return new LinkResult(copyFull, LinkOutcome.Failed, 0, $"CreateHardLink: {error}");
        }

        // ⚠️ What deleting the copy gives back is read off the copy before it goes, not taken
        // from its length: its own clusters, which are fewer than its length when NTFS stores
        // it compressed — and nothing at all when it is itself a second name somewhere, because
        // that other name keeps the clusters. The length went into the total either way.
        int names = FileIdentity.NameCountOf(aside);
        long held = Kernel32.CompressedSizeOf(aside);

        if (!Discard(aside, otherNames: names != 1))
        {
            // The link is there and the path works; what is left is a stray file holding the
            // space this was supposed to give back. Reported as such, not as a success.
            return new LinkResult(copyFull, LinkOutcome.Failed, 0, L.T("link.strandedCopy", aside));
        }

        return new LinkResult(copyFull, LinkOutcome.Linked, names == 1 ? held : 0);
    }

    /// <summary>
    /// Tells the index what a successful <see cref="Replace"/> did to the disk.
    /// <para>
    /// ⚠️ It used to be told nothing. The copy stayed in it as a file of its own, the keeper
    /// with one name, so the search that rebuilds the list right afterwards found every
    /// group again — each replaced copy back on the list as recoverable, beside a status
    /// line saying it had just been freed. Linking it again was refused as "already linked",
    /// which was the first true thing the list said about it.
    /// </para>
    /// </summary>
    /// <returns>What left the index: the copy's own record, when it had no other name.</returns>
    public static Removal Record(VolumeIndex index, string keeper, string copy)
    {
        ArgumentNullException.ThrowIfNull(index);

        int kept = index.FindEntry(keeper);
        int gone = index.FindEntry(copy);
        if (kept < 0 || gone < 0 || kept == gone) return default;

        // The keeper's record now answers to the copy's path as well.
        index.ChangeNameCount(kept, +1);

        // The copy's record lost that name. With no other, it is gone from the disk; with
        // another, it lives on there and only its count changes — the path the index shows
        // for it is the one it just lost, and the next scan puts it under the one it kept.
        if (index.Entries[gone].HardLinkCount > 1)
        {
            index.ChangeNameCount(gone, -1);
            return default;
        }

        return index.MarkDeleted(gone);
    }

    /// <summary>Removes the old copy's name. False when it will not go.</summary>
    /// <param name="otherNames">Whether the file may live on under another name once this one is gone.</param>
    private static bool Discard(string aside, bool otherNames)
    {
        try
        {
            var file = new FileInfo(aside);
            FileAttributes attributes = file.Attributes;

            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                file.Delete();
                return !File.Exists(aside);
            }

            // ⚠️ Read-only is not a decision anybody made about keeping this copy, and Delete
            // refuses on it. The copy used to stay beside its new link under a made-up name,
            // holding exactly the space this was meant to give back, reported as a failure.
            file.IsReadOnly = false;

            if (!otherNames)
            {
                file.Delete();
                return !File.Exists(aside);
            }

            // The mark belongs to the file, not to the name. A copy that is also a name
            // somewhere else lives on under it, and gets its mark back through a handle held
            // across the delete — the name itself goes when the handle closes.
            using (SafeFileHandle handle = File.OpenHandle(aside, FileMode.Open, FileAccess.Write,
                                                           FileShare.ReadWrite | FileShare.Delete))
            {
                File.Delete(aside);
                File.SetAttributes(handle, attributes);
            }

            return !File.Exists(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Byte for byte, streamed.</summary>
    private static bool SameContent(string left, string right)
    {
        try
        {
            using FileStream a = Open(left);
            using FileStream b = Open(right);

            if (a.Length != b.Length) return false;

            byte[] hashA = SHA256.HashData(a);
            byte[] hashB = SHA256.HashData(b);

            return hashA.AsSpan().SequenceEqual(hashB);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
}
