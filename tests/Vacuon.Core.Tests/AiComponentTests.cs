using Microsoft.Win32;
using Vacuon.Core.Optimization;
using Xunit;

namespace Vacuon.Core.Tests;

public class AiCatalogTests
{
    [Fact]
    public void EveryEntry_IsWellFormedAndCitesItsSource()
    {
        foreach (AiComponent c in AiComponentCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.StartsWith("ai.", c.NameKey);
            Assert.StartsWith("ai.", c.DescriptionKey);

            // A control nobody can look up is a control nobody can check.
            Assert.StartsWith("https://learn.microsoft.com/", c.DocumentationUrl);

            if (c.IsActionable)
            {
                Assert.NotNull(c.Hive);
                Assert.False(string.IsNullOrWhiteSpace(c.SubKey));
                Assert.False(string.IsNullOrWhiteSpace(c.ValueName));
                Assert.NotEqual(c.OnValue, c.OffValue);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(c.PackagePrefix));
            }
        }
    }

    [Fact]
    public void Ids_AreUnique()
    {
        // The journal keys undo by id. Two entries sharing one would undo each other.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AiComponent c in AiComponentCatalog.All)
            Assert.True(seen.Add(c.Id), $"id repetido: {c.Id}");
    }

    [Fact]
    public void Catalog_ListsOnlyMicrosoftComponents()
    {
        // The tab says "Microsoft components". A machine can be full of third-party AI apps —
        // Manus, Codex, whatever — and none of them belong here.
        foreach (AiComponent c in AiComponentCatalog.All)
        {
            string where = c.SubKey ?? c.PackagePrefix ?? string.Empty;
            Assert.Contains("Microsoft", where, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public class PolicyJournalTests
{
    [Fact]
    public void RoundTrip_KeepsTheDifferenceBetweenZeroAndAbsent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vacuon-journal-{Guid.NewGuid():N}.json");

        try
        {
            var journal = new PolicyJournal(path);

            journal.Append(new PolicyChange
            {
                ComponentId = "a",
                Hive = "CurrentUser",
                SubKey = @"Software\X",
                ValueName = "V",
                PreviousValue = null,
                KeyCreated = true,
                WrittenValue = 1,
                AtUtc = DateTime.UtcNow,
            });
            journal.Append(new PolicyChange
            {
                ComponentId = "b",
                Hive = "CurrentUser",
                SubKey = @"Software\Y",
                ValueName = "W",
                PreviousValue = 0,
                KeyCreated = false,
                WrittenValue = 1,
                AtUtc = DateTime.UtcNow,
            });

            // "was absent" and "was zero" have to survive the round trip as different things.
            Assert.Null(journal.LastFor("a")!.PreviousValue);
            Assert.Equal(0, journal.LastFor("b")!.PreviousValue);
            Assert.True(journal.LastFor("a")!.KeyCreated);

            journal.RemoveLast("a");
            Assert.Null(journal.LastFor("a"));
            Assert.NotNull(journal.LastFor("b"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_ReadsAsEmptyRatherThanThrowing()
    {
        var journal = new PolicyJournal(Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.json"));
        Assert.Empty(journal.Read());
    }
}

public class AiComponentSwitchTests
{
    /// <summary>A component pointing at a scratch key under HKCU. Needs no elevation.</summary>
    private static AiComponent Scratch(string subKey) => new()
    {
        Id = "teste",
        NameKey = "ai.copilot.name",
        DescriptionKey = "ai.copilot.desc",
        Control = ControlKind.RegistryPolicy,
        Hive = RegistryHive.CurrentUser,
        SubKey = subKey,
        ValueName = "TesteDoVacuon",
        OffValue = 1,
        OnValue = 0,
        DocumentationUrl = "https://learn.microsoft.com/",
    };

    [Fact]
    public void TurnOff_WritesTheValueAndReadsItBack()
    {
        string sub = $@"Software\Vacuon\Testes\{Guid.NewGuid():N}";
        string journalPath = Path.Combine(Path.GetTempPath(), $"vacuon-{Guid.NewGuid():N}.json");
        AiComponent component = Scratch(sub);

        try
        {
            var sw = new AiComponentSwitch(new PolicyJournal(journalPath));
            SwitchResult result = sw.TurnOff(component);

            Assert.Equal(SwitchOutcome.Applied, result.Outcome);
            Assert.Null(result.PreviousValue);

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sub);
            Assert.Equal(1, key!.GetValue("TesteDoVacuon"));

            // Second call changes nothing and says so, instead of writing again.
            Assert.Equal(SwitchOutcome.NoChange, sw.TurnOff(component).Outcome);
        }
        finally
        {
            Cleanup(sub, journalPath);
        }
    }

    [Fact]
    public void Undo_DeletesAValueThatDidNotExist_RatherThanWritingZero()
    {
        // Writing 0 would leave an explicit "allow" where Windows had its own default —
        // a third state that is neither before nor after.
        string sub = $@"Software\Vacuon\Testes\{Guid.NewGuid():N}";
        string journalPath = Path.Combine(Path.GetTempPath(), $"vacuon-{Guid.NewGuid():N}.json");
        AiComponent component = Scratch(sub);

        try
        {
            var sw = new AiComponentSwitch(new PolicyJournal(journalPath));
            sw.TurnOff(component);

            Assert.Equal(SwitchOutcome.Applied, sw.Undo(component).Outcome);

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sub);
            Assert.Null(key?.GetValue("TesteDoVacuon"));
        }
        finally
        {
            Cleanup(sub, journalPath);
        }
    }

    [Fact]
    public void Undo_RestoresTheValueThatWasThereBefore()
    {
        string sub = $@"Software\Vacuon\Testes\{Guid.NewGuid():N}";
        string journalPath = Path.Combine(Path.GetTempPath(), $"vacuon-{Guid.NewGuid():N}.json");
        AiComponent component = Scratch(sub);

        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(sub))
                key.SetValue("TesteDoVacuon", 7, RegistryValueKind.DWord);

            var sw = new AiComponentSwitch(new PolicyJournal(journalPath));
            Assert.Equal(SwitchOutcome.Applied, sw.TurnOff(component).Outcome);
            Assert.Equal(SwitchOutcome.Applied, sw.Undo(component).Outcome);

            using RegistryKey? after = Registry.CurrentUser.OpenSubKey(sub);
            Assert.Equal(7, after!.GetValue("TesteDoVacuon"));
        }
        finally
        {
            Cleanup(sub, journalPath);
        }
    }

    [Fact]
    public void Undo_WithoutAJournalEntry_LeavesTheMachineAlone()
    {
        string sub = $@"Software\Vacuon\Testes\{Guid.NewGuid():N}";
        string journalPath = Path.Combine(Path.GetTempPath(), $"vacuon-{Guid.NewGuid():N}.json");

        try
        {
            var sw = new AiComponentSwitch(new PolicyJournal(journalPath));

            // Vacuon never touched this one, so it has no previous state to claim knowledge of.
            Assert.Equal(SwitchOutcome.NoChange, sw.Undo(Scratch(sub)).Outcome);
        }
        finally
        {
            Cleanup(sub, journalPath);
        }
    }

    [Fact]
    public void ReportOnlyComponents_RefuseToBeSwitched()
    {
        var sw = new AiComponentSwitch(new PolicyJournal(
            Path.Combine(Path.GetTempPath(), $"vacuon-{Guid.NewGuid():N}.json")));

        AiComponent package = AiComponentCatalog.All.First(c => c.Control == ControlKind.Package);

        Assert.Equal(SwitchOutcome.NotActionable, sw.TurnOff(package).Outcome);
        Assert.Equal(SwitchOutcome.NotActionable, sw.Undo(package).Outcome);
    }

    private static void Cleanup(string subKey, string journalPath)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch (Exception) { }
        try { if (File.Exists(journalPath)) File.Delete(journalPath); } catch (IOException) { }
    }
}

public class AiComponentScannerTests
{
    [Fact]
    public void Scan_ReportsEveryCatalogueEntryAndTouchesNothing()
    {
        AiScanReport report = new AiComponentScanner().Scan();

        Assert.Equal(AiComponentCatalog.All.Count, report.Items.Count);

        foreach (AiComponentStatus status in report.Items)
        {
            // Measured, never estimated: zero is a legitimate answer and must stay zero.
            Assert.True(status.MeasuredBytes >= 0);
            Assert.True(status.RunningProcesses >= 0);
            if (status.RunningProcesses == 0) Assert.Equal(0, status.MeasuredBytes);
        }
    }
}
