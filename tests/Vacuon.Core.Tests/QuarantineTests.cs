using System.Text;
using System.Text.Json;
using Vacuon.Core.Actions;
using Vacuon.Core.Safety;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The quarantine is milestone M4 — the undo the rest of the app has been promising. What
/// is guarded here is mostly the promise: that a batch can always be put back, that putting
/// it back never destroys anything else, and that holding files is never reported as freeing
/// space.
/// </summary>
public class QuarantineServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vacuon-quarantine-tests", Guid.NewGuid().ToString("N"));

    private readonly string _store;

    public QuarantineServiceTests()
    {
        Directory.CreateDirectory(_root);

        // On the same volume as the files under test, because that is the whole premise:
        // a quarantine that crossed volumes would be a copy, not a rename.
        _store = Path.Combine(_root, "store");
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private QuarantineService Service(TimeProvider? time = null) =>
        new(time, _ => _store);

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string File_(string folder, string name, string content = "vacuon")
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Execute_MovesTheFileOutAndTheOriginalPathIsEmpty()
    {
        string from = Dir("from");
        string source = File_(from, "render.mp4", "the final cut");

        QuarantineReport report = Service().Execute([source]);

        QuarantineResult result = Assert.Single(report.Results);
        Assert.Equal(QuarantineOutcome.Quarantined, result.Outcome);
        Assert.False(File.Exists(source));
        Assert.NotNull(report.BatchId);
    }

    [Fact]
    public void Execute_HoldsBytes_ItDoesNotFreeThem()
    {
        // The distinction this whole type exists to keep. A rename inside a volume moves a
        // directory entry; every cluster is still allocated. Only Purge frees anything, and
        // there is no BytesFreed on the report to reach for by accident.
        string from = Dir("from");
        string source = File_(from, "big.bin", new string('x', 4096));

        QuarantineReport report = Service().Execute([source]);

        Assert.Equal(4096, report.BytesHeld);
        Assert.Equal(1, report.QuarantinedCount);
    }

    [Fact]
    public void Restore_PutsTheFileBackWithItsContentIntact()
    {
        string from = Dir("from");
        string source = File_(from, "notes.txt", "do not lose this");

        var service = Service();
        QuarantineReport report = service.Execute([source]);
        Assert.Equal(1, report.QuarantinedCount);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        IReadOnlyList<RestoreResult> restored = service.Restore(batch);

        RestoreResult result = Assert.Single(restored);
        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.Equal("do not lose this", File.ReadAllText(source));
    }

    [Fact]
    public void Restore_RefusesToOverwriteWhateverTookTheOriginalPathBack()
    {
        // An undo that destroys a file the user never asked to lose is worse than no undo.
        string from = Dir("from");
        string source = File_(from, "clip.mkv", "the quarantined one");

        var service = Service();
        service.Execute([source]);

        File_(from, "clip.mkv", "a NEW file with the same name");

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        RestoreResult result = Assert.Single(service.Restore(batch));

        Assert.Equal(RestoreOutcome.OriginalPathTaken, result.Outcome);
        Assert.Equal("a NEW file with the same name", File.ReadAllText(source));
    }

    [Fact]
    public void Restore_RecreatesTheOriginalFolderWhenItIsGone()
    {
        string from = Dir("from");
        string source = File_(from, "orphan.txt", "still mine");

        var service = Service();
        service.Execute([source]);

        Directory.Delete(from, recursive: true);
        Assert.False(Directory.Exists(from));

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        RestoreResult result = Assert.Single(service.Restore(batch));

        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.Equal("still mine", File.ReadAllText(source));
    }

    [Fact]
    public void Manifest_IsWrittenBeforeAnythingMoves_SoNoFileIsEverAnonymous()
    {
        // The failure that actually loses data is a file sitting in the store as 00001.bin
        // with nothing recording where it came from — its original name is gone too, so no
        // amount of inspection recovers it. Every stored name is decided up front and the
        // manifest is written first, so a crash mid-batch still describes the whole intent.
        string from = Dir("from");
        string a = File_(from, "a.txt");
        string b = File_(from, "b.txt");

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));

        Assert.Equal(2, batch.Items.Count);
        Assert.Equal(["00001.bin", "00002.bin"], batch.Items.Select(i => i.StoredName).ToArray());
        Assert.Contains(batch.Items, i => i.OriginalPath == a);
        Assert.Contains(batch.Items, i => i.OriginalPath == b);
    }

    [Fact]
    public void Restore_SkipsAnEntryWhoseFileNeverArrived()
    {
        // Simulates the crash the manifest-first ordering exists for: the manifest lists an
        // item, the store does not hold it. That is a skip with a reason, never a failure
        // that stops the rest of the batch from coming back.
        string from = Dir("from");
        string a = File_(from, "a.txt", "A");
        string b = File_(from, "b.txt", "B");

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        File.Delete(Path.Combine(batch.BatchFolder, "00001.bin"));

        IReadOnlyList<RestoreResult> results = service.Restore(batch);

        Assert.Equal(2, results.Count);
        Assert.Equal(RestoreOutcome.MissingFromQuarantine, results[0].Outcome);
        Assert.Equal(RestoreOutcome.Restored, results[1].Outcome);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Restore_TakesJustTheItemsAsked()
    {
        string from = Dir("from");
        string a = File_(from, "a.txt", "A");
        string b = File_(from, "b.txt", "B");

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        RestoreResult result = Assert.Single(service.Restore(batch, ["00002.bin"]));

        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Execute_TakesAWholeFolderAndRestoreBringsTheTreeBack()
    {
        string from = Dir("from");
        string tree = Path.Combine(from, "project");
        Directory.CreateDirectory(Path.Combine(tree, "nested"));
        File.WriteAllText(Path.Combine(tree, "nested", "deep.txt"), "deep");

        var service = Service();
        QuarantineReport report = service.Execute([tree]);

        Assert.Equal(1, report.QuarantinedCount);
        Assert.True(Assert.Single(report.Results).IsDirectory);
        Assert.False(Directory.Exists(tree));

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        Assert.Equal(RestoreOutcome.Restored, Assert.Single(service.Restore(batch)).Outcome);
        Assert.Equal("deep", File.ReadAllText(Path.Combine(tree, "nested", "deep.txt")));
    }

    [Fact]
    public void Plan_TouchesNothingOnDisk()
    {
        string from = Dir("from");
        string source = File_(from, "render.mp4");

        QuarantineReport plan = Service().Plan([source]);

        Assert.True(plan.WasDryRun);
        Assert.Equal(1, plan.QuarantinedCount);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetDirectories(_store));
    }

    [Fact]
    public void ProtectedPath_IsBlockedAndNeverMoved()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        QuarantineReport report = Service().Execute([windows]);

        QuarantineResult result = Assert.Single(report.Results);
        Assert.Equal(QuarantineOutcome.Blocked, result.Outcome);
        Assert.True(Directory.Exists(windows));
        Assert.Null(report.BatchId);
    }

    [Fact]
    public void Purge_ReportsOnlyWhatItReallyRemoved()
    {
        string from = Dir("from");
        string source = File_(from, "big.bin", new string('x', 2048));

        var service = Service();
        service.Execute([source]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        long freed = service.Purge(batch);

        Assert.Equal(2048, freed);
        Assert.False(Directory.Exists(batch.BatchFolder));
        Assert.Empty(service.ListBatches("C:\\"));
    }

    [Fact]
    public void Purge_CountsNothingForAnItemThatWasAlreadyRestored()
    {
        // "Freed" has to mean bytes that actually went away. An item taken back out of the
        // store before the purge is still on the disk, under its original name.
        string from = Dir("from");
        string a = File_(from, "a.bin", new string('x', 1000));
        string b = File_(from, "b.bin", new string('y', 500));

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        service.Restore(batch, ["00001.bin"]);

        Assert.Equal(500, service.Purge(batch));
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Held_CountsWhatIsThereNow_NotWhatTheManifestSetOutToHold()
    {
        // Found by running the CLI against a real batch: after restoring everything, the
        // listing still read "2 items · 34 B held", because it printed the manifest total.
        // The batch was holding nothing at that point — the files were back under their own
        // names. Claiming to hold bytes that are not there is the same defect as any other
        // number the app did not measure.
        string from = Dir("from");
        string a = File_(from, "a.bin", new string('x', 300));
        string b = File_(from, "b.bin", new string('y', 700));

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        Assert.Equal((1000, 2), service.Held(batch));
        Assert.Equal(1000, batch.TotalBytes);

        service.Restore(batch, ["00001.bin"]);

        // The manifest still describes both; only one is still held.
        Assert.Equal(1000, batch.TotalBytes);
        Assert.Equal((700, 1), service.Held(batch));
    }

    [Fact]
    public void Restore_OfEverythingRemovesTheBatchInsteadOfLeavingAnEmptyOne()
    {
        // Otherwise every fully restored batch stays in the listing forever, describing
        // items it no longer has.
        string from = Dir("from");
        string a = File_(from, "a.txt");
        string b = File_(from, "b.txt");

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        service.Restore(batch);

        Assert.False(Directory.Exists(batch.BatchFolder));
        Assert.Empty(service.ListBatches("C:\\"));
    }

    [Fact]
    public void Restore_OfPartOfABatchKeepsTheRest()
    {
        string from = Dir("from");
        string a = File_(from, "a.txt");
        string b = File_(from, "b.txt");

        var service = Service();
        service.Execute([a, b]);

        QuarantineBatch batch = Assert.Single(service.ListBatches("C:\\"));
        service.Restore(batch, ["00001.bin"]);

        // Still one item in there, so the batch and its manifest stay.
        Assert.True(Directory.Exists(batch.BatchFolder));
        QuarantineBatch still = Assert.Single(service.ListBatches("C:\\"));
        Assert.Equal(1, service.Held(still).Count);
    }

    [Fact]
    public void Expired_UsesTheClockItWasGiven()
    {
        string from = Dir("from");
        string source = File_(from, "old.txt");

        var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = Service(clock);
        service.Execute([source]);

        Assert.Empty(service.Expired("C:\\", TimeSpan.FromDays(30)));

        clock.Advance(TimeSpan.FromDays(31));
        QuarantineBatch expired = Assert.Single(service.Expired("C:\\", TimeSpan.FromDays(30)));
        Assert.Single(expired.Items);
    }

    [Fact]
    public void ListBatches_IsNewestFirstAndSurvivesAnUnreadableManifest()
    {
        string from = Dir("from");

        var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = Service(clock);

        service.Execute([File_(from, "first.txt")]);
        clock.Advance(TimeSpan.FromHours(2));
        service.Execute([File_(from, "second.txt")]);

        // A corrupt manifest must cost its own batch, not the listing.
        string broken = Path.Combine(_store, "2026-01-01T00-00-00Z-dead");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, QuarantineManifest.FileName), "{ not json");

        IReadOnlyList<QuarantineBatch> batches = service.ListBatches("C:\\");

        Assert.Equal(2, batches.Count);
        Assert.True(batches[0].CreatedUtc > batches[1].CreatedUtc);
    }

    [Fact]
    public void BatchId_SortsChronologicallyAsPlainText()
    {
        // The listing is a directory listing; if ids did not sort as text, ordering the
        // quarantine would mean parsing every folder name.
        var early = QuarantineManifest.NewBatchId(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var later = QuarantineManifest.NewBatchId(new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.True(string.CompareOrdinal(early, later) < 0);
        Assert.DoesNotContain(':', early);
        Assert.Equal(-1, early.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void Manifest_RoundTripsThroughDisk()
    {
        var batch = new QuarantineBatch
        {
            BatchId = "2026-08-21T23-00-00Z-aaaa",
            CreatedUtc = new DateTime(2026, 8, 21, 23, 0, 0, DateTimeKind.Utc),
            Volume = "C:\\",
            Items =
            [
                new QuarantineItem
                {
                    StoredName = "00001.bin",
                    OriginalPath = @"C:\Users\Joaozinho\Videos\render.mp4",
                    Bytes = 9_412_233_216,
                    IsDirectory = false,
                    Reason = "manual",
                },
            ],
        };

        string folder = Path.Combine(_root, "batch");
        QuarantineManifest.Write(folder, batch);

        QuarantineBatch? read = QuarantineManifest.Read(folder);

        Assert.NotNull(read);
        Assert.Equal(batch.BatchId, read!.BatchId);
        Assert.Equal(batch.CreatedUtc, read.CreatedUtc);
        Assert.Equal(9_412_233_216, read.TotalBytes);
        Assert.Equal(folder, read.BatchFolder);
        Assert.Equal(@"C:\Users\Joaozinho\Videos\render.mp4", read.Items[0].OriginalPath);
    }

    [Fact]
    public void Manifest_IsNotLeftHalfWritten()
    {
        // Written to a temporary name and swapped in: a truncated manifest names files that
        // are no longer where it says they are, which is worse than having none.
        string folder = Path.Combine(_root, "atomic");
        var batch = new QuarantineBatch
        {
            BatchId = "b",
            CreatedUtc = DateTime.UtcNow,
            Volume = "C:\\",
            Items = [],
        };

        QuarantineManifest.Write(folder, batch);

        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.Single(Directory.GetFiles(folder));

        // And the file that landed is valid JSON, not a partial write.
        using JsonDocument doc =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, QuarantineManifest.FileName)));
        Assert.Equal("b", doc.RootElement.GetProperty("BatchId").GetString());
    }

    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        private DateTimeOffset _now = new(utcNow, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

/// <summary>
/// The quarantine holds what the user already decided to remove, which makes it the most
/// attractive folder on the disk to a cleanup tool — including this one.
/// </summary>
public class QuarantineProtectionTests
{
    [Fact]
    public void TheQuarantineItselfIsProtectedOnEveryVolume()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            string quarantine = Path.Combine(drive.Name, ProtectedPaths.QuarantineFolderName);

            ProtectionVerdict verdict = ProtectedPaths.Check(quarantine);
            Assert.Equal(ProtectionReason.Quarantine, verdict.Reason);

            // And everything inside it, or a delete would empty the undo one batch at a time.
            Assert.True(ProtectedPaths.IsProtected(Path.Combine(quarantine, "2026-08-21T00-00-00Z-aaaa")));
            Assert.True(ProtectedPaths.IsProtected(Path.Combine(quarantine, "2026-08-21T00-00-00Z-aaaa", "00001.bin")));
        }
    }

    [Fact]
    public void TheFolderNameHasExactlyOneDefinition()
    {
        // ProtectedPaths must refuse the same folder the service writes into. Two copies of
        // a name that has to agree is how AppInfo came to read 0.3.2 while the assembly
        // metadata read 0.3.1.
        Assert.EndsWith(ProtectedPaths.QuarantineFolderName,
                        QuarantineManifest.RootFor("C:\\"), StringComparison.Ordinal);
    }
}
