using Vacuon.Core.Index;
using Xunit;

namespace Vacuon.Core.Tests;

public class PathQueryTests
{
    /// <summary>The same shape <see cref="VolumeIndexTests"/> uses: MFT layout, root is record 5.</summary>
    private static VolumeIndex BuildSample()
    {
        var names = new NameBlob(256);
        var entries = new FileEntry[16];

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

        Set(5, ".", 5, 0, dir: true);
        Set(6, "Videos", 5, 0, dir: true);
        Set(7, "render.mp4", 6, 1000);
        Set(9, "Documentos", 5, 0, dir: true);

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1_000_000, 500_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft);
    }

    [Fact]
    public void AFolderPathResolvesToItsEntry()
    {
        PathQueryResult result = PathQuery.Resolve(@"C:\Videos", BuildSample());

        Assert.Equal(PathQueryOutcome.Folder, result.Outcome);
        Assert.Equal(6, result.EntryIndex);
    }

    [Fact]
    public void AFilePathResolvesToTheFile()
    {
        // Which the caller turns into "list the folder, highlight this one". People paste a
        // full file path far more often than they mean to paste its folder.
        PathQueryResult result = PathQuery.Resolve(@"C:\Videos\render.mp4", BuildSample());

        Assert.Equal(PathQueryOutcome.File, result.Outcome);
        Assert.Equal(7, result.EntryIndex);
    }

    [Theory]
    [InlineData(@"C:\Videos\")]
    [InlineData(@"  C:\Videos  ")]
    [InlineData("\"C:\\Videos\"")]          // what Explorer's Copy as path hands you
    [InlineData("C:/Videos")]               // what every shell and half the internet does
    [InlineData(@"c:\videos")]
    [InlineData(@"\Videos")]                // rooted at the scanned volume
    [InlineData(@"Videos\")]                // a trailing separator is enough to mean a place
    public void TheSpellingsPeopleActuallyPasteAllLandInTheSamePlace(string typed)
    {
        Assert.Equal(6, PathQuery.Resolve(typed, BuildSample()).EntryIndex);
    }

    [Fact]
    public void APlainWordStaysAPlainSearch()
    {
        // The whole feature is worthless if it costs you the ordinary search. A word with no
        // separator in it was never a path and is never treated as one.
        Assert.Equal(PathQueryOutcome.NotAPath, PathQuery.Resolve("render", BuildSample()).Outcome);
    }

    [Fact]
    public void AVagueFragmentThatResolvesToNothingFallsBackToSearching()
    {
        // "a\b" could be a path. It is not one here, and reporting "no such folder" would
        // replace a search that might have found something with an error that helps nobody.
        Assert.Equal(PathQueryOutcome.NotAPath, PathQuery.Resolve(@"holiday\2019", BuildSample()).Outcome);
    }

    [Fact]
    public void AnUnmistakablePathThatIsMissingSaysSo()
    {
        // A drive letter is not ambiguous. Silently searching for files named "C:\Nowhere"
        // would answer a question nobody asked, with nothing.
        PathQueryResult result = PathQuery.Resolve(@"C:\Nowhere", BuildSample());

        Assert.Equal(PathQueryOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public void APathOnAnotherDriveIsNotAPathThatDoesNotExist()
    {
        // ⚠️ The folder may be perfectly real. Saying "no such folder" would be false; what
        // is true is that this scan is of another volume, and that is what gets said.
        PathQueryResult result = PathQuery.Resolve(@"D:\Backup", BuildSample());

        Assert.Equal(PathQueryOutcome.OtherVolume, result.Outcome);
    }

    [Fact]
    public void AUncShareIsAnotherVolumeToo()
    {
        Assert.Equal(PathQueryOutcome.OtherVolume,
                     PathQuery.Resolve(@"\\nas\media", BuildSample()).Outcome);
    }

    [Fact]
    public void ABareDriveLetterMeansItsRoot()
    {
        PathQueryResult result = PathQuery.Resolve("C:", BuildSample());

        Assert.Equal(PathQueryOutcome.Folder, result.Outcome);
        Assert.Equal(5, result.EntryIndex);
    }

    [Fact]
    public void EmptyTextIsNotAPath()
    {
        Assert.Equal(PathQueryOutcome.NotAPath, PathQuery.Resolve("   ", BuildSample()).Outcome);
    }
}
