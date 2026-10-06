using Vacuon.Core.Optimization;
using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

public class StartupApprovedTests
{
    [Theory]
    [InlineData(0x02, false)]   // ligado, o valor mais comum
    [InlineData(0x06, false)]   // ligado tambem — e o que o SecurityHealth traz
    [InlineData(0x0A, false)]
    [InlineData(0x03, true)]    // desligado pelo usuario
    [InlineData(0x07, true)]
    public void State_IsTheLowBit_NotAnEqualityCheckAgainstTwo(byte first, bool expectedDisabled)
    {
        // Comparar com 2 chamaria o SecurityHealth (0x06) de desligado, que e falso.
        Assert.Equal(expectedDisabled, StartupScanner.IsDisabled([first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Payload_IsTwelveBytesAndRoundTripsThroughTheReader()
    {
        byte[] off = StartupSwitch.Payload(enabled: false);
        byte[] on = StartupSwitch.Payload(enabled: true);

        Assert.Equal(12, off.Length);
        Assert.Equal(12, on.Length);

        Assert.True(StartupScanner.IsDisabled(off));
        Assert.False(StartupScanner.IsDisabled(on));

        // Um item ligado carrega timestamp zerado, como o Gerenciador de Tarefas escreve.
        Assert.Equal(0, BitConverter.ToInt64(on, 4));
        Assert.True(BitConverter.ToInt64(off, 4) > 0);
    }

    [Fact]
    public void EmptyPayload_ReadsAsEnabledRatherThanThrowing()
    {
        Assert.False(StartupScanner.IsDisabled([]));
    }
}

public class StartupCommandParsingTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --background", @"C:\Program Files\App\app.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c algo", @"C:\Windows\System32\cmd.exe")]
    [InlineData("C:\\Tools\\semextensao --flag", @"C:\Tools\semextensao")]
    public void ExtractExecutable_HandlesQuotedAndUnquoted(string command, string expected)
    {
        Assert.Equal(expected, StartupScanner.ExtractExecutable(command));
    }

    [Fact]
    public void ExtractExecutable_PrefersQuotesBecauseSpacesCannotBeGuessed()
    {
        // Sem as aspas, cortar no primeiro espaco daria "C:\Program" — e a memoria de outro
        // processo acabaria creditada a esta entrada.
        Assert.Equal(@"C:\Program Files\Foo Bar\x.exe",
                     StartupScanner.ExtractExecutable("\"C:\\Program Files\\Foo Bar\\x.exe\" -q"));
    }

    [Fact]
    public void ExtractExecutable_ReturnsNullOnEmpty()
    {
        Assert.Null(StartupScanner.ExtractExecutable("   "));
    }
}

public class StartupTargetTests : IDisposable
{
    // A space in the name on purpose: every real Startup folder has one, in "Start Menu".
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-startup tests " + Guid.NewGuid().ToString("N"));

    public StartupTargetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AShortcutInAFolderWithASpace_PointsAtItsTarget_NotAtHalfItsOwnPath()
    {
        // Measured before this was written: all three shortcuts in my Startup folders were
        // shown as pointing at a file that does not exist. The item's own path went through
        // the command-line parser, which cut it at the space in "Start Menu".
        string program = Path.Combine(_root, "Some App", "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllBytes(program, [0x4D, 0x5A]);

        string shortcut = Path.Combine(_root, "Start Menu", "App.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        Assert.True(ShellLink.Write(shortcut, program));

        Assert.Equal(program, StartupScanner.FolderItemTarget(shortcut), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AShortcutWhoseProgramWasRemoved_StillNamesIt_SoItCanBeCalledMissing()
    {
        string program = Path.Combine(_root, "Gone", "gone.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllBytes(program, [0x4D, 0x5A]);

        string shortcut = Path.Combine(_root, "Gone.lnk");
        Assert.True(ShellLink.Write(shortcut, program));
        File.Delete(program);

        string? target = StartupScanner.FolderItemTarget(shortcut);
        Assert.Equal(program, target, StringComparer.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void AnythingElseInTheStartupFolder_LaunchesItself()
    {
        string script = Path.Combine(_root, "run me.cmd");
        File.WriteAllText(script, "@echo off");

        Assert.Equal(script, StartupScanner.FolderItemTarget(script));
    }

    [Fact]
    public void AnUnquotedPathWithSpaces_IsWalkedTheWayWindowsWalksIt()
    {
        const string docker = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
        bool Exists(string path) => string.Equals(path, docker, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(docker, StartupScanner.TargetOf(docker + " --minimized", Exists));
    }

    [Fact]
    public void EnvironmentVariablesAreExpanded()
    {
        // A REG_SZ value keeps its %...%; checking it as written found nothing at "%SystemRoot%".
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "app.exe");
        bool Exists(string path) => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expected, StartupScanner.TargetOf("\"%SystemRoot%\\app.exe\" -x", Exists),
                     StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABareNameIsLookedForWhereWindowsLooks()
    {
        // "rundll32.exe ..." was checked against the current folder, and found missing.
        string? target = StartupScanner.TargetOf("rundll32.exe C:\\algum.dll,Entrada");

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), target,
                     StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyAPathThatCanBeNamedExactly_IsEverCalledMissing()
    {
        static bool Nothing(string _) => false;

        // A path ending in .exe that is not there: missing, and named.
        Assert.Equal(@"C:\Old App\tool.exe", StartupScanner.TargetOf(@"C:\Old App\tool.exe /quiet", Nothing));

        // One word, rooted: that file.
        Assert.Equal(@"C:\Tools\script.bat", StartupScanner.TargetOf(@"C:\Tools\script.bat", Nothing));

        // Spaces, no extension, nothing on the disk: which part is the program is a guess.
        Assert.Null(StartupScanner.TargetOf(@"C:\Some Tool\launcher --flag", Nothing));

        // A bare name found nowhere: Windows may know it some other way.
        Assert.Null(StartupScanner.TargetOf("ferramenta-que-nao-existe.exe /x", Nothing));
    }
}

public class StartupScannerTests
{
    [Fact]
    public void Scan_ReadsTheListWithoutChangingIt()
    {
        StartupReport report = new StartupScanner().Scan();

        Assert.True(report.EnabledCount <= report.Entries.Count);

        foreach (StartupEntry e in report.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Command));

            // Medido, nunca projetado: sem processo, o custo e zero.
            Assert.True(e.MeasuredBytes >= 0);
            if (e.RunningProcesses == 0) Assert.Equal(0, e.MeasuredBytes);
        }
    }

    [Fact]
    public void DisabledEntries_AreCreditedWithNothing()
    {
        // A primeira versao casava processo pelo NOME e creditava 13 GiB de Opera - 54
        // processos abertos a mao - a uma entrada que estava desligada e nao iniciou nada.
        // Entrada desligada nao inicia processo nenhum, entao nao pode somar nada.
        StartupReport report = new StartupScanner().Scan();

        foreach (StartupEntry e in report.Entries)
        {
            if (e.IsEnabled) continue;

            Assert.Equal(0, e.MeasuredBytes);
            Assert.Equal(0, e.RunningProcesses);
        }
    }

    [Fact]
    public void Total_NeverExceedsTheSumOfTheEnabledEntries()
    {
        StartupReport report = new StartupScanner().Scan();

        long fromEnabled = 0;
        foreach (StartupEntry e in report.Entries)
            if (e.IsEnabled) fromEnabled += e.MeasuredBytes;

        Assert.Equal(fromEnabled, report.MeasuredBytes);
    }
}
