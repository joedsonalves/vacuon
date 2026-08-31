using Vacuon.Core.Transfer;
using Xunit;

namespace Vacuon.Core.Tests;

public class RobocopyOutputTests
{
    // Every literal in this class was copied out of a real robocopy run on a Windows 11
    // machine, invoked exactly the way RobocopyArguments invokes it. Writing them from
    // memory is how a parser passes its tests and reads nothing on the day.
    private const string FileLine =
        "\t    New File  \t\t  200000\tC:\\folder\\file1.bin";

    private const string ExtraLine =
        "\t  *EXTRA File \t\t  150000\tC:\\folder\\sub\\gone.bin";

    private const string DirLine =
        "\t*EXTRA Dir        -1\tC:\\folder\\sub\\";

    [Fact]
    public void FileLine_YieldsItsBytesAndItsPath()
    {
        RobocopyLine line = RobocopyOutput.Parse(FileLine);

        Assert.Equal(RobocopyLineKind.File, line.Kind);
        Assert.Equal(200_000, line.Bytes);
        Assert.Equal(@"C:\folder\file1.bin", line.Path);
    }

    [Fact]
    public void PurgeLine_IsToldApartFromACopy()
    {
        // Both carry a size and a path. Only one of them means a file stopped existing, and
        // a delete that counted as a copy would report bytes moved that never moved.
        RobocopyLine line = RobocopyOutput.Parse(ExtraLine);

        Assert.Equal(RobocopyLineKind.Extra, line.Kind);
        Assert.Equal(150_000, line.Bytes);
    }

    [Fact]
    public void DirectoryRow_IsNotAFile()
    {
        // It arrives even with /NDL, carries -1 for a size, and ends in a separator. Counted
        // as a file it would add a phantom item to every folder in the batch.
        Assert.Equal(RobocopyLineKind.Ignored, RobocopyOutput.Parse(DirLine).Kind);
    }

    [Theory]
    [InlineData("  0%  ", 0)]
    [InlineData("100%  ", 100)]
    [InlineData("\t 12.3%", 12)]
    public void PercentLines_AreRead(string line, int expected)
    {
        RobocopyLine parsed = RobocopyOutput.Parse(line);

        Assert.Equal(RobocopyLineKind.Percent, parsed.Kind);
        Assert.Equal(expected, parsed.Percent);
    }

    [Fact]
    public void SummaryRow_IsRecognisedWithoutReadingItsLabel()
    {
        // ⚠️ The label is translated on some installs — the run these lines came from signed
        // off with "Ended : domingo, 30 de agosto de 2026" while the table stayed English.
        // Matching on the word "Bytes" would work here and fail on somebody else's machine.
        Assert.True(RobocopyOutput.TryParseSummaryRow(
            "   Bytes :    750000    750000         0         0         0         0",
            out long copied, out long failed, out long extras));

        Assert.Equal(750_000, copied);
        Assert.Equal(0, failed);
        Assert.Equal(0, extras);
    }

    [Fact]
    public void SummaryRow_CarriesTheFailedAndExtrasColumns()
    {
        Assert.True(RobocopyOutput.TryParseSummaryRow(
            "   Bytes :    750000         0         0         0      1024    750000",
            out long copied, out long failed, out long extras));

        Assert.Equal(0, copied);
        Assert.Equal(1024, failed);
        Assert.Equal(750_000, extras);
    }

    [Theory]
    [InlineData("               Total    Copied   Skipped  Mismatch    FAILED    Extras")]
    [InlineData("   Times :   0:00:00   0:00:00                       0:00:00   0:00:00")]
    [InlineData("   Ended : domingo, 30 de agosto de 2026 20:33:08")]
    [InlineData("   Speed :           750.000.000 Bytes/sec.")]
    [InlineData("------------------------------------------------------------------------------")]
    public void OtherSummaryLines_AreNotMistakenForNumbers(string line)
    {
        Assert.False(RobocopyOutput.TryParseSummaryRow(line, out _, out _, out _));
    }

