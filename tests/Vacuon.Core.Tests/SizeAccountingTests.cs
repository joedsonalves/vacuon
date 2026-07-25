using Vacuon.Core.Index;
using Vacuon.Core.Tests.Fixtures;
using Vacuon.Native.Ntfs;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Regressions from the first elevated run on a real 476 GiB volume, which reported
/// "758 GiB on disk". Both defects came from reading a field whose meaning is close to, but
/// not the same as, what its name suggests.
/// </summary>
public class SizeAccountingTests
{
    private const int Sector = MftRecordBuilder.SectorSize;

    private static ParsedMftRecord ParseOf(MftRecordBuilder builder, uint number)
    {
        byte[] record = builder.Build();
        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        return MftRecordParser.Parse(record, number);
    }

    // ==================== ADS: sparse streams occupy nothing ====================

    [Fact]
    public void SparseNamedStream_ContributesNothingToOccupiedSpace()
    {
        // This is $BadClus:$Bad, which every NTFS volume carries: a named stream sized to
        // the WHOLE VOLUME that allocates not one cluster. Counting its logical size added
        // 476 GiB of imaginary data to a 476 GiB disk.
        //
        // Note what 0x28 says here — the full volume — and that the answer must still be
        // zero. Reading 0x28 instead of 0x40 reproduces the bug with the logical-size
        // fallback already removed.
        var builder = new MftRecordBuilder { RecordNumber = 8 };
        builder.WithFileName("$BadClus", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(logicalSize: 0, allocatedSize: 0);
        builder.WithNonResidentData(logicalSize: 510_950_866_944, allocatedSize: 510_950_866_944,
                                    streamName: "$Bad", flags: NtfsLayout.AttrFlagSparse,
                                    compressedSize: 0);

        ParsedMftRecord parsed = ParseOf(builder, 8);

        Assert.True(parsed.HasAds);
        Assert.Equal(0, parsed.AdsBytes);
    }

    [Fact]
    public void PartiallyAllocatedSparseStream_CountsOnlyWhatIsAllocated()
    {
        // $UsnJrnl:$J is the other one: a huge logical range with only the tail on disk.
        var builder = new MftRecordBuilder { RecordNumber = 9 };
        builder.WithFileName("$UsnJrnl", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(logicalSize: 99_723_378_688, allocatedSize: 99_723_378_688,
                                    streamName: "$J", flags: NtfsLayout.AttrFlagSparse,
                                    compressedSize: 33_554_432);

        ParsedMftRecord parsed = ParseOf(builder, 9);

        Assert.Equal(33_554_432, parsed.AdsBytes);
    }

    [Fact]
    public void CompressedFile_ReportsWhatItOccupiesNotWhatItWouldOccupy()
    {
        // A compressed attribute carries the run space at 0x28 and the real footprint at
        // 0x40. Counting 0x28 makes compression look like it saved nothing, and inflates
        // the volume total by the entire saving.
        var builder = new MftRecordBuilder { RecordNumber = 15 };
        builder.WithFileName("registro.log", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(logicalSize: 104_857_600, allocatedSize: 104_857_600,
                                    flags: NtfsLayout.AttrFlagCompressed,
                                    compressedSize: 12_582_912);

        ParsedMftRecord parsed = ParseOf(builder, 15);

        Assert.True(parsed.IsCompressed);
        Assert.Equal(104_857_600, parsed.LogicalSize);
        Assert.Equal(12_582_912, parsed.AllocatedSize);
    }

    [Fact]
    public void UncompressedFile_StillReadsTheOrdinaryAllocatedSizeField()
    {
        // Without the flag there is no field at 0x40 — it is data runs. Reading it anyway
        // would turn every normal file's size into garbage.
        var builder = new MftRecordBuilder { RecordNumber = 16 };
        builder.WithFileName("normal.bin", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(logicalSize: 104_857_600, allocatedSize: 104_861_696);

        ParsedMftRecord parsed = ParseOf(builder, 16);

        Assert.False(parsed.IsCompressed);
        Assert.False(parsed.IsSparse);
        Assert.Equal(104_861_696, parsed.AllocatedSize);
    }

    [Fact]
    public void ResidentNamedStream_CostsNoCluster()
    {
        // A Zone.Identifier tag lives inside the MFT record. The record's cost is accounted
        // for separately, so charging the stream again would double-count it — the same
        // treatment the unnamed resident $DATA already gets.
        var builder = new MftRecordBuilder { RecordNumber = 10 };
        builder.WithFileName("baixado.exe", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(1_000_000, 1_003_520);
        builder.WithResidentData(new byte[26], streamName: "Zone.Identifier");

        ParsedMftRecord parsed = ParseOf(builder, 10);

        Assert.True(parsed.HasAds);
        Assert.Equal(0, parsed.AdsBytes);
        Assert.Equal(1_003_520, parsed.AllocatedSize); // o fluxo principal segue intacto
    }

    [Fact]
    public void OrdinaryNamedStream_StillCountsItsAllocation()
    {
        // The fix must not swing the other way: a real, non-sparse ADS occupies real space.
        var builder = new MftRecordBuilder { RecordNumber = 11 };
        builder.WithFileName("arquivo.dat", 5, NtfsNameType.Win32);
        builder.WithNonResidentData(1000, 4096);
        builder.WithNonResidentData(50_000, 53_248, streamName: "oculto");

        ParsedMftRecord parsed = ParseOf(builder, 11);

        Assert.Equal(53_248, parsed.AdsBytes);
    }

    // ==================== hardlinks: the 8.3 alias is not a link ====================

    [Fact]
    public void LongFileName_IsNotAHardlink()
    {
        // The defect that hid 217 GiB: NTFS gives any name that does not fit 8.3 a second
        // $FILE_NAME in the DOS namespace, and counts it in the record header's link count.
        // Three quarters of a real volume looked hardlinked, and hardlinked files are
        // deliberately charged to the disk only once — so they were charged zero.
        var builder = new MftRecordBuilder { RecordNumber = 12, StatedLinkCount = 2 };
        builder.WithFileName("VDEO0~1.MP4", 5, NtfsNameType.Dos);
        builder.WithFileName("VÍDEO 01 - PRONTO SEM LEGENDA.mp4", 5, NtfsNameType.Win32);

        ParsedMftRecord parsed = ParseOf(builder, 12);

        Assert.Equal(2, parsed.StatedLinkCount);   // what the header claims
        Assert.Equal(1, parsed.NameCount);         // what is actually true
    }

    [Fact]
    public void ShortFileName_NeedsNoAliasAndCountsOnce()
    {
        var builder = new MftRecordBuilder { RecordNumber = 13, StatedLinkCount = 1 };
        builder.WithFileName("PAGEFILE.SYS", 5, NtfsNameType.Win32AndDos);

        ParsedMftRecord parsed = ParseOf(builder, 13);

        Assert.Equal(1, parsed.NameCount);
    }

    [Fact]
    public void GenuineHardlinks_AreStillCounted()
    {
        // msedge.dll really is linked from several places. Undercounting here would bring
        // back the double-counting the hardlink rule exists to prevent.
        var builder = new MftRecordBuilder { RecordNumber = 14, StatedLinkCount = 4 };
        builder.WithFileName("msedge.dll", 100, NtfsNameType.Win32AndDos);
        builder.WithFileName("msedge.dll", 200, NtfsNameType.Win32AndDos);
        builder.WithFileName("MSEDGE~1.DLL", 300, NtfsNameType.Dos);
        builder.WithFileName("msedge.dll", 300, NtfsNameType.Win32);

        ParsedMftRecord parsed = ParseOf(builder, 14);

        Assert.Equal(3, parsed.NameCount);
    }

    // ==================== the cross-check that would have caught it ====================

    [Fact]
    public void Reconciliation_FlagsMeasuringMoreThanTheVolumeHolds()
    {
        // The exact shape of the bug report: 758 GiB measured, 377 GiB actually used.
        var check = new Reconciliation(813_596_508_160, 405_453_912_064, ScanStrategy.Mft);

        Assert.Equal(ReconciliationVerdict.Overcounted, check.Verdict);
        Assert.True(check.IsImpossible);
    }

    [Fact]
    public void Reconciliation_AcceptsTheHealthyCaseOfMeasuringSlightlyLess()
    {
        // Directory indexes and $LogFile occupy clusters that belong to no file, so landing
        // a few percent under the reported figure is correct, not a defect.
        var check = new Reconciliation(396_000_000_000, 405_453_912_064, ScanStrategy.Mft);

        Assert.Equal(ReconciliationVerdict.Agrees, check.Verdict);
        Assert.False(check.IsImpossible);
    }

    [Fact]
    public void Reconciliation_ExplainsAWideGapDifferentlyForAnUnprivilegedWalk()
    {
        // A traversal without Administrator cannot open every folder, so the same ratio
        // means "expected" there and "look into it" on the MFT path.
        var walk = new Reconciliation(200_000_000_000, 405_453_912_064, ScanStrategy.Win32Walk);
        var mft = new Reconciliation(200_000_000_000, 405_453_912_064, ScanStrategy.Mft);

        Assert.Equal(ReconciliationVerdict.Undercounted, walk.Verdict);
        Assert.Equal(ReconciliationVerdict.Undercounted, mft.Verdict);
        Assert.NotEqual(walk.Describe(), mft.Describe());
    }

    [Fact]
    public void Reconciliation_SaysNothingWhenThereIsNothingToCompare()
    {
        var check = new Reconciliation(0, 0, ScanStrategy.Mft);

        Assert.Equal(ReconciliationVerdict.Unknown, check.Verdict);
        Assert.False(check.IsImpossible);
    }
}
