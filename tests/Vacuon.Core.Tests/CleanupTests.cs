using Vacuon.Core.Actions;
using Vacuon.Core.Cleanup;
using Vacuon.Core.Safety;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M5. Cleanup rules are the part of the app most likely to be run without being
/// read — on a schedule, or by someone in a hurry with a full disk. So what is guarded here
/// is mostly what the engine refuses to do.
/// </summary>
public class RuleCatalogTests
{
    [Fact]
    public void TheBuiltInCatalogLoads()
    {
        // The embedded-resource failure mode this project already paid for once: MSBuild
        // renames the resource, GetManifestResourceStream returns null, and nothing in the
        // build says a word.
        IReadOnlyList<CleanupRule> rules = RuleCatalog.Load(userCatalogPath: "does-not-exist.json");

        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.Id == "windows.userTemp");
    }

    [Fact]
    public void EveryShippedRuleIsUsable()
    {
        foreach (CleanupRule rule in RuleCatalog.Load("does-not-exist.json"))
            Assert.Null(RuleCatalog.Rejected(rule));
    }

    [Fact]
    public void EverySystemToolRuleNamesAToolThatExists()
    {
        foreach (CleanupRule rule in RuleCatalog.Load("does-not-exist.json"))
        {
            if (!rule.IsSystemTool) continue;

            Assert.NotNull(rule.Tool);
            Assert.Contains(rule.Tool, SystemTools.KnownTools);
        }
    }

    [Fact]
    public void NoShippedRuleDeletesFilesInsideTheWindowsFolder()
    {
        // The rule this milestone had to be designed around. %WINDIR%\Temp is the most
        // obvious cleanup target there is and every other tool sweeps it — here it is
        // refused, because ProtectedPaths has no override and never will. A rule pointing
        // in there would match nothing at execution time and quietly under-deliver, which
        // is worse than not shipping it. The Windows folder is cleaned by Microsoft's own
        // tools, through a SystemTool rule, or not at all.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                                    .TrimEnd('\\').ToLowerInvariant();

        foreach (CleanupRule rule in RuleCatalog.Load("does-not-exist.json"))
        {
            foreach (string pattern in rule.Paths)
            {
                string expanded = RuleCatalog.Expand(pattern).ToLowerInvariant();
                if (expanded.Length == 0) continue;

                Assert.False(expanded.StartsWith(windows + "\\", StringComparison.Ordinal),
                             $"rule {rule.Id} points inside the Windows folder: {expanded}");
            }
        }
    }

    [Fact]
    public void ARuleAimedAtTheWindowsFolderIsRejected()
    {
        var rule = new CleanupRule
        {
            Id = "bad.windowsTemp",
            Category = "x",
            Name = "Windows temp",
            Description = "the classic",
            Risk = CleanupRisk.Safe,
            Paths = ["%WINDIR%\\Temp\\**"],
        };

        string? rejection = RuleCatalog.Rejected(rule);

        Assert.NotNull(rejection);
        Assert.Contains("protected", rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARuleAimedAtSystem32IsRejected()
    {
        var rule = new CleanupRule
        {
            Id = "bad.system32",
            Category = "x",
            Name = "System32",
            Description = "no",
            Risk = CleanupRisk.Safe,
            Paths = ["%WINDIR%\\System32\\**"],
        };

        Assert.NotNull(RuleCatalog.Rejected(rule));
    }

    [Fact]
    public void ARuleInsideAProtectedUserFolderIsAllowed()
    {
        // The distinction the protection list draws: the Documents folder must survive, the
        // 9 GB render sitting in it must not have to.
        var rule = new CleanupRule
        {
            Id = "ok.underAppData",
            Category = "x",
            Name = "a cache",
            Description = "regenerates",
            Risk = CleanupRisk.Safe,
            Paths = ["%LOCALAPPDATA%\\SomeVendor\\Cache\\**"],
        };

        Assert.Null(RuleCatalog.Rejected(rule));
    }

    [Fact]
    public void SystemToolRuleWithoutAToolIsRejected()
    {
        var rule = new CleanupRule
        {
            Id = "bad.noTool",
            Category = "x",
            Name = "x",
            Description = "x",
            Risk = CleanupRisk.Safe,
            Method = CleanupMethod.SystemTool,
        };

        Assert.NotNull(RuleCatalog.Rejected(rule));
    }

    [Fact]
    public void AnUnsetEnvironmentVariableMatchesNothingInsteadOfEverything()
    {
        // "%NOPE%\**" left unexpanded would be a relative pattern, and a relative pattern
        // resolves against the working directory — which is how a cleanup rule ends up
        // pointing at the application folder.
        Assert.Equal(string.Empty, RuleCatalog.Expand("%DEFINITELY_NOT_SET_12345%\\**"));
    }

    [Theory]
    [InlineData(@"C:\a\b\**", @"C:\a\b")]
    [InlineData(@"C:\a\*\Cache\**", @"C:\a")]
    [InlineData(@"C:\a\b\*.db", @"C:\a\b")]
    public void TheFixedRootIsEverythingBeforeTheFirstWildcard(string pattern, string expected)
    {
        Assert.Equal(expected, RuleCatalog.FixedRootOf(pattern));
    }

    [Fact]
    public void AUserCatalogOverridesAShippedRuleById()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vacuon-rules-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, """
            {
              "version": 1,
              "rules": [
                {
                  "Id": "windows.userTemp",
                  "Category": "cleanup.cat.windows",
                  "Name": "My own temp rule",
                  "Description": "replaced",
                  "Risk": "Caution",
                  "Paths": [ "%TEMP%\\**" ],
                  "MinimumAgeDays": 90
                }
              ]
            }
            """);

        try
        {
            CleanupRule rule = Assert.Single(
                RuleCatalog.Load(path), r => r.Id == "windows.userTemp");

            Assert.Equal("My own temp rule", rule.Name);
            Assert.Equal(CleanupRisk.Caution, rule.Risk);
            Assert.Equal(90, rule.MinimumAgeDays);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABrokenUserCatalogDoesNotTakeTheShippedOnesDown()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vacuon-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");

        try
        {
            Assert.NotEmpty(RuleCatalog.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class RuleEngineTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vacuon-cleanup-tests", Guid.NewGuid().ToString("N"));

    public RuleEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, int bytes = 100, int ageDays = 0)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);

        if (ageDays > 0)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));

        return path;
    }

    private CleanupRule Rule(string pattern, CleanupRisk risk = CleanupRisk.Safe, int minAge = 0) => new()
    {
        Id = "test.rule",
        Category = "test",
        Name = "test",
        Description = "test",
        Risk = risk,
        Paths = [Path.Combine(_root, pattern)],
        MinimumAgeDays = minAge,
    };

    [Fact]
    public void PlanFindsTheFilesAndTouchesNothing()
    {
        Write("a.tmp", 500);
        Write("b.tmp", 300);

        CleanupPlan plan = new RuleEngine().Plan([Rule("**")], CleanupProfile.Quick, isElevated: false);

        RulePlan rule = Assert.Single(plan.Rules);
        Assert.Equal(2, rule.Matches.Count);
        Assert.Equal(800, plan.MatchedBytes);

        // Still there. Planning is a read.
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [Fact]
    public void MinimumAgeKeepsRecentFiles()
    {
        Write("old.tmp", 100, ageDays: 10);
        Write("new.tmp", 100);

        CleanupPlan plan = new RuleEngine().Plan(
            [Rule("**", minAge: 5)], CleanupProfile.Quick, isElevated: false);

        CleanupMatch match = Assert.Single(Assert.Single(plan.Rules).Matches);
        Assert.EndsWith("old.tmp", match.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickProfileRefusesAnythingAboveSafe()
    {
        Write("a.tmp");

        var engine = new RuleEngine();

        RulePlan caution = Assert.Single(
            engine.Plan([Rule("**", CleanupRisk.Caution)], CleanupProfile.Quick, false).Rules);
        Assert.Equal(RuleSkipReason.RiskAboveProfile, caution.Skipped);

        RulePlan deep = Assert.Single(
            engine.Plan([Rule("**", CleanupRisk.Caution)], CleanupProfile.Deep, false).Rules);
        Assert.Equal(RuleSkipReason.None, deep.Skipped);

        RulePlan dangerousInDeep = Assert.Single(
            engine.Plan([Rule("**", CleanupRisk.Dangerous)], CleanupProfile.Deep, false).Rules);
        Assert.Equal(RuleSkipReason.RiskAboveProfile, dangerousInDeep.Skipped);
    }

    [Fact]
    public void ARuleNeedingElevationIsSkippedWithItsOwnReason()
    {
        // Not "nothing matched": the difference decides whether re-running elevated helps.
        var rule = Rule("**") with { NeedsElevation = true };

        RulePlan plan = Assert.Single(
            new RuleEngine().Plan([rule], CleanupProfile.Quick, isElevated: false).Rules);

        Assert.Equal(RuleSkipReason.NeedsElevation, plan.Skipped);
    }

    [Fact]
    public void ARuleBlockedByARunningProcessSaysWhichOne()
    {
        Write("a.tmp");

        // This test process is running, by definition.
        string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var rule = Rule("**") with { BlockedByProcesses = [self] };

        RulePlan plan = Assert.Single(
            new RuleEngine().Plan([rule], CleanupProfile.Quick, false).Rules);

        Assert.Equal(RuleSkipReason.ProcessRunning, plan.Skipped);
        Assert.Equal(self, plan.SkipDetail);
    }

    [Fact]
    public void SystemToolRulesContributeNoPredictedBytes()
    {
        // DISM and vssadmin report what they freed afterwards. Printing the catalog's
        // "typical gain" as if it were this machine's number would be an estimate wearing
        // a measurement's clothes.
        var rule = new CleanupRule
        {
            Id = "tool",
            Category = "test",
            Name = "tool",
            Description = "tool",
            Risk = CleanupRisk.Safe,
            Method = CleanupMethod.SystemTool,
            Tool = "dism.componentCleanup",
        };

        CleanupPlan plan = new RuleEngine().Plan([rule], CleanupProfile.Quick, isElevated: true);

        Assert.Equal(0, plan.MatchedBytes);
        Assert.Single(plan.SystemTools);
        Assert.True(Assert.Single(plan.Rules).WillDoSomething);
    }

    [Fact]
    public void ExecuteOnlyEverActsOnWhatThePlanShowed()
    {
        // F3.3, structurally: Execute takes a plan, not a profile. A file created between
        // the planning and the doing cannot be swept up, because the list was already fixed.
        Write("planned.tmp", 100, ageDays: 1);

        var engine = new RuleEngine();
        CleanupPlan plan = engine.Plan([Rule("**")], CleanupProfile.Quick, false);
        Assert.Single(Assert.Single(plan.Rules).Matches);

        Write("appeared-later.tmp", 100);

        CleanupReport report = engine.Execute(plan, CleanupDisposal.Permanent);

        Assert.Equal(1, report.Handled);
        Assert.False(File.Exists(Path.Combine(_root, "planned.tmp")));
        Assert.True(File.Exists(Path.Combine(_root, "appeared-later.tmp")));
    }

    [Fact]
    public void QuarantineIsTheDefaultAndDoesNotClaimToFreeAnything()
    {
        Write("a.tmp", 400);

        var engine = new RuleEngine();
        CleanupPlan plan = engine.Plan([Rule("**")], CleanupProfile.Quick, false);
        CleanupReport report = engine.Execute(plan, CleanupDisposal.Quarantine);

        Assert.False(report.BytesWereFreed);
        Assert.NotNull(report.QuarantineBatchId);

        // Purge what this test set aside. The quarantine store is the REAL one for the volume
        // the temp folder lives on, so without this every run leaves a batch sitting in
        // C:\$Vacuon.Quarantine forever — 80 KiB of them had piled up before anyone looked,
        // and they showed up on the Quarantine screen as if a person had put them there.
        // A test that litters the disk of whoever runs it is a test with a side effect it
        // never declared.
        var store = new QuarantineService();
        foreach (QuarantineBatch batch in store.ListBatches(Path.GetPathRoot(_root)!))
        {
            if (batch.BatchId == report.QuarantineBatchId) store.Purge(batch);
        }
    }

    [Fact]
    public void OnlyThePermanentRouteReportsBytesAsFreed()
    {
        Write("a.tmp", 400);

        var engine = new RuleEngine();
        CleanupPlan plan = engine.Plan([Rule("**")], CleanupProfile.Quick, false);
        CleanupReport report = engine.Execute(plan, CleanupDisposal.Permanent);

        Assert.True(report.BytesWereFreed);
        Assert.Equal(400, report.Bytes);
    }

    [Fact]
    public void AProtectedPathIsNeverMatchedEvenIfARuleReachesIt()
    {
        // The last gate. A glob can reach further than whoever wrote it expected, so every
        // match is checked, not just the pattern's root.
        // Shallow and specific on purpose. An earlier version of this test used
        // "%WINDIR%\**", which proved the same thing by walking the entire Windows folder
        // and took the suite from 19 s to nearly 3 minutes. A test that is right and slow
        // still gets skipped by the person waiting for it.
        var rule = new CleanupRule
        {
            Id = "reaching",
            Category = "test",
            Name = "test",
            Description = "test",
            Risk = CleanupRisk.Safe,
            Paths = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "*.exe")],
        };

        CleanupPlan plan = new RuleEngine().Plan([rule], CleanupProfile.Quick, false);

        // explorer.exe lives right there and is very much matched by the glob.
        Assert.Empty(Assert.Single(plan.Rules).Matches);
    }
}

