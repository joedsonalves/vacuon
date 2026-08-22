using Vacuon.Core.Monitoring;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M9, F8.3 — deciding when to interrupt someone.
/// <para>
/// Nearly every test here asserts that nothing was shown. That is the feature: a warning that
/// repeats on every poll is one people turn off, and a warning people turned off protects
/// nobody the day the disk actually fills.
/// </para>
/// </summary>
public class SpaceAlertTests
{
    private const long Threshold = 10_000_000_000;   // 10 GB

    private static SpaceReading Reading(long free, char drive = 'C') =>
        new(DateTimeOffset.Now, drive, free, 500_000_000_000);

    [Fact]
    public void CrossingBelowTheThresholdAlertsOnce()
    {
        var alerter = new SpaceAlerter();

        Assert.NotNull(alerter.Consider(Reading(9_000_000_000), Threshold));
    }

    [Fact]
    public void StayingBelowDoesNotAlertAgain()
    {
        var alerter = new SpaceAlerter();
        alerter.Consider(Reading(9_000_000_000), Threshold);

        for (int poll = 0; poll < 50; poll++)
            Assert.Null(alerter.Consider(Reading(8_000_000_000), Threshold));
    }

    [Fact]
    public void SittingAboveTheThresholdNeverAlerts()
    {
        var alerter = new SpaceAlerter();

        Assert.Null(alerter.Consider(Reading(400_000_000_000), Threshold));
        Assert.Empty(alerter.Alerted);
    }

    [Fact]
    public void ADiskHoveringOnTheLineRingsOnlyOnce()
    {
        // The case that makes people disable notifications: free space oscillating a few
        // megabytes either side of the threshold as ordinary work creates and deletes files.
        var alerter = new SpaceAlerter();

        int alerts = 0;

        for (int i = 0; i < 20; i++)
        {
            long free = i % 2 == 0 ? Threshold - 50_000_000 : Threshold + 50_000_000;
            if (alerter.Consider(Reading(free), Threshold) is not null) alerts++;
        }

        Assert.Equal(1, alerts);
    }

    [Fact]
    public void RealRecoveryLetsItWarnAgainLater()
    {
        // Someone cleaned up. The next time the disk fills, that is news again.
        var alerter = new SpaceAlerter();

        Assert.NotNull(alerter.Consider(Reading(9_000_000_000), Threshold));

        alerter.Consider(Reading(200_000_000_000), Threshold);   // cleaned up

        Assert.NotNull(alerter.Consider(Reading(9_000_000_000), Threshold));
    }

    [Fact]
    public void ClearingTheLineByOneByteIsNotRecovery()
    {
        var alerter = new SpaceAlerter();
        alerter.Consider(Reading(9_000_000_000), Threshold);

        alerter.Consider(Reading(Threshold + 1), Threshold);     // barely above

        Assert.Null(alerter.Consider(Reading(9_000_000_000), Threshold));
    }

    [Fact]
    public void VolumesAreTrackedApart()
    {
        // D: filling has nothing to do with whether C: already warned.
        var alerter = new SpaceAlerter();

        Assert.NotNull(alerter.Consider(Reading(9_000_000_000, 'C'), Threshold));
        Assert.NotNull(alerter.Consider(Reading(9_000_000_000, 'D'), Threshold));
    }

    [Fact]
    public void TheAlertCarriesHowMuchIsMissing()
    {
        var alerter = new SpaceAlerter();

        SpaceAlert alert = alerter.Consider(Reading(9_000_000_000), Threshold)!;

        Assert.Equal(1_000_000_000, alert.Shortfall);
        Assert.Equal('C', alert.DriveLetter);
    }

    [Fact]
    public void RearmingByHandWarnsOnTheNextReading()
    {
        var alerter = new SpaceAlerter();
        alerter.Consider(Reading(9_000_000_000), Threshold);

        alerter.Rearm('C');

        Assert.NotNull(alerter.Consider(Reading(9_000_000_000), Threshold));
    }

    [Fact]
    public void TheDriveLetterIsMatchedWithoutRegardToCase()
    {
        var alerter = new SpaceAlerter();

        Assert.NotNull(alerter.Consider(Reading(9_000_000_000, 'c'), Threshold));
        Assert.Null(alerter.Consider(Reading(9_000_000_000, 'C'), Threshold));
    }
}
