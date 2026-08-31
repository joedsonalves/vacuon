using Vacuon.Core.Cleanup;
using Vacuon.Core.Scheduling;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M9, the scheduling half. Almost everything here guards one rule: a run nobody
/// is watching may only quarantine. Unattended deletion is the single most dangerous thing
/// this application could be asked to do.
/// </summary>
public class ScheduledCleanupTests
{
    private const string Exe = @"C:\Tools\Vacuon\vacuon.exe";

    [Fact]
    public void AScheduledRunAlwaysQuarantines()
    {
        // Not a default that a caller can override — Build takes no disposal at all, so
        // there is no parameter through which "permanent" could ever arrive.
        foreach (CleanupProfile profile in new[] { CleanupProfile.Quick, CleanupProfile.Deep })
        {
            string command = ScheduledCleanup.Build(Exe, profile);

            Assert.Contains("--to=quarantine", command, StringComparison.Ordinal);
            Assert.DoesNotContain("--to=permanent", command, StringComparison.Ordinal);
            Assert.DoesNotContain("--to=recycle", command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCustomProfileCannotBeScheduled()
    {
        // Custom includes rules graded dangerous — turning hibernation off, deleting restore
        // points. Arranging for those to happen on a timer, with nobody present, is not a
        // thing the app offers.
        var scheduler = new ScheduledCleanup(_ => (0, "should not run"));

        ScheduleResult result = scheduler.Create(Exe, ScheduleFrequency.Daily,
                                                 new TimeOnly(3, 0), CleanupProfile.Custom);

        Assert.False(result.Succeeded);
        Assert.Contains("custom", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreatingAScheduleAsksSchtasksForWhatItSays()
    {
        string? seen = null;
        var scheduler = new ScheduledCleanup(args => { seen = args; return (0, "SUCCESS"); });

        ScheduleResult result = scheduler.Create(Exe, ScheduleFrequency.Weekly,
                                                 new TimeOnly(2, 30), CleanupProfile.Quick);

        Assert.True(result.Succeeded);
        Assert.NotNull(seen);
        Assert.Contains("/Create", seen, StringComparison.Ordinal);
        Assert.Contains("/SC WEEKLY", seen, StringComparison.Ordinal);
        Assert.Contains("/ST 02:30", seen, StringComparison.Ordinal);
        Assert.Contains("--to=quarantine", seen, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateOverwritesRatherThanPrompting()
    {
        // Without /F, schtasks asks whether to replace an existing task — and a prompt in a
        // context with no console attached waits forever.
        string? seen = null;
        var scheduler = new ScheduledCleanup(args => { seen = args; return (0, string.Empty); });

        scheduler.Create(Exe, ScheduleFrequency.Daily, new TimeOnly(3, 0), CleanupProfile.Quick);

        Assert.Contains("/F", seen!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonZeroExitIsReportedAsFailure()
    {
        var scheduler = new ScheduledCleanup(_ => (1, "ERROR: Access is denied."));

        ScheduleResult result = scheduler.Create(Exe, ScheduleFrequency.Daily,
                                                 new TimeOnly(3, 0), CleanupProfile.Quick);

        Assert.False(result.Succeeded);
        Assert.Contains("Access is denied", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TasksAreCreatedUnderVacuonsOwnFolder()
    {
        // So that listing finds them, and so that a person browsing Task Scheduler can see
        // at a glance what this app put there.
        string? seen = null;
        var scheduler = new ScheduledCleanup(args => { seen = args; return (0, string.Empty); });

        scheduler.Create(Exe, ScheduleFrequency.Daily, new TimeOnly(3, 0), CleanupProfile.Deep);

        Assert.Contains(ScheduledCleanup.TaskPrefix, seen!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a,b,c", 3)]
    [InlineData("\"one,two\",three", 2)]
    [InlineData("\"quoted\",\"also, quoted\",plain", 3)]
    public void CsvSplittingHonoursQuotedCommas(string line, int expected)
    {
        // schtasks /FO CSV puts the command line in a field, and a command line contains
        // commas. Splitting naively would shift every column after it.
        Assert.Equal(expected, ScheduledCleanup.SplitCsv(line).Length);
    }

    /// <summary>One header row plus tasks, the way schtasks /FO CSV /V actually answers.</summary>
    private static string Csv(params string[] taskNames)
    {
        const string Header =
            """
            "HostName","TaskName","Next Run Time","Status","d","e","f","g","Task To Run"
            """;

        var sb = new System.Text.StringBuilder();
        sb.Append(Header).Append('\n');

        foreach (string name in taskNames)
        {
            sb.Append($""""
                "PC","{name}","22/08/2026 03:00","Ready","d","e","f","g","vacuon.exe clean, quietly"
                """").Append('\n');
        }

        return sb.ToString();
    }

    [Fact]
    public void ListingFindsVacuonsTasksAmongEverythingElseOnTheMachine()
    {
        // schtasks refuses a folder in /TN — it exits 255 — so the query asks for every task
        // and filters here. This is the shape of the real answer, other people's tasks included.
        var scheduler = new ScheduledCleanup(_ => (0, Csv(
            @"\Microsoft\Windows\Defrag\ScheduledDefrag",
            @"\Vacuon\QuickCleanup",
            @"\OneDrive Reporting Task")));

        ScheduleListing listing = scheduler.List();

        Assert.True(listing.Succeeded);
        Assert.Equal(@"Vacuon\QuickCleanup", Assert.Single(listing.Tasks).Name);
    }

    [Fact]
    public void TheRepeatedHeaderIsNotMistakenForATask()
    {
        // The header line comes back once per task folder, not only at the top.
        var scheduler = new ScheduledCleanup(_ => (0, Csv(@"\Vacuon\DeepCleanup") + Csv()));

        Assert.Single(scheduler.List().Tasks);
    }

    [Fact]
    public void AFailedQueryIsNotReportedAsAnEmptySchedule()
    {
        // The bug this test exists for: the query failed, the list came back empty, and the
        // CLI announced "Vacuon has nothing scheduled" about a machine that had a task on it.
        // Deleting that same task moments later worked, which is how it was caught.
        var scheduler = new ScheduledCleanup(_ => (255, "ERROR: The system cannot find the file specified."));

        ScheduleListing listing = scheduler.List();

        Assert.False(listing.Succeeded);
        Assert.Empty(listing.Tasks);
        Assert.Contains("255", listing.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TasksThatArrivedAreKeptEvenWhenTheSweepReportsTrouble()
    {
        // Enumerating every folder on a real machine trips over tasks the caller may not
        // read, and schtasks says so in the exit code while returning the rest intact.
        // Throwing away a task it did return would be its own kind of lie.
        var scheduler = new ScheduledCleanup(_ => (1, Csv(@"\Vacuon\QuickCleanup")));

        ScheduleListing listing = scheduler.List();

        Assert.True(listing.Succeeded);
        Assert.Single(listing.Tasks);
    }
}
