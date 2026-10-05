using Vacuon.Core.Transfer;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Drives the real robocopy against real files in the temp folder.
/// <para>
/// The parser tests above check the shapes robocopy printed on the day they were written.
/// These check that it still prints them — which is the part no amount of unit testing can
/// stand in for, and the part that breaks silently: a changed output format does not throw,
/// it just makes the progress bar stop moving while the copy works perfectly.
/// </para>
/// <para>
/// Nothing here needs elevation and nothing here leaves the temp folder. Every test cleans
/// up after itself in a finally, including the one that fails.
/// </para>
/// </summary>
public class TransferIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-transfer-tests-" + Guid.NewGuid().ToString("N"));

    public TransferIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string folder, string name, int bytes)
    {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task CopyingATree_MovesEveryFileAndCountsExactlyWhatItWrote()
    {
        string source = Dir("source");
        string destination = Dir("destination");

        WriteFile(source, "one.bin", 200_000);
        WriteFile(source, "two.bin", 200_000);
        WriteFile(Path.Combine(source, "sub"), "three.bin", 150_000);

        // A non-ASCII name on purpose: without robocopy's /UNICODE the name comes back
        // through the pipe in the OEM code page and the window shows mojibake.
        WriteFile(Path.Combine(source, "sub"), "acentuação.bin", 50_000);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([source], destination, TransferKind.Copy);

        Assert.Equal(600_000, plan.Bytes);

        var seen = new List<TransferProgress>();
        TransferReport report = await service.ExecuteAsync(
            plan, new Progress<TransferProgress>(seen.Add), CancellationToken.None);

        string landed = Path.Combine(destination, "source");

        Assert.Equal(TransferPhase.Finished, report.Phase);
        Assert.True(File.Exists(Path.Combine(landed, "one.bin")));
        Assert.True(File.Exists(Path.Combine(landed, "sub", "three.bin")));
        Assert.True(File.Exists(Path.Combine(landed, "sub", "acentuação.bin")));

        // The bytes counted off robocopy's own lines against the bytes on the disk. If the
        // output format ever changes under us, this is the assertion that notices.
        Assert.Equal(600_000, report.BytesTransferred);
        Assert.False(report.TotalIsUncertain);
        Assert.Equal(0, report.FailedCount);

        // The source is untouched: this was a copy.
        Assert.True(File.Exists(Path.Combine(source, "one.bin")));

        // And a copy never reports space as freed, whatever it moved.
        Assert.False(report.BytesWereFreed);
    }

    [Fact]
    public async Task TheFileCountCountsFiles_NotSelectedItems()
    {
        // ⚠️ The readout said "1,501 / 1,001" on a real batch: a thousand loose files plus one
        // folder holding five hundred more. The numerator counted files as robocopy reported
        // them landing; the denominator counted the things that had been ticked, and a folder
        // is one of those however deep it goes. Two units in one fraction.
        string source = Dir("count-source");
        string destination = Dir("count-destination");

        var picked = new List<string>();
        for (int i = 0; i < 4; i++) picked.Add(WriteFile(source, $"loose{i}.bin", 1000));

        string folder = Path.Combine(source, "holds-three");
        for (int i = 0; i < 3; i++) WriteFile(folder, $"inside{i}.bin", 1000);
        picked.Add(folder);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan(picked, destination, TransferKind.Copy);

        // Five things were ticked. Seven files will actually be written.
        Assert.Equal(5, plan.Count);
        Assert.Equal(7, plan.FileCount);

        var seen = new List<TransferProgress>();
        TransferReport report = await service.ExecuteAsync(
            plan, new Progress<TransferProgress>(seen.Add), CancellationToken.None);

        Assert.Equal(TransferPhase.Finished, report.Phase);
        Assert.All(seen, p => Assert.True(p.FilesDone <= p.FilesTotal,
            $"progress reported {p.FilesDone} of {p.FilesTotal}"));
    }

    [Fact]
    public async Task ProgressIsRaisedWithMeasuredBytes_NotJustAtTheEnd()
    {
        string source = Dir("progress-source");
        string destination = Dir("progress-destination");

        for (int i = 0; i < 12; i++) WriteFile(source, $"f{i}.bin", 100_000);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([source], destination, TransferKind.Copy);

        var seen = new List<TransferProgress>();
        TransferReport report = await service.ExecuteAsync(
            plan, new Progress<TransferProgress>(seen.Add), CancellationToken.None);

        Assert.Equal(TransferPhase.Finished, report.Phase);

        // Progress<T> hands its callbacks to the synchronisation context, and a test has
        // none — so they land on the thread pool and the last few can still be in flight.
        // What matters is that some arrived carrying bytes, not exactly how many.
        Assert.Contains(seen, p => p.BytesDone > 0);
        Assert.All(seen, p => Assert.Equal(1_200_000, p.BytesTotal));
    }

    [Fact]
    public async Task NothingIsOverwritten_ASecondCopyGoesInUnderAnotherName()
    {
        string source = Dir("dup-source");
        string destination = Dir("dup-destination");

        WriteFile(source, "clip.mp4", 1000);
        WriteFile(destination, "clip.mp4", 7777);

        var service = new FileTransferService();
        string file = Path.Combine(source, "clip.mp4");

        TransferPlan plan = service.Plan([file], destination, TransferKind.Copy);

        // The plan says so before anything runs, which is what a confirmation could show.
        Assert.True(plan.Items[0].Renamed);

        TransferReport report = await service.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(TransferPhase.Finished, report.Phase);
        Assert.True(File.Exists(Path.Combine(destination, "clip (2).mp4")));

        // The file that was already there is exactly as it was.
        Assert.Equal(7777, new FileInfo(Path.Combine(destination, "clip.mp4")).Length);

        // And the scratch folder the rename went through does not survive.
        Assert.Empty(Directory.GetDirectories(destination));
    }

    [Fact]
    public async Task MovingATree_LeavesNothingBehind()
    {
        string source = Dir("move-source");
        string destination = Dir("move-destination");

        WriteFile(source, "a.bin", 120_000);
        WriteFile(Path.Combine(source, "deep"), "b.bin", 80_000);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([source], destination, TransferKind.Move);

        TransferReport report = await service.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(TransferPhase.Finished, report.Phase);
        Assert.True(File.Exists(Path.Combine(destination, "move-source", "deep", "b.bin")));
        Assert.False(Directory.Exists(source));

        // A move within one volume frees nothing — it rewrote directory entries.
        Assert.False(report.BytesWereFreed);
    }

    [Fact]
    public async Task DeletingAFolder_EmptiesItAndThenRemovesIt()
    {
        string doomed = Dir("doomed");

        WriteFile(doomed, "x.bin", 300_000);
        WriteFile(Path.Combine(doomed, "nested", "deeper"), "y.bin", 200_000);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([doomed], string.Empty, TransferKind.Delete);

        TransferReport report = await service.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(TransferPhase.Finished, report.Phase);
        Assert.False(Directory.Exists(doomed));

        // The mirror reports what it removed as Extras, and those are the bytes counted.
        Assert.Equal(500_000, report.BytesTransferred);

        // This is the one kind of batch that may say "freed", and it does.
        Assert.True(report.BytesWereFreed);
    }

    [Fact]
    public void AProtectedPathIsRefusedInThePlan_BeforeAnyProcessExists()
    {
        // ⚠️ The delete path aims /MIR at a folder. The guard has to sit in the plan, where
        // there is still nothing running to stop.
        var service = new FileTransferService();

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        TransferPlan plan = service.Plan([windows], string.Empty, TransferKind.Delete);

        Assert.Equal(TransferOutcome.Blocked, plan.Items[0].Refusal);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void AVolumeRootIsRefusedToo()
    {
        var service = new FileTransferService();
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        TransferPlan plan = service.Plan([root], string.Empty, TransferKind.Delete);

        Assert.True(plan.Items[0].IsRefused);
    }

    [Fact]
    public async Task AFolderCannotBeCopiedIntoItself()
    {
        string outer = Dir("outer");
        string inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        WriteFile(outer, "f.bin", 1000);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([outer], inner, TransferKind.Copy);

        Assert.Equal(TransferOutcome.IntoItself, plan.Items[0].Refusal);

        // And a plan with nothing movable in it runs to a finished report that did nothing,
        // rather than starting a process that would discover this halfway through.
        TransferReport report = await service.ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Equal(0, report.BytesTransferred);
    }

    [Fact]
    public async Task AnItemWhoseAncestorIsAlsoSelectedTravelsOnlyOnce()
    {
        string parent = Dir("collapse-parent");
        string child = Path.Combine(parent, "child");
        WriteFile(child, "c.bin", 4000);

        string destination = Dir("collapse-destination");

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([parent, child], destination, TransferKind.Copy);

        // The second trip would start from a path that the first one had already dealt with.
        Assert.Single(plan.Items);

        TransferReport report = await service.ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Equal(TransferPhase.Finished, report.Phase);
    }

    [Fact]
    public async Task AFileSomebodyElseHasOpen_IsNamedInTheReport()
    {
        // The whole point of this one: a folder is a single item, so before this the report
        // could only say that something inside had failed. It runs the real robocopy against
        // a real lock, because "robocopy prints an ERROR line naming the file" is exactly the
        // kind of claim that is worth nothing written from memory.
        string source = Dir("locked-source");
        string destination = Dir("locked-destination");

        WriteFile(source, "fine.bin", 4096);
        string locked = WriteFile(source, "locked.bin", 4096);

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new FileTransferService();
            TransferPlan plan = service.Plan([source], destination, TransferKind.Copy);
            TransferReport report = await service.ExecuteAsync(plan);

            Assert.Equal(TransferPhase.Failed, report.Phase);

            // Named, once, despite the retry naming it a second time.
            Assert.Equal(1, report.FailedFilePaths.Count(p => string.Equals(p, locked, StringComparison.OrdinalIgnoreCase)));

            // And the tool's own count agrees with the list, so the window has nothing to
            // apologise for.
            Assert.Equal(1, report.FailedFileCount);

            // The one that was not locked still travelled.
            Assert.True(File.Exists(Path.Combine(destination, "locked-source", "fine.bin"))
                        || File.Exists(Path.Combine(destination, "fine.bin")));
        }
    }

    [Fact]
    public async Task BytesOfAFileThatFailed_AreNotCountedAsTransferred()
    {
        // Robocopy announces a file before it knows whether it will land, and then announces
        // it again on the retry. Counting those lines and stopping there had the window
        // reporting more bytes transferred than the plan weighed in the first place.
        string source = Dir("accounting-source");
        string destination = Dir("accounting-destination");

        WriteFile(source, "arrives.bin", 4096);
        string locked = WriteFile(source, "never-arrives.bin", 4096);

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new FileTransferService();
            TransferPlan plan = service.Plan([source], destination, TransferKind.Copy);
            TransferReport report = await service.ExecuteAsync(plan);

            // Exactly the one file that made it, once.
            Assert.Equal(4096, report.BytesTransferred);

            // And with the phantom bytes gone, the two readings agree, so nothing has to be
            // reported as uncertain.
            Assert.False(report.TotalIsUncertain);
        }
    }

    [Fact]
    public async Task ALockThatIsStillHeld_IsNotRescuedBySayingSoTwice()
    {
        // The honest limit of the second pass, kept in a test so nobody later reads the
        // retry as "the copy always finishes". Measured against the real thing in five
        // sharing modes: robocopy, File.Copy and the most permissive open .NET offers
        // succeed on the same files and fail on the same files. A file whose owner denies
        // read sharing cannot be read by anything on the machine, Explorer included.
        string source = Dir("held-source");
        string destination = Dir("held-destination");

        string locked = WriteFile(source, "held.bin", 4096);

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new FileTransferService();
            TransferPlan plan = service.Plan([source], destination, TransferKind.Copy);
            TransferReport report = await service.ExecuteAsync(plan);

            Assert.Equal(0, report.RecoveredCount);
            Assert.Equal(TransferPhase.Failed, report.Phase);
            Assert.Contains(report.FailedFilePaths,
                            p => string.Equals(p, locked, StringComparison.OrdinalIgnoreCase));

            // And nothing was invented at the destination to cover for it.
            Assert.False(File.Exists(Path.Combine(destination, "held-source", "held.bin")));
        }
    }

    [Fact]
    public async Task ALockThatLetGo_IsPickedUpByTheSecondPass()
    {
        // The case the second pass exists for. The hold is released while the batch is
        // still running, which is what happens on a real copy: robocopy gives up on a file
        // about a second after meeting it, and a batch runs for minutes.
        string source = Dir("released-source");
        string destination = Dir("released-destination");

        WriteFile(source, "ordinary.bin", 4096);
        string locked = WriteFile(source, "released.bin", 8192);

        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([source], destination, TransferKind.Copy);

        // Let go once robocopy has had its go at it. The retry runs after the whole batch,
        // so by then the file is free.
        using var releasing = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(400, releasing.Token);
            hold.Dispose();
        }, releasing.Token);

        TransferReport report = await service.ExecuteAsync(plan);
        await releasing.CancelAsync();
        hold.Dispose();

        // Whether this pass or the tool's own retry got it, the file is not a failure and
        // nothing is left on the list.
        Assert.Empty(report.FailedFilePaths);
        Assert.Equal(TransferPhase.Finished, report.Phase);

        string landed = Path.Combine(destination, "released-source", "released.bin");
        Assert.True(File.Exists(landed));
        Assert.Equal(8192, new FileInfo(landed).Length);

        // And the bytes it brought over are in the total, not left out of it.
        // The total is the closing table's, so it is what landed and not what was announced.
        Assert.Equal(4096 + 8192, report.BytesTransferred);
        Assert.False(report.TotalIsUncertain);
    }

    [Fact]
    public async Task DeletingAFolder_DoesNotReportItsOwnTotalAsUncertain()
    {
        // A purge copies nothing, so the Copied column of its closing table is zero while
        // the files it removed are counted under Extras. Reading Copied for both jobs made
        // every single delete end with "the two counts of these bytes disagree, so no single
        // total is quoted" — on a number that was never in doubt.
        string target = Dir("purge-me");
        WriteFile(target, "a.bin", 100_000);
        WriteFile(target, "b.bin", 200_000);
        WriteFile(Path.Combine(target, "sub"), "c.bin", 300_000);

        var service = new FileTransferService();
        TransferPlan plan = service.Plan([target], string.Empty, TransferKind.Delete);
        TransferReport report = await service.ExecuteAsync(plan);

        Assert.False(Directory.Exists(target));
        Assert.False(report.TotalIsUncertain);
        Assert.Equal(600_000, report.BytesTransferred);
        Assert.True(report.BytesWereFreed);
    }

    /// <summary>
    /// Keeps every report, in order, on the thread that raised it. <see cref="Progress{T}"/>
    /// would post them to the thread pool, where they arrive late and out of order.
    /// </summary>
    private sealed class Recorder : IProgress<TransferProgress>
    {
        public List<TransferProgress> Seen { get; } = [];

        public void Report(TransferProgress value)
        {
            lock (Seen) Seen.Add(value);
        }
    }

    [Fact]
    public async Task TheSecondPass_SaysWhichFileItIsOn_BeforeTryingIt()
    {
        // It used to work in silence after robocopy exited: the last line of the tool stayed
        // on screen and every figure stood still, for as long as the pass took.
        string source = Dir("second-pass-source");
        string destination = Dir("second-pass-destination");

        WriteFile(source, "fine.bin", 4096);
        string held = WriteFile(source, "held.bin", 4096);

        using var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

        var service = new FileTransferService();
        var recorder = new Recorder();
        await service.ExecuteAsync(service.Plan([source], destination, TransferKind.Copy), recorder);

        // Robocopy names the held file twice, once per attempt; the pass goes back for it once.
        TransferProgress second = Assert.Single(recorder.Seen, p => p.SecondTryOf > 0);
        Assert.Equal(1, second.SecondTry);
        Assert.Equal(1, second.SecondTryOf);
        Assert.Equal(held, second.CurrentItem, ignoreCase: true);

        // And the closing report is not still inside the pass.
        Assert.Equal(0, recorder.Seen[^1].SecondTryOf);
    }

    /// <summary>Presses Stop the moment the second pass says it is starting on a file.</summary>
    private sealed class StopOnSecondTry(CancellationTokenSource stop) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            if (value.SecondTryOf > 0) stop.Cancel();
        }
    }

    [Fact]
    public async Task StoppingDuringTheSecondPass_StopsIt_InsteadOfThrowing()
    {
        // A region locked by another handle: robocopy fails on it, but the second pass can
        // open the file, so Stop lands inside its copy. The cancellation came out of that
        // copy as an exception nobody caught, and the window's async Loaded handler handed it
        // to the dispatcher - which closes the app.
        string source = Dir("stop-second-source");
        string destination = Dir("stop-second-destination");

        WriteFile(source, "fine.bin", 4096);
        string locked = WriteFile(source, "region.bin", 1_000_000);

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        hold.Lock(500_000, 100_000);

        try
        {
            using var stop = new CancellationTokenSource();
            var service = new FileTransferService();

            TransferReport report = await service.ExecuteAsync(
                service.Plan([source], destination, TransferKind.Copy), new StopOnSecondTry(stop), stop.Token);

            Assert.Equal(TransferPhase.Cancelled, report.Phase);
            Assert.Contains(report.FailedFilePaths,
                            p => string.Equals(p, locked, StringComparison.OrdinalIgnoreCase));

            // And the second pass's own half-written copy is not left behind as the file.
            Assert.False(File.Exists(Path.Combine(destination, "stop-second-source", "region.bin")));
        }
        finally
        {
            hold.Unlock(500_000, 100_000);
        }
    }

    private static readonly DateTime Then = new(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    /// <summary>A source file dated <see cref="Then"/>, and something at its destination.</summary>
    private (string Source, string Target, TransferItem Item) SourceAndTarget(string name, int length, int written,
                                                                             DateTime writtenAt)
    {
        string from = Dir(name + "-from");
        string to = Dir(name + "-to");

        string source = WriteFile(from, "f.bin", length);
        File.SetLastWriteTimeUtc(source, Then);

        string target = WriteFile(to, "f.bin", written);
        File.SetLastWriteTimeUtc(target, writtenAt);

        return (source, target, new TransferItem(source, target, length, IsDirectory: false));
    }

    [Fact]
    public void AFileAsLongAsItsSourceButNotDatedLikeIt_IsNotWhole()
    {
        // What a stopped copy leaves: robocopy sizes the file first, so a fragment is as long
        // as its source. Only the date robocopy gives the file at the very end tells them apart.
        (string source, string target, _) = SourceAndTarget("prealloc", 1000, 1000, DateTime.UtcNow);
        Assert.False(FileTransferService.IsWhole(new FileInfo(source), new FileInfo(target)));

        File.SetLastWriteTimeUtc(target, Then);
        Assert.True(FileTransferService.IsWhole(new FileInfo(source), new FileInfo(target)));

        // A FAT or exFAT destination keeps times to two seconds. A whole copy there must not
        // read as a fragment, or the sweep after a stop would delete it.
        File.SetLastWriteTimeUtc(target, Then.AddSeconds(2));
        Assert.True(FileTransferService.IsWhole(new FileInfo(source), new FileInfo(target)));
    }

    [Fact]
    public void AFragmentGoes_AWholeCopyStays_AndACopyWhoseSourceIsGoneIsNeverTouched()
    {
        (string shortSource, string shortTarget, TransferItem shortItem) = SourceAndTarget("short", 1000, 400, Then);
        Assert.True(FileTransferService.RemovePartial(shortItem, shortSource));
        Assert.False(File.Exists(shortTarget));

        (string fullSource, string fullTarget, TransferItem fullItem) = SourceAndTarget("full", 1000, 1000, DateTime.UtcNow);
        Assert.True(FileTransferService.RemovePartial(fullItem, fullSource));
        Assert.False(File.Exists(fullTarget));

        (string wholeSource, string wholeTarget, TransferItem wholeItem) = SourceAndTarget("whole", 1000, 1000, Then);
        Assert.False(FileTransferService.RemovePartial(wholeItem, wholeSource));
        Assert.True(File.Exists(wholeTarget));

        // A move that finished this file before it was stopped already removed the source:
        // what is at the destination is the only copy left, whatever it looks like.
        (string goneSource, string goneTarget, TransferItem goneItem) = SourceAndTarget("orphan", 1000, 400, Then);
        File.Delete(goneSource);
        Assert.False(FileTransferService.RemovePartial(goneItem, goneSource));
        Assert.True(File.Exists(goneTarget));
    }

    [Fact]
    public async Task AFileRobocopyCouldNotFinish_IsNotLeftAtTheDestinationUnderItsName()
    {
        // Measured: a region another handle has locked stops robocopy in the middle of the
        // file, and it leaves what it had written — 10 MB of a 50 MB file — under the real
        // name, dated today. The second pass then stops at the same region.
        string source = Dir("fragment-source");
        string destination = Dir("fragment-destination");

        WriteFile(source, "fine.bin", 4096);
        string locked = WriteFile(source, "region.bin", 2_000_000);

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        hold.Lock(1_000_000, 100_000);

        try
        {
            var service = new FileTransferService();
            TransferReport report = await service.ExecuteAsync(service.Plan([source], destination, TransferKind.Copy));

            Assert.Contains(report.FailedFilePaths,
                            p => string.Equals(p, locked, StringComparison.OrdinalIgnoreCase));

            string landed = Path.Combine(destination, "fragment-source");
            Assert.True(File.Exists(Path.Combine(landed, "fine.bin")));
            Assert.False(File.Exists(Path.Combine(landed, "region.bin")));
        }
        finally
        {
            hold.Unlock(1_000_000, 100_000);
        }
    }

    [Fact]
    public async Task AStoppedCopy_LeavesNothingAtTheDestinationThatIsNotWhole()
    {
        // Measured: four 1 GiB files stopped after 0.7 s were all at the destination at full
        // length, with under a fifth of each written. Whenever the stop lands, nothing left
        // behind may be anything but a complete copy of its source.
        string source = Dir("stopped-source");
        string destination = Dir("stopped-destination");

        var block = new byte[1 << 20];
        new Random(7).NextBytes(block);

        for (int f = 0; f < 3; f++)
        {
            string path = Path.Combine(source, $"big{f}.bin");
            using (var stream = File.Create(path))
                for (int i = 0; i < 96; i++) stream.Write(block);

            File.SetLastWriteTimeUtc(path, Then);
        }

        var service = new FileTransferService();
        using var stop = new CancellationTokenSource();

        // Stop the moment robocopy creates the first file. It creates it at full length and
        // then fills it, so this lands with the data part-way in - the case a timer would only
        // sometimes hit.
        Task watching = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (Directory.Exists(destination)
                    && Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any())
                {
                    stop.Cancel();
                    return;
                }

                await Task.Delay(2);
            }
        });

        TransferReport report = await service.ExecuteAsync(
            service.Plan([source], destination, TransferKind.Copy), null, stop.Token);
        await watching;

        Assert.Equal(TransferPhase.Cancelled, report.Phase);

        foreach (string written in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
        {
            var original = new FileInfo(Path.Combine(source, Path.GetFileName(written)));
            Assert.True(FileTransferService.IsWhole(original, new FileInfo(written)),
                        $"{written} was left behind and is not a whole copy");
        }
    }

    [Fact]
    public async Task WhatTheSecondPassCopies_CarriesTheDatesAndMarksRobocopyWould()
    {
        // Measured on robocopy itself: /COPY:DAT carries all three dates and the read-only and
        // hidden marks. The second pass carried the bytes and nothing else.
        string from = Dir("dates-from");
        string to = Dir("dates-to");

        string source = WriteFile(from, "kept.bin", 70_000);
        File.SetCreationTimeUtc(source, new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(source, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);

        string target = Path.Combine(to, "kept.bin");

        try
        {
            // Read before the copy, as the second pass reads it: reading the file afterwards
            // moves its last-access time, and that is the source's date this has to carry.
            var original = new FileInfo(source);
            _ = original.Length;

            await FileTransferService.CopyWholeAsync(original, target, CancellationToken.None);

            // The dates first. Reading the contents below touches the access time.
            var copy = new FileInfo(target);
            Assert.Equal(new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc), copy.CreationTimeUtc);
            Assert.Equal(new DateTime(2020, 6, 7, 8, 9, 10, DateTimeKind.Utc), copy.LastWriteTimeUtc);
            Assert.Equal(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), copy.LastAccessTimeUtc);
            Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive, copy.Attributes);

            // And by the rule that decides what counts as arrived, it has.
            Assert.True(FileTransferService.IsWhole(original, copy));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task DeletingAFolderWithAFileHeldOpen_NamesIt_AndDoesNotCountItAsFreed()
    {
        // Measured on the real tool before this was written: the purge removes everything
        // else, prints an ERROR line for the held file, counts it under Extras anyway, reports
        // nothing under FAILED and exits with 2 — success. The folder removal after it then
        // threw, and the item came back as "failed, 0 bytes", naming nothing, while the total
        // claimed the held file's bytes as freed.
        string target = Dir("held-delete");
        WriteFile(target, "a.bin", 100_000);
        WriteFile(Path.Combine(target, "sub"), "b.bin", 200_000);
        string held = WriteFile(target, "held.bin", 5_000_000);

        using (var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new FileTransferService();
            TransferPlan plan = service.Plan([target], string.Empty, TransferKind.Delete);
            TransferReport report = await service.ExecuteAsync(plan);

            TransferItemResult item = Assert.Single(report.Results);
            Assert.Equal(TransferOutcome.Failed, item.Outcome);

            // The file that stopped it, by name — which is what the window lists, and what it
            // asks the Restart Manager about.
            Assert.Contains(report.FailedFilePaths,
                            p => string.Equals(p, held, StringComparison.OrdinalIgnoreCase));

            // Freed is what left the disk, and not one byte of the file still on it.
            Assert.Equal(300_000, report.BytesTransferred);
            Assert.Equal(300_000, item.BytesTransferred);

            // And the disk agrees: the folder is down to the one file.
            Assert.True(File.Exists(held));
            Assert.False(Directory.Exists(Path.Combine(target, "sub")));
            Assert.Single(Directory.GetFileSystemEntries(target));
        }
    }

    [Fact]
    public async Task ASingleFileHeldOpen_IsNamedToo_AndNotCountedAsFreed()
    {
        // A lone file in a delete batch skips robocopy. It used to count its bytes as freed
        // before looking, and to fail as an exception with nothing named.
        string folder = Dir("held-single");
        string held = WriteFile(folder, "held.bin", 70_000);
        string fine = WriteFile(folder, "fine.bin", 30_000);

        using (var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new FileTransferService();
            TransferPlan plan = service.Plan([held, fine], string.Empty, TransferKind.Delete);
            TransferReport report = await service.ExecuteAsync(plan);

            Assert.Equal(30_000, report.BytesTransferred);
            Assert.Contains(report.FailedFilePaths,
                            p => string.Equals(p, held, StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(fine));
            Assert.True(File.Exists(held));
        }
    }
}
