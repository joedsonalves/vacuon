using Vacuon.Core.Actions;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// A restore point before a batch that could go wrong (PRD F7.8) — and, above all, telling
/// whether one was really made.
/// </summary>
public class RestorePointTests
{
    [Theory]
    [InlineData("486", 486)]
    [InlineData("  486  ", 486)]
    [InlineData("none", 0)]
    [InlineData("NONE", 0)]
    public void ASequenceNumberIsASequenceNumber(string output, int expected)
    {
        // "none" is a real answer — the list is empty, there are no points yet — and it is
        // not the same as not having been able to ask.
        Assert.Equal(expected, RestorePointService.ParseSequence(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Access denied.")]
    [InlineData("Get-CimInstance : Invalid namespace")]
    public void AnythingElseIsNotAnAnswer(string output)
    {
        Assert.Equal(-1, RestorePointService.ParseSequence(output));
    }

    [Fact]
    public void ThisMachineCanBeAsked()
    {
        // Reads the real list. What is asserted is the shape: a number that could be a
        // sequence, or an honest "could not ask".
        int latest = RestorePointService.LatestSequence();

        Assert.True(latest >= -1);
    }

    [Fact]
    public void CreatedMeansAPointThatDidNotExistBefore()
    {
        // ⚠️ The distinction this whole service exists for. Windows keeps a frequency limit
        // — one point per 24 hours by default — and when it declines for that reason,
        // CreateRestorePoint returns success and creates nothing. Trusting the return value
        // would tell somebody they had a restore point on the day they most needed one.
        var created = new RestorePointResult(RestorePointOutcome.Created, 486, 487, TimeSpan.Zero);
        var silent = new RestorePointResult(RestorePointOutcome.NothingHappened, 486, 486, TimeSpan.Zero);

        Assert.True(created.Succeeded);
        Assert.True(created.SequenceAfter > created.SequenceBefore);

        Assert.False(silent.Succeeded);
        Assert.Equal(silent.SequenceBefore, silent.SequenceAfter);
    }

    [Fact]
    public void ProtectionBeingOffIsNotAFailure()
    {
        // System Protection is off by default on every drive but the system one, and on some
        // machines on that one too. Nothing went wrong; there was nothing to write to.
        var result = new RestorePointResult(RestorePointOutcome.Unavailable, -1, -1, TimeSpan.Zero);

        Assert.False(result.Succeeded);
        Assert.Equal(RestorePointOutcome.Unavailable, result.Outcome);
    }
}