public class SystemToolsTests
{
    [Fact]
    public void EachToolIdMapsToTheCommandLineItClaims()
    {
        var seen = new List<(string Exe, string Args)>();

        var tools = new SystemTools(
            runner: (exe, args, _) => { seen.Add((exe, args)); return (0, "done"); },
            freeSpace: _ => 1_000_000);

        tools.Run("dism.componentCleanup");
        tools.Run("powercfg.hibernateOff");
        tools.Run("vssadmin.deleteOldShadows", "D:\\");

        Assert.Equal("dism.exe", seen[0].Exe);
        Assert.Contains("/StartComponentCleanup", seen[0].Args);

        Assert.Equal("powercfg.exe", seen[1].Exe);
        Assert.Contains("/hibernate off", seen[1].Args);

        Assert.Equal("vssadmin.exe", seen[2].Exe);
        Assert.Contains("/for=D:", seen[2].Args);
        Assert.Contains("/oldest", seen[2].Args);
    }

    [Fact]
    public void FreedSpaceIsMeasuredBeforeAndAfter_NotTakenFromTheTool()
    {
        // DISM prints no total, vssadmin prints one that counts differently, and the
        // catalog's range is from someone else's disk. The only figure about this machine
        // is the difference in free space.
        long free = 1_000;

        var tools = new SystemTools(
            runner: (_, _, _) => { free += 5_000; return (0, "Deleted a lot"); },
            freeSpace: _ => free);

        ToolResult result = tools.Run("dism.componentCleanup");

        Assert.True(result.Succeeded);
        Assert.True(result.FreedBytesMeasured);
        Assert.Equal(5_000, result.FreedBytes);
    }

