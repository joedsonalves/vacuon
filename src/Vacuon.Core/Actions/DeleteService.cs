using System.Runtime.Versioning;
using Vacuon.Core.Localization;
using Vacuon.Core.Safety;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

/// <summary>How the caller wants the items gone.</summary>
public enum DeleteMode
{
    /// <summary>Recycle Bin. Recoverable, and the default everywhere in the UI.</summary>
    RecycleBin,
    /// <summary>Gone for good. Requires an explicit choice from the user.</summary>
    Permanent,
}

public enum DeleteOutcome
{
    Deleted,
    /// <summary>Refused by <see cref="ProtectedPaths"/>. Never attempted.</summary>
    Blocked,
    /// <summary>Already gone by the time we got there.</summary>
    NotFound,
    /// <summary>Another process holds a handle.</summary>
    InUse,
    /// <summary>No permission. Usually means the operation needs elevation.</summary>
    AccessDenied,
    Failed,
}

public sealed record DeleteResult(
    string Path,
    DeleteOutcome Outcome,
    long Bytes,
    bool IsDirectory,
    string? Message = null)
{
    public bool Succeeded => Outcome == DeleteOutcome.Deleted;
}

public sealed record DeleteReport(IReadOnlyList<DeleteResult> Results, DeleteMode Mode, bool WasDryRun)
{
    public int DeletedCount => Results.Count(r => r.Succeeded);
    public int FailedCount => Results.Count(r => !r.Succeeded);
    public long BytesFreed => Results.Where(r => r.Succeeded).Sum(r => r.Bytes);

    public IEnumerable<DeleteResult> Blocked => Results.Where(r => r.Outcome == DeleteOutcome.Blocked);
    public IEnumerable<DeleteResult> Failures => Results.Where(r => !r.Succeeded);
}

