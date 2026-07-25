using System.Runtime.Versioning;

namespace Vacuon.Core.Safety;

/// <summary>Why a path may not be deleted.</summary>
public enum ProtectionReason
{
    /// <summary>Not protected — deletion is allowed.</summary>
    None,
    /// <summary>The root of a volume. Deleting it means deleting the disk.</summary>
    VolumeRoot,
    /// <summary>Windows itself, or a folder it needs in order to boot.</summary>
    OperatingSystem,
    /// <summary>Installed program binaries. Uninstall through the app, not by deleting files.</summary>
    InstalledProgram,
    /// <summary>A well-known user folder (Documents, Desktop…). Files inside are deletable, the folder is not.</summary>
    UserProfileFolder,
    /// <summary>Managed by the kernel — pagefile, hiberfil, swapfile, NTFS metafiles.</summary>
    KernelManaged,
    /// <summary>Credential and key stores.</summary>
    Credentials,
    /// <summary>Vacuon's own installation. Self-deletion is never useful.</summary>
    Vacuon,
}

public readonly record struct ProtectionVerdict(ProtectionReason Reason, string? Detail = null)
{
    public bool IsProtected => Reason != ProtectionReason.None;
    public static ProtectionVerdict Allowed => new(ProtectionReason.None);
}

/// <summary>
/// The paths Vacuon refuses to delete, no matter what the caller asks.
/// <para>
/// There is deliberately no flag, setting or override that unblocks this list. An app
/// that deletes files gets exactly one chance to be wrong, and the cost of a false
/// "blocked" is a mildly annoyed user, while the cost of a false "allowed" is an
/// unbootable machine.
/// </para>
/// <para>
/// Checking happens on the CANONICAL path: symlinks, junctions, 8.3 short names and
/// the <c>\\?\</c> prefix are all resolved first, so a caller cannot smuggle
/// <c>C:\Windows</c> past the check by spelling it <c>C:\WINDOW~1</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedPaths
{
    /// <summary>Folders that must survive, matched on the whole path.</summary>
    private static readonly (string Folder, ProtectionReason Reason)[] ExactFolders = BuildExactFolders();

    /// <summary>Anything at or below these is off limits.</summary>
    private static readonly (string Prefix, ProtectionReason Reason)[] Subtrees = BuildSubtrees();

    /// <summary>File names the kernel owns. Freeing them needs an API, not a delete.</summary>
    private static readonly string[] KernelFiles =
    [
        "pagefile.sys", "swapfile.sys", "hiberfil.sys", "dumpstack.log.tmp",
        "$mft", "$mftmirr", "$logfile", "$volume", "$attrdef", "$bitmap",
        "$boot", "$badclus", "$secure", "$upcase", "$extend",
    ];

    /// <summary>
    /// Is this path safe to delete?
    /// </summary>
    /// <param name="path">Any path. Does not need to exist.</param>
    public static ProtectionVerdict Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ProtectionVerdict(ProtectionReason.VolumeRoot);

        string raw = path.Trim().Trim('"');

        // A bare drive spec is drive-RELATIVE on Windows: Path.GetFullPath("C:")
        // returns the process's current directory on C:, not the root. As a deletion
        // target that is a trap, so "C:" is read as the volume root and refused.
        if (raw.Length == 2 && raw[1] == ':' && char.IsLetter(raw[0]))
            return new ProtectionVerdict(ProtectionReason.VolumeRoot, raw + "\\");

        string full = Canonicalize(path);
        if (full.Length == 0) return new ProtectionVerdict(ProtectionReason.VolumeRoot);

        // A volume root is 3 characters: "C:\". Deleting it means deleting the disk.
        if (full.Length <= 3 && full.EndsWith(":\\", StringComparison.Ordinal))
            return new ProtectionVerdict(ProtectionReason.VolumeRoot, full);

        string trimmed = full.TrimEnd('\\');
        string fileName = Path.GetFileName(trimmed).ToLowerInvariant();

        // Kernel-owned files live at the volume root and must be matched by name:
        // pagefile.sys is not deletable, and moving it is powercfg's job.
        if (Array.IndexOf(KernelFiles, fileName) >= 0)
            return new ProtectionVerdict(ProtectionReason.KernelManaged, fileName);

        string lower = trimmed.ToLowerInvariant();

        foreach ((string folder, ProtectionReason reason) in ExactFolders)
        {
            if (folder.Length > 0 && string.Equals(lower, folder, StringComparison.Ordinal))
                return new ProtectionVerdict(reason, trimmed);
        }

        foreach ((string prefix, ProtectionReason reason) in Subtrees)
        {
            if (prefix.Length == 0) continue;

            // Prefix plus separator: "C:\Windows2" must not match "C:\Windows".
            if (lower.StartsWith(prefix + "\\", StringComparison.Ordinal) ||
                string.Equals(lower, prefix, StringComparison.Ordinal))
            {
                return new ProtectionVerdict(reason, trimmed);
            }
        }

        return ProtectionVerdict.Allowed;
    }

    public static bool IsProtected(string path) => Check(path).IsProtected;

    /// <summary>
    /// Resolves a path to its canonical form so that alternative spellings cannot
    /// bypass the comparison.
    /// </summary>
    private static string Canonicalize(string path)
    {
        string candidate = path.Trim().Trim('"');

        // Strip the long-path prefixes before comparing; they are spelling, not location.
        if (candidate.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            candidate = @"\\" + candidate[8..];
        else if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal))
            candidate = candidate[4..];

        try
        {
            string full = Path.GetFullPath(candidate);

            // Resolve reparse points: a junction pointing at C:\Windows must be caught.
            try
            {
                FileSystemInfo? resolved = Directory.Exists(full)
                    ? new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)
                    : File.Exists(full) ? new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) : null;

                if (resolved is not null) full = resolved.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                            or NotSupportedException or ArgumentException)
            {
                // Unresolvable link: keep the literal path. Falling back to the original
                // is the conservative choice — it still gets checked below.
            }

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                        or PathTooLongException or IOException)
        {
            return candidate;
        }
    }

    private static (string, ProtectionReason)[] BuildExactFolders()
    {
        string Folder(Environment.SpecialFolder id) =>
            Environment.GetFolderPath(id).TrimEnd('\\').ToLowerInvariant();

        // The FOLDER is protected; the files inside are not. Someone may well want to
        // delete a 9 GB render sitting in Videos — they must not delete Videos itself.
        return
        [
            (Folder(Environment.SpecialFolder.UserProfile), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.MyDocuments), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.MyPictures), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.MyVideos), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.MyMusic), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.Desktop), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.ApplicationData), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.LocalApplicationData), ProtectionReason.UserProfileFolder),
            (Folder(Environment.SpecialFolder.ProgramFiles), ProtectionReason.InstalledProgram),
            (Folder(Environment.SpecialFolder.ProgramFilesX86), ProtectionReason.InstalledProgram),
            (Folder(Environment.SpecialFolder.CommonApplicationData), ProtectionReason.OperatingSystem),
        ];
    }

    private static (string, ProtectionReason)[] BuildSubtrees()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                                    .TrimEnd('\\').ToLowerInvariant();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System)
                                   .TrimEnd('\\').ToLowerInvariant();
        string systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
                                      .TrimEnd('\\').ToLowerInvariant();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                                    .TrimEnd('\\').ToLowerInvariant();

        string vacuon = AppContext.BaseDirectory.TrimEnd('\\').ToLowerInvariant();

        var list = new List<(string, ProtectionReason)>
        {
            // Everything under Windows. WinSxS, drivers, the boot chain and the
            // registry hives all live here; there is no safe subset to carve out by
            // hand, and the legitimate cleanups go through DISM, not through delete.
            (windows, ProtectionReason.OperatingSystem),
            (system, ProtectionReason.OperatingSystem),
            (systemX86, ProtectionReason.OperatingSystem),

            // Credential and key stores.
            ($@"{appData}\microsoft\crypto", ProtectionReason.Credentials),
            ($@"{appData}\microsoft\protect", ProtectionReason.Credentials),
            ($@"{appData}\microsoft\vault", ProtectionReason.Credentials),

            // Vacuon must not delete itself while it runs.
            (vacuon, ProtectionReason.Vacuon),
        };

        // System Volume Information and the recycler live on every volume.
        foreach (DriveInfo drive in SafeDrives())
        {
            string root = drive.Name.TrimEnd('\\').ToLowerInvariant();
            list.Add(($@"{root}\system volume information", ProtectionReason.OperatingSystem));
            list.Add(($@"{root}\$recycle.bin", ProtectionReason.OperatingSystem));
            list.Add(($@"{root}\recovery", ProtectionReason.OperatingSystem));
            list.Add(($@"{root}\boot", ProtectionReason.OperatingSystem));
        }

        return [.. list];
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return []; }

        return drives.Where(d =>
        {
            try { return d.IsReady; }
            catch (IOException) { return false; }
        });
    }
}