    [Fact]
    public void FreeSpaceGoingDownIsReportedAsUnmeasuredRatherThanAsNegativeOrZeroGain()
    {
        // Something else on the machine wrote while the tool ran. There is no honest number
        // to report, so the result says so instead of inventing one.
        long free = 10_000;

        var tools = new SystemTools(
            runner: (_, _, _) => { free -= 2_000; return (0, "ok"); },
            freeSpace: _ => free);

        ToolResult result = tools.Run("dism.componentCleanup");

        Assert.True(result.Succeeded);
        Assert.False(result.FreedBytesMeasured);
        Assert.Equal(0, result.FreedBytes);
    }

    [Fact]
    public void AnUnknownToolIdRunsNothing()
    {
        bool ran = false;

        var tools = new SystemTools(
            runner: (_, _, _) => { ran = true; return (0, string.Empty); },
            freeSpace: _ => 1);

        ToolResult result = tools.Run("rm.everything");

        Assert.False(ran);
        Assert.False(result.Ran);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ANonZeroExitIsNotSuccessEvenIfTheToolPrintedSomething()
    {
        var tools = new SystemTools(
            runner: (_, _, _) => (87, "Error: 87"),
            freeSpace: _ => 1_000);

        ToolResult result = tools.Run("dism.componentCleanup");

        Assert.True(result.Ran);
        Assert.False(result.Succeeded);
        Assert.Equal(87, result.ExitCode);
    }
}