/// <summary>
/// Deletes files and folders — to the Recycle Bin or permanently.
/// <para>
/// Every path is checked against <see cref="ProtectedPaths"/> BEFORE anything is
/// attempted, and a blocked path is reported rather than skipped in silence.
/// </para>
/// <para>
/// This is the destructive half of the app, and it arrived before the reversible
/// quarantine of milestone M4. Until that lands, the Recycle Bin is the only undo
/// there is — which is exactly why it is the default and why the permanent path
/// demands a separate, explicit gesture from the caller.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeleteService
{
    /// <summary>
    /// Plans a deletion without touching the disk. The UI shows this before asking
    /// for confirmation, so the user sees exactly what is about to happen.
    /// </summary>
    public DeleteReport Plan(IEnumerable<string> paths, DeleteMode mode) =>
        Run(paths, mode, dryRun: true, CancellationToken.None);

    public DeleteReport Execute(IEnumerable<string> paths, DeleteMode mode,
                               CancellationToken cancellationToken = default) =>
        Run(paths, mode, dryRun: false, cancellationToken);

    private DeleteReport Run(IEnumerable<string> paths, DeleteMode mode, bool dryRun,
                             CancellationToken cancellationToken)
    {
        var results = new List<DeleteResult>();

        // Deduplicate and drop paths already covered by a selected ancestor: deleting
        // a folder takes its children with it, and trying them afterwards would report
        // spurious "not found" failures.
        foreach (string path in Collapse(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProtectionVerdict verdict = ProtectedPaths.Check(path);
            if (verdict.IsProtected)
            {
                results.Add(new DeleteResult(path, DeleteOutcome.Blocked, 0, IsDirectory(path),
                                             Describe(verdict.Reason)));
                continue;
            }

            (long bytes, bool isDirectory, bool exists) = Measure(path);

            if (!exists)
            {
                results.Add(new DeleteResult(path, DeleteOutcome.NotFound, 0, isDirectory));
                continue;
            }

            if (dryRun)
            {
                results.Add(new DeleteResult(path, DeleteOutcome.Deleted, bytes, isDirectory));
                continue;
            }

            results.Add(Delete(path, mode, bytes, isDirectory));
        }

        return new DeleteReport(results, mode, dryRun);
    }

    private static DeleteResult Delete(string path, DeleteMode mode, long bytes, bool isDirectory)
    {
        try
        {
            if (mode == DeleteMode.RecycleBin) return ToRecycleBin(path, bytes, isDirectory);

            if (isDirectory) Directory.Delete(path, recursive: true);
            else
            {
                // Clear the read-only attribute first: File.Delete throws on it, and a
                // read-only flag is not a safety decision the user made about this file.
                var info = new FileInfo(path);
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
            }

            return new DeleteResult(path, DeleteOutcome.Deleted, bytes, isDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DeleteResult(path, DeleteOutcome.AccessDenied, 0, isDirectory, ex.Message);
        }
        catch (IOException ex)
        {
            // IOException covers both "file in use" and genuine I/O failures. The HRESULT
            // separates them: 0x20 is ERROR_SHARING_VIOLATION, 0x21 ERROR_LOCK_VIOLATION.
            int code = ex.HResult & 0xFFFF;
            DeleteOutcome outcome = code is 32 or 33 ? DeleteOutcome.InUse : DeleteOutcome.Failed;
            return new DeleteResult(path, outcome, 0, isDirectory, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DeleteResult(path, DeleteOutcome.Failed, 0, isDirectory, ex.Message);
        }
    }

    /// <summary>
    /// Sends one item to the Recycle Bin.
    /// <para>
    /// Items larger than the bin's quota are deleted outright by Windows even with
    /// <c>FOF_ALLOWUNDO</c> — the caller is warned about that up front rather than
    /// discovering it afterwards.
    /// </para>
    /// </summary>
    private static DeleteResult ToRecycleBin(string path, long bytes, bool isDirectory)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FileOperation.Delete,
            // Double null terminator: pFrom is a list, and a single terminator makes
            // the Shell read past the end of the intended batch.
            pFrom = path + "\0\0",
            fFlags = FileOperationFlags.AllowUndo
                   | FileOperationFlags.NoConfirmation
                   | FileOperationFlags.NoErrorUi
                   | FileOperationFlags.Silent,
        };

        int code = Shell32.SHFileOperation(ref operation);

        if (code == 0 && !operation.fAnyOperationsAborted)
            return new DeleteResult(path, DeleteOutcome.Deleted, bytes, isDirectory);

        DeleteOutcome outcome = code switch
        {
            0x78 => DeleteOutcome.AccessDenied,  // DE_ACCESSDENIEDSRC
            0x7C => DeleteOutcome.NotFound,      // DE_INVALIDFILES
            0x10000 => DeleteOutcome.Failed,     // ERRORONDEST
            _ when operation.fAnyOperationsAborted => DeleteOutcome.InUse,
            _ => DeleteOutcome.Failed,
        };

        return new DeleteResult(path, outcome, 0, isDirectory, $"SHFileOperation 0x{code:X}");
    }

    /// <summary>
    /// Removes duplicates and any path whose ancestor is also in the batch.
    /// </summary>
    internal static List<string> Collapse(IEnumerable<string> paths)
    {
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            string normalized = path.TrimEnd('\\');
            if (seen.Add(normalized)) unique.Add(normalized);
        }

        // Shortest first, so a parent is always evaluated before its children.
        unique.Sort(static (a, b) => a.Length.CompareTo(b.Length));

        var kept = new List<string>(unique.Count);

        foreach (string candidate in unique)
        {
            bool covered = kept.Any(parent =>
                candidate.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase));

            if (!covered) kept.Add(candidate);
        }

        return kept;
    }

    private static (long Bytes, bool IsDirectory, bool Exists) Measure(string path)
    {
        try
        {
            if (Directory.Exists(path)) return (DirectorySize(path), true, true);

            var file = new FileInfo(path);
            return file.Exists ? (file.Length, false, true) : (0, false, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, false, false);
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
                // Never follow a junction while measuring: it would count another
                // subtree and, worse, could loop.
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

    private static bool IsDirectory(string path)
    {
        try { return Directory.Exists(path); }
        catch (IOException) { return false; }
    }

    private static string Describe(ProtectionReason reason) => L.T(reason switch
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