    [Theory]
    [InlineData(0, true)]   // nothing to do
    [InlineData(1, true)]   // files copied
    [InlineData(2, true)]   // extras — what a successful purge returns
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]  // something could not be copied
    [InlineData(16, false)] // fatal
    public void ExitCode_IsABitmaskAndEightIsWhereItTurnsBad(int code, bool ok)
    {
        // Treating this like errno — zero good, anything else bad — would report every
        // successful copy as a failure, because a copy that copied something returns 1.
        Assert.Equal(ok, RobocopyOutput.Succeeded(code));
    }
}

public class RobocopyArgumentTests
{
    [Fact]
    public void CopyingATree_AsksForSubdirectoriesAndThreads()
    {
        List<string> args = RobocopyArguments.Copy(@"C:\a", @"D:\b", null, 32);

        Assert.Equal(@"C:\a", args[0]);
        Assert.Equal(@"D:\b", args[1]);
        Assert.Contains("/E", args);
        Assert.Contains("/MT:32", args);
    }

    [Fact]
    public void CopyingOneFile_NamesItAndDropsTheThreads()
    {
        // /MT buys nothing on a single file, and it costs the per-file percentage: with
        // several threads open each percentage belongs to a different file, so reading them
        // as one progress figure would be inventing a number.
        List<string> args = RobocopyArguments.Copy(@"C:\a", @"D:\b", "clip.mp4", 32);

        Assert.Equal("clip.mp4", args[2]);
        Assert.DoesNotContain("/E", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/MT", StringComparison.Ordinal));
    }

    [Fact]
    public void RetriesAreCappedOnEveryRun()
    {
        // Robocopy's own default is a million retries thirty seconds apart. One locked file
        // would hold a transfer window open for most of a year.
        foreach (List<string> args in new[]
        {
            RobocopyArguments.Copy(@"C:\a", @"D:\b", null, 32),
            RobocopyArguments.Move(@"C:\a", @"D:\b", null, 32),
            RobocopyArguments.Purge(@"C:\empty", @"D:\b", 32),
        })
        {
            Assert.Contains("/R:1", args);
            Assert.Contains("/W:1", args);
            Assert.Contains("/BYTES", args);
        }
    }

    [Fact]
    public void MoveAddsMoveAndKeepsEverythingCopyAsked()
    {
        List<string> copy = RobocopyArguments.Copy(@"C:\a", @"D:\b", null, 8);
        List<string> move = RobocopyArguments.Move(@"C:\a", @"D:\b", null, 8);

        Assert.Contains("/MOVE", move);
        foreach (string argument in copy) Assert.Contains(argument, move);
    }

    [Fact]
    public void PurgeMirrorsAnEmptyFolderOntoTheTarget()
    {
        List<string> args = RobocopyArguments.Purge(@"C:\empty", @"D:\doomed", 32);

        Assert.Equal(@"C:\empty", args[0]);
        Assert.Equal(@"D:\doomed", args[1]);
        Assert.Contains("/MIR", args);
    }

    [Fact]
    public void ThreadCountIsClamped()
    {
        // Robocopy refuses anything outside 1..128 and exits without copying, which would
        // look exactly like a transfer that silently did nothing.
        Assert.Contains("/MT:128", RobocopyArguments.Copy(@"C:\a", @"D:\b", null, 9999));
        Assert.DoesNotContain(RobocopyArguments.Copy(@"C:\a", @"D:\b", null, 1),
                              a => a.StartsWith("/MT", StringComparison.Ordinal));
    }
}

public class TransferRateMeterTests
{
    [Fact]
    public void OneReadingIsNotARate()
    {
        var meter = new TransferRateMeter();
        meter.Record(TimeSpan.Zero, 0, 0);

        Assert.Equal(0, meter.BytesPerSecond);
        Assert.Null(meter.Estimate(1_000_000));
    }

    [Fact]
    public void RateIsTheDifferenceAcrossTheWindow()
    {
        var meter = new TransferRateMeter(TimeSpan.FromSeconds(5));
        meter.Record(TimeSpan.Zero, 0, 0);
        meter.Record(TimeSpan.FromSeconds(2), 20_000_000, 40);

        Assert.Equal(10_000_000, meter.BytesPerSecond, 0);
        Assert.Equal(20, meter.FilesPerSecond, 0);
    }

