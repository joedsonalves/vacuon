using Vacuon.Core.Monitoring;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The distinction a monitor lives or dies by: nothing happened, versus we could not tell.
/// <para>
/// This guards a bug that shipped. <c>DiskMonitor.Poll</c> caught the journal wrapping past
/// its position and returned an empty snapshot — with a comment above it saying that saying
/// so beats pretending the gap was quiet, and no field on the snapshot able to say it. The
/// localized string for the warning existed and was never printed by anything.
/// </para>
/// </summary>
public class ActivitySnapshotTests
{
    [Fact]
    public void AQuietIntervalAndALostOneAreNotTheSameValue()
    {
        var quiet = new ActivitySnapshot([], 0, 100, 0, TimeSpan.FromSeconds(5));
        var lost = new ActivitySnapshot([], 0, 100, 0, TimeSpan.FromSeconds(5), JournalGap: true);

        Assert.False(quiet.JournalGap);
        Assert.True(lost.JournalGap);
        Assert.NotEqual(quiet, lost);
    }

    [Fact]
    public void AnIntervalIsCompleteUnlessSaidOtherwise()
    {
        // The flag defaults to false, so every existing construction keeps meaning what it
        // meant. Only the wrap path sets it.
        Assert.False(new ActivitySnapshot([], 0, 0, 0, TimeSpan.Zero).JournalGap);
    }

    [Fact]
    public void AGapStillCarriesTheFreeSpaceItMeasured()
    {
        // Two reads subtracted owe the journal nothing. What the gap costs is knowing which
        // folders account for the change — not whether the disk moved.
        var lost = new ActivitySnapshot([], 0, 500, -4_096, TimeSpan.FromSeconds(5), JournalGap: true);

        Assert.Equal(-4_096, lost.FreeBytesDelta);
        Assert.Empty(lost.Folders);
    }

    [Fact]
    public void BytesAddedSumsTheFolders()
    {
        var snapshot = new ActivitySnapshot(
        [
            new FolderActivity(@"C:\a", 2, 0, 1, 4_096),
            new FolderActivity(@"C:\b", 0, 3, 0, 1_024),
        ], 6, 1_000, -5_120, TimeSpan.FromSeconds(1));

        Assert.Equal(5_120, snapshot.BytesAdded);
    }

    [Fact]
    public void AFolderTotalCountsEveryKindOfChange()
    {
        Assert.Equal(6, new FolderActivity(@"C:\x", 1, 2, 3, 0).Total);
    }
}
