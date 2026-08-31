using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The reparse point that makes the old path keep working after a folder moves (F5.11/F7.10).
/// </summary>
public class JunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-junction-tests-" + Guid.NewGuid().ToString("N"));

    public JunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // ⚠️ Junctions first, and without recursing into them: deleting a junction with a
        // recursive delete follows it and takes the target's contents with it, which is the
        // classic way to lose the files this feature exists to keep.
        foreach (string dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Reverse())
        {
            if (Junction.Exists(dir))
            {
                try { Directory.Delete(dir); } catch (IOException) { }
            }
        }

        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheOldPathAnswersForTheNewOne()
    {
        // The whole promise: the folder is somewhere else and nothing that opens the old
        // path can tell. Not a shortcut — the file system does the standing-in.
        string target = Path.Combine(_root, "destino");
        string link = Path.Combine(_root, "origem");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "render.txt"), "conteudo");

        Assert.True(Junction.Create(link, target));

        Assert.True(Directory.Exists(link));
        Assert.True(Junction.Exists(link));
        Assert.Equal("conteudo", File.ReadAllText(Path.Combine(link, "render.txt")));

        // And a file written through the junction lands in the real folder.
        File.WriteAllText(Path.Combine(link, "novo.txt"), "escrito pelo caminho antigo");
        Assert.True(File.Exists(Path.Combine(target, "novo.txt")));
    }

    [Fact]
    public void ItSaysWhereItPoints()
    {
        string target = Path.Combine(_root, "alvo");
        string link = Path.Combine(_root, "atalho");
        Directory.CreateDirectory(target);

        Assert.True(Junction.Create(link, target));

        // Both names go into the reparse buffer. Writing only the one the file system
        // follows produces a junction that works and that `dir` shows with no target at all.
        Assert.Equal(target, Junction.TargetOf(link));
    }

    [Fact]
    public void AnOrdinaryFolderIsNotAJunction()
    {
        string plain = Path.Combine(_root, "pasta-comum");
        Directory.CreateDirectory(plain);

        Assert.False(Junction.Exists(plain));
        Assert.Null(Junction.TargetOf(plain));
    }

    [Fact]
    public void ATargetThatIsNotThereIsRefused_AndNothingIsLeftBehind()
    {
        string link = Path.Combine(_root, "para-lugar-nenhum");

        Assert.False(Junction.Create(link, Path.Combine(_root, "nao-existe")));
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void AFolderWithSomethingInItIsRefused()
    {
        // The reparse point is set on a directory, and setting it on one that holds files
        // would hide them behind the junction rather than move them.
        string target = Path.Combine(_root, "t2");
        string link = Path.Combine(_root, "ocupada");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "estou-aqui.txt"), "x");

        Assert.False(Junction.Create(link, target));
        Assert.True(File.Exists(Path.Combine(link, "estou-aqui.txt")));
    }
}
