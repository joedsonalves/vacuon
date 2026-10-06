using System.Diagnostics;
using Vacuon.Core.Optimization;
using Xunit;

namespace Vacuon.Core.Tests;

public class ProtectedProcessTests
{
    [Theory]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("lsass")]
    [InlineData("services")]
    [InlineData("winlogon")]
    [InlineData("smss")]
    [InlineData("System")]
    [InlineData("Registry")]
    [InlineData("dwm")]
    [InlineData("svchost")]
    [InlineData("Memory Compression")]
    public void CriticalProcesses_AreRefused(string name)
    {
        // Matar qualquer um destes derruba a maquina na hora, sem chance de salvar nada.
        // Mesma regra do ProtectedPaths: sem override, sem "modo avancado", sem checkbox.
        Assert.True(ProtectedProcesses.IsProtected(name));
    }

    [Fact]
    public void Vacuon_RefusesToCloseItself()
    {
        Assert.True(ProtectedProcesses.IsProtected("Vacuon"));
        Assert.True(ProtectedProcesses.IsProtected("vacuon"));
    }

    [Fact]
    public void ProtectionSurvivesTheGroupedDisplayName()
    {
        // A lista agrupa por nome e mostra a contagem junto: "svchost (112)". Se a protecao
        // olhasse a string inteira, o nome agrupado passaria batido.
        Assert.True(ProtectedProcesses.IsProtected("svchost (112)"));
        Assert.True(ProtectedProcesses.IsProtected("csrss (2)"));
    }

    [Theory]
    [InlineData("opera")]
    [InlineData("notepad")]
    [InlineData("opera (54)")]
    public void OrdinaryProgramsAreNotProtected(string name)
    {
        Assert.False(ProtectedProcesses.IsProtected(name));
    }

    [Fact]
    public void CloseByName_RefusesAProtectedProcessWithoutTouchingIt()
    {
        TerminateResult result = new ProcessTerminator().CloseByName("csrss");

        Assert.Equal(TerminateOutcome.Protected, result.Outcome);
        Assert.Equal(0, result.ClosedCount);
        Assert.Equal(0, result.AttemptedCount);
    }

    [Fact]
    public void CloseByName_ReportsNotFoundForSomethingThatIsNotRunning()
    {
        TerminateResult result = new ProcessTerminator()
            .CloseByName($"nao-existe-{Guid.NewGuid():N}");

        Assert.Equal(TerminateOutcome.NotFound, result.Outcome);
    }
}

/// <summary>
/// Closing really closes things, so this runs real processes: copies of cmd and ping under
/// names nothing else on the machine has, so closing "by name" can only ever reach these.
/// </summary>
public class ProcessTerminatorTreeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vacuon-close-tests-" + Guid.NewGuid().ToString("N"));

    public ProcessTerminatorTreeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Closing_TakesTheProcessesWithThatName_NotEverythingTheyStarted()
    {
        // ⚠️ The kill took each process's whole tree. The confirmation names the processes in
        // the row and nothing else — and Explorer, which is not protected, is the parent of
        // what the Start menu and the desktop launch: closing it from the memory panel would
        // have taken every one of those programs with it, unsaved work and all.
        string tag = Guid.NewGuid().ToString("N")[..8];
        string parentName = "vacuonparent" + tag;
        string childName = "vacuonchild" + tag;

        string parent = Path.Combine(_dir, parentName + ".exe");
        string child = Path.Combine(_dir, childName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), parent);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), child);

        using Process started = Process.Start(new ProcessStartInfo(parent, $"/c \"{child}\" -n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        Process? kid = null;
        for (int i = 0; i < 100 && kid is null; i++)
        {
            kid = Process.GetProcessesByName(childName).FirstOrDefault();
            if (kid is null) Thread.Sleep(100);
        }

        Assert.NotNull(kid);

        try
        {
            TerminateResult result = new ProcessTerminator().CloseByName(parentName);

            Assert.Equal(TerminateOutcome.Closed, result.Outcome);
            Assert.True(started.HasExited);

            kid.Refresh();
            Assert.False(kid.HasExited, "the child, under another name, went down with its parent");
        }
        finally
        {
            try
            {
                if (!kid.HasExited) kid.Kill();
                kid.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

            kid.Dispose();
        }
    }
}

public class TerminateResultTests
{
    [Fact]
    public void HeldAndReclaimed_AreReportedAsTwoSeparateFacts()
    {
        // Rara vez batem: o Windows devolve as paginas, mas outros processos e o cache pegam
        // parte de volta no mesmo segundo. Mostrar so o numero mais bonito seria a aritmetica
        // que este app existe para nao fazer.
        var result = new TerminateResult
        {
            Name = "algo",
            Outcome = TerminateOutcome.Closed,
            HeldBytes = 2_000,
            AvailableBeforeBytes = 10_000,
            AvailableAfterBytes = 11_400,
        };

        Assert.Equal(2_000, result.HeldBytes);
        Assert.Equal(1_400, result.AvailableRoseBytes);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AvailableCanRiseLessThanWasHeld_AndThatIsNotAnError()
    {
        var result = new TerminateResult
        {
            Name = "algo",
            Outcome = TerminateOutcome.Closed,
            HeldBytes = 5_000,
            AvailableBeforeBytes = 1_000,
            AvailableAfterBytes = 1_100,
        };

        Assert.True(result.AvailableRoseBytes < result.HeldBytes);
        Assert.True(result.Succeeded);
    }
}
