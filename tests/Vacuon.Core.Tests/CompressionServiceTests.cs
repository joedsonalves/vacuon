using Vacuon.Core.Actions;
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
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
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
