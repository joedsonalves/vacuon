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
}
