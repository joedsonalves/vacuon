using Vacuon.Core.Index;
using Vacuon.Core.Security;
using Xunit;

namespace Vacuon.Core.Tests;

public class SuspiciousFileAnalyzerTests
{
    private static VolumeIndex BuildIndex(params (string Name, uint Parent, EntryFlags Flags, long Ads)[] files)
    {
        var names = new NameBlob(512);
        var entries = new FileEntry[64];

        entries[5] = new FileEntry
        {
            RecordNumber = 5, ParentIndex = 5,
            NameOffset = names.Append("."), NameLength = 1,
            Flags = EntryFlags.Directory,
        };

        entries[6] = new FileEntry
        {
            RecordNumber = 6, ParentIndex = 5,
            NameOffset = names.Append("Users"), NameLength = 5,
            Flags = EntryFlags.Directory,
        };
        entries[7] = new FileEntry
        {
            RecordNumber = 7, ParentIndex = 6,
            NameOffset = names.Append("joao"), NameLength = 4,
            Flags = EntryFlags.Directory,
        };
        entries[8] = new FileEntry
        {
            RecordNumber = 8, ParentIndex = 7,
            NameOffset = names.Append("Downloads"), NameLength = 9,
            Flags = EntryFlags.Directory,
        };

        var adsBytes = new Dictionary<int, long>();
        int slot = 20;

        foreach ((string name, uint parent, EntryFlags flags, long ads) in files)
        {
            entries[slot] = new FileEntry
            {
                RecordNumber = (uint)slot,
                ParentIndex = parent,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = flags,
                LogicalSize = 1024,
                AllocatedSize = 4096,
                HardLinkCount = 1,
                LastWriteUtc = DateTime.UtcNow.ToFileTimeUtc(),
                CreatedUtc = DateTime.UtcNow.AddYears(-3).ToFileTimeUtc(),
            };

            if (ads > 0) adsBytes[slot] = ads;
            slot++;
        }

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1_000_000, 500_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft, adsBytes);
    }

    [Fact]
    public void Detects_DoubleExtension()
    {
        VolumeIndex index = BuildIndex(("nota_fiscal.pdf.exe", 8, EntryFlags.None, 0));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        SuspiciousFile item = Assert.Single(found);
        Assert.Equal(Suspicion.HighlySuspicious, item.Level);
        Assert.Contains("Extensão dupla", item.Reason);
    }

    [Fact]
    public void Detects_RightToLeftOverrideInName()
    {
        // "fatura‮gpj.exe" aparece na tela como "fatura exe.jpg".
        VolumeIndex index = BuildIndex(("fatura‮gpj.exe", 8, EntryFlags.None, 0));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        SuspiciousFile item = Assert.Single(found);
        Assert.Equal(Suspicion.HighlySuspicious, item.Level);
        Assert.Contains("RLO", item.Reason);
    }

    [Fact]
    public void Detects_HiddenExecutable()
    {
        VolumeIndex index = BuildIndex(("servico.exe", 8, EntryFlags.Hidden, 0));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        SuspiciousFile item = Assert.Single(found);
        Assert.True(item.Level >= Suspicion.Suspicious);
        Assert.Contains("oculto", item.Reason);
    }

    [Fact]
    public void Detects_ExecutableCarryingLargeAlternateDataStream()
    {
        VolumeIndex index = BuildIndex(("instalador.exe", 8, EntryFlags.HasAds, 1_048_576));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        SuspiciousFile item = Assert.Single(found);
        Assert.Contains("Alternate Data Stream", item.Reason);
    }

    [Fact]
    public void Detects_HighRiskExtensions()
    {
        VolumeIndex index = BuildIndex(("protetor.scr", 8, EntryFlags.None, 0));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        Assert.Single(found);
    }

    [Fact]
    public void Ignores_OrdinaryFiles()
    {
        // Falso positivo é o que mata a utilidade da lista. Arquivo comum não entra.
        VolumeIndex index = BuildIndex(
            ("relatorio.pdf", 8, EntryFlags.None, 0),
            ("video.mp4", 8, EntryFlags.None, 0),
            ("setup.exe", 8, EntryFlags.None, 0),
            ("biblioteca.dll", 8, EntryFlags.None, 0));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        Assert.Empty(found);
    }

    [Theory]
    [InlineData(@"C:\projeto\node_modules\es-iterator-helpers\test\Iterator.zip.js")]
    [InlineData(@"C:\Users\joao\.bun\install\cache\es-iterator-helpers@1.2.1\test\Iterator.zip.js")]
    [InlineData(@"C:\app\.venv\Lib\site-packages\pacote\teste.zip.js")]
    public void IgnoresDoubleExtensionInsideDependencyTrees(string path)
    {
        // Caso real: o pacote npm es-iterator-helpers traz um Iterator.zip.js (o método
        // Iterator.zip). Numa máquina de desenvolvedor isso rendia dezenas de alarmes.
        Assert.True(SuspiciousFileAnalyzer.IsInsideDependencyFolder(path));
    }

    [Theory]
    [InlineData(@"C:\Users\joao\Downloads\nota_fiscal.pdf.exe")]
    [InlineData(@"C:\Users\joao\Desktop\boleto.jpg.scr")]
    public void StillFlagsDoubleExtensionOutsideDependencyTrees(string path)
    {
        Assert.False(SuspiciousFileAnalyzer.IsInsideDependencyFolder(path));
    }

    [Theory]
    [InlineData("relatorio.pdf.lnk")]
    [InlineData("planilha.csv.LNK")]
    [InlineData("curriculo.doc.lnk")]
    public void DoesNotFlagShortcutsAsDoubleExtension(string name)
    {
        // "documento.pdf.lnk" é exatamente como o Windows nomeia um atalho para
        // "documento.pdf". A pasta Recentes é cheia deles; incluir .lnk na regra de
        // extensão dupla marcava dezenas de atalhos normais em qualquer máquina.
        VolumeIndex index = BuildIndex((name, 8, EntryFlags.None, 0));

        Assert.Empty(new SuspiciousFileAnalyzer().Analyze(index));
    }

    [Theory]
    [InlineData(@"C:\Users\joao\AppData\Roaming\Microsoft\Windows\Recent\algo.pdf.lnk")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\app.lnk")]
    public void RecognizesSystemGeneratedFolders(string path)
    {
        Assert.True(SuspiciousFileAnalyzer.IsSystemGenerated(path));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\Bubbles.scr")]
    [InlineData(@"C:\Windows\System32\Ribbons.scr")]
    [InlineData(@"C:\Windows\SysWOW64\algo.scr")]
    public void RecognizesFilesShippedWithWindows(string path)
    {
        // Bubbles.scr e Ribbons.scr são os protetores de tela do sistema. Marcá-los
        // como phishing é ruído garantido em toda máquina.
        Assert.True(SuspiciousFileAnalyzer.IsShippedWithWindows(path));
    }

    [Fact]
    public void ScreensaverOutsideWindowsIsStillFlagged()
    {
        VolumeIndex index = BuildIndex(("protetor.scr", 8, EntryFlags.None, 0));
        Assert.Single(new SuspiciousFileAnalyzer().Analyze(index));
    }

    [Fact]
    public void StillFlagsRealDoubleExtension()
    {
        // A correção do .lnk não pode enfraquecer o caso que importa.
        VolumeIndex index = BuildIndex(("fatura.pdf.exe", 8, EntryFlags.None, 0));

        SuspiciousFile item = Assert.Single(new SuspiciousFileAnalyzer().Analyze(index));
        Assert.Equal(Suspicion.HighlySuspicious, item.Level);
    }

    [Fact]
    public void MarksTheEntryInTheIndex()
    {
        VolumeIndex index = BuildIndex(("boleto.jpg.scr", 8, EntryFlags.None, 0));

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(index);

        SuspiciousFile item = Assert.Single(found);
        Assert.True((index.Entries[item.Index].Flags & EntryFlags.Suspicious) != 0);
    }
}
