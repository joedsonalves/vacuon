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

        // A link is deleted as a link, so nothing it points at is freed. The index reads the
        // folder the way the disk holds it and already says zero; a walk would start on the
        // far side of the door and count everything there.
        TransferMeasurement size = kind == TransferKind.Delete && isDirectory && Links.IsLink(path)
            ? new TransferMeasurement(0, 0)
            : measure?.Invoke(path) ?? Weigh(path, isDirectory);

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
        (int recovered, bool stopped) = cancelled
            ? (0, false)
            : await RetryFailedAsync(plan, results, state, cancellationToken).ConfigureAwait(false);

        // Whatever is still on the failed list may have left a fragment at the destination,
        // under the file's own name. Those go now; see RemovePartial.
        if (plan.Kind != TransferKind.Delete)
        {
            foreach (TransferItemResult result in results)
                foreach (string source in result.FailedPaths)
                    RemovePartial(result.Item, source);
        }

        TransferPhase phase = cancelled || stopped
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
    /// <returns>
    /// How many files the second pass brought over, and whether Stop cut it short with files
    /// still to go — which makes the whole batch a stopped one, not merely a failed one.
    /// </returns>
    private static async Task<(int Recovered, bool Stopped)> RetryFailedAsync(
        TransferPlan plan, List<TransferItemResult> results, RunState state, CancellationToken cancellationToken)
    {
        int recovered = 0;
        bool stopped = false;

        // Counted up front, so the window can say "3 of 120" and not only "3".
        int total = 0;
        foreach (TransferItemResult result in results) total += result.FailedPaths.Count;

        int number = 0;

        try
        {
            for (int i = 0; i < results.Count; i++)
            {
                TransferItemResult result = results[i];
                if (result.FailedPaths.Count == 0) continue;

                if (cancellationToken.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                var stillFailed = new List<string>(result.FailedPaths.Count);
                long extraBytes = 0;

                foreach (string source in result.FailedPaths)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        stopped = true;
                        stillFailed.Add(source);
                        continue;
                    }

                    // Said before the attempt, not after it: a file that is going to fail
                    // reports nothing on its way out, and those are most of what is here.
                    state.TryingAgain(source, ++number, total);

                    long bytes = await RetryOneAsync(plan.Kind, result.Item, source, state, cancellationToken)
                        .ConfigureAwait(false);

                    // -1: still out of reach. -2: it is at the destination already, which
                    // happens when the tool's own retry got it after announcing the failure —
                    // it is not failed, and this pass did not rescue it either.
                    if (bytes == -1)
                    {
                        stillFailed.Add(source);
                        if (cancellationToken.IsCancellationRequested) stopped = true;
                    }
                    else if (bytes >= 0)
                    {
                        recovered++;
                        extraBytes += bytes;
                    }
                }

                if (stillFailed.Count == result.FailedPaths.Count) continue;

                // Everything that was missing arrived, so the item is no longer a failure — and
                // if some of it is still missing, it stays one, with the shorter list.
                bool finished = stillFailed.Count == 0 && result.Outcome == TransferOutcome.Failed
                                && Finished(plan.Kind, result.Item);

                results[i] = result with
                {
                    Outcome = finished ? TransferOutcome.Done : result.Outcome,
                    BytesTransferred = result.BytesTransferred + extraBytes,
                    Message = finished ? null : result.Message,
                    FailedPaths = stillFailed,
                };
            }
        }
        finally
        {
            // Whatever happened in there, the window is not on a second try any more.
            state.SecondPassOver();
        }

        return (recovered, stopped);
    }

    /// <summary>
    /// Whether an item whose files have all been dealt with is now really done.
    /// <para>
    /// For a copy, the files arriving is the whole job. A folder delete is done when the
    /// folder is gone: the purge left it standing because of the files the second pass has
    /// just removed, so it gets one more go here — and stays a failure if it still will not go.
    /// </para>
    /// </summary>
    private static bool Finished(TransferKind kind, TransferItem item)
    {
        if (kind == TransferKind.Copy) return true;

        if (!item.IsDirectory) return !File.Exists(item.Source);

        // Robocopy removes each source folder as it empties it, so the folders the second
        // pass has just finished emptying are still standing. A move left there is not one —
        // and a junction put in the old place afterwards would find a folder in its way. Only
        // a tree with no file left anywhere in it goes; one that still holds something stays,
        // and the item with it.
        if (kind == TransferKind.Move && HoldsAnyFile(item.Source)) return false;

        RemoveFolder(item.Source);
        return !Directory.Exists(item.Source);
    }

    /// <summary>Whether a folder has a file anywhere below it. True when it cannot be read, to be safe.</summary>
    private static bool HoldsAnyFile(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Removes a move's source once its copy is whole. False when it will not go.</summary>
    private static bool Release(FileInfo source)
    {
        try
        {
            // As every permanent delete here does: read-only is not a reason a move should stall.
            if (source.Exists && source.IsReadOnly) source.IsReadOnly = false;
            source.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return !File.Exists(source.FullName);
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
                var file = new FileInfo(source);

                // Gone already — whoever held it deleted it, or the purge's folder removal took
                // it. Not a failure any more, and not this pass's doing either. A folder the
                // purge could not read is named too, and that one this pass cannot help.
                if (!file.Exists) return Directory.Exists(source) ? -1 : -2;

                // Read before it goes. This used to count every file it removed as zero bytes,
                // so a folder freed in two passes reported only the first one's share.
                long size = file.Length;

                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
                if (File.Exists(source)) return -1;

                state.CountFile(source, size);
                return size;
            }

            if (!File.Exists(source)) return -1;

            string target = MapToDestination(item, source);
            if (target.Length == 0) return -1;

            var original = new FileInfo(source);
            long length = original.Length;

            // A failed attempt can leave a stub behind. Only a whole file counts as arrived —
            // see IsWhole for why the same length is not enough — and anything else is an
            // unfinished file that gets written over. This is the one place a transfer
            // overwrites anything, and it is only ever its own wreckage.
            if (IsWhole(original, new FileInfo(target)))
            {
                // ⚠️ For a copy that is the end of it. For a move it is half: the file is still
                // here as well, which is exactly what robocopy names a file for when another
                // program has it open for reading — it can copy it and cannot remove it. This
                // used to return "already there" for a move too, the file came off the failure
                // list, and the move reported itself complete with the file still at the source.
                if (kind != TransferKind.Move) return -2;

                // Its bytes were counted when robocopy copied it. What this pass adds is the
                // leaving, and that is what makes it rescued.
                return Release(original) ? 0 : -1;
            }

            string? folder = Path.GetDirectoryName(target);
            if (folder is not null) Directory.CreateDirectory(folder);

            try
            {
                await CopyWholeAsync(original, target, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ⚠️ Stop, pressed while this file was on its way. That used to leave as an
                // exception nobody caught: out of the transfer, into the window's async Loaded
                // handler, onto the dispatcher — which closes the app. It is a file that did
                // not make it, like any other here. What this attempt had written is a fragment
                // under the file's own name, since it opened the target with Create, so it goes.
                try { File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

                return -1;
            }

            // A move is a copy that then lets go. One whose source will not go has not moved
            // anything: it stays on the failure list, and the whole copy it made stays at the
            // destination - IsWhole keeps the fragment sweep away from it.
            if (kind == TransferKind.Move && !Release(original)) return -1;

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

    /// <summary>
    /// Whether a file at the destination is the whole of its source.
    /// <para>
    /// ⚠️ <b>The same length proves nothing.</b> Measured on the real tool: a copy of four
    /// 1 GiB files stopped after 0.7 s left all four at the destination at their full length —
    /// robocopy sizes the file before it fills it — with only 177 to 186 MB of each actually
    /// written, and today's date on them. What robocopy does last, once the data is all in, is
    /// give the file its source's date. So a file counts as whole only when it is as long as
    /// its source <b>and</b> carries its source's last-write time.
    /// </para>
    /// <para>
    /// Within two seconds rather than exactly: a FAT or exFAT destination keeps times at that
    /// granularity, and robocopy's own /FFT exists for the same reason. A fragment stopped
    /// within two seconds of its source being written is the price, and it is the safe side
    /// of the line — that fragment stays, where a stricter rule would delete whole copies on
    /// every memory card.
    /// </para>
    /// </summary>
    internal static bool IsWhole(FileInfo source, FileInfo destination)
    {
        if (!source.Exists || !destination.Exists) return false;
        if (source.Length != destination.Length) return false;

        TimeSpan apart = source.LastWriteTimeUtc - destination.LastWriteTimeUtc;
        return apart.Duration() <= TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Removes a file this transfer left half-written at its destination.
    /// <para>
    /// ⚠️ Measured on the real tool: a file robocopy cannot finish stays at the destination
    /// under its real name — 10,485,760 bytes of a 52,428,800-byte file whose middle another
    /// program had locked, and at full length but mostly empty when the copy is stopped. It
    /// sits in the folder looking like the file, beside a failure list saying otherwise that
    /// is easy to miss — and the next step after a copy is often deleting the original.
    /// </para>
    /// <para>
    /// Only ever this transfer's own wreckage: every destination is a name the plan made sure
    /// was free, so nothing there predates the run. And never a destination whose source is
    /// gone — a move that finished that file before it was stopped — because that one is the
    /// only copy there is.
    /// </para>
    /// </summary>
    /// <returns>True when a fragment was there and is gone now.</returns>
    internal static bool RemovePartial(TransferItem item, string source)
    {
        string target = MapToDestination(item, source);
        if (target.Length == 0) return false;

        try
        {
            var written = new FileInfo(target);
            if (!written.Exists) return false;

            var original = new FileInfo(source);
            if (!original.Exists || IsWhole(original, written)) return false;

            if (written.IsReadOnly) written.IsReadOnly = false;
            written.Delete();
            return !File.Exists(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// After a stop, everything under the item's destination that is not whole.
    /// <para>
    /// Robocopy runs thirty-two files at once, and a stop kills it with any of them part-way.
    /// Its output names a file when it starts on it, not when it finishes, so the only list of
    /// what was in flight is the disk: whatever under the destination is not its source's
    /// equal. Earlier items finished before this one started, so only this one is walked.
    /// </para>
    /// </summary>
    private static void SweepPartials(TransferItem item)
    {
        if (!Directory.Exists(item.Destination)) return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        try
        {
            foreach (string written in Directory.EnumerateFiles(item.Destination, "*", options))
            {
                string relative = Path.GetRelativePath(item.Destination, written);
                RemovePartial(item, Path.Combine(item.Source, relative));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sweep that cannot finish leaves what it did not reach as it was — no worse off
            // than before there was a sweep at all.
        }
    }

    /// <summary>The attributes a copy carries over. The rest describe how a file is stored, not what it is.</summary>
    private const FileAttributes Carried = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System
                                         | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    /// <summary>
    /// Copies one file the way robocopy's default <c>/COPY:DAT</c> does: the data, then the
    /// three times and the attributes.
    /// <para>
    /// ⚠️ Measured on the real tool rather than read off its help: a file created in 2019,
    /// written in 2020, read in 2021 and marked read-only and hidden came out of robocopy
    /// with all three dates and both marks. The second pass copied the bytes and nothing
    /// else, so a file it rescued arrived dated today and visible — sorted, filtered and
    /// backed up as something it was not, beside a thousand others that came over right.
    /// And with the date wrong, <see cref="IsWhole"/> would rightly call it a fragment.
    /// </para>
    /// <para>
    /// The dates go on before the attributes: once a file is read-only, it does not take them.
    /// </para>
    /// </summary>
    internal static async Task CopyWholeAsync(FileInfo original, string target, CancellationToken cancellationToken)
    {
        // The most permissive request there is: whatever the owner allows, this accepts.
        using (var reader = new FileStream(original.FullName, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
        using (var writer = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await reader.CopyToAsync(writer, cancellationToken).ConfigureAwait(false);
        }

        File.SetCreationTimeUtc(target, original.CreationTimeUtc);
        File.SetLastWriteTimeUtc(target, original.LastWriteTimeUtc);
        File.SetLastAccessTimeUtc(target, original.LastAccessTimeUtc);

        FileAttributes carried = original.Attributes & Carried;
        File.SetAttributes(target, carried == 0 ? FileAttributes.Normal : carried);
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
            if (code == RunState.CancelledExitCode) SweepPartials(item);

            return Verdict(kind, item, code, state.BytesDone - before, Directory.Exists(item.Destination),
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
            if (code == RunState.CancelledExitCode) RemovePartial(item, item.Source);

            return Verdict(kind, item, code, state.BytesDone - before, File.Exists(item.Destination),
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

            // Only a whole file earns the real name. A stopped or failed run can leave its
            // fragment in the scratch folder, and moving that out would put it in the place
            // where the file is meant to be; left here, it goes with the scratch folder.
            string landed = Path.Combine(staging, name);
            if (IsWhole(new FileInfo(item.Source), new FileInfo(landed)))
                File.Move(landed, item.Destination, overwrite: false);

            return Verdict(kind, item, code, state.BytesDone - before, File.Exists(item.Destination),
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
            try
            {
                var file = new FileInfo(item.Source);

                // As the other permanent delete does it: a read-only flag is not a decision
                // anybody made about keeping the file, and File.Delete refuses on it.
                if (file.Exists && file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Named the way a purge names the files it could not take, so the window lists
                // it with whoever holds it, and the second pass gets to try it again.
                state.NameFailed(item.Source);
                return new TransferItemResult(item, TransferOutcome.Failed, 0, ex.Message) { FailedPaths = [item.Source] };
            }

            // Counted once it is gone, never before: the bytes of a file still sitting there
            // were going into the "freed" total either way.
            if (File.Exists(item.Source)) return new TransferItemResult(item, TransferOutcome.Failed, 0);

            state.CountFile(item.Source, item.Bytes);
            return new TransferItemResult(item, TransferOutcome.Done, item.Bytes);
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

        long before = state.BytesDone;
        int failedBefore = state.FailedCount;

        // ⚠️ A link is a door, and the purge walks through doors — see Links. Aimed at a
        // junction, it empties the folder the junction stands for. A link goes as a link, by
        // itself, which is what Explorer and rd do with one too.
        if (Links.IsLink(full))
        {
            if (Links.Remove(full)) return new TransferItemResult(item, TransferOutcome.Done, 0);

            state.NameFailed(full);
            return new TransferItemResult(item, TransferOutcome.Failed, 0, L.T("transfer.linkNotRemoved", full))
            {
                FailedPaths = state.FailedSince(failedBefore),
            };
        }

        // The links inside go first, each by itself, so the purge finds no door left to walk
        // through. One that will not go stops the folder: purging around it is the very walk
        // this is here to prevent.
        if (RemoveLinksInside(full, state) is string blocked)
        {
            return new TransferItemResult(item, TransferOutcome.Failed, 0, blocked)
            {
                FailedPaths = state.FailedSince(failedBefore),
            };
        }

        string empty = Path.Combine(Path.GetTempPath(), $".vacuon-empty-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(empty);

            List<string> args = RobocopyArguments.Purge(empty, full, _threads);
            int code = await RunRobocopyAsync(args, item, state, cancellationToken).ConfigureAwait(false);

            if (code == RunState.CancelledExitCode)
            {
                return new TransferItemResult(item, TransferOutcome.Cancelled, state.BytesDone - before)
                {
                    FailedPaths = state.FailedSince(failedBefore),
                };
            }

            // The mirror empties the folder; it does not remove it.
            string? refusal = RemoveFolder(full);

            IReadOnlyList<string> failed = state.FailedSince(failedBefore);

            // ⚠️ Extras is what the purge FOUND, not what it removed. Measured against the real
            // tool, on a folder of three files plus one that another process held open: the
            // table read 4 files and 5,300,000 bytes under Extras, 0 under FAILED, the exit
            // code was 2 — success — and the 5,000,000-byte file was still there. Whatever it
            // named as failed and is still on disk now goes back out of the total, which was
            // otherwise reporting that file's bytes as freed.
            state.Unfree(failed);

            long bytes = state.BytesDone - before;

            // Gone is gone, whatever the exit code said on the way: the folder not being there
            // is the one thing a delete promised.
            if (!Directory.Exists(full)) return new TransferItemResult(item, TransferOutcome.Done, bytes);

            // Still there. This used to arrive as the exception from the removal above, which
            // the caller turned into "failed, 0 bytes" with no file named — a folder that had
            // lost everything but one file, reported as if nothing had happened to it at all.
            string message = !RobocopyOutput.Succeeded(code) ? RobocopyOutput.Describe(code, TransferKind.Delete)
                           : failed.Count == 0 && refusal is not null ? refusal
                           : L.T("transfer.notGone");

            return new TransferItemResult(item, TransferOutcome.Failed, bytes, message) { FailedPaths = failed };
        }
        finally
        {
            try { if (Directory.Exists(empty)) Directory.Delete(empty, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Removes every folder link inside a folder about to be purged, each one by itself.
    /// </summary>
    /// <returns>Why the folder must not be purged, or null when nothing in it leads anywhere else.</returns>
    private static string? RemoveLinksInside(string folder, RunState state)
    {
        List<string> links;

        try
        {
            links = Links.FoldersBelow(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return L.T("transfer.linksUnsearchable", ex.Message);
        }

        foreach (string link in links)
        {
            if (Links.Remove(link)) continue;

            state.NameFailed(link);
            return L.T("transfer.linkInside", link);
        }

        return null;
    }

    /// <summary>Removes what a purge left of a folder.</summary>
    /// <returns>Why it would not go, or null when it went or was already gone.</returns>
    private static string? RemoveFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// "Robocopy returned a friendly number" and "the item is over there" are different
    /// statements, and only the second one is worth reporting. Both get checked.
    /// </summary>
    private static TransferItemResult Verdict(TransferKind kind, TransferItem item, int code, long bytes,
                                              bool arrived, IReadOnlyList<string> failed)
    {
        if (code == RunState.CancelledExitCode)
            return new TransferItemResult(item, TransferOutcome.Cancelled, bytes) { FailedPaths = failed };

        if (!RobocopyOutput.Succeeded(code))
        {
            return new TransferItemResult(item, TransferOutcome.Failed, bytes, RobocopyOutput.Describe(code, kind))
            {
                FailedPaths = failed,
            };
        }

        // ⚠️ A friendly exit code and a file named as failed can arrive together. Measured on
        // the real tool: a move whose source file another program held open for reading copied
        // it, printed "ERROR 32 ... Deleting Source File", counted nothing under FAILED and
        // exited with 1. The file was at both ends and the item read as moved, so the second
        // pass never got to finish it either. Whatever is named is a failure until the second
        // pass says otherwise — and it says so at once for a file robocopy's own retry got
        // after naming it.
        if (failed.Count > 0)
        {
            string why = kind == TransferKind.Move ? L.T("transfer.notReleased") : RobocopyOutput.Describe(8, kind);
            return new TransferItemResult(item, TransferOutcome.Failed, bytes, why) { FailedPaths = failed };
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
            try
            {
                process.Kill(entireProcessTree: true);

                // Killing is asynchronous. Until it has exited, the files it was writing are
                // still open in it, and the sweep that follows a stop could not remove them.
                process.WaitForExit(5000);
            }
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

        // The second pass's place: which file, of how many. Zero outside it.
        private int _secondTry;
        private int _secondTryOf;

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

        /// <summary>Names a file this side failed on itself, the way an error line names one for robocopy.</summary>
        public void NameFailed(string path)
        {
            lock (_gate)
            {
                if (_failedSeen.Add(path)) _failed.Add(path);
            }
        }

        /// <summary>
        /// Takes back out of the total the files a purge counted and could not remove — the
        /// ones it named as failed that are still on the disk, at the size they have there.
        /// </summary>
        public void Unfree(IReadOnlyList<string> paths)
        {
            long bytes = 0;
            int files = 0;

            foreach (string path in paths)
            {
                // A folder robocopy could not read is named too; its contents were never in
                // the Extras column, so there is nothing of it to take back.
                var file = new FileInfo(path);
                if (!file.Exists) continue;

                bytes += file.Length;
                files++;
            }

            if (files == 0) return;

            lock (_gate)
            {
                _bytes -= bytes;
                _files -= files;
            }
        }

        public void Consume(string? line, TransferItem item)
        {
            if (line is null) return;

            // Usually one piece. See RobocopyOutput.Pieces for when it is two.
            foreach (string piece in RobocopyOutput.Pieces(line.TrimStart('﻿'))) ConsumeOne(piece, item);
        }

        private void ConsumeOne(string line, TransferItem item)
        {
            RobocopyLine parsed = RobocopyOutput.Parse(line);

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

        /// <summary>Says which file the second pass is about to try, before it tries it.</summary>
        public void TryingAgain(string path, int number, int of)
        {
            lock (_gate)
            {
                _secondTry = number;
                _secondTryOf = of;
                _percent = null;
            }

            // The first one past the throttle: robocopy's last line went out a moment ago, and
            // a pass that fails fast on one file would otherwise never get onto the screen.
            Report(TransferPhase.Running, path, force: number == 1);
        }

        /// <summary>Ends the second pass, so the closing report is not read as still inside it.</summary>
        public void SecondPassOver()
        {
            lock (_gate)
            {
                _secondTry = 0;
                _secondTryOf = 0;
            }
        }

        public void Report(TransferPhase phase, string? current, bool force = false)
        {
            if (_progress is null) return;

            TransferProgress snapshot;

            lock (_gate)
            {
                TimeSpan now = _clock.Elapsed;

                bool terminal = phase is not (TransferPhase.Running or TransferPhase.Preparing);
                if (!terminal && !force && _lastReport is TimeSpan last && now - last < ReportEvery) return;

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
                    _percent)
                {
                    SecondTry = _secondTry,
                    SecondTryOf = _secondTryOf,
                };
            }

            _progress.Report(snapshot);
        }
    }
}
