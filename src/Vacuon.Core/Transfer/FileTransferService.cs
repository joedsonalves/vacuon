using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Core.Actions;
using Vacuon.Core.Localization;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Transfer;

/// <summary>
/// Copies, moves and permanently deletes through robocopy, with a live account of what it
/// is doing.
/// <para>
/// The shell's own copy is one file at a time down a single thread, and on a folder of
/// thousands of small files that is most of the wall clock. <c>robocopy /E /MT:32</c> walks
/// the tree with thirty-two threads instead, and — the reason it is here rather than a
/// faster loop of our own — it reports every file as it lands, so a progress window can show
/// measured bytes rather than a bar that moves because time passed.
/// </para>
/// <para>
/// <b>No console window, ever.</b> The app starts <c>robocopy.exe</c> directly, with
/// <c>UseShellExecute</c> off and <c>CreateNoWindow</c> on. There is no <c>cmd</c> anywhere
/// in the chain, so there is nothing that could flash a black rectangle at somebody.
/// </para>
/// <para>
/// <b>What this deliberately does not do.</b> The Recycle Bin: robocopy cannot recycle, and
/// a "fast delete" that quietly turned recycling into erasure would be the app promising one
/// thing and doing another. Recycling stays with the shell, and <see cref="TransferKind.Delete"/>
/// here means permanent. A move that stays on one volume: that is a rename, already instant,
/// and routing it through a copy engine would make it take minutes instead of milliseconds.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileTransferService
{
    /// <summary>The thread count the app asks robocopy for. Robocopy's own default is 8.</summary>
    public const int DefaultThreads = 32;

    /// <summary>How often progress is raised. Faster than this is redraw the eye cannot use.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(120);

    private readonly int _threads;

    public FileTransferService(int threads = DefaultThreads) => _threads = threads;

    // ==================== planning ====================

    /// <summary>
    /// Works out the whole batch without touching anything: what is allowed, what each item
    /// will be called when it arrives, and how many bytes are involved.
    /// </summary>
    /// <param name="measure">
    /// Sizes and file counts already known to the caller — the volume index carries a
    /// subtree total and a subtree file count for every folder, and asking it costs nothing
    /// next to walking the tree again here. Without it both are worked out on the spot.
    /// </param>
    public TransferPlan Plan(IEnumerable<string> sources, string destination, TransferKind kind,
                             Func<string, TransferMeasurement>? measure = null)
    {
        string folder = kind == TransferKind.Delete ? string.Empty : MoveService.Normalize(destination);
        var items = new List<TransferItem>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A folder carries its children, so an item whose ancestor is also selected must not
        // travel twice — the second trip would start from a path that no longer exists.
        foreach (string path in DeleteService.Collapse(sources))
            items.Add(PlanOne(path, folder, kind, taken, measure));

        return new TransferPlan(kind, folder, items);
    }

    private static TransferItem PlanOne(string path, string folder, TransferKind kind,
                                        HashSet<string> taken, Func<string, TransferMeasurement>? measure)
    {
        bool isDirectory = Directory.Exists(path);

        ProtectionVerdict protection = ProtectedPaths.Check(path);
        if (protection.IsProtected)
        {
            return new TransferItem(path, path, 0, isDirectory,
                                    TransferOutcome.Blocked, MoveService.Describe(protection.Reason));
        }

        if (!(isDirectory || File.Exists(path)))
            return new TransferItem(path, path, 0, isDirectory, TransferOutcome.NotFound);

        TransferMeasurement size = measure?.Invoke(path) ?? Weigh(path, isDirectory);
        long bytes = size.Bytes;
        int files = Math.Max(1, size.Files);

        if (kind == TransferKind.Delete)
            return new TransferItem(path, string.Empty, bytes, isDirectory) { Files = files };

        string parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? string.Empty;

        // Only a move is pointless into the folder the item already sits in. Copying a file
        // beside itself is a duplicate, which people ask for on purpose — it goes in as
        // "name (2)" like any other taken name.
        if (kind == TransferKind.Move
            && string.Equals(MoveService.Normalize(parent), folder, StringComparison.OrdinalIgnoreCase))
        {
            return new TransferItem(path, path, bytes, isDirectory, TransferOutcome.AlreadyThere) { Files = files };
        }

        // A folder cannot swallow itself. Robocopy would find this out halfway through,
        // after it had already written a few thousand files into a target that keeps growing.
        if (isDirectory && MoveService.IsInside(folder, path))
            return new TransferItem(path, path, bytes, isDirectory, TransferOutcome.IntoItself) { Files = files };

        string target = MoveService.FreeName(folder, Path.GetFileName(path.TrimEnd('\\')), isDirectory, taken);

        if (target.Length == 0)
        {
            return new TransferItem(path, path, bytes, isDirectory,
                                    TransferOutcome.Failed, L.T("move.outcomeNoFreeName"))
            { Files = files };
        }

        taken.Add(target);
        return new TransferItem(path, target, bytes, isDirectory) { Files = files };
    }

    /// <summary>
    /// Bytes and files in one pass, for a caller that has no index to ask.
    /// <para>
    /// The two used to come from different places — bytes from a tree walk, the file count
    /// assumed to be one per selected item. Reading them together is what keeps the progress
    /// readout dividing files by files.
    /// </para>
    /// </summary>
    private static TransferMeasurement Weigh(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            try { return new TransferMeasurement(new FileInfo(path).Length, 1); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new TransferMeasurement(0, 1); }
        }

        long bytes = 0;
        int files = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", options))
            {
                files++;
                try { bytes += new FileInfo(file).Length; }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new TransferMeasurement(bytes, files);
    }

    // ==================== running ====================

    public async Task<TransferReport> ExecuteAsync(TransferPlan plan,
                                                   IProgress<TransferProgress>? progress = null,
                                                   CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<TransferItemResult>(plan.Items.Count);

        foreach (TransferItem refused in plan.Refused)
            results.Add(new TransferItemResult(refused, refused.Refusal, 0, refused.RefusalMessage));

        List<TransferItem> work = [.. plan.Movable];

        if (work.Count == 0)
        {
            return new TransferReport(plan.Kind, plan.Destination, results,
                                      TransferPhase.Finished, 0, false, clock.Elapsed);
        }

        var state = new RunState(plan, clock, new TransferRateMeter(), progress);
        state.Report(TransferPhase.Preparing, string.Empty);

        bool cancelled = false;

        foreach (TransferItem item in work)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new TransferItemResult(item, TransferOutcome.Cancelled, 0));
                continue;
            }

            TransferItemResult result = await RunOneAsync(item, plan.Kind, state, cancellationToken)
                .ConfigureAwait(false);

            results.Add(result);
            if (result.Outcome == TransferOutcome.Cancelled) cancelled = true;
        }

        // Second pass over what the tool could not take, now that the batch has had time to
        // run: see RetryFailedAsync for what this can and cannot rescue.
        int recovered = cancelled ? 0 : await RetryFailedAsync(plan, results, state, cancellationToken)
            .ConfigureAwait(false);

        TransferPhase phase = cancelled
            ? TransferPhase.Cancelled
            : results.Any(r => r.Outcome == TransferOutcome.Failed)
                ? TransferPhase.Failed
                : TransferPhase.Finished;

        state.Report(phase, string.Empty);

        // Uncertain now means one thing only: a run ended without a readable closing table,
        // so the figure on screen is the optimistic count off the per-file lines and nothing
        // confirmed it. When the table is there, it is the total — comparing it against the
        // lines and reporting the disagreement was reporting a disagreement that is normal.
        bool uncertain = state.MissingSummary;

        return new TransferReport(plan.Kind, plan.Destination, results, phase,
                                  state.BytesDone, uncertain, clock.Elapsed)
        {
            // The tool's own count, kept beside the list of names so the window can say when
            // the two disagree instead of showing the shorter one as the whole story.
            FailedFileCount = state.SummaryFilesFailed,

            RecoveredCount = recovered,

            // A copy frees nothing anywhere. A move frees the source volume only by leaving
            // it, and only the caller knows which volumes are in play. A permanent delete
            // always does.
            BytesWereFreed = plan.Kind == TransferKind.Delete,
        };
    }

    /// <summary>
    /// Goes back for the files the tool could not take, one at a time, at the end.
    /// <para>
    /// ⚠️ <b>What this rescues, and what it cannot.</b> Measured before it was written, with
    /// a file held open in five different sharing modes: robocopy, <c>File.Copy</c> and the
    /// most permissive open .NET offers all succeed on exactly the same three and all fail on
    /// exactly the same two. A file whose owner denies read sharing is unreadable to every
    /// program on the machine — Explorer included — and only a shadow copy gets around that.
    /// So this is <b>not</b> a second engine that is cleverer than the first one, and saying
    /// it was would be promising what it cannot do.
    /// </para>
    /// <para>
    /// What it does rescue is the far more common case: a lock that was a moment. Robocopy
    /// is run with <c>/R:1 /W:1</c>, so it gives up on a file about a second after meeting
    /// it, while a batch runs for minutes — a file that a program had open at second three
    /// is usually free by the end. Trying again then costs one open per failed file and
    /// finishes the copy instead of handing back a list.
    /// </para>
    /// </summary>
    /// <returns>How many files the second pass brought over.</returns>
    private static async Task<int> RetryFailedAsync(TransferPlan plan, List<TransferItemResult> results,
                                                    RunState state, CancellationToken cancellationToken)
    {
        int recovered = 0;

        for (int i = 0; i < results.Count; i++)
        {
            TransferItemResult result = results[i];
            if (result.FailedPaths.Count == 0) continue;
            if (cancellationToken.IsCancellationRequested) break;

            var stillFailed = new List<string>(result.FailedPaths.Count);
            long extraBytes = 0;

            foreach (string source in result.FailedPaths)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    stillFailed.Add(source);
                    continue;
                }

                long bytes = await RetryOneAsync(plan.Kind, result.Item, source, state, cancellationToken)
                    .ConfigureAwait(false);

                // -1: still out of reach. -2: it is at the destination already, which
                // happens when the tool's own retry got it after announcing the failure —
                // it is not failed, and this pass did not rescue it either.
                if (bytes == -1) stillFailed.Add(source);
                else if (bytes >= 0)
                {
                    recovered++;
                    extraBytes += bytes;
                }
            }

            if (stillFailed.Count == result.FailedPaths.Count) continue;

            // Everything that was missing arrived, so the item is no longer a failure — and
            // if some of it is still missing, it stays one, with the shorter list.
            TransferOutcome outcome = stillFailed.Count == 0 && result.Outcome == TransferOutcome.Failed
                ? TransferOutcome.Done
                : result.Outcome;

            results[i] = result with
            {
                Outcome = outcome,
                BytesTransferred = result.BytesTransferred + extraBytes,
                Message = stillFailed.Count == 0 ? null : result.Message,
                FailedPaths = stillFailed,
            };
        }

        return recovered;
    }

    /// <summary>
    /// One file, the long way round.
    /// </summary>
    /// <returns>
    /// The bytes this pass brought over, <c>-1</c> when the file is still out of reach, or
    /// <c>-2</c> when it turned out to be at the destination already.
    /// </returns>
    private static async Task<long> RetryOneAsync(TransferKind kind, TransferItem item, string source,
                                                  RunState state, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == TransferKind.Delete)
            {
                File.Delete(source);
                if (File.Exists(source)) return -1;

                long size = 0;
                state.CountFile(source, size);
                return size;
            }

            if (!File.Exists(source)) return -1;

            string target = MapToDestination(item, source);
            if (target.Length == 0) return -1;

            long length = new FileInfo(source).Length;

            // A failed attempt can leave a stub behind. Same length is treated as arrived;
            // any other length is an unfinished file and gets written over — this is the one
            // place a transfer overwrites anything, and it is only ever its own wreckage.
            if (File.Exists(target) && new FileInfo(target).Length == length) return -2;

            string? folder = Path.GetDirectoryName(target);
            if (folder is not null) Directory.CreateDirectory(folder);

            // The most permissive request there is: whatever the owner allows, this accepts.
            using (var reader = new FileStream(source, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await reader.CopyToAsync(writer, cancellationToken).ConfigureAwait(false);
            }

            // A move is a copy that then lets go. If the source will not go, the copy still
            // happened and the item is no worse off than the tool left it.
            if (kind == TransferKind.Move)
            {
                try { File.Delete(source); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            state.CountFile(target, length);
            return length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or System.ComponentModel.Win32Exception
                                        or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>Where a file under a travelling item is meant to land.</summary>
    private static string MapToDestination(TransferItem item, string source)
    {
        if (!item.IsDirectory)
            return string.Equals(source, item.Source, StringComparison.OrdinalIgnoreCase) ? item.Destination : string.Empty;

        string root = item.Source.TrimEnd('\\');
        if (!source.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        string relative = source[root.Length..].TrimStart('\\');
        return relative.Length == 0 ? string.Empty : Path.Combine(item.Destination, relative);
    }

    private async Task<TransferItemResult> RunOneAsync(TransferItem item, TransferKind kind,
                                                       RunState state, CancellationToken cancellationToken)
    {
        try
        {
            return kind == TransferKind.Delete
                ? await DeleteAsync(item, state, cancellationToken).ConfigureAwait(false)
                : await CopyOrMoveAsync(item, kind, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new TransferItemResult(item, TransferOutcome.Cancelled, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or System.ComponentModel.Win32Exception)
        {
            return new TransferItemResult(item, TransferOutcome.Failed, 0, ex.Message);
        }
    }

    private async Task<TransferItemResult> CopyOrMoveAsync(TransferItem item, TransferKind kind,
                                                           RunState state, CancellationToken cancellationToken)
    {
        long before = state.BytesDone;
        int failedBefore = state.FailedCount;

        if (item.IsDirectory)
        {
            // A directory needs no staging even when it is renamed: the destination path is
            // simply the new name, and robocopy creates whatever it is pointed at.
            List<string> args = kind == TransferKind.Move
                ? RobocopyArguments.Move(item.Source, item.Destination, null, _threads)
                : RobocopyArguments.Copy(item.Source, item.Destination, null, _threads);

            int code = await RunRobocopyAsync(args, item, state, cancellationToken).ConfigureAwait(false);
            return Verdict(item, code, state.BytesDone - before, Directory.Exists(item.Destination),
                           state.FailedSince(failedBefore));
        }

        string sourceFolder = Path.GetDirectoryName(item.Source) ?? string.Empty;
        string destinationFolder = Path.GetDirectoryName(item.Destination) ?? string.Empty;
        string name = Path.GetFileName(item.Source);

        if (!item.Renamed)
        {
            List<string> direct = kind == TransferKind.Move
                ? RobocopyArguments.Move(sourceFolder, destinationFolder, name, _threads)
                : RobocopyArguments.Copy(sourceFolder, destinationFolder, name, _threads);

            int code = await RunRobocopyAsync(direct, item, state, cancellationToken).ConfigureAwait(false);
            return Verdict(item, code, state.BytesDone - before, File.Exists(item.Destination),
                           state.FailedSince(failedBefore));
        }

        // Robocopy writes files under the name they already have, so a file whose name is
        // taken at the destination cannot be renamed on the way in. It lands in a scratch
        // folder alongside and is renamed from there — a same-volume rename, and instant.
        string staging = Path.Combine(destinationFolder, $".vacuon-transfer-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);

            List<string> args = kind == TransferKind.Move
                ? RobocopyArguments.Move(sourceFolder, staging, name, _threads)
                : RobocopyArguments.Copy(sourceFolder, staging, name, _threads);

            int code = await RunRobocopyAsync(args, item, state, cancellationToken).ConfigureAwait(false);

            string landed = Path.Combine(staging, name);
            if (File.Exists(landed)) File.Move(landed, item.Destination, overwrite: false);

            return Verdict(item, code, state.BytesDone - before, File.Exists(item.Destination),
                           state.FailedSince(failedBefore));
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<TransferItemResult> DeleteAsync(TransferItem item, RunState state,
                                                       CancellationToken cancellationToken)
    {
        if (!item.IsDirectory)
        {
            // One file has nothing to parallelise. Robocopy would cost a process launch to
            // do what a single call does.
            File.Delete(item.Source);
            state.CountFile(item.Source, item.Bytes);

            return new TransferItemResult(item,
                File.Exists(item.Source) ? TransferOutcome.Failed : TransferOutcome.Done, item.Bytes);
        }

        // ⚠️ /MIR erases whatever it is aimed at. Everything that could make it the wrong
        // folder is checked here, before a process exists — the guard cannot live inside the
        // argument builder, because by the time that runs the decision has been made.
        if (ProtectedPaths.Check(item.Source).IsProtected)
            return new TransferItemResult(item, TransferOutcome.Blocked, 0);

        string full = Path.GetFullPath(item.Source);

        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            return new TransferItemResult(item, TransferOutcome.Blocked, 0, L.T("protect.volumeRoot"));

        if (!Directory.Exists(full))
            return new TransferItemResult(item, TransferOutcome.NotFound, 0);

        string empty = Path.Combine(Path.GetTempPath(), $".vacuon-empty-{Guid.NewGuid():N}");
        long before = state.BytesDone;
        int failedBefore = state.FailedCount;

        try
        {
            Directory.CreateDirectory(empty);

            List<string> args = RobocopyArguments.Purge(empty, full, _threads);
            int code = await RunRobocopyAsync(args, item, state, cancellationToken).ConfigureAwait(false);

            // The mirror empties the folder; it does not remove it.
            if (code != RunState.CancelledExitCode && Directory.Exists(full))
                Directory.Delete(full, recursive: true);

            return Verdict(item, code, state.BytesDone - before, !Directory.Exists(full),
                           state.FailedSince(failedBefore));
        }
        finally
        {
            try { if (Directory.Exists(empty)) Directory.Delete(empty, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// "Robocopy returned a friendly number" and "the item is over there" are different
    /// statements, and only the second one is worth reporting. Both get checked.
    /// </summary>
    private static TransferItemResult Verdict(TransferItem item, int code, long bytes, bool arrived,
                                              IReadOnlyList<string> failed)
    {
        if (code == RunState.CancelledExitCode)
            return new TransferItemResult(item, TransferOutcome.Cancelled, bytes) { FailedPaths = failed };

        if (!RobocopyOutput.Succeeded(code))
        {
            return new TransferItemResult(item, TransferOutcome.Failed, bytes, RobocopyOutput.Describe(code))
            {
                FailedPaths = failed,
            };
        }

        return arrived
            ? new TransferItemResult(item, TransferOutcome.Done, bytes) { FailedPaths = failed }
            : new TransferItemResult(item, TransferOutcome.Failed, bytes, L.T("transfer.notThere"))
            {
                FailedPaths = failed,
            };
    }

    private async Task<int> RunRobocopyAsync(List<string> arguments, TransferItem item,
                                             RunState state, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ⚠️ No StandardOutputEncoding, and no /UNICODE. Both were here, on the reasoning
            // that a file called "acentuação.bin" needs UTF-16 to survive the pipe. Measured
            // against the real tool, /UNICODE governs robocopy's log file and not its
            // redirected stdout: the bytes kept arriving in the default encoding while this
            // end decoded them as UTF-16, every line came out as mojibake, nothing matched
            // the parser, and a transfer that copied perfectly reported zero bytes moved.
            //
            // The default decode reads that same file name back intact. On an install whose
            // console code page is not UTF-8 an accented name may still come through wrong,
            // and that costs a name in the window — never a count, because the tabs, the
            // digits and the drive letter are ASCII either way.
        };

        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        state.BeginRun(arguments.Contains("/MIR"));

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => state.Consume(e.Data, item);

        // Standard error is redirected, so it has to be drained: a pipe nobody reads fills up
        // and the child blocks writing to it, which would hang a copy rather than fail it.
        // Robocopy put its error lines on stdout in every run measured here, so this is a
        // safeguard and not the path the failure list depends on.
        process.ErrorDataReceived += (_, e) => state.Consume(e.Data, item);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException
                                            or System.ComponentModel.Win32Exception)
            { }

            return RunState.CancelledExitCode;
        }

        state.EndRun();
        return process.ExitCode;
    }

    // ==================== live accounting ====================

    /// <summary>
    /// Everything one run accumulates. Robocopy's output arrives on a thread pool thread, so
    /// every field it touches is behind the lock.
    /// </summary>
    private sealed class RunState
    {
        internal const int CancelledExitCode = -1;

        private readonly TransferPlan _plan;
        private readonly Stopwatch _clock;
        private readonly TransferRateMeter _meter;
        private readonly IProgress<TransferProgress>? _progress;
        private readonly Lock _gate = new();

        private long _bytes;
        private int _files;
        private string _current = string.Empty;
        private int? _percent;

        // ⚠️ Nullable, not a TimeSpan.MinValue sentinel. Subtracting MinValue from the
        // stopwatch overflows, and it threw on the very first progress report — so a
        // transfer with nobody watching worked and one with the window open died at the
        // first file. Null is the only "never reported yet" that cannot be arithmetic.
        private TimeSpan? _lastReport;

        /// <summary>
        /// The closing table of the run in progress, and what the total was before it began.
        /// <para>
        /// ⚠️ The per-file lines are an <b>optimistic</b> count and cannot be anything else:
        /// robocopy announces a file before it knows whether it will land. Measured on the
        /// real tool, three ways the lines lie about the total — a failed file is announced
        /// with its full size; a retry announces it a second time; and a retry that
        /// <b>succeeds</b> was seen to land without announcing itself again at all. Trying to
        /// undo each announcement as its error arrives fixes the first two and breaks the
        /// third: the file arrived and its bytes had been taken back out.
        /// </para>
        /// <para>
        /// So the lines drive the bar while the run is going, and the moment the run ends the
        /// total is snapped to the closing table — the tool's own count of what it actually
        /// wrote. A run that prints no readable table leaves the optimistic figure standing
        /// and says the total is uncertain, which is the honest end of it.
        /// </para>
        /// </summary>
        private int _summaryRows;
        private long _runSummaryBytes = -1;
        private long _runSummaryFiles = -1;
        private long _runBaseBytes;
        private int _runBaseFiles;
        private bool _runIsPurge;
        private bool _missingSummary;
        private int _summaryFilesFailed;

        // Order matters, because this is the list the window shows, so a list beside a set
        // rather than a set alone. The retry names the same file a second time.
        private readonly List<string> _failed = [];
        private readonly HashSet<string> _failedSeen = new(StringComparer.OrdinalIgnoreCase);

        /* removido: a memória do que está em voo — ver a nota em _runSummaryBytes.
        /// <summary>
        /// What each file in flight was counted as, so a failure can take it back out.
        /// <para>
        /// ⚠️ Robocopy announces a file <b>before</b> it knows whether it will land: a locked
        /// file gets a "New File" line with its full size, then an error, then both again for
        /// the retry. Counting the lines and stopping there had the window reporting
        /// <c>10.0 MiB / 6.5 MiB</c> transferred and <c>34 / 20</c> files on a batch where
        /// fourteen of twenty files never moved a byte — a total larger than the plan it was
        /// dividing into, which is the app claiming what it did not measure.
        /// </para>
        /// <para>
        /// Bounded on purpose. An error arrives right behind its own file line, and with
        /// /MT:32 at most a few dozen are in flight, so the last few hundred is plenty; a
        /// dictionary that remembered every file would grow with the copy. When a name has
        /// fallen out, the byte total simply stays high and the closing cross-check against
        /// robocopy's own summary reports the total as uncertain — which is the honest
        /// outcome, and the one that was already there.
        /// </para>
        /// </summary>
        private const int InFlightMemory = 512; */

        public RunState(TransferPlan plan, Stopwatch clock, TransferRateMeter meter,
                        IProgress<TransferProgress>? progress)
        {
            _plan = plan;
            _clock = clock;
            _meter = meter;
            _progress = progress;
        }

        public long BytesDone { get { lock (_gate) return _bytes; } }

        /// <summary>
        /// True when a run ended without a readable closing table, so the total on screen is
        /// still the optimistic count off the per-file lines.
        /// </summary>
        public bool MissingSummary { get { lock (_gate) return _missingSummary; } }

        /// <summary>Files the closing tables counted as FAILED, added up across the batch.</summary>
        public int SummaryFilesFailed { get { lock (_gate) return _summaryFilesFailed; } }

        /// <summary>How many distinct files have been named as failed so far.</summary>
        public int FailedCount { get { lock (_gate) return _failed.Count; } }

        /// <summary>
        /// The files named as failed since a mark. Items run one after another, so a mark
        /// taken before an item and read after it belongs to that item alone.
        /// </summary>
        public IReadOnlyList<string> FailedSince(int mark)
        {
            lock (_gate) return _failed.GetRange(mark, _failed.Count - mark);
        }

        public void CountFile(string path, long bytes)
        {
            lock (_gate)
            {
                _bytes += bytes;
                _files++;
                _current = path;
                _percent = null;

            }

            Report(TransferPhase.Running, path);
        }

        public void Consume(string? line, TransferItem item)
        {
            if (line is null) return;

            RobocopyLine parsed = RobocopyOutput.Parse(line.TrimStart('﻿'));

            switch (parsed.Kind)
            {
                case RobocopyLineKind.File:
                case RobocopyLineKind.Extra:
                    CountFile(parsed.Path, parsed.Bytes);
                    break;

                case RobocopyLineKind.Percent:
                    // Only meaningful while a single file is open. With /MT several are, and
                    // each percentage belongs to whichever file its own thread is on —
                    // reading them as one number would be inventing a figure.
                    lock (_gate)
                    {
                        if (!item.IsDirectory) _percent = parsed.Percent;
                        else return;
                    }
                    Report(TransferPhase.Running, null);
                    break;

                case RobocopyLineKind.Error:
                    lock (_gate)
                    {
                        // One retry means every failure is announced twice. Listing it twice
                        // would read as two files lost where one was.
                        if (_failedSeen.Add(parsed.Path)) _failed.Add(parsed.Path);
                    }
                    break;

                case RobocopyLineKind.SummaryRow:
                    lock (_gate)
                    {
                        _summaryRows++;

                        // Dirs, Files, then Bytes — counted, not read off a label, because
                        // the labels are translated on some installs and the order is not.
                        //
                        // ⚠️ Which column is the answer depends on the job. A copy writes, so
                        // Copied is what landed. A purge — the /MIR onto an empty folder that
                        // is this app's fast delete — copies nothing and removes everything,
                        // so its work is in Extras. Reading Copied for both is why a 2 GiB
                        // delete reported "the two counts of these bytes disagree" every
                        // single time: the table said zero copied, because nothing was.
                        if (_summaryRows == 2)
                        {
                            _runSummaryFiles = _runIsPurge ? parsed.Extras : parsed.Bytes;
                            _summaryFilesFailed += (int)parsed.Failed;
                        }
                        else if (_summaryRows == 3)
                        {
                            _runSummaryBytes = _runIsPurge ? parsed.Extras : parsed.Bytes;
                        }
                    }
                    break;
            }
        }

        /// <summary>Marks the start of one robocopy run, so its closing table can be applied to it.</summary>
        public void BeginRun(bool purge)
        {
            lock (_gate)
            {
                _runBaseBytes = _bytes;
                _runBaseFiles = _files;
                _runSummaryBytes = -1;
                _runSummaryFiles = -1;
                _summaryRows = 0;
                _runIsPurge = purge;
            }
        }

        /// <summary>
        /// Replaces this run's optimistic count with what its closing table said it wrote.
        /// </summary>
        public void EndRun()
        {
            lock (_gate)
            {
                if (_runSummaryBytes < 0 || _runSummaryFiles < 0)
                {
                    // No readable table. The lines are all there is, and the report says so
                    // rather than presenting an optimistic figure as measured.
                    _missingSummary = true;
                    return;
                }

                _bytes = _runBaseBytes + _runSummaryBytes;
                _files = _runBaseFiles + (int)_runSummaryFiles;
            }
        }

        public void Report(TransferPhase phase, string? current)
        {
            if (_progress is null) return;

            TransferProgress snapshot;

            lock (_gate)
            {
                TimeSpan now = _clock.Elapsed;

                bool terminal = phase is not (TransferPhase.Running or TransferPhase.Preparing);
                if (!terminal && _lastReport is TimeSpan last && now - last < ReportEvery) return;

                _lastReport = now;
                if (current is not null) _current = current;

                _meter.Record(now, _bytes, _files);

                snapshot = new TransferProgress(
                    phase,
                    _current,
                    _files,
                    _plan.FileCount,
                    _bytes,
                    _plan.Bytes,
                    _meter.BytesPerSecond,
                    _meter.FilesPerSecond,
                    now,
                    _meter.Estimate(_plan.Bytes - _bytes),
                    _percent);
            }

            _progress.Report(snapshot);
        }
    }
}
