using Vacuon.Core.Tests.Fixtures;
using Vacuon.Native.Ntfs;
using Xunit;

namespace Vacuon.Core.Tests;

public class MftRecordParserTests
{
    private const int Sector = MftRecordBuilder.SectorSize;

    [Fact]
    public void Fixups_RestoreTheBytesTheNtfsOverwrote()
    {
        // Um valor reconhecível no fim do primeiro setor: se os fixups não forem
        // aplicados, o parser lê o USN no lugar dele.
        var builder = new MftRecordBuilder { RecordNumber = 42 };
        builder.WithFileName("teste.txt", parentRecord: 5, NtfsNameType.Win32);
        byte[] record = builder.Build();

        // Antes dos fixups, o rabo de cada setor carrega o USN.
        Assert.Equal(0x1234, BitConverter.ToUInt16(record, Sector - 2));

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));

        ParsedMftRecord parsed = MftRecordParser.Parse(record, 42);
        Assert.True(parsed.IsValid);
        Assert.True(parsed.InUse);
        Assert.Equal("teste.txt", parsed.Name.ToString());
    }

    [Fact]
    public void Fixups_RejectRecordWithTornWrite()
    {
        byte[] record = new MftRecordBuilder()
            .WithFileName("a.txt", 5, NtfsNameType.Win32)
            .Build();

        // Corrompe o rabo do segundo setor: simula gravação interrompida.
        record[Sector * 2 - 1] = 0xFF;

        Assert.False(MftRecordParser.ApplyFixups(record, Sector));
    }

    [Fact]
    public void Parse_PrefersWin32NameOverDosShortName()
    {
        // Um arquivo com nome longo tem DOIS $FILE_NAME: o Win32 e o 8.3.
        // Contar os dois duplicaria a contagem de arquivos do volume inteiro.
        var builder = new MftRecordBuilder { RecordNumber = 100 };
        builder.WithFileName("PROGRA~1", 5, NtfsNameType.Dos);
        builder.WithFileName("Program Files", 5, NtfsNameType.Win32);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 100);

        Assert.Equal("Program Files", parsed.Name.ToString());
        Assert.Equal(NtfsNameType.Win32, parsed.NameType);
    }

    [Fact]
    public void Parse_ReadsNonResidentSizes()
    {
        var builder = new MftRecordBuilder { RecordNumber = 7 };
        builder.WithFileName("video.mkv", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(logicalSize: 9_412_233_216, allocatedSize: 9_412_235_264);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 7);

        Assert.True(parsed.HasData);
        Assert.Equal(9_412_233_216, parsed.LogicalSize);
        Assert.Equal(9_412_235_264, parsed.AllocatedSize);
    }

    [Fact]
    public void Parse_IgnoresSizesFromFragmentsBeyondTheFirst()
    {
        // Em arquivo muito fragmentado, só o fragmento com StartingVCN 0 carrega os
        // tamanhos reais. Ler o de um fragmento posterior devolveria zero.
        var builder = new MftRecordBuilder { RecordNumber = 8 };
        builder.WithFileName("fragmentado.bin", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(logicalSize: 0, allocatedSize: 0, startVcn: 4096);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 8);

        Assert.False(parsed.HasData);
    }

    [Fact]
    public void Parse_ResidentDataCountsAsLogicalSizeWithoutAllocation()
    {
        // Arquivo minúsculo mora dentro do próprio registro da MFT: ocupa 0 clusters.
        var builder = new MftRecordBuilder { RecordNumber = 9 };
        builder.WithFileName("nota.txt", 5, NtfsNameType.Win32);
        builder.WithResidentData(new byte[64]);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 9);

        Assert.True(parsed.HasData);
        Assert.Equal(64, parsed.LogicalSize);
        Assert.Equal(0, parsed.AllocatedSize);
    }

    [Fact]
    public void Parse_CountsNamedDataStreamAsAds()
    {
        var builder = new MftRecordBuilder { RecordNumber = 11 };
        builder.WithFileName("baixado.exe", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(1000, 4096);
        builder.WithNonResidentData(50_000, 53_248, streamName: "oculto");
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 11);

        Assert.True(parsed.HasData);
        Assert.Equal(1000, parsed.LogicalSize);
        Assert.True(parsed.HasAds);
        Assert.Equal(53_248, parsed.AdsBytes);
    }

    [Fact]
    public void Parse_DetectsExtensionRecordPointingAtItsBase()
    {
        var builder = new MftRecordBuilder { RecordNumber = 500, BaseRecordNumber = 120 };
        builder.WithNonResidentData(1_000_000, 1_003_520);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 500);

        Assert.Equal(120u, parsed.BaseRecordNumber);
    }

    [Fact]
    public void Parse_SkipsRecordThatIsNotInUse()
    {
        var builder = new MftRecordBuilder { RecordNumber = 13, InUse = false };
        builder.WithFileName("apagado.txt", 5, NtfsNameType.Win32);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 13);

        Assert.True(parsed.IsValid);
        Assert.False(parsed.InUse);
        Assert.False(parsed.HasName);
    }

    [Fact]
    public void Parse_RejectsBuffersWithoutFileMagic()
    {
        byte[] garbage = new byte[1024];
        "BAAD"u8.CopyTo(garbage);

        ParsedMftRecord parsed = MftRecordParser.Parse(garbage, 0);
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public void Parse_TranslatesTimestampsAndAttributes()
    {
        var created = new DateTime(2020, 3, 14, 10, 30, 0, DateTimeKind.Utc);
        var modified = new DateTime(2026, 7, 24, 22, 0, 0, DateTimeKind.Utc);
        var accessed = new DateTime(2026, 7, 24, 23, 0, 0, DateTimeKind.Utc);

        var builder = new MftRecordBuilder { RecordNumber = 21 };
        builder.WithStandardInformation(created, modified, accessed,
            NtfsFileAttributes.Hidden | NtfsFileAttributes.System);
        builder.WithFileName("oculto.dat", 5, NtfsNameType.Win32);
        byte[] record = builder.Build();

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        ParsedMftRecord parsed = MftRecordParser.Parse(record, 21);

        Assert.True(parsed.HasStandardInfo);
        Assert.Equal(created.ToFileTimeUtc(), parsed.CreationTime);
        Assert.Equal(modified.ToFileTimeUtc(), parsed.ModifiedTime);
        Assert.Equal(accessed.ToFileTimeUtc(), parsed.AccessTime);
        Assert.True(parsed.FileAttributes.HasFlag(NtfsFileAttributes.Hidden));
        Assert.True(parsed.FileAttributes.HasFlag(NtfsFileAttributes.System));
    }
}
