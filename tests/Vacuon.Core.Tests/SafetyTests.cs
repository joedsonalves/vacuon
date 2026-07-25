using Vacuon.Core.Actions;
using Vacuon.Core.Safety;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The block list is the last thing standing between a user and an unbootable machine.
/// A false "blocked" costs a mildly annoyed user; a false "allowed" costs the machine.
/// </summary>
public class ProtectedPathsTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"c:")]
    public void VolumeRootIsNeverDeletable(string path)
    {
        Assert.Equal(ProtectionReason.VolumeRoot, ProtectedPaths.Check(path).Reason);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\WinSxS\anything")]
    [InlineData(@"c:\windows\system32\drivers\etc\hosts")]
    public void WindowsSubtreeIsProtected(string path)
    {
        Assert.Equal(ProtectionReason.OperatingSystem, ProtectedPaths.Check(path).Reason);
    }

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    public void ProgramFoldersThemselvesAreProtected(string path)
    {
        Assert.Equal(ProtectionReason.InstalledProgram, ProtectedPaths.Check(path).Reason);
    }

    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("$MFT")]
    public void KernelOwnedFilesAreProtected(string name)
    {
        Assert.Equal(ProtectionReason.KernelManaged, ProtectedPaths.Check($@"C:\{name}").Reason);
    }

    [Fact]
    public void SystemVolumeInformationAndRecyclerAreProtected()
    {
        Assert.True(ProtectedPaths.IsProtected(@"C:\System Volume Information"));
        Assert.True(ProtectedPaths.IsProtected(@"C:\$Recycle.Bin"));
        Assert.True(ProtectedPaths.IsProtected(@"C:\$Recycle.Bin\S-1-5-21-1\file.mp4"));
    }

    [Fact]
    public void WellKnownUserFoldersAreProtectedButTheirContentsAreNot()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) return;

        // The whole point: somebody may well want to delete a 9 GB render sitting in
        // Videos. They must not be able to delete Videos itself.
        Assert.Equal(ProtectionReason.UserProfileFolder, ProtectedPaths.Check(videos).Reason);
        Assert.False(ProtectedPaths.IsProtected(Path.Combine(videos, "render_v3.mp4")));
    }

    [Fact]
    public void PrefixMatchDoesNotLeakIntoSiblingNames()
    {
        // "C:\Windows2" must not be caught by the rule that protects "C:\Windows".
        Assert.False(ProtectedPaths.IsProtected(@"C:\Windows2"));
        Assert.False(ProtectedPaths.IsProtected(@"C:\WindowsProjects\file.txt"));
    }

    [Fact]
    public void LongPathPrefixCannotSmuggleAProtectedPath()
    {
        // The \\?\ prefix is spelling, not location — stripping it before comparing is
        // what stops it being used as a bypass.
        Assert.True(ProtectedPaths.IsProtected(@"\\?\C:\Windows\System32"));
    }

    [Fact]
    public void RelativeSpellingsResolveBeforeComparison()
    {
        Assert.True(ProtectedPaths.IsProtected(@"C:\Windows\System32\..\System32"));
        Assert.True(ProtectedPaths.IsProtected(@"C:\Windows\.\System32"));
    }

    [Fact]
    public void VacuonRefusesToDeleteItself()
    {
        Assert.Equal(ProtectionReason.Vacuon, ProtectedPaths.Check(AppContext.BaseDirectory).Reason);
    }

    [Fact]
    public void OrdinaryUserFilesAreAllowed()
    {
        Assert.False(ProtectedPaths.IsProtected(@"C:\Projects\build\output.log"));
        Assert.False(ProtectedPaths.IsProtected(@"D:\Renders\final.mp4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPathIsRefused(string path)
    {
        Assert.True(ProtectedPaths.IsProtected(path));
    }
}

public class DeleteServiceTests : IDisposable
{
    private readonly string _sandbox;

    public DeleteServiceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vacuon-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    private string File_(string name, int bytes = 16)
    {
        string path = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string Folder_(string name)
    {
        string path = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Plan_TouchesNothing()
    {
        string file = File_("keep-me.bin", 128);

        DeleteReport report = new DeleteService().Plan([file], DeleteMode.Permanent);

        Assert.True(report.WasDryRun);
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(128, report.BytesFreed);
        Assert.True(File.Exists(file));   // the whole point of a dry run
    }

    [Fact]
    public void Permanent_DeletesFileAndReportsBytes()
    {
        string file = File_("gone.bin", 512);

        DeleteReport report = new DeleteService().Execute([file], DeleteMode.Permanent);

        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(512, report.BytesFreed);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Permanent_DeletesFolderRecursively()
    {
        string folder = Folder_("tree");
        File_(@"tree\a.bin", 100);
        File_(@"tree\deep\b.bin", 200);

        DeleteReport report = new DeleteService().Execute([folder], DeleteMode.Permanent);

        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(300, report.BytesFreed);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void ReadOnlyFileIsStillDeleted()
    {
        // A read-only flag is not a safety decision the user made about this file, and
        // File.Delete throws on it. Clearing it first is the honest behaviour.
        string file = File_("locked-attribute.bin", 64);
        new FileInfo(file).IsReadOnly = true;

        DeleteReport report = new DeleteService().Execute([file], DeleteMode.Permanent);

        Assert.Equal(1, report.DeletedCount);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ProtectedPathIsBlockedAndReported()
    {
        DeleteReport report = new DeleteService().Execute([@"C:\Windows\System32"], DeleteMode.Permanent);

        DeleteResult result = Assert.Single(report.Results);
        Assert.Equal(DeleteOutcome.Blocked, result.Outcome);
        Assert.Equal(0, report.DeletedCount);
        Assert.NotNull(result.Message);
        Assert.True(Directory.Exists(@"C:\Windows\System32"));
    }

    [Fact]
    public void MissingPathIsReportedNotThrown()
    {
        DeleteReport report = new DeleteService()
            .Execute([Path.Combine(_sandbox, "never-existed.bin")], DeleteMode.Permanent);

        Assert.Equal(DeleteOutcome.NotFound, Assert.Single(report.Results).Outcome);
    }

    [Fact]
    public void FileHeldOpenIsReportedAsInUse()
    {
        string file = File_("busy.bin", 32);

        using FileStream hold = new(file, FileMode.Open, FileAccess.Read, FileShare.None);

        DeleteReport report = new DeleteService().Execute([file], DeleteMode.Permanent);

        DeleteResult result = Assert.Single(report.Results);
        Assert.Equal(DeleteOutcome.InUse, result.Outcome);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void OneFailureDoesNotAbortTheBatch()
    {
        string first = File_("first.bin", 10);
        string busy = File_("busy.bin", 10);
        string last = File_("last.bin", 10);

        using FileStream hold = new(busy, FileMode.Open, FileAccess.Read, FileShare.None);

        DeleteReport report = new DeleteService().Execute([first, busy, last], DeleteMode.Permanent);

        Assert.Equal(2, report.DeletedCount);
        Assert.Equal(1, report.FailedCount);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(last));
    }

    [Fact]
    public void Collapse_DropsChildrenOfSelectedFolders()
    {
        // Selecting a folder and a file inside it must not try the file afterwards —
        // the folder took it already, and the second attempt would report "not found".
        List<string> kept = DeleteService.Collapse([
            @"C:\a\b",
            @"C:\a\b\c.txt",
            @"C:\a\b\deep\d.txt",
            @"C:\a\other.txt",
        ]);

        Assert.Equal([@"C:\a\b", @"C:\a\other.txt"], kept);
    }

    [Fact]
    public void Collapse_RemovesDuplicatesIgnoringCaseAndTrailingSlash()
    {
        List<string> kept = DeleteService.Collapse([@"C:\a\file.txt", @"c:\A\FILE.TXT", @"C:\a\file.txt\"]);

        Assert.Single(kept);
    }

    [Fact]
    public void Collapse_KeepsSiblingsWithSharedPrefix()
    {
        List<string> kept = DeleteService.Collapse([@"C:\a\build", @"C:\a\build2"]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void RecycleBin_RemovesTheFileFromItsPath()
    {
        string file = File_("to-the-bin.bin", 48);

        DeleteReport report = new DeleteService().Execute([file], DeleteMode.RecycleBin);

        // The item may end up in the bin or, if it exceeds the quota, be removed
        // outright — either way it must no longer be at its original path.
        Assert.Equal(DeleteMode.RecycleBin, report.Mode);
        Assert.Equal(1, report.DeletedCount);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void EmptySelectionProducesEmptyReport()
    {
        DeleteReport report = new DeleteService().Execute([], DeleteMode.Permanent);

        Assert.Empty(report.Results);
        Assert.Equal(0, report.DeletedCount);
    }
}
