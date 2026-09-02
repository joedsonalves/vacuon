using System.Text;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Keeping a refused edit on disk so closing the app does not throw it away.
/// <para>
/// Every test points the store at its own folder. One that wrote into
/// <c>%LocalAppData%</c> would be leaving edits on the machine that runs it.
/// </para>
/// </summary>
public class PendingSaveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-pendingstore-tests-" + Guid.NewGuid().ToString("N"));

    public PendingSaveStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private PendingSaveStore Store() => new(Path.Combine(_root, "store"));

    [Fact]
    public void WhatWasKeptComesBackAfterAReopen()
    {
        // The whole point: a different instance, as if the app had been closed and started.
        PendingSaveStore first = Store();
        first.Keep(@"C:\alvo\config.json", Encoding.UTF8.GetBytes("{ \"a\": 1 }"));

        IReadOnlyList<StoredSave> waiting = Store().Load();

        StoredSave entry = Assert.Single(waiting);
        Assert.Equal(@"C:\alvo\config.json", entry.Path);
        Assert.Equal("config.json", entry.FileName);
        Assert.Equal("{ \"a\": 1 }", Encoding.UTF8.GetString(Store().Content(entry)!));
    }

    [Fact]
    public void EditingTheSameFileAgainReplacesTheOlderEdit()
    {
        PendingSaveStore store = Store();

        store.Keep(@"C:\alvo\a.txt", Encoding.UTF8.GetBytes("primeira"));
        store.Keep(@"C:\alvo\a.txt", Encoding.UTF8.GetBytes("segunda"));

        StoredSave entry = Assert.Single(store.Load());
        Assert.Equal("segunda", Encoding.UTF8.GetString(store.Content(entry)!));
    }

    [Fact]
    public void TwoDifferentFilesBothWait()
    {
        PendingSaveStore store = Store();

        store.Keep(@"C:\alvo\a.txt", [1]);
        store.Keep(@"C:\alvo\b.txt", [2]);

        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public void AnEntryWhoseContentIsGoneIsNotOffered()
    {
        // ⚠️ Offering it would be promising to write something the app can no longer produce.
        PendingSaveStore store = Store();
        StoredSave? entry = store.Keep(@"C:\alvo\some.txt", [1, 2, 3]);

        Assert.NotNull(entry);

        foreach (string blob in Directory.GetFiles(Path.Combine(_root, "store"), "*.bin"))
            File.Delete(blob);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void ForgettingByPathDropsBothTheEntryAndTheContent()
    {
        PendingSaveStore store = Store();
        store.Keep(@"C:\alvo\a.txt", [1, 2, 3]);

        store.Forget(@"C:\alvo\a.txt");

        Assert.Empty(store.Load());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "store"), "*.bin"));
    }

    [Fact]
    public void ForgettingIsCaseInsensitiveLikeTheFileSystem()
    {
        PendingSaveStore store = Store();
        store.Keep(@"C:\Alvo\A.txt", [1]);

        store.Forget(@"c:\alvo\a.txt");

        Assert.Empty(store.Load());
    }

    [Fact]
    public void ClearingLeavesNothingBehind()
    {
        PendingSaveStore store = Store();
        store.Keep(@"C:\alvo\a.txt", [1]);
        store.Keep(@"C:\alvo\b.txt", [2]);

        store.Clear();

        Assert.Empty(store.Load());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "store"), "*.bin"));
    }

    [Fact]
    public void AnIndexFromAnotherVersionIsThrownAway()
    {
        string folder = Path.Combine(_root, "store");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.tsv"), "vacuon-pending\t99\nx\tC:\\a\t1\t2026-01-01\n");

        Assert.Empty(Store().Load());
    }

    [Fact]
    public void NothingKeptMeansNothingWaiting()
    {
        Assert.Empty(Store().Load());
    }

    [Fact]
    public void TheBytesAreExactlyWhatWasHandedIn()
    {
        // Every byte value, because this is the copy that will be written over somebody's
        // file when the wait ends.
        var content = new byte[256];
        for (int i = 0; i < 256; i++) content[i] = (byte)i;

        PendingSaveStore store = Store();
        store.Keep(@"C:\alvo\bytes.bin", content);

        Assert.Equal(content, store.Content(store.Load()[0]));
    }
}
