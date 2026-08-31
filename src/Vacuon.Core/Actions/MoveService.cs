using System.Runtime.Versioning;
using Vacuon.Core.Localization;
using Vacuon.Core.Safety;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

public enum MoveOutcome
{
    Moved,
    /// <summary>Refused by <see cref="ProtectedPaths"/>. Never attempted.</summary>
    Blocked,
    /// <summary>Already gone by the time we got there.</summary>
    NotFound,
    /// <summary>The destination is the folder the item is already in. Nothing to do.</summary>
    AlreadyThere,
    /// <summary>A folder cannot be moved into itself or into anything below it.</summary>
    IntoItself,
    /// <summary>Another process holds a handle.</summary>
    InUse,
    AccessDenied,
    Failed,
}

/// <summary>Whether the chosen folder can receive files at all.</summary>
public enum DestinationVerdict
{
    Ok,
    /// <summary>No such folder.</summary>
    Missing,
    /// <summary>The path exists but is a file.</summary>
    NotAFolder,
    /// <summary>Somewhere Windows or an installed program owns.</summary>
    Protected,
}

/// <summary>
/// One item's move. <see cref="Destination"/> is the full path it ended up at, which is
/// not always <c>folder\originalName</c> — see <see cref="Renamed"/>.
/// </summary>
public sealed record MoveResult(
    string Source,
    string Destination,
    MoveOutcome Outcome,
    long Bytes,
    bool IsDirectory,
    string? Message = null)
{
    public bool Succeeded => Outcome == MoveOutcome.Moved;

    /// <summary>
    /// True when the destination folder already held that name and the item went in under
    /// another one. Nothing is ever overwritten, so the rename is the only alternative to
    /// refusing — but the user has to be told, or two files silently become one name.
    /// </summary>
    public bool Renamed => !string.Equals(
        Path.GetFileName(Source.TrimEnd('\\')),
        Path.GetFileName(Destination.TrimEnd('\\')),
        StringComparison.OrdinalIgnoreCase);

    public string FinalName => Path.GetFileName(Destination.TrimEnd('\\'));

    /// <summary>
    /// True when the item left this volume — the only case where a move frees space, and
    /// the only case where the item stops existing as far as this volume's index knows.
    /// </summary>
    public bool CrossVolume => !MoveService.SameVolume(Source, Destination);
}

public sealed record MoveReport(
    IReadOnlyList<MoveResult> Results,
    string Destination,
    DestinationVerdict Verdict,
    bool CrossVolume,
    bool WasDryRun)
{
    public int MovedCount => Results.Count(r => r.Succeeded);
    public int FailedCount => Results.Count(r => !r.Succeeded && r.Outcome != MoveOutcome.AlreadyThere);
    public int SkippedCount => Results.Count(r => r.Outcome == MoveOutcome.AlreadyThere);
    public long Bytes => Results.Where(r => r.Succeeded).Sum(r => r.Bytes);

    public IEnumerable<MoveResult> Movable => Results.Where(r => r.Succeeded);
    public IEnumerable<MoveResult> Blocked => Results.Where(r => r.Outcome == MoveOutcome.Blocked);
    public IEnumerable<MoveResult> Renames => Results.Where(r => r.Succeeded && r.Renamed);
    public IEnumerable<MoveResult> Failures =>
        Results.Where(r => !r.Succeeded && r.Outcome != MoveOutcome.AlreadyThere);
}

