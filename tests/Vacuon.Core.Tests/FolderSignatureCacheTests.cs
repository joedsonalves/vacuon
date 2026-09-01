using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Remembering the content signature of a folder between runs.
/// <para>
/// Every test here points the cache at its own file. A test that read the cache in
/// <c>%LocalAppData%</c> would be testing the machine it runs on.
/// </para>
/// </summary>
public class FolderSignatureCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-foldercache-tests-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_root, "cache.tsv");

    public FolderSignatureCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Tree(string name, params (string Name, int Bytes)[] files)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        foreach ((string file, int bytes) in files)
        {
            var data = new byte[bytes];
            for (int i = 0; i < bytes; i++) data[i] = (byte)(i % 251);
            File.WriteAllBytes(Path.Combine(folder, file), data);
        }

        return folder;
    }

    [Fact]
    public void WhatWasStoredComesBack()
    {
        string folder = Tree("a", ("um.bin", 4096));

        var cache = new FolderSignatureCache(CachePath);
        string stamp = FolderSignatureCache.StampOf(folder, out _)!;

        Assert.Null(cache.Get(folder, stamp));

        cache.Put(folder, stamp, "ABCDEF");

        Assert.Equal("ABCDEF", cache.Get(folder, stamp));
    }

    [Fact]
    public void ItSurvivesBeingWrittenAndReadBack()
    {
        string folder = Tree("b", ("um.bin", 4096));
        string stamp = FolderSignatureCache.StampOf(folder, out _)!;

        var first = new FolderSignatureCache(CachePath);
        first.Put(folder, stamp, "0123456789ABCDEF");
        first.Save();

        var second = new FolderSignatureCache(CachePath);

        Assert.Equal("0123456789ABCDEF", second.Get(folder, stamp));
    }

    [Fact]
    public void AFileAddedInsideInvalidatesTheFolder()
    {
        // ⚠️ The whole safety of the cache. An entry that outlived a change to the tree
        // would let the search assert two folders are identical when one of them is not.
        string folder = Tree("c", ("um.bin", 4096));

        var cache = new FolderSignatureCache(CachePath);
        string before = FolderSignatureCache.StampOf(folder, out _)!;
        cache.Put(folder, before, "ANTES");

        File.WriteAllBytes(Path.Combine(folder, "dois.bin"), new byte[2048]);

        string after = FolderSignatureCache.StampOf(folder, out _)!;

        Assert.NotEqual(before, after);
        Assert.Null(cache.Get(folder, after));
    }

    [Fact]
    public void AFileRewrittenInsideInvalidatesTheFolder()
    {
        string folder = Tree("d", ("um.bin", 4096));
        string file = Path.Combine(folder, "um.bin");

        string before = FolderSignatureCache.StampOf(folder, out _)!;

        // Same length on purpose: what has to catch this is the write time, not the size.
        var data = new byte[4096];
        for (int i = 0; i < data.Length; i++) data[i] = 0xAB;

        File.WriteAllBytes(file, data);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        string after = FolderSignatureCache.StampOf(folder, out _)!;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AnEmptySubfolderIsPartOfTheStamp()
    {
        string folder = Tree("e", ("um.bin", 4096));
        string before = FolderSignatureCache.StampOf(folder, out _)!;

        Directory.CreateDirectory(Path.Combine(folder, "vazia"));

        Assert.NotEqual(before, FolderSignatureCache.StampOf(folder, out _)!);
    }

    [Fact]
    public void TwoCopiesOfTheSameTreeHaveDifferentStamps()
    {
        // Stamps are not signatures and must never be mistaken for them: two identical
        // folders copied at different moments carry different times, so their stamps
        // differ while their content is the same. The stamp answers "is this still that
        // folder", never "are these two the same folder".
        string one = Tree("f1", ("um.bin", 4096));
        string two = Tree("f2", ("um.bin", 4096));

        File.SetLastWriteTimeUtc(Path.Combine(one, "um.bin"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(two, "um.bin"), new DateTime(2024, 6, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(FolderSignatureCache.StampOf(one, out _)!,
                        FolderSignatureCache.StampOf(two, out _)!);

        Assert.Equal(DuplicateFolderFinder.SignatureOf(one, CancellationToken.None),
                     DuplicateFolderFinder.SignatureOf(two, CancellationToken.None));
    }

    [Fact]
    public void TheStampWalkListsTheFilesTheReadWouldOpen()
    {
        // A cache hit costs one walk and a miss costs the same walk plus the reading, so
        // the walk has to hand over the list rather than have it gathered twice.
        string folder = Tree("g", ("um.bin", 1024), ("dois.bin", 2048));
        Directory.CreateDirectory(Path.Combine(folder, "dentro"));
        File.WriteAllBytes(Path.Combine(folder, "dentro", "tres.bin"), new byte[512]);

        FolderSignatureCache.StampOf(folder, out List<string>? files);

        Assert.NotNull(files);
        Assert.Equal(3, files!.Count);
    }

    [Fact]
    public void AFolderThatIsGoneHasNoStampAndNoAnswer()
    {
        Assert.Null(FolderSignatureCache.StampOf(Path.Combine(_root, "nao-existe"), out List<string>? files));
        Assert.Null(files);
    }

    [Fact]
    public void AFileFromAnotherVersionIsThrownAway()
    {
        // Parsing it hopefully would attribute signatures to the wrong folders, and there
        // would be no way to tell from the result that it had happened.
        File.WriteAllText(CachePath, "vacuon-folder-signatures\t99\nC:\\x\tSTAMP\tSIG\n");

        Assert.Equal(0, new FolderSignatureCache(CachePath).Count);
    }

    [Fact]
    public void TheSearchReadsNothingTheSecondTime()
    {
        string first = Tree("h1", ("um.bin", 300_000), ("dois.bin", 900_000));
        string second = Tree("h2", ("um.bin", 300_000), ("dois.bin", 900_000));

        var cache = new FolderSignatureCache(CachePath);

        string? a = DuplicateFolderFinder.SignatureOf(first, cache, null, CancellationToken.None);
        string? b = DuplicateFolderFinder.SignatureOf(second, cache, null, CancellationToken.None);

        Assert.NotNull(a);
        Assert.Equal(a, b);

        // Second time round the answer comes back without the folders being read, and it is
        // the same answer.
        Assert.Equal(a, cache.Get(first, FolderSignatureCache.StampOf(first, out _)!));
        Assert.Equal(b, cache.Get(second, FolderSignatureCache.StampOf(second, out _)!));
    }
}
