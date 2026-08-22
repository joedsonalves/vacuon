using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M8 — what changed between two scans.
/// <para>
/// This answers a question a single scan cannot: not "what is big" but "what <b>moved</b>".
/// The ninety-gigabyte folder that was already there last month is not why the disk filled
/// this week, and a ranking by size will always put it first.
/// </para>
/// </summary>
public class SnapshotDiffTests
{
    private static readonly DateTime Monday = new(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Friday = new(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

    private static LoadedSnapshot Snapshot(DateTime takenAt,
                                           params (string Folder, long Bytes)[] folders)
    {
        var built = new (string Name, long Bytes, DateTime Written)[folders.Length];

        for (int i = 0; i < folders.Length; i++)
            built[i] = (folders[i].Folder, folders[i].Bytes, takenAt);

        return new LoadedSnapshot(SyntheticIndex.Build(@"C:\data", built),
                                  JournalMark.None, takenAt);
    }

    [Fact]
    public void AFolderThatGrewIsReportedWithTheDifference()
    {
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("downloads", 100L * 1024 * 1024)),
            Snapshot(Friday, ("downloads", 900L * 1024 * 1024)));

        FolderChange change = Assert.Single(diff.Changes);

        Assert.Equal(800L * 1024 * 1024, change.ByteDelta);
        Assert.True(change.Grew);
    }

    [Fact]
    public void AFolderThatShrankKeepsItsSign()
    {
        // A cleanup that worked is as worth seeing as a disk filling up. Reporting only
        // growth would hide the one and make the other look like the whole story.
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("cache", 900L * 1024 * 1024)),
            Snapshot(Friday, ("cache", 100L * 1024 * 1024)));

        FolderChange change = Assert.Single(diff.Changes);

        Assert.Equal(-800L * 1024 * 1024, change.ByteDelta);
        Assert.False(change.Grew);
        Assert.Single(diff.Shrank);
    }

    [Fact]
    public void AFolderThatAppearedIsReported()
    {
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday),
            Snapshot(Friday, ("new-project", 500L * 1024 * 1024)));

        Assert.True(Assert.Single(diff.Changes).Appeared);
    }

    [Fact]
    public void AFolderThatVanishedIsReportedRatherThanOmitted()
    {
        // A folder present in the first scan and absent from the second never appears when
        // walking the second, and leaving it out reports a deletion as nothing having
        // happened — the same failure as a monitor calling a lost interval quiet.
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("old-project", 700L * 1024 * 1024)),
            Snapshot(Friday));

        FolderChange change = Assert.Single(diff.Changes);

        Assert.True(change.Vanished);
        Assert.Equal(-700L * 1024 * 1024, change.ByteDelta);
    }

    [Fact]
    public void NoiseBelowTheFloorIsLeftOut()
    {
        // Every folder on a live volume moves by a few kilobytes; listing all of them buries
        // the answer under the question.
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("settings", 1_000_000)),
            Snapshot(Friday, ("settings", 1_004_096)));

        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void TheBiggestMoveComesFirstWhicheverWayItWent()
    {
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("a", 100L * 1024 * 1024), ("b", 900L * 1024 * 1024)),
            Snapshot(Friday, ("a", 300L * 1024 * 1024), ("b", 100L * 1024 * 1024)));

        // b lost 800 MiB, a gained 200. The larger movement leads, sign notwithstanding.
        Assert.Equal("b", Path.GetFileName(diff.Changes[0].Folder.TrimEnd('\\')));
    }

    [Fact]
    public void TheVolumeTotalsAreCarried()
    {
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("a", 100L * 1024 * 1024)),
            Snapshot(Friday, ("a", 300L * 1024 * 1024)));

        Assert.Equal(100L * 1024 * 1024, diff.BeforeBytes);
        Assert.Equal(300L * 1024 * 1024, diff.AfterBytes);
        Assert.Equal(200L * 1024 * 1024, diff.ByteDelta);
    }

    [Fact]
    public void TheTimeBetweenTheScansIsCarried()
    {
        // Without it, "eight hundred megabytes" is not an answer. Eight hundred in a week and
        // eight hundred in an hour are different problems.
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("a", 100L * 1024 * 1024)),
            Snapshot(Friday, ("a", 900L * 1024 * 1024)));

        Assert.Equal(TimeSpan.FromDays(4), diff.Elapsed);
    }

    [Fact]
    public void TwoIdenticalScansShowNoChanges()
    {
        SnapshotComparison diff = SnapshotDiff.Compare(
            Snapshot(Monday, ("a", 100L * 1024 * 1024), ("b", 50L * 1024 * 1024)),
            Snapshot(Friday, ("a", 100L * 1024 * 1024), ("b", 50L * 1024 * 1024)));

        Assert.Empty(diff.Changes);
        Assert.Equal(0, diff.ByteDelta);
    }
}
