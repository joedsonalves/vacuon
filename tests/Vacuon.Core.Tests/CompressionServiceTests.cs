using Vacuon.Core.Actions;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Vacuon.Native.Interop;
using Vacuon.Native.Ntfs;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Turning NTFS compression on for things already on disk (PRD F7.11).
/// <para>
/// Against real files on the real volume: what is being tested is what the file system does,
/// and a fake would be testing the fake.
/// </para>
/// </summary>
public class CompressionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-comp-tests-" + Guid.NewGuid().ToString("N"));

    public CompressionServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // Links first, each by itself: the recursive delete reports a junction as an error
            // after removing it, and leaves the folder.
            if (Directory.Exists(_root))
            {
                foreach (string link in Links.FoldersBelow(_root)) Links.Remove(link);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>Text that compresses, which is the case this feature exists for.</summary>
    private string WriteLog(string name, int lines)
    {
        string path = Path.Combine(_root, name);
        using var writer = new StreamWriter(path);

        for (int i = 0; i < lines; i++)
            writer.WriteLine($"2026-08-31 18:00:00 INFO  request {i} handled in 12 ms, status 200, path /api/items");

        return path;
    }

    [Fact]
    public void ALogFileGivesBackClustersAndKeepsItsLength()
    {
        string path = WriteLog("app.log", 20_000);
        long length = new FileInfo(path).Length;

        CompressResult result = CompressionService.Compress(path);

        Assert.Equal(CompressOutcome.Compressed, result.Outcome);
        Assert.True(CompressionService.IsCompressed(path));

        // The gain is measured from the clusters, before and after — never from the
        // catalogue's assumed ratio, which is a number off somebody else's disk.
        Assert.True(result.Freed > 0, $"nao liberou nada: {result.Before} -> {result.After}");
        Assert.True(result.After < result.Before);

        // And the file is still the same file, at the same length, still readable.
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Contains("request 19999", File.ReadAllText(path));
    }

    [Fact]
    public void UndoingItPutsTheClustersBack()
    {
        string path = WriteLog("undo.log", 20_000);

        CompressResult compressed = CompressionService.Compress(path);
        CompressResult back = CompressionService.Decompress(path);

        Assert.Equal(CompressOutcome.Decompressed, back.Outcome);
        Assert.False(CompressionService.IsCompressed(path));
        Assert.True(back.After >= compressed.After);
    }

    [Fact]
    public void AskingForWhatIsAlreadyTrueChangesNothing()
    {
        string path = WriteLog("twice.log", 5_000);

        CompressionService.Compress(path);
        CompressResult again = CompressionService.Compress(path);

        Assert.Equal(CompressOutcome.Unchanged, again.Outcome);
        Assert.Equal(0, again.Freed);
    }

    [Fact]
    public void AFolderCarriesItsFilesWithIt()
    {
        // ⚠️ The attribute on a folder only governs what is written into it later. A folder
        // set and left would report zero saved and be telling the truth, having done nothing
        // to the gigabytes already inside.
        string folder = Path.Combine(_root, "logs");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "old"));

        for (int i = 0; i < 3; i++) WriteLog(Path.Combine("logs", $"a{i}.log"), 8_000);
        WriteLog(Path.Combine("logs", "old", "deep.log"), 8_000);

        CompressResult result = CompressionService.Compress(folder);

        Assert.Equal(CompressOutcome.Compressed, result.Outcome);
        Assert.True(result.Freed > 0);

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            Assert.True(CompressionService.IsCompressed(file), file);
    }

    [Fact]
    public void AFolderWithAJunctionInside_LeavesTheFolderBehindItAlone()
    {
        // Measured before this was written: the walk went through the junction, compressed
        // the folder on the far side, and counted what that gave back as this folder's saving.
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string theirs = WriteLog(Path.Combine("outside", "theirs.log"), 20_000);

        string folder = Path.Combine(_root, "mine");
        Directory.CreateDirectory(folder);
        string mine = WriteLog(Path.Combine("mine", "mine.log"), 20_000);
        Assert.True(Junction.Create(Path.Combine(folder, "door"), outside));

        long mineBefore = new FileInfo(mine).Length;
        CompressResult result = CompressionService.Compress(folder);

        Assert.Equal(CompressOutcome.Compressed, result.Outcome);
        Assert.True(CompressionService.IsCompressed(mine));
        Assert.False(CompressionService.IsCompressed(theirs));
        Assert.False(CompressionService.IsCompressed(outside));

        // The saving is this folder's own: no more than its one file ever held.
        Assert.True(result.Before <= mineBefore + 4096, $"counted {result.Before} bytes for a folder holding {mineBefore}");
    }

    [Fact]
    public void AJunction_IsRefused_AndTheFolderItStandsForIsLeftAlone()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string theirs = WriteLog(Path.Combine("outside", "theirs.log"), 20_000);
        string door = Path.Combine(_root, "door");
        Assert.True(Junction.Create(door, outside));

        CompressResult result = CompressionService.Compress(door);

        Assert.Equal(CompressOutcome.Blocked, result.Outcome);
        Assert.Contains(outside, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(CompressionService.IsCompressed(theirs));
        Assert.False(CompressionService.IsCompressed(outside));
    }

    /// <summary>An index of one file under the test folder, sized the way a scan sized it before compression.</summary>
    private VolumeIndex IndexOf(string file)
    {
        var names = new NameBlob(512);
        var entries = new FileEntry[4];
        string name = Path.GetFileName(file);
        long length = new FileInfo(file).Length;

        entries[0] = new FileEntry
        {
            RecordNumber = 0,
            ParentIndex = 0,
            NameOffset = names.Append(_root),
            NameLength = (ushort)_root.Length,
            Flags = EntryFlags.Directory,
            HardLinkCount = 1,
        };
        entries[1] = new FileEntry
        {
            RecordNumber = 1,
            ParentIndex = 0,
            NameOffset = names.Append(name),
            NameLength = (ushort)name.Length,
            LogicalSize = length,
            AllocatedSize = (length + 4095) / 4096 * 4096,
            HardLinkCount = 1,
        };

        var volume = new VolumeInfo('C', "Test", "NTFS", 1_000_000_000, 500_000_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Win32Walk);
    }

    [Fact]
    public void AfterCompressing_TheIndexHoldsTheClustersTheFileHoldsNow()
    {
        // Compressing changes no length, so nothing in the index moved: the list showed the
        // file, and its folder, at the size it had before, beside "1 item compressed · freed".
        string path = WriteLog("measured.log", 20_000);
        VolumeIndex index = IndexOf(path);
        long before = index.TotalBytesOnDisk;

        List<(int Entry, string Path)> files = OnDiskRemeasure.FilesUnder(index, [0]);
        Assert.Equal(CompressOutcome.Compressed, CompressionService.Compress(path).Outcome);

        Assert.True(OnDiskRemeasure.Apply(index, OnDiskRemeasure.Measure(files, 4096)));

        long held = Kernel32.CompressedSizeOf(path);
        Assert.Equal(held, index.Entries[1].AllocatedSize);
        Assert.True((index.Entries[1].Flags & EntryFlags.Compressed) != 0);
        Assert.Equal(held, index.TotalBytesOnDisk);
        Assert.True(index.TotalBytesOnDisk < before, $"{index.TotalBytesOnDisk} is not below {before}");
    }

    [Fact]
    public void AfterAReopen_TheJournalsCompressionRecordReadsTheClustersAgain()
    {
        // The journal says a file was compressed and nothing about how big it is now. The
        // replay used to leave the size alone — and when it did read one, it rounded the
        // length, which for a compressed file is the size it had before.
        string path = WriteLog("replayed.log", 20_000);
        VolumeIndex index = IndexOf(path);
        Assert.Equal(CompressOutcome.Compressed, CompressionService.Compress(path).Outcome);

        var applier = new DeltaApplier(index);
        var record = new UsnRecord
        {
            IsValid = true,
            FileReferenceNumber = 1,
            ParentFileReferenceNumber = 0,
            Reason = UsnReason.CompressionChange,
            Attributes = NtfsFileAttributes.Compressed,
        };
        applier.Apply(ref record);
        applier.Finish();

        Assert.Equal(Kernel32.CompressedSizeOf(path), index.Entries[1].AllocatedSize);
        Assert.True(index.Entries[1].AllocatedSize < new FileInfo(path).Length);
    }

    [Fact]
    public void APathUnderWindowsIsRefused()
    {
        CompressResult result = CompressionService.Compress(@"C:\Windows\explorer.exe");

        Assert.Equal(CompressOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public void SomethingThatIsNotThereIsNotAFailure()
    {
        Assert.Equal(CompressOutcome.NotFound,
                     CompressionService.Compress(Path.Combine(_root, "nao-existe.log")).Outcome);
    }

    [Fact]
    public void AlreadyCompressedDataIsAllowedToGainNothing()
    {
        // Random bytes do not compress. The result may even be slightly larger, and that is
        // reported as a negative saving rather than rounded up to a happy zero.
        string path = Path.Combine(_root, "random.bin");
        var bytes = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        CompressResult result = CompressionService.Compress(path);

        Assert.Equal(CompressOutcome.Compressed, result.Outcome);
        Assert.True(result.Freed <= 0 || result.Freed < bytes.Length / 10,
                    $"dados aleatorios nao deveriam encolher: liberou {result.Freed}");
    }
}
