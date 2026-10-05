using Vacuon.Core.Actions;
using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Telling a link from a folder, and finding the ones in a tree without going through them.
/// <para>
/// Junctions are made with the app's own <see cref="Junction"/>, which needs no elevation.
/// A directory symlink does, or Developer Mode, so the test that needs one says it was
/// skipped where it cannot make one, instead of passing without having checked anything.
/// </para>
/// </summary>
public class LinksTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-links-tests-" + Guid.NewGuid().ToString("N"));

    public LinksTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // Links first, each by itself. The recursive delete does not go through one, but
            // it reports a junction as an error after removing it, and leaves the folder.
            if (Directory.Exists(_root))
            {
                foreach (string link in Links.FoldersBelow(_root)) Links.Remove(link);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Dir(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Door(string relative, string target)
    {
        string link = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.True(Junction.Create(link, target), $"could not make the junction {link}");
        return link;
    }

    [Fact]
    public void AJunctionIsALink_AFolderAndAFileAreNot()
    {
        string room = Dir("room");
        string file = Path.Combine(room, "f.txt");
        File.WriteAllText(file, "x");
        string door = Door("door", room);

        Assert.True(Links.IsLink(door));
        Assert.False(Links.IsLink(room));
        Assert.False(Links.IsLink(file));
        Assert.False(Links.IsLink(Path.Combine(_root, "not-there")));
    }

    [Fact]
    public void AJunctionWhoseTargetIsGone_IsStillALink()
    {
        // The entry is the link; whether anything is behind it is a different question. A
        // dangling one taken for a plain folder would be handed to the purge like one.
        string room = Dir("room");
        string door = Door("door", room);
        Directory.Delete(room);

        Assert.True(Links.IsLink(door));
    }

    [Fact]
    public void TheLinksBelowAFolder_AreFoundAtAnyDepth_WithoutGoingThroughThem()
    {
        string outside = Dir("outside");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "x");

        // A link on the far side of a door is not inside the folder: reporting it would mean
        // the walk went through.
        Door(Path.Combine("outside", "further"), Dir("elsewhere"));

        string tree = Dir("tree");
        string near = Door(Path.Combine("tree", "near"), outside);
        string deep = Door(Path.Combine("tree", "a", "b", "deep"), outside);
        File.WriteAllText(Path.Combine(tree, "a", "own.txt"), "x");

        List<string> found = Links.FoldersBelow(tree);

        Assert.Equal(2, found.Count);
        Assert.Contains(near, found, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(deep, found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovingALink_RemovesTheLinkAndNothingBehindIt()
    {
        string room = Dir("room");
        string precious = Path.Combine(room, "precious.txt");
        File.WriteAllText(precious, "must survive");
        string door = Door("door", room);

        Assert.True(Links.Remove(door));

        Assert.False(Directory.Exists(door));
        Assert.Equal("must survive", File.ReadAllText(precious));
    }

    [SkippableFact]
    public void ADirectorySymlinkIsALinkToo()
    {
        string room = Dir("room");
        string link = Path.Combine(_root, "symlink");

        try
        {
            Directory.CreateSymbolicLink(link, room);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "this session may not make symbolic links (it needs elevation or Developer Mode).");
        }

        Assert.True(Links.IsLink(link));
        Assert.Contains(link, Links.FoldersBelow(_root), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDeleteDialog_WeighsAJunctionAsNothing()
    {
        // Walked, a junction is weighed from the far side of the door: the dialog would have
        // promised every byte of the folder it stands for, and deleting it frees none.
        string room = Dir("room");
        File.WriteAllBytes(Path.Combine(room, "precious.bin"), new byte[70_000]);
        string door = Door("door", room);

        DeleteResult planned = Assert.Single(new DeleteService().Plan([door], DeleteMode.Permanent).Results);

        Assert.Equal(0, planned.Bytes);
    }
}
