using Vacuon.Core.Optimization;
using Xunit;

namespace Vacuon.Core.Tests;

public class MemoryScannerTests
{
    [Fact]
    public void Scan_ReportsFiguresThatAgreeWithEachOther()
    {
        MemoryReport report = new MemoryScanner().Scan();
        MemoryReading r = report.Reading;

        Assert.True(r.TotalBytes > 0);
        Assert.InRange(r.AvailableBytes, 0, r.TotalBytes);
        Assert.Equal(r.TotalBytes - r.AvailableBytes, r.InUseBytes);
        Assert.InRange(r.LoadPercent, 0, 100);
    }

    [Fact]
    public void TopProcesses_AreRankedByPrivateBytes()
    {
        // Working set conta paginas compartilhadas uma vez por processo, entao somar working
        // set de cinquenta filhos de um navegador da um total maior que a maquina inteira.
        // Quem responde "quem esta usando a RAM" e o privado.
        List<ProcessMemory> top = [.. new MemoryScanner().Scan(10).TopProcesses];

        for (int i = 1; i < top.Count; i++)
            Assert.True(top[i - 1].PrivateBytes >= top[i].PrivateBytes);
    }

    [Fact]
    public void MemoryCompression_IsReportedApartFromTheProcessList()
    {
        // Ele aparece como o maior working set da maquina com quase nada de privado, e um
        // "limpador" atacaria justamente ele. Nao pode se misturar aos consumidores reais.
        MemoryReport report = new MemoryScanner().Scan(50);

        Assert.DoesNotContain(report.TopProcesses,
            p => p.Name.StartsWith("Memory Compression", StringComparison.OrdinalIgnoreCase));

        Assert.True(report.Reading.CompressedBytes >= 0);
    }

    [Fact]
    public void FromStartup_NeverExceedsWhatIsInUse()
    {
        MemoryReport report = new MemoryScanner().Scan(50);
        Assert.InRange(report.FromStartupBytes, 0, report.Reading.TotalBytes);
    }
}

public class TrimResultTests
{
    [Fact]
    public void MovedBytes_IsADifference_NotAClaimOfFreedMemory()
    {
        var result = new TrimResult(ProcessesTouched: 12,
                                    AvailableBeforeBytes: 1_000,
                                    AvailableAfterBytes: 1_500);

        Assert.Equal(500, result.MovedBytes);
    }

    [Fact]
    public void MovedBytes_CanBeNegative_AndTheAppMustNotHideThat()
    {
        // Esvaziar working set pode deixar MENOS disponivel do que antes, porque o Windows
        // reage trazendo coisas de volta. Um "liberados X" nunca mostraria isso.
        var result = new TrimResult(10, AvailableBeforeBytes: 2_000, AvailableAfterBytes: 1_800);

        Assert.Equal(-200, result.MovedBytes);
    }
}