/// <summary>
/// Moves files and folders into another folder.
/// <para>
/// Sorting a folder by hand — this one stays, that one goes to <c>Approved\</c> — is the
/// one bulk operation Vacuon had no answer for: the only batch action was destructive.
/// Moving is not, and it deliberately does not go through the Recycle Bin at all.
/// </para>
/// <para>
/// Two rules shape everything here. Nothing is ever overwritten: a name that is already
/// taken at the destination gets a <c>(2)</c> suffix and the report says so. And a move
/// within one volume frees no space whatsoever — it rewrites a directory entry — so the
/// caller is handed the bytes moved and the volume comparison, never a "freed" figure it
/// could quote by accident.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MoveService
{
    /// <summary>
    /// Works out what would happen, touching nothing. The confirmation dialog shows this.
    /// </summary>
    public MoveReport Plan(IEnumerable<string> paths, string destination) =>
        Run(paths, destination, dryRun: true, CancellationToken.None);

    public MoveReport Execute(IEnumerable<string> paths, string destination,
                              CancellationToken cancellationToken = default) =>
        Run(paths, destination, dryRun: false, cancellationToken);

    private MoveReport Run(IEnumerable<string> paths, string destination, bool dryRun,
                           CancellationToken cancellationToken)
    {
        string folder = Normalize(destination);
        DestinationVerdict verdict = CheckDestination(folder);

        var results = new List<MoveResult>();

        if (verdict != DestinationVerdict.Ok)
            return new MoveReport(results, folder, verdict, CrossVolume: false, dryRun);

        // Names this batch has already claimed at the destination. Two files called
        // render.mp4 coming from two different folders would otherwise both be planned
        // onto the same target — and the shell, told not to ask, would overwrite one
        // with the other.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool crossVolume = false;

        // Same collapse the delete uses: a folder carries its children with it, so an item
        // whose ancestor is also selected must not be moved a second time from a path that
        // no longer exists.
        foreach (string path in DeleteService.Collapse(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!SameVolume(path, folder)) crossVolume = true;

            results.Add(dryRun
                ? PlanOne(path, folder, taken)
                : MoveOne(path, folder, taken));
        }

        return new MoveReport(results, folder, verdict, crossVolume, dryRun);
    }

    private static MoveResult PlanOne(string path, string folder, HashSet<string> taken)
    {
        (MoveResult? refusal, string target, long bytes, bool isDirectory) = Prepare(path, folder, taken);
        if (refusal is not null) return refusal;

        taken.Add(target);
        return new MoveResult(path, target, MoveOutcome.Moved, bytes, isDirectory);
    }

    private static MoveResult MoveOne(string path, string folder, HashSet<string> taken)
    {
        (MoveResult? refusal, string target, long bytes, bool isDirectory) = Prepare(path, folder, taken);
        if (refusal is not null) return refusal;

        taken.Add(target);
        return Move(path, target, bytes, isDirectory);
    }

    /// <summary>
    /// Everything both the plan and the run have to agree on: is this allowed, and what
    /// exactly would the item be called once it is over there.
    /// </summary>
    private static (MoveResult? Refusal, string Target, long Bytes, bool IsDirectory) Prepare(
        string path, string folder, HashSet<string> taken)
    {
        bool isDirectory = Directory.Exists(path);

        ProtectionVerdict protection = ProtectedPaths.Check(path);
        if (protection.IsProtected)
        {
            return (new MoveResult(path, path, MoveOutcome.Blocked, 0, isDirectory,
                                   Describe(protection.Reason)), path, 0, isDirectory);
        }

        (long bytes, bool exists) = Measure(path, isDirectory);

        if (!exists)
            return (new MoveResult(path, path, MoveOutcome.NotFound, 0, isDirectory), path, 0, isDirectory);

        string parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? string.Empty;

        if (string.Equals(Normalize(parent), folder, StringComparison.OrdinalIgnoreCase))
            return (new MoveResult(path, path, MoveOutcome.AlreadyThere, bytes, isDirectory), path, bytes, isDirectory);

        // A folder cannot swallow itself. Windows refuses this too, but it refuses it
        // halfway through, after the first files have already been copied.
        if (isDirectory && IsInside(folder, path))
            return (new MoveResult(path, path, MoveOutcome.IntoItself, bytes, isDirectory), path, bytes, isDirectory);

        string target = FreeName(folder, Path.GetFileName(path.TrimEnd('\\')), isDirectory, taken);

        if (target.Length == 0)
        {
            return (new MoveResult(path, path, MoveOutcome.Failed, bytes, isDirectory,
                                   L.T("move.outcomeNoFreeName")), path, bytes, isDirectory);
        }

        return (null, target, bytes, isDirectory);
    }

    private static MoveResult Move(string source, string target, long bytes, bool isDirectory)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FileOperation.Move,
            // Both lists are double-null-terminated: pFrom and pTo are lists even when
            // they hold one path each, and a single terminator truncates the batch.
            pFrom = source + "\0\0",
            pTo = target + "\0\0",
            fFlags = FileOperationFlags.NoConfirmation
                   | FileOperationFlags.NoConfirmMkDir
                   | FileOperationFlags.NoErrorUi
                   // Undo information, so Explorer's Ctrl+Z can put it back. It costs
                   // nothing here and the app never claims it as a guarantee.
                   | FileOperationFlags.AllowUndo,
        };

        int code = Shell32.SHFileOperation(ref operation);

        if (code == 0 && !operation.fAnyOperationsAborted)
        {
            // "The shell returned zero" and "it is over there" are different statements.
            // Only the second one is worth reporting, so it gets checked.
            bool arrived = isDirectory ? Directory.Exists(target) : File.Exists(target);

            return arrived
                ? new MoveResult(source, target, MoveOutcome.Moved, bytes, isDirectory)
                : new MoveResult(source, target, MoveOutcome.Failed, 0, isDirectory,
                                 L.T("move.outcomeNotThere"));
        }

        MoveOutcome outcome = code switch
        {
            0x78 => MoveOutcome.AccessDenied,   // DE_ACCESSDENIEDSRC
            0x75 => MoveOutcome.Failed,         // DE_OPCANCELLED — the user hit Cancel
            0x7C => MoveOutcome.NotFound,       // DE_INVALIDFILES
            0x10000 => MoveOutcome.Failed,      // ERRORONDEST
            _ when operation.fAnyOperationsAborted => MoveOutcome.InUse,
            _ => MoveOutcome.Failed,
        };

        return new MoveResult(source, target, outcome, 0, isDirectory, $"SHFileOperation 0x{code:X}");
    }

    /// <summary>
    /// A name nothing else answers to at the destination — neither a file on disk nor
    /// another item in this same batch.
    /// </summary>
    /// <returns>The full target path, or an empty string when even 999 suffixes were taken.</returns>
    internal static string FreeName(string folder, string name, bool isDirectory, HashSet<string> taken)
    {
        string candidate = Path.Combine(folder, name);
        if (IsFree(candidate, taken)) return candidate;

        // A folder called "My.Videos" is not a file with an extension, so its whole name
        // is the stem — splitting it would produce "My (2).Videos".
        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isDirectory ? string.Empty : Path.GetExtension(name);

        for (int n = 2; n <= 999; n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (IsFree(candidate, taken)) return candidate;
        }

        return string.Empty;
    }

    private static bool IsFree(string path, HashSet<string> taken) =>
        !taken.Contains(path) && !File.Exists(path) && !Directory.Exists(path);

    /// <summary>
    /// Can this folder receive files?
    /// <para>
    /// Deliberately not the same question <see cref="ProtectedPaths"/> answers about a
    /// deletion. <c>Videos</c> is a protected folder — it must not be deleted — and it is
    /// still a perfectly ordinary place to move a video to. What is refused here is
    /// writing into what Windows and installed programs own.
    /// </para>
    /// </summary>
    internal static DestinationVerdict CheckDestination(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return DestinationVerdict.Missing;
        if (File.Exists(folder)) return DestinationVerdict.NotAFolder;
        if (!Directory.Exists(folder)) return DestinationVerdict.Missing;

        return ProtectedPaths.Check(folder).Reason switch
        {
            ProtectionReason.OperatingSystem => DestinationVerdict.Protected,
            ProtectionReason.InstalledProgram => DestinationVerdict.Protected,
            ProtectionReason.Credentials => DestinationVerdict.Protected,
            ProtectionReason.KernelManaged => DestinationVerdict.Protected,
            ProtectionReason.Vacuon => DestinationVerdict.Protected,
            _ => DestinationVerdict.Ok,
        };
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="folder"/> itself or below it.</summary>
    internal static bool IsInside(string candidate, string folder)
    {
        string a = Normalize(candidate);
        string b = Normalize(folder);

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same volume means the move is a directory-entry rewrite: instant, and it frees
    /// nothing. A different volume means a real copy followed by a delete.
    /// </summary>
    internal static bool SameVolume(string a, string b)
    {
        string rootA = Path.GetPathRoot(Path.GetFullPath(a)) ?? string.Empty;
        string rootB = Path.GetPathRoot(Path.GetFullPath(b)) ?? string.Empty;

        return rootA.Length > 0 && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));

            // A volume root keeps its backslash; everything else loses it, so that
            // comparisons and Path.Combine agree on one spelling.
            return full.Length <= 3 ? full : full.TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                        or PathTooLongException or IOException)
        {
            return string.Empty;
        }
    }

    internal static (long Bytes, bool Exists) Measure(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory) return (DirectorySize(path), true);

            var file = new FileInfo(path);
            return file.Exists ? (file.Length, true) : (0, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, false);
        }
    }

    private static long DirectorySize(string path)
    {
        long total = 0;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (string file in Directory.EnumerateFiles(path, "*", options))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    internal static string Describe(ProtectionReason reason) => L.T(reason switch
    {
        ProtectionReason.VolumeRoot => "protect.volumeRoot",
        ProtectionReason.OperatingSystem => "protect.operatingSystem",
        ProtectionReason.InstalledProgram => "protect.installedProgram",
        ProtectionReason.UserProfileFolder => "protect.userProfileFolder",
        ProtectionReason.KernelManaged => "protect.kernelManaged",
        ProtectionReason.Credentials => "protect.credentials",
        ProtectionReason.Vacuon => "protect.vacuon",
        _ => "protect.unknown",
    });
}
