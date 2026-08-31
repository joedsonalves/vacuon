using Vacuon.Core;
using Vacuon.Core.Update;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Reading winget's answer. Every literal here was copied out of a real run on a Windows
/// whose display language is Portuguese — which is the point: the column headers are
/// translated and the package id is not.
/// </summary>
public class UpdateCheckTests
{
    private const string ListedOutput = """
Nome   ID                  Versão Origem
-----------------------------------------
Vacuon Joedsonalves.Vacuon 0.5.0  winget
""";

    private const string NothingNewer = """
Nenhuma atualização disponível foi encontrada.
Nenhuma versão de pacote mais recente está disponível nas origens configuradas.
""";

    // A real upgrade row, taken from the same machine's full `winget upgrade` listing and
    // with the package swapped for this one. The shape is measured; the name is not.
    private const string UpgradeRow = """
Nome                    ID                                 Versão               Disponível          Origem
-----------------------------------------------------------------------------------------------------------
Vacuon                  Joedsonalves.Vacuon                0.5.0                0.6.0               winget
""";

    [Fact]
    public void AListingRowGivesTheInstalledVersion()
    {
        string[] fields = UpdateCheck.FieldsAfterId(ListedOutput);

        Assert.Equal("0.5.0", fields[0]);
        Assert.Equal("winget", fields[1]);
    }

    [Fact]
    public void AListingRowIsNotAnOffer()
    {
        // Two fields after the id: the version and the source. Reading the source as an
        // available version would have the app announcing an update to "winget".
        Assert.Null(UpdateCheck.AvailableIn(ListedOutput));
    }

    [Fact]
    public void AnUpgradeRowGivesTheVersionOnOffer()
    {
        Assert.Equal("0.6.0", UpdateCheck.AvailableIn(UpgradeRow));
    }

    [Fact]
    public void NothingNewerIsNotAnOffer()
    {
        Assert.Null(UpdateCheck.AvailableIn(NothingNewer));
        Assert.Empty(UpdateCheck.FieldsAfterId(NothingNewer));
    }

    [Fact]
    public void TheNameColumnCanCarryDigitsWithoutBeingMistakenForAVersion()
    {
        // Measured: winget prints names like "AdsPower Global 7.12.29" and "Antigravity
        // 2.0.6". Only what comes after the id is read, for exactly this reason.
        string row = "Vacuon 0.5.0 portable   Joedsonalves.Vacuon   0.5.0   0.6.0   winget";

        Assert.Equal("0.6.0", UpdateCheck.AvailableIn(row));
    }

    [Theory]
    [InlineData("0.6.0", "0.5.0", 1)]
    [InlineData("0.5.0", "0.6.0", -1)]
    [InlineData("0.6.0", "0.6.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void VersionsCompareByTheirNumbers_NotTheirText(string left, string right, int expected)
    {
        // "0.10.0" sorts before "0.9.0" as text, and an app that compared them that way would
        // announce a downgrade as an update.
        Assert.Equal(expected, Math.Sign(UpdateStatus.Compare(left, right)));
    }

    [Fact]
    public void ARunningBuildAheadOfTheSourceIsNotBehindIt()
    {
        // The window between a release going out and its manifest being merged. During it
        // winget answers "nothing newer" and it is telling the truth about the source.
        var status = new UpdateStatus(UpdateOutcome.UpToDate, "0.6.0", "0.5.0", null);

        Assert.True(status.RunningIsAhead);
    }

    [Fact]
    public void TheUpgradeCommandNamesThePackageExactly()
    {
        // It is shown on screen so somebody can run it themselves, so it has to be the
        // command that actually works — id and --exact included.
        Assert.Contains(UpdateCheck.PackageId, UpdateCheck.UpgradeCommand);
        Assert.Contains("--exact", UpdateCheck.UpgradeCommand);
    }

    [Fact]
    public void TheVersionItReportsAsRunningIsTheOneThisBuildIs()
    {
        Assert.Equal(AppInfo.Version, new UpdateStatus(UpdateOutcome.UpToDate, AppInfo.Version, null, null).Running);
    }
}