    [Fact]
    public void OldReadingsFallOutSoTheRateFollowsWhatIsHappeningNow()
    {
        // Ten thousand small files then one large one are different speeds. An average since
        // the start keeps quoting the first long after it stopped being true — which is
        // exactly the estimate everyone has learned not to trust.
        var meter = new TransferRateMeter(TimeSpan.FromSeconds(2));

        meter.Record(TimeSpan.Zero, 0, 0);
        meter.Record(TimeSpan.FromSeconds(1), 100_000_000, 0);      // fast start
        meter.Record(TimeSpan.FromSeconds(10), 101_000_000, 0);     // then a crawl
        meter.Record(TimeSpan.FromSeconds(11), 102_000_000, 0);

        // Averaged since zero this would read about 9 MB/s. Over the window it is 1.
        Assert.InRange(meter.BytesPerSecond, 900_000, 1_100_000);
    }

    [Fact]
    public void NoEstimateWhileNothingIsMoving()
    {
        // A stalled transfer must not print "0 seconds left". It has no idea, and says so.
        var meter = new TransferRateMeter();
        meter.Record(TimeSpan.Zero, 5_000, 1);
        meter.Record(TimeSpan.FromSeconds(3), 5_000, 1);

        Assert.Null(meter.Estimate(1_000_000));
    }

    [Fact]
    public void EstimateIsWhatIsLeftDividedByTheRate()
    {
        var meter = new TransferRateMeter();
        meter.Record(TimeSpan.Zero, 0, 0);
        meter.Record(TimeSpan.FromSeconds(1), 1_000_000, 1);

        TimeSpan? estimate = meter.Estimate(5_000_000);

        Assert.NotNull(estimate);
        Assert.Equal(5, estimate!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void NothingLeftIsZero_NotNull()
    {
        var meter = new TransferRateMeter();
        Assert.Equal(TimeSpan.Zero, meter.Estimate(0));
    }

    [Fact]
    public void AnAbsurdEstimateIsWithheldRatherThanPrinted()
    {
        // A byte a second against a terabyte is thirty thousand years. That is not an
        // estimate, it is a rate that has collapsed, and no one would act on the number.
        var meter = new TransferRateMeter();
        meter.Record(TimeSpan.Zero, 0, 0);
        meter.Record(TimeSpan.FromSeconds(1), 1, 0);

        Assert.Null(meter.Estimate(1_000_000_000_000));
    }
}

public class TransferProgressTests
{
    private static TransferProgress Progress(long done, long total) =>
        new(TransferPhase.Running, "x", 1, 2, done, total, 0, 0, TimeSpan.Zero, null);

    [Fact]
    public void NoTotalMeansNoPercentage()
    {
        // An empty folder tree weighs nothing and cannot be a fraction of itself. The window
        // goes indeterminate rather than sitting at zero, which reads as "stuck".
        Assert.Null(Progress(0, 0).Fraction);
    }

    [Fact]
    public void FractionIsClampedToTheTotalItWasGiven()
    {
        // A folder that grew between the plan and the run would otherwise push the bar past
        // its own right-hand end.
        Assert.Equal(1.0, Progress(2_000, 1_000).Fraction);
        Assert.Equal(0.5, Progress(500, 1_000).Fraction);
    }
}

public class TransferReportTests
{
    private static TransferReport Report(TransferKind kind) =>
        new(kind, @"D:\x", [], TransferPhase.Finished, 1024, false, TimeSpan.FromSeconds(1));

    [Fact]
    public void OnlyADeleteReportsBytesAsFreed()
    {
        // The same rule the quarantine and the Recycle Bin already answer to. A copy adds
        // bytes to one volume and takes none from the other; calling that "freed" would be
        // the app asserting a figure the disk plainly disagrees with.
        Assert.False(Report(TransferKind.Copy).BytesWereFreed);
        Assert.False(Report(TransferKind.Move).BytesWereFreed);
        Assert.True(Report(TransferKind.Delete) with { BytesWereFreed = true } is { BytesWereFreed: true });
    }
}
