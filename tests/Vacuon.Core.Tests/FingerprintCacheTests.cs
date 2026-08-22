using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M8 — the cache that makes the picture and video finders usable more than once.
/// <para>
/// The invariant worth guarding is not that it remembers. It is that it stops remembering the
/// moment the file underneath changes: a fingerprint served for a file that is no longer the
/// one it was computed from puts unrelated things in the same group, and nothing on screen
/// would explain why.
/// </para>
/// </summary>
public class FingerprintCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vacuon-fp-{Guid.NewGuid():N}.tsv");

    private static readonly DateTime When = new(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WhatWasStoredComesBack()
    {
        var cache = new FingerprintCache(_path);
        cache.Put(@"C:\pictures\a.jpg", 1_000, When, [0xDEADBEEFCAFEF00D]);

        ulong[]? hashes = cache.Get(@"C:\pictures\a.jpg", 1_000, When, out _);

        Assert.Equal(0xDEADBEEFCAFEF00D, Assert.Single(hashes!));
    }

    [Fact]
    public void AFileThatChangedSizeIsNotServedFromTheCache()
    {
        var cache = new FingerprintCache(_path);
        cache.Put(@"C:\pictures\a.jpg", 1_000, When, [1]);

        Assert.Null(cache.Get(@"C:\pictures\a.jpg", 2_000, When, out _));
    }

    [Fact]
    public void AFileTouchedSinceIsNotServedFromTheCache()
    {
        var cache = new FingerprintCache(_path);
        cache.Put(@"C:\pictures\a.jpg", 1_000, When, [1]);

        Assert.Null(cache.Get(@"C:\pictures\a.jpg", 1_000, When.AddSeconds(1), out _));
    }

    [Fact]
    public void SomethingNeverSeenIsAMiss()
    {
        Assert.Null(new FingerprintCache(_path).Get(@"C:\nowhere.jpg", 1, When, out _));
    }

    [Fact]
    public void ItSurvivesBeingWrittenAndReadBack()
    {
        var first = new FingerprintCache(_path);
        first.Put(@"C:\v\clip.mp4", 5_000, When, [1, 2, 3, 4, 5], TimeSpan.FromSeconds(120));
        first.Save();

        var second = new FingerprintCache(_path);
        ulong[]? hashes = second.Get(@"C:\v\clip.mp4", 5_000, When, out TimeSpan duration);

        Assert.Equal<ulong[]>([1, 2, 3, 4, 5], hashes!);
        Assert.Equal(TimeSpan.FromSeconds(120), duration);
    }

    [Fact]
    public void PathsAreMatchedWithoutRegardToCase()
    {
        var cache = new FingerprintCache(_path);
        cache.Put(@"C:\Pictures\A.JPG", 10, When, [7]);

        Assert.NotNull(cache.Get(@"c:\pictures\a.jpg", 10, When, out _));
    }

    [Fact]
    public void AFileFromAnOlderFormatIsDiscardedRatherThanMisread()
    {
        // Guessing at a line whose meaning changed would attribute fingerprints to the wrong
        // files, which shows up as groups that make no sense and no way to tell why. One slow
        // run is the cheaper mistake.
        File.WriteAllText(_path, "vacuon-fingerprints\t1\nC:\\a.jpg\t1\t2\tFFFF\n");

        Assert.Equal(0, new FingerprintCache(_path).Count);
    }

    [Fact]
    public void AMangledLineCostsThatLineAndNothingElse()
    {
        var cache = new FingerprintCache(_path);
        cache.Put(@"C:\a.jpg", 1, When, [1]);
        cache.Put(@"C:\b.jpg", 2, When, [2]);
        cache.Save();

        File.AppendAllText(_path, "this is not an entry\n");

        Assert.Equal(2, new FingerprintCache(_path).Count);
    }

    [Fact]
    public void NothingIsWrittenWhenNothingChanged()
    {
        var cache = new FingerprintCache(_path);
        cache.Save();

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AMissingFileIsAnEmptyCacheNotAFailure()
    {
        Assert.Equal(0, new FingerprintCache(_path).Count);
    }

    [Fact]
    public void WhatThisRunLookedAtSurvivesEviction()
    {
        // Over capacity, the entries worth keeping are the files still being scanned — not
        // whichever ones happen to enumerate first.
        var seed = new FingerprintCache(_path);

        for (int i = 0; i < FingerprintCache.Capacity + 10; i++)
            seed.Put($@"C:\old\{i}.jpg", i, When, [(ulong)i]);

        seed.Save();

        var run = new FingerprintCache(_path);
        run.Get(@"C:\old\0.jpg", 0, When, out _);       // touched by this run
        run.Put(@"C:\new\fresh.jpg", 99, When, [99]);   // added by this run
        run.Save();

        var reloaded = new FingerprintCache(_path);

        Assert.True(reloaded.Count <= FingerprintCache.Capacity);
        Assert.NotNull(reloaded.Get(@"C:\old\0.jpg", 0, When, out _));
        Assert.NotNull(reloaded.Get(@"C:\new\fresh.jpg", 99, When, out _));
    }
}
