using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M6. The requirement that shapes everything here is "zero false positives":
/// telling someone two files are the same when they are not is how a duplicate finder
/// deletes a person's work. So the sampled stages are only ever allowed to rule copies
/// OUT, and the verdict is always the full-file hash.
/// </summary>
public class DuplicateFinderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vacuon-dup-tests", Guid.NewGuid().ToString("N"));

    public DuplicateFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    // ---- helpers -------------------------------------------------------------

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Pattern(int size, byte seed)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(seed + (i % 251));
        return data;
    }

    /// <summary>
    /// Builds an index over the real files under <see cref="_root"/>.
    /// <para>
    /// Uses <see cref="ScanStrategy.Win32Walk"/> because in that mode the root entry carries the
    /// full path of the scope, which is what makes the synthetic index resolve to real
    /// files on disk — the finder has to actually read them.
    /// </para>
    /// </summary>
    private VolumeIndex Index(IReadOnlyDictionary<string, int>? hardLinkCounts = null)
    {
        var names = new NameBlob(1024);
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var entries = new FileEntry[files.Length + 1];

        entries[0] = new FileEntry
        {
            RecordNumber = 0,
            ParentIndex = 0,
            NameOffset = names.Append(_root),
            NameLength = (ushort)_root.Length,
            Flags = EntryFlags.Directory,
            HardLinkCount = 1,
        };

        for (int i = 0; i < files.Length; i++)
        {
            var info = new FileInfo(files[i]);
            string name = Path.GetRelativePath(_root, files[i]);

            int count = hardLinkCounts is not null
                     && hardLinkCounts.TryGetValue(Path.GetFileName(files[i]), out int c) ? c : 1;

            entries[i + 1] = new FileEntry
            {
                RecordNumber = (uint)(i + 1),
                ParentIndex = 0,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = EntryFlags.None,
                LogicalSize = info.Length,
                AllocatedSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc.ToFileTimeUtc(),
                HardLinkCount = (ushort)count,
            };
        }

        var volume = new VolumeInfo('C', "Test", "NTFS", 1_000_000_000, 500_000_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Win32Walk);
    }

    private static DuplicateOptions Small(DuplicateOptions? o = null) =>
        (o ?? new DuplicateOptions()) with { MinimumBytes = 1 };

    // ---- the promise ---------------------------------------------------------


    [Fact]
    public void TheSameFilesGiveTheSameGroups_HoweverTheThreadsFinish()
    {
        // The files are hashed several at a time now. A result that depended on which
        // thread got there first would be a result nobody could check twice, so the hashes
        // land at the position their file had and the grouping is built by walking that
        // array in order — which is what this asks for, twenty times over.
        for (int i = 0; i < 12; i++)
        {
            byte[] shared = Pattern(60_000, (byte)(i + 1));
            Write($"pasta-a/copia-{i}.bin", shared);
            Write($"pasta-b/outro-nome-{i}.bin", shared);
            Write($"pasta-c/terceira-{i}.bin", shared);

            // And one file per size that only looks like the others.
            Write($"pasta-d/sozinho-{i}.bin", Pattern(60_000, (byte)(i + 100)));
        }

        VolumeIndex index = Index();
        string first = Signature(new DuplicateFinder().Find(index, Small()));

        for (int run = 0; run < 20; run++)
            Assert.Equal(first, Signature(new DuplicateFinder().Find(index, Small())));

        // Twelve groups of three, and the lookalikes grouped with nobody.
        Assert.Equal(12, first.Split('|').Length - 1);
    }

    [Fact]
    public void DifferentNamesWithIdenticalContentAreTheSameFile()
    {
        // ⚠️ Reported as a suspicion: "different names, same size — that is a coincidence,
        // not a duplicate". The size never decides anything here; the full hash does. Two
        // names over one set of bytes is exactly what a duplicate is, and refusing to say so
        // would hide the real ones — an installer kept twice under generated names is the
        // most common large duplicate on a Windows disk.
        byte[] content = Pattern(120_000, 5);
        Write("instalador-antigo.exe", content);
        Write("setup_12.5.7_full.exe", content);

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        DuplicateGroup group = Assert.Single(report.Groups);
        Assert.Equal(2, group.CopyCount);
    }

    [Fact]
    public void TheSameNameWithDifferentContentIsNotTheSameFile()
    {
        // The other half of the same misunderstanding: grouping by name and size would put
        // these two together, and they share nothing but a name and a length.
        Write("um/config.json", Pattern(50_000, 11));
        Write("dois/config.json", Pattern(50_000, 22));

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        Assert.Empty(report.Groups);
    }

    /// <summary>Groups and their members, in order, as one comparable string.</summary>
    private static string Signature(DuplicateReport report)
    {
        var text = new System.Text.StringBuilder();

        foreach (DuplicateGroup group in report.Groups)
        {
            text.Append('|').Append(group.Bytes).Append(':');
            foreach (DuplicateFile file in group.Files) text.Append(Path.GetFileName(file.Path)).Append(',');
        }

        return text.ToString();
    }

    [Fact]
    public void TwoIdenticalFilesAreOneGroup()
    {
        byte[] content = Pattern(40_000, 7);
        Write("a.bin", content);
        Write("b.bin", content);

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        DuplicateGroup group = Assert.Single(report.Groups);
        Assert.Equal(2, group.CopyCount);
        Assert.Single(group.Redundant);
        Assert.Equal(40_000, group.RecoverableBytes);
    }

    [Fact]
    public void SameSizeDifferentContentIsNotADuplicate()
    {
        Write("a.bin", Pattern(40_000, 1));
        Write("b.bin", Pattern(40_000, 2));

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        Assert.Empty(report.Groups);
        Assert.Equal(0, report.RecoverableBytes);
    }

    [Fact]
    public void SameSizeSameHeadSameTailButDifferentMiddleIsNotADuplicate()
    {
        // The false positive this milestone exists to not produce. Two renders out of the
        // same encoder share a size, a header and a trailer; only the full hash can tell
        // them apart, which is why the sampled stages are never allowed to decide.
        byte[] a = Pattern(200_000, 3);
        byte[] b = (byte[])a.Clone();
        b[100_000] ^= 0xFF;

        Write("a.bin", a);
        Write("b.bin", b);

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        Assert.Empty(report.Groups);

        // And it did have to read both files whole to find that out.
        Assert.Equal(2, report.FilesHashed);
    }

    [Fact]
    public void ThreeCopiesLeaveTwoRedundant()
    {
        byte[] content = Pattern(30_000, 9);
        Write("a.bin", content);
        Write("b.bin", content);
        Write("c.bin", content);

        DuplicateGroup group = Assert.Single(new DuplicateFinder().Find(Index(), Small()).Groups);

        Assert.Equal(3, group.CopyCount);
        Assert.Equal(2, group.Redundant.Count);
        Assert.Equal(60_000, group.RecoverableBytes);
    }

    [Fact]
    public void TheKeeperIsNeverInTheRedundantList()
    {
        // F4.9, structurally rather than by convention: there is no way to obtain a list
        // from this object that contains every copy, so "delete all of them" cannot be
        // expressed, let alone clicked.
        byte[] content = Pattern(20_000, 4);
        Write("a.bin", content);
        Write("b.bin", content);
        Write("c.bin", content);

        DuplicateGroup group = Assert.Single(new DuplicateFinder().Find(Index(), Small()).Groups);

        Assert.DoesNotContain(group.Keeper, group.Redundant);
        Assert.Equal(group.CopyCount - 1, group.Redundant.Count);
    }

    // ---- stage 1 costs nothing ----------------------------------------------

    [Fact]
    public void ASizeSharedByNobodyIsRuledOutWithoutTouchingTheDisk()
    {
        // Stage 1 comes free from the index. It filters less than one might expect — on the
        // author's 2.49 M file volume, 90% of the files above the minimum still share a size
        // with something — but a size shared by nobody is settled without a single read.
        Write("a.bin", Pattern(1_000, 1));
        Write("b.bin", Pattern(2_000, 2));
        Write("c.bin", Pattern(3_000, 3));

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        Assert.Empty(report.Groups);
        Assert.Equal(0, report.BytesRead);
        Assert.Equal(0, report.FilesHashed);
    }

    [Fact]
    public void SmallFilesAreReadOnceInsteadOfThreeTimes()
    {
        // Below the sampling cutoff the head sample already covers the whole file, so the
        // sampled stages would re-read it to learn nothing.
        byte[] content = Pattern(500, 5);
        Write("a.bin", content);
        Write("b.bin", content);

        DuplicateReport report = new DuplicateFinder().Find(Index(), Small());

        Assert.Single(report.Groups);
        Assert.Equal(2, report.FilesHashed);
        Assert.Equal(1_000, report.BytesRead);
    }

    // ---- what is really recoverable ------------------------------------------

    [Fact]
    public void AHardlinkedCopyIsIdenticalButFreesNothing()
    {
        // NTFS charges hardlinked content once and releases it only when the last name
        // goes. Counting such a copy as recoverable promises space that never arrives —
        // the same accounting mistake that once hid 217 GiB in this app.
        byte[] content = Pattern(50_000, 6);
        Write("original.bin", content);
        Write("linked.bin", content);

        VolumeIndex index = Index(new Dictionary<string, int> { ["linked.bin"] = 2 });
        DuplicateReport report = new DuplicateFinder().Find(index, Small());

        DuplicateGroup group = Assert.Single(report.Groups);

        // It is still reported as a duplicate — it genuinely is one.
        Assert.Equal(2, group.CopyCount);

        // The hardlinked one is kept, so what is offered for removal frees real space.
        Assert.True(group.Keeper.IsHardLinked);
        Assert.Equal("original.bin", Path.GetFileName(Assert.Single(group.Redundant).Path));
        Assert.Equal(50_000, group.RecoverableBytes);
    }

    [Fact]
    public void WhenEveryCopyIsHardlinkedNothingIsClaimedAsRecoverable()
    {
        byte[] content = Pattern(50_000, 8);
        Write("a.bin", content);
        Write("b.bin", content);

        VolumeIndex index = Index(new Dictionary<string, int> { ["a.bin"] = 2, ["b.bin"] = 2 });
        DuplicateReport report = new DuplicateFinder().Find(index, Small());

        DuplicateGroup group = Assert.Single(report.Groups);

        Assert.Equal(0, group.RecoverableBytes);
        Assert.Equal(0, report.RecoverableBytes);
        Assert.Equal(1, report.HardLinkedCopies);
    }

    // ---- choosing what stays -------------------------------------------------

    [Fact]
    public void KeepOldestPicksTheOneWrittenFirst()
    {
        byte[] content = Pattern(20_000, 2);
        string first = Write("first.bin", content);
        string second = Write("second.bin", content);

        File.SetLastWriteTimeUtc(first, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DuplicateGroup group = Assert.Single(
            new DuplicateFinder().Find(Index(), Small()).Groups);

        Assert.Equal("first.bin", Path.GetFileName(group.Keeper.Path));
    }

    [Fact]
    public void KeepNewestPicksTheOtherOne()
    {
        byte[] content = Pattern(20_000, 2);
        string first = Write("first.bin", content);
        string second = Write("second.bin", content);

        File.SetLastWriteTimeUtc(first, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DuplicateGroup group = Assert.Single(
            new DuplicateFinder()
                .Find(Index(), Small(new DuplicateOptions { Keep = KeepPreference.Newest }))
                .Groups);

        Assert.Equal("second.bin", Path.GetFileName(group.Keeper.Path));
    }

    [Fact]
    public void KeepShallowestPrefersTheCopyNearerTheRoot()
    {
        byte[] content = Pattern(20_000, 2);
        Write(Path.Combine("deep", "deeper", "buried.bin"), content);
        Write("top.bin", content);

        DuplicateGroup group = Assert.Single(
            new DuplicateFinder()
                .Find(Index(), Small(new DuplicateOptions { Keep = KeepPreference.ShallowestPath }))
                .Groups);

        Assert.Equal("top.bin", Path.GetFileName(group.Keeper.Path));
    }

    // ---- options and edge cases ---------------------------------------------

    [Fact]
    public void FilesUnderTheMinimumAreIgnored()
    {
        byte[] content = Pattern(100, 1);
        Write("a.bin", content);
        Write("b.bin", content);

        // Default minimum is 4 KiB: thousands of identical tiny files are normal on a
        // Windows volume and chasing them costs more than the space they hold.
        Assert.Empty(new DuplicateFinder().Find(Index()).Groups);
        Assert.Single(new DuplicateFinder().Find(Index(), Small()).Groups);
    }

    [Fact]
    public void ByteForByteVerificationAgreesWithTheHash()
    {
        byte[] content = Pattern(60_000, 3);
        Write("a.bin", content);
        Write("b.bin", content);
        Write("c.bin", Pattern(60_000, 4));

        DuplicateReport report = new DuplicateFinder()
            .Find(Index(), Small(new DuplicateOptions { VerifyByteForByte = true }));

        DuplicateGroup group = Assert.Single(report.Groups);
        Assert.Equal(2, group.CopyCount);
    }

    [Fact]
    public void DirectoriesAreNeverCandidates()
    {
        Directory.CreateDirectory(Path.Combine(_root, "one"));
        Directory.CreateDirectory(Path.Combine(_root, "two"));
        Write("a.bin", Pattern(9_000, 1));

        Assert.Empty(new DuplicateFinder().Find(Index(), Small()).Groups);
    }

    [Fact]
    public void GroupsComeBackBiggestRecoverableFirst()
    {
        byte[] small = Pattern(10_000, 1);
        Write("s1.bin", small);
        Write("s2.bin", small);

        byte[] big = Pattern(90_000, 2);
        Write("b1.bin", big);
        Write("b2.bin", big);

        IReadOnlyList<DuplicateGroup> groups = new DuplicateFinder().Find(Index(), Small()).Groups;

        Assert.Equal(2, groups.Count);
        Assert.True(groups[0].RecoverableBytes > groups[1].RecoverableBytes);
        Assert.Equal(90_000, groups[0].RecoverableBytes);
    }

    [Fact]
    public void ProgressCountsTheCandidatesNotTheWholeVolume()
    {
        byte[] content = Pattern(30_000, 1);
        Write("a.bin", content);
        Write("b.bin", content);
        Write("unique.bin", Pattern(12_345, 2));

        var seen = new List<DuplicateProgress>();

        // NOT Progress<T>. That one posts through the SynchronizationContext, and a test
        // runner has none, so the callbacks land on the thread pool and arrive whenever
        // they arrive. Asserting on them right after the call passes or fails by timing —
        // which is exactly what happened: green here, red on CI, green again on the next
        // three runs without anything being fixed. A test that reports the weather is
        // worse than no test, because it teaches people to re-run until it is green.
        new DuplicateFinder().Find(Index(), Small(), new SyncProgress<DuplicateProgress>(seen.Add));

        Assert.NotEmpty(seen);
        Assert.Equal(2, seen[0].FilesTotal);

        // The last report accounts for every candidate, and only candidates: the third
        // file shares its size with nobody and never reaches a read.
        Assert.Equal(2, seen[^1].FilesDone);
        Assert.Equal(2, seen[^1].FilesTotal);
    }

    /// <summary>Invokes the handler on the calling thread, so assertions are deterministic.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
