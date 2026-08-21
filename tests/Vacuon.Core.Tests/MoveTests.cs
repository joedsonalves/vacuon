using Vacuon.Core.Actions;
using Vacuon.Core.Index;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The move is the only batch action in the app that destroys nothing — as long as it
/// never overwrites. Most of what is guarded here is that one promise.
/// </summary>
public class MoveServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vacuon-move-tests", Guid.NewGuid().ToString("N"));

    public MoveServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string File_(string folder, string name, int bytes = 8)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Plan_TakenNameGetsASuffixInsteadOfOverwriting()
    {
        string from = Dir("from");
        string to = Dir("to");

        string source = File_(from, "render.mp4", 100);
        File_(to, "render.mp4", 5000);

        MoveReport plan = new MoveService().Plan([source], to);

        MoveResult result = Assert.Single(plan.Results);
        Assert.Equal(MoveOutcome.Moved, result.Outcome);
        Assert.True(result.Renamed);
        Assert.Equal("render (2).mp4", result.FinalName);

        // The plan touches nothing: the file at the destination is still the old one.
        Assert.Equal(5000, new FileInfo(Path.Combine(to, "render.mp4")).Length);
    }

    [Fact]
    public void Plan_TwoSourcesWithOneNameDoNotLandOnTheSameTarget()
    {
        string a = Dir("a");
        string b = Dir("b");
        string to = Dir("to");

        string first = File_(a, "clip.mkv");
        string second = File_(b, "clip.mkv");

        MoveReport plan = new MoveService().Plan([first, second], to);

        // Nothing exists at the destination yet, so only the batch itself can tell the
        // second one that the name is taken. Without that, the shell — told not to ask —
        // would move one file over the other and report two successes.
        Assert.Equal(2, plan.Results.Count);
        Assert.NotEqual(plan.Results[0].Destination, plan.Results[1].Destination);
        Assert.Equal("clip (2).mkv", plan.Results[1].FinalName);
    }

    [Fact]
    public void Plan_FolderKeepsItsWholeNameWhenTheNameIsTaken()
    {
        string from = Dir("from");
        string to = Dir("to");

        string source = Path.Combine(from, "My.Videos");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(to, "My.Videos"));

        MoveReport plan = new MoveService().Plan([source], to);

        // A dot in a folder name is not an extension: "My (2).Videos" would be wrong.
        Assert.Equal("My.Videos (2)", plan.Results[0].FinalName);
    }

    [Fact]
    public void Plan_MovingIntoTheFolderItIsAlreadyInIsNotAFailure()
    {
        string folder = Dir("folder");
        string source = File_(folder, "a.txt");

        MoveReport plan = new MoveService().Plan([source], folder);

        Assert.Equal(MoveOutcome.AlreadyThere, plan.Results[0].Outcome);
        Assert.Equal(0, plan.FailedCount);
        Assert.Equal(1, plan.SkippedCount);
    }

    [Fact]
    public void Plan_FolderRefusesToBeMovedInsideItself()
    {
        string outer = Dir("outer");
        string inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);

        MoveReport plan = new MoveService().Plan([outer], inner);

        // Windows accepts this and fails halfway, after copying files into the copy of
        // itself it is making.
        Assert.Equal(MoveOutcome.IntoItself, plan.Results[0].Outcome);
    }

    [Fact]
    public void Plan_ProtectedSourceIsBlockedAndNeverAttempted()
    {
        string to = Dir("to");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        MoveReport plan = new MoveService().Plan([windows], to);

        Assert.Equal(MoveOutcome.Blocked, plan.Results[0].Outcome);
        Assert.Single(plan.Blocked);
    }

    [Fact]
    public void Destination_WindowsIsRefusedButAUserFolderIsNot()
    {
        // Not the same question ProtectedPaths answers about deleting. Videos is a
        // protected folder — it must not be deleted — and it is still an ordinary place
        // to move a video to.
        Assert.Equal(DestinationVerdict.Protected,
            MoveService.CheckDestination(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));

        Assert.Equal(DestinationVerdict.Ok,
            MoveService.CheckDestination(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
    }

    [Fact]
    public void Destination_AFileIsNotAFolderAndAGhostIsMissing()
    {
        string file = File_(Dir("d"), "not-a-folder.txt");

        Assert.Equal(DestinationVerdict.NotAFolder, MoveService.CheckDestination(file));
        Assert.Equal(DestinationVerdict.Missing,
            MoveService.CheckDestination(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void Execute_MovesTheFileAndLeavesTheOldOneAlone()
    {
        string from = Dir("from");
        string to = Dir("to");

        string source = File_(from, "render.mp4", 100);
        File_(to, "render.mp4", 5000);

        MoveReport report = new MoveService().Execute([source], to);

        Assert.Equal(1, report.MovedCount);
        Assert.False(File.Exists(source));

        // The file that was already there kept its content: nothing was overwritten.
        Assert.Equal(5000, new FileInfo(Path.Combine(to, "render.mp4")).Length);
        Assert.Equal(100, new FileInfo(Path.Combine(to, "render (2).mp4")).Length);
    }

    [Fact]
    public void Execute_ReportsBytesMovedAndNoneFreed()
    {
        string from = Dir("from");
        string to = Dir("to");

        MoveService service = new();
        MoveReport report = service.Execute([File_(from, "a.bin", 4096)], to);

        Assert.Equal(4096, report.Bytes);

        // Same volume: there is no "freed" figure anywhere in the report to quote by
        // accident, and the flag that would let a caller invent one is false.
        Assert.False(report.CrossVolume);
    }
}

/// <summary>
/// A move inside one volume must not look like a deletion to the index: the file is
/// still there, and the volume total must not move.
/// </summary>
public class IndexMoveTests
{
    private static VolumeIndex Build()
    {
        var names = new NameBlob(256);
        var entries = new FileEntry[32];

        void Set(int i, string name, uint parent, long size, bool dir = false)
        {
            entries[i] = new FileEntry
            {
                RecordNumber = (uint)i,
                ParentIndex = parent,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = dir ? EntryFlags.Directory : EntryFlags.None,
                LogicalSize = size,
                AllocatedSize = size,
                HardLinkCount = 1,
            };
        }

        Set(5, ".", 5, 0, dir: true);          // raiz
        Set(6, "Bruto", 5, 0, dir: true);
        Set(7, "take01.mp4", 6, 1000);
        Set(8, "take02.mp4", 6, 2000);
        Set(9, "Aprovados", 5, 0, dir: true);
        Set(10, "Serie", 6, 0, dir: true);
        Set(11, "ep1.mp4", 10, 500);

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1_000_000, 500_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft);
    }

    [Fact]
    public void MarkMoved_ChangesTheParentAndTheTotalStaysPut()
    {
        VolumeIndex index = Build();
        long before = index.TotalLogicalBytes;

        Assert.True(index.MarkMoved(7, 9, "take01.mp4"));

        Assert.Equal(@"C:\Aprovados\take01.mp4", index.GetFullPath(7));

        // Bruto keeps take02 (2000) and Serie\ep1 (500); the 1000 that left is gone from
        // its total and turned up in the destination's.
        Assert.Equal(2500, index.GetSubtreeSize(6));
        Assert.Equal(1000, index.GetSubtreeSize(9));

        // The bytes never left the volume. A delete-and-forget would have dropped the
        // total by a gigabyte while the free space on disk did not move.
        Assert.Equal(before, index.TotalLogicalBytes);
    }

    [Fact]
    public void MarkMoved_RenamesWhenTheDestinationAlreadyHadTheName()
    {
        VolumeIndex index = Build();

        Assert.True(index.MarkMoved(7, 9, "take01 (2).mp4"));

        Assert.Equal("take01 (2).mp4", index.GetName(7).ToString());
        Assert.Equal(@"C:\Aprovados\take01 (2).mp4", index.GetFullPath(7));
    }

    [Fact]
    public void MarkMoved_TakesTheWholeSubtreeAlong()
    {
        VolumeIndex index = Build();

        Assert.True(index.MarkMoved(10, 9, "Serie"));

        Assert.Equal(@"C:\Aprovados\Serie\ep1.mp4", index.GetFullPath(11));
        Assert.Equal(500, index.GetSubtreeSize(9));
    }

    [Fact]
    public void MarkMoved_RefusesToMakeAFolderItsOwnDescendantsChild()
    {
        VolumeIndex index = Build();

        // Bruto into Serie, which lives inside Bruto. The ring it would build makes every
        // walk up the parent chain spin until its guard fires.
        Assert.False(index.MarkMoved(6, 10, "Bruto"));
        Assert.Equal(@"C:\Bruto\", index.GetFullPath(6));
    }

    [Fact]
    public void MarkMoved_RefusesTheRootAndANonFolderParent()
    {
        VolumeIndex index = Build();

        Assert.False(index.MarkMoved(index.RootIndex, 9, "."));
        Assert.False(index.MarkMoved(7, 8, "take01.mp4"));   // parent is a file
    }

    [Fact]
    public void FindEntry_WalksDownByNameAndIgnoresCase()
    {
        VolumeIndex index = Build();

        Assert.Equal(9, index.FindEntry(@"C:\Aprovados"));
        Assert.Equal(11, index.FindEntry(@"c:\bruto\serie\EP1.MP4"));
        Assert.Equal(index.RootIndex, index.FindEntry(@"C:\"));
        Assert.Equal(-1, index.FindEntry(@"C:\Bruto\naoexiste"));
        Assert.Equal(-1, index.FindEntry(@"D:\Bruto"));
    }

    [Fact]
    public void AddDirectory_PlacesAFolderTheScanNeverSaw()
    {
        VolumeIndex index = Build();

        // The record number is the real one, read from the file system — the whole point
        // of adopting rather than inventing a slot.
        int added = index.AddDirectory(20, 9, "Novos");

        Assert.Equal(20, added);
        Assert.Equal(@"C:\Aprovados\Novos\", index.GetFullPath(20));
        Assert.Equal(20, index.FindEntry(@"C:\Aprovados\Novos"));

        Assert.True(index.MarkMoved(7, 20, "take01.mp4"));
        Assert.Equal(1000, index.GetSubtreeSize(20));
    }

    [Fact]
    public void AddDirectory_RefusesASlotSomethingElseIsUsing()
    {
        VolumeIndex index = Build();

        // Occupied means the index and the disk disagree about that record. Overwriting
        // would bury the disagreement under a folder that looks right.
        Assert.Equal(-1, index.AddDirectory(7, 9, "Novos"));
        Assert.Equal(-1, index.AddDirectory(9999, 9, "Novos"));
        Assert.Equal(-1, index.AddDirectory(21, 7, "Novos"));   // parent is a file
    }
}
