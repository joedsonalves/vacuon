using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Safety;
using Vacuon.Core.Scan;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The Recycle Bin and the quarantine hold files on their way out. A finder that groups by
/// content and picks one copy to keep could pick the one in there, and offer the real file
/// for removal beside it.
/// </summary>
public class HoldingAreasTests
{
    private const long Big = 50L * 1024 * 1024;

    /// <summary>
    /// <c>C:\data\clip.mp4</c>, a copy of it in the bin and another in the quarantine, and an
    /// ordinary duplicate pair under <c>C:\other</c>. Same sizes, so stage one would group them.
    /// </summary>
    private static VolumeIndex Volume()
    {
        var names = new NameBlob(512);
        var entries = new FileEntry[32];

        void Set(int i, string name, int parent, long size, bool dir = false) => entries[i] = new FileEntry
        {
            RecordNumber = (uint)i,
            ParentIndex = (uint)parent,
            NameOffset = names.Append(name),
            NameLength = (ushort)name.Length,
            Flags = dir ? EntryFlags.Directory : EntryFlags.None,
            LogicalSize = size,
            AllocatedSize = size,
            HardLinkCount = 1,
        };

        Set(5, ".", 5, 0, dir: true);

        Set(6, "data", 5, 0, dir: true);
        Set(7, "clip.mp4", 6, Big);

        Set(8, "$Recycle.Bin", 5, 0, dir: true);
        Set(9, "S-1-5-21-1000", 8, 0, dir: true);
        Set(10, "$R0ZX1A.mp4", 9, Big);

        Set(11, ProtectedPaths.QuarantineFolderName, 5, 0, dir: true);
        Set(12, "20261005-120000", 11, 0, dir: true);
        Set(13, "00001.bin", 12, Big);

        Set(14, "other", 5, 0, dir: true);
        Set(15, "b.mp4", 14, Big + 1);
        Set(16, "c.mp4", 14, Big + 1);

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1L << 40, 1L << 39, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft);
    }

    [Fact]
    public void TheBinAndTheQuarantine_AndEverythingInThem_AreHoldingAreas()
    {
        HoldingAreas held = HoldingAreas.Of(Volume());

        foreach (int inside in new[] { 8, 9, 10, 11, 12, 13 }) Assert.True(held.Contains(inside), $"entry {inside}");
        foreach (int outside in new[] { 5, 6, 7, 14, 15, 16 }) Assert.False(held.Contains(outside), $"entry {outside}");
    }

    [Fact]
    public void ExactDuplicates_NeverGroupACopyThatIsOnItsWayOut()
    {
        // Three files of the same size would be one bucket — and the copy kept would be
        // whichever came first, which here is the one already in the bin.
        DuplicateScope scope = new DuplicateFinder().Scope(Volume());

        Assert.Equal(2, scope.CandidateFiles);     // the ordinary pair, and only that
        Assert.Equal(1, scope.SizeBuckets);
    }

    [Fact]
    public void SimilarVideos_DoNotCountWhatIsInTheBinOrTheQuarantine()
    {
        VideoScope scope = new VideoDuplicateFinder().Scope(Volume());

        Assert.Equal(3, scope.Candidates);          // clip.mp4, b.mp4, c.mp4
    }

    [Fact]
    public void DuplicateFolders_DoNotOfferTheBinOrTheQuarantineAsCopies()
    {
        // Folders of one 50 MiB file each: data, the bin's user folder and the batch would be
        // three folders of the same shape.
        DuplicateFolderScope scope = DuplicateFolderFinder.Scope(Volume(), minimumBytes: 1024);

        Assert.Equal(2, scope.FoldersConsidered);  // data and other
    }
}
