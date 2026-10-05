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
/// This is the destructive half of the app. It arrived before the quarantine, back when
/// the Recycle Bin was the only undo there was; the reversible route now lives in
/// <see cref="QuarantineService"/>, and permanent deletion stays what it always was —
/// a separate, explicit gesture the caller has to make on purpose.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeleteService
{
    /// <summary>
    /// How many items go to the Recycle Bin in one call to the shell.
    /// <para>
    /// Measured on my machine, 100-byte files: one call per file is 31.2 ms a file, one call
    /// for the whole list is 6.47 ms a file with 200 and 5.92 ms with 2,000. Big enough to get
    /// that, small enough that a list of a hundred thousand is not one string of megabytes.
    /// </para>
    /// </summary>
    internal const int RecycleChunk = 500;

    /// <summary>The shell's delete-to-the-bin, given a list. Tests hand in their own.</summary>
    private readonly Func<IReadOnlyList<string>, int> _recycle;

    public DeleteService() : this(null) { }

    internal DeleteService(Func<IReadOnlyList<string>, int>? recycle) => _recycle = recycle ?? RecycleThroughShell;

    /// <summary>
    /// Plans a deletion without touching the disk. The UI shows this before asking
    /// for confirmation, so the user sees exactly what is about to happen.
    /// </summary>
    /// <param name="sizeOf">
    /// What a folder weighs, when the caller already knows: the volume index carries a
    /// subtree total for every folder. Null, or null for a path, means walk it. Measured on
    /// my machine, walking npm's cache to weigh it took 9.7 s - before the confirmation
    /// could even appear, and again when the delete ran.
    /// </param>
    public DeleteReport Plan(IEnumerable<string> paths, DeleteMode mode, Func<string, long?>? sizeOf = null) =>
        Run(paths, mode, dryRun: true, sizeOf, CancellationToken.None);

    public DeleteReport Execute(IEnumerable<string> paths, DeleteMode mode,
                               CancellationToken cancellationToken = default,
                               Func<string, long?>? sizeOf = null) =>
        Run(paths, mode, dryRun: false, sizeOf, cancellationToken);

    private DeleteReport Run(IEnumerable<string> paths, DeleteMode mode, bool dryRun,
                             Func<string, long?>? sizeOf, CancellationToken cancellationToken)
    {
        var results = new List<DeleteResult>();
        var toRecycle = new List<(int Slot, string Path, long Bytes, bool IsDirectory)>();

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

            (long bytes, bool isDirectory, bool exists) = Measure(path, sizeOf);

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

            // The bin goes in batches, after this loop. Its slot is kept so the report still
            // lists everything in the order it was asked for.
            if (mode == DeleteMode.RecycleBin)
            {
                toRecycle.Add((results.Count, path, bytes, isDirectory));
                results.Add(null!);
                continue;
            }

            results.Add(Delete(path, mode, bytes, isDirectory));
        }

        if (toRecycle.Count > 0) RecycleAll(toRecycle, results, cancellationToken);

        return new DeleteReport(results, mode, dryRun);
    }

    /// <summary>
    /// Sends everything in <paramref name="items"/> to the Recycle Bin, a chunk per call to
    /// the shell, and reads the outcome of each one off the disk.
    /// <para>
    /// ⚠️ The shell's word is about the whole call, not about each file — and measured, it
    /// does not carry on past a file it cannot move: 200 files with the 51st held open by
    /// another program came back as 0x20 with the first 50 in the bin and the other 150,
    /// held one included, still in place. So what went is whatever is no longer there, in
    /// order; the first one still there is asked about on its own, which moves it if it can
    /// and says why if it cannot; and the chunk goes on from the file after it.
    /// </para>
    /// </summary>
    private void RecycleAll(List<(int Slot, string Path, long Bytes, bool IsDirectory)> items,
                            List<DeleteResult> results, CancellationToken cancellationToken)
    {
        int next = 0;

        while (next < items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int take = Math.Min(RecycleChunk, items.Count - next);
            var chunk = new List<string>(take);
            for (int k = next; k < next + take; k++) chunk.Add(items[k].Path);

            _recycle(chunk);

            int i = next;

            for (; i < next + take; i++)
            {
                (int slot, string path, long bytes, bool isDirectory) = items[i];
                if (StillThere(path, isDirectory)) break;

                results[slot] = new DeleteResult(path, DeleteOutcome.Deleted, bytes, isDirectory);
            }

            if (i == next + take)
            {
                next = i;
                continue;
            }

            // The one the shell stopped at, or skipped: asked about by itself.
            (int stuckSlot, string stuck, long stuckBytes, bool stuckIsDirectory) = items[i];
            results[stuckSlot] = Delete(stuck, DeleteMode.RecycleBin, stuckBytes, stuckIsDirectory);
            next = i + 1;
        }
    }

    private static bool StillThere(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    /// <summary>One call to the shell for a list of items. The return code covers the call, not the items.</summary>
    private static int RecycleThroughShell(IReadOnlyList<string> paths)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FileOperation.Delete,
            // Each path ends in a null, and the list in a second one.
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FileOperationFlags.AllowUndo
                   | FileOperationFlags.NoConfirmation
                   | FileOperationFlags.NoErrorUi
                   | FileOperationFlags.Silent,
        };

        return Shell32.SHFileOperation(ref operation);
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

    private static (long Bytes, bool IsDirectory, bool Exists) Measure(string path, Func<string, long?>? sizeOf)
    {
        try
        {
            if (Directory.Exists(path)) return (sizeOf?.Invoke(path) ?? DirectorySize(path), true, true);

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
