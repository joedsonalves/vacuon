using Vacuon.Core.Optimization;
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
