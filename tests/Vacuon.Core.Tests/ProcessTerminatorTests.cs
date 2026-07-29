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
