using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Whole folders that are identical, rather than the four thousand files inside them (F4.8).
/// </summary>
public class DuplicateFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-dupfolder-tests-" + Guid.NewGuid().ToString("N"));

    public DuplicateFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private void Write(string relative, int bytes, byte seed)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var data = new byte[bytes];
        for (int i = 0; i < bytes; i++) data[i] = (byte)(seed + (i % 251));
        File.WriteAllBytes(path, data);
    }

    /// <summary>An index over the real folders under the temp root.</summary>
    private VolumeIndex Index()
    {
        var names = new NameBlob(4096);
        string[] files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        string[] dirs = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories);

        var entries = new FileEntry[files.Length + dirs.Length + 1];
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [_root] = 0,
        };

        entries[0] = new FileEntry
        {
            RecordNumber = 0,
            ParentIndex = 0,
            NameOffset = names.Append(_root),
            NameLength = (ushort)_root.Length,
            Flags = EntryFlags.Directory,
            HardLinkCount = 1,
        };

        int at = 1;

        foreach (string dir in dirs.OrderBy(d => d.Length))
        {
            string name = Path.GetFileName(dir);
            entries[at] = new FileEntry
            {
                RecordNumber = (uint)at,
                ParentIndex = (uint)indexOf[Path.GetDirectoryName(dir)!],
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = EntryFlags.Directory,
                HardLinkCount = 1,
                LastWriteUtc = Directory.GetLastWriteTimeUtc(dir).ToFileTimeUtc(),
            };

            indexOf[dir] = at++;
        }

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            var info = new FileInfo(file);

            entries[at] = new FileEntry
            {
                RecordNumber = (uint)at,
                ParentIndex = (uint)indexOf[Path.GetDirectoryName(file)!],
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                LogicalSize = info.Length,
                AllocatedSize = info.Length,
                HardLinkCount = 1,
                LastWriteUtc = info.LastWriteTimeUtc.ToFileTimeUtc(),
            };

            at++;
        }

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1_000_000_000, 500_000_000, 4096, true);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Win32Walk);
    }

    [Fact]
    public void TwoIdenticalTreesAreOneGroup()
    {
        Write(@"backup-a\video.mp4", 700_000, 1);
        Write(@"backup-a\notas\leia.txt", 400_000, 2);
        Write(@"backup-b\video.mp4", 700_000, 1);
        Write(@"backup-b\notas\leia.txt", 400_000, 2);

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        DuplicateFolderGroup group = Assert.Single(report.Groups);
        Assert.Equal(2, group.CopyCount);
        Assert.Single(group.Redundant);
        Assert.Equal(1_100_000, group.Bytes);
        Assert.Equal(1_100_000, group.RecoverableBytes);
    }

    [Fact]
    public void OneExtraFileAndItIsNotADuplicate()
    {
        // ⚠️ Exactly the point: a folder holding one more file is not a duplicate of the
        // other. It contains the other, which is a different question.
        Write(@"a\um.bin", 600_000, 3);
        Write(@"a\dois.bin", 600_000, 4);
        Write(@"b\um.bin", 600_000, 3);
        Write(@"b\dois.bin", 600_000, 4);
        Write(@"b\tres.bin", 10_000, 5);

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        Assert.Empty(report.Groups);
    }

    [Fact]
    public void TheSameBytesUnderDifferentNamesIsNotTheSameFolder()
    {
        // ⚠️ Unlike the file search, the name matters here. A program opening config.json in
        // one of these would find nothing in the other, so they are not the same tree.
        Write(@"x\config.json", 800_000, 7);
        Write(@"y\settings.json", 800_000, 7);

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        Assert.Empty(report.Groups);
    }

    [Fact]
    public void SameNamesWithDifferentContentIsNotTheSameFolderEither()
    {
        Write(@"p\dados.bin", 700_000, 9);
        Write(@"q\dados.bin", 700_000, 11);

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        Assert.Empty(report.Groups);
    }

    [Fact]
    public void OnlyTheTopmostPairIsReported()
    {
        // Two identical trees have identical subtrees all the way down. Reporting every
        // level says the same thing four times, and the least useful phrasing is the one
        // somebody scrolls to first.
        Write(@"raiz-a\meio\fundo\arquivo.bin", 900_000, 13);
        Write(@"raiz-b\meio\fundo\arquivo.bin", 900_000, 13);

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        DuplicateFolderGroup group = Assert.Single(report.Groups);
        Assert.Contains("raiz-", group.Keeper.Path);
        Assert.DoesNotContain("fundo", group.Keeper.Path);
    }

    [Fact]
    public void AnEmptySubfolderCountsAsADifference()
    {
        // Nothing above would have noticed: the file counts and the byte totals match.
        Write(@"c1\arquivo.bin", 800_000, 17);
        Write(@"c2\arquivo.bin", 800_000, 17);
        Directory.CreateDirectory(Path.Combine(_root, "c2", "vazia"));

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        Assert.Empty(report.Groups);
    }

    [Fact]
    public void TheKeeperIsNeverInTheRedundantList()
    {
        Write(@"k1\a.bin", 500_000, 19);
        Write(@"k2\a.bin", 500_000, 19);
        Write(@"k3\a.bin", 500_000, 19);

        DuplicateFolderGroup group = Assert.Single(
            DuplicateFolderFinder.Find(Index(), minimumBytes: 1000).Groups);

        Assert.Equal(3, group.CopyCount);
        Assert.Equal(2, group.Redundant.Count);
        Assert.DoesNotContain(group.Keeper, group.Redundant);
        Assert.Equal(1_000_000, group.RecoverableBytes);
    }

    [Fact]
    public void FoldersOfDifferentShapesAreRuledOutWithoutReadingAnything()
    {
        // Stage 1 is free: two folders can only be identical if they hold the same number of
        // files and the same number of bytes, and both come from the index.
        Write(@"g1\um.bin", 900_000, 23);
        Write(@"g2\um.bin", 900_001, 23);

        DuplicateFolderReport report = DuplicateFolderFinder.Find(Index(), minimumBytes: 1000);

        Assert.Empty(report.Groups);
        Assert.Equal(0, report.FoldersHashed);
        Assert.Equal(0, report.BytesRead);
    }
}
