using System.Text;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Holding an edit that a locked file refused, and writing it when the lock goes.
/// </summary>
public class PendingSavesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-pending-tests-" + Guid.NewGuid().ToString("N"));

    public PendingSavesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AFreeFileCanBeWritten()
    {
        Assert.True(FileAvailability.CanWrite(Write("livre.txt", "oi")));
    }

    [Fact]
    public void AFileHeldWithoutSharingCannotBeWritten()
    {
        string path = Write("preso.txt", "oi");

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(FileAvailability.CanWrite(path));
    }

    [Fact]
    public void AFileSharedForReadingOnlyCannotBeWritten()
    {
        // ⚠️ This is why the check is an actual open rather than a look at who holds the file.
        // Something has it open and is sharing it — for reading. A write still fails, and the
        // holder list would have said the same thing in both cases.
        string path = Write("compartilhado.txt", "oi");

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(FileAvailability.CanWrite(path));
    }

    [Fact]
    public void AMissingFileCannotBeWritten()
    {
        Assert.False(FileAvailability.CanWrite(Path.Combine(_root, "nao-existe.txt")));
    }

    [Fact]
    public void AQueuedEditIsWrittenOnceTheLockGoes()
    {
        string path = Write("alvo.txt", "antes");

        var saves = new PendingSaves();
        saves.Queue(path, Encoding.UTF8.GetBytes("depois"));

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Nada de ler o arquivo aqui: o cadeado deste teste é FileShare.None, e ele
            // barra a própria conferência tanto quanto barraria a escrita.
            Assert.Equal(0, saves.TryAll());
            Assert.Equal(1, saves.Count);
        }

        Assert.Equal(1, saves.TryAll());
        Assert.Equal(0, saves.Count);
        Assert.Equal("depois", File.ReadAllText(path));
    }

    [Fact]
    public void SettledSaysWhatHappened()
    {
        string path = Write("aviso.txt", "antes");

        var saves = new PendingSaves();
        PendingSaveResult? seen = null;

        saves.Settled += (_, e) => seen = e;
        saves.Queue(path, Encoding.UTF8.GetBytes("depois"));
        saves.TryAll();

        Assert.NotNull(seen);
        Assert.Equal(SaveOutcome.Saved, seen!.Outcome);
        Assert.Equal(path, seen.Save.Path);
    }

    [Fact]
    public void QueueingTheSamePathTwiceKeepsTheNewerEdit()
    {
        // The person edited again. The older bytes are a version they already moved past.
        string path = Write("duas.txt", "antes");

        var saves = new PendingSaves();
        saves.Queue(path, Encoding.UTF8.GetBytes("primeira"));
        saves.Queue(path, Encoding.UTF8.GetBytes("segunda"));

        Assert.Equal(1, saves.Count);

        saves.TryAll();

        Assert.Equal("segunda", File.ReadAllText(path));
    }

    [Fact]
    public void ARefusalThatIsNotTheLockIsFinal()
    {
        // ⚠️ Holding on to a protected path would mean retrying for ever against something
        // that is never going to change.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var saves = new PendingSaves();
        saves.Queue(Path.Combine(windows, "vacuon-nunca.txt"), [1, 2, 3]);

        saves.TryAll();

        Assert.Equal(0, saves.Count);
    }

    [Fact]
    public void AFileThatWentAwayIsNotBroughtBack()
    {
        // ⚠️ Writing the bytes now would resurrect something somebody deleted, under a name
        // they expected to be gone. Losing the edit is the lesser harm, and it is reported.
        string path = Write("sumiu.txt", "antes");

        var saves = new PendingSaves();
        saves.Queue(path, Encoding.UTF8.GetBytes("depois"));

        File.Delete(path);
        saves.TryAll();

        Assert.Equal(0, saves.Count);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ForgettingDropsIt()
    {
        string path = Write("desistir.txt", "antes");

        var saves = new PendingSaves();
        saves.Queue(path, Encoding.UTF8.GetBytes("depois"));

        Assert.True(saves.Forget(path));
        Assert.Equal(0, saves.Count);

        saves.TryAll();
        Assert.Equal("antes", File.ReadAllText(path));
    }

    [Fact]
    public async Task WatchingStopsWhenEverythingIsWritten()
    {
        string path = Write("esperando.txt", "antes");

        var saves = new PendingSaves();
        saves.Queue(path, Encoding.UTF8.GetBytes("depois"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await saves.WatchAsync(TimeSpan.FromMilliseconds(50), cts.Token);

        Assert.Equal(0, saves.Count);
        Assert.Equal("depois", File.ReadAllText(path));
    }

    [Fact]
    public async Task WatchingGivesUpWhenCalledOff()
    {
        string path = Write("cancelado.txt", "antes");

        var saves = new PendingSaves();
        saves.Queue(path, Encoding.UTF8.GetBytes("depois"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await saves.WatchAsync(TimeSpan.FromMilliseconds(50), cts.Token);
            Assert.Equal(1, saves.Count);
        }

        Assert.Equal("antes", File.ReadAllText(path));
    }
}
