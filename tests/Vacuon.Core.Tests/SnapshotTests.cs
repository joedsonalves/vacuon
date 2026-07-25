using System.Buffers.Binary;
using System.Text;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Vacuon.Core.Localization;
using Vacuon.Native.Ntfs;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// A snapshot that loads as a plausible-looking index full of garbage is far worse than
/// one that refuses to load. These tests are mostly about the refusals.
/// </summary>
public class IndexSnapshotTests : IDisposable
{
    private readonly string _dir;

    public IndexSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vacuon-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Path_ => System.IO.Path.Combine(_dir, "test.vsnap");

    private static VolumeIndex Sample()
    {
        var names = new NameBlob(256);
        var entries = new FileEntry[16];

        void Set(int i, string name, uint parent, long size, bool dir = false)
        {
            entries[i] = new FileEntry
            {
                RecordNumber = (uint)i,
                ParentIndex = parent,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = dir ? EntryFlags.Directory : EntryFlags.None,
                LogicalSize = size,
                AllocatedSize = size,
                HardLinkCount = 1,
                LastWriteUtc = new DateTime(2026, 7, 25, 3, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(),
            };
        }

        Set(5, ".", 5, 0, dir: true);
        Set(6, "Videos", 5, 0, dir: true);
        Set(7, "render.mp4", 6, 9_000_000_000);
        Set(8, "nota.txt", 5, 42);

        var volume = new VolumeInfo('C', "Disco de teste", "NTFS", 500_000_000_000, 20_000_000_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft,
                               new Dictionary<int, long> { [8] = 1024 });
    }

    [Fact]
    public void RoundTripPreservesEverythingTheIndexNeeds()
    {
        VolumeIndex original = Sample();
        var mark = new JournalMark(0xDEADBEEFCAFEUL, 987_654_321);

        IndexSnapshot.Save(original, mark, Path_);
        LoadedSnapshot? loaded = IndexSnapshot.Load(Path_, expectedVolumeSerial: 0);

        Assert.NotNull(loaded);
        VolumeIndex copy = loaded!.Index;

        Assert.Equal(mark, loaded.Journal);
        Assert.Equal(original.Entries.Length, copy.Entries.Length);
        Assert.Equal(original.FileCount, copy.FileCount);
        Assert.Equal(original.DirectoryCount, copy.DirectoryCount);
        Assert.Equal(original.TotalLogicalBytes, copy.TotalLogicalBytes);

        // Names survive by offset into the blob, so a path is the real proof the two
        // parallel arrays were written and read in step.
        Assert.Equal(@"C:\Videos\render.mp4", copy.GetFullPath(7));
        Assert.Equal(@"C:\nota.txt", copy.GetFullPath(8));

        Assert.Equal(1024, copy.GetAdsBytes(8));
        Assert.Equal(original.Volume, copy.Volume);
        Assert.Equal(ScanStrategy.Mft, copy.Strategy);
    }

    [Fact]
    public void LargeSizesSurviveIntact()
    {
        // 9 GB does not fit in 32 bits; a width mistake anywhere in the format would
        // show up here and nowhere else.
        IndexSnapshot.Save(Sample(), JournalMark.None, Path_);
        LoadedSnapshot loaded = IndexSnapshot.Load(Path_, 0)!;

        Assert.Equal(9_000_000_000, loaded.Index.Entries[7].LogicalSize);
    }

    [Fact]
    public void MissingFileLoadsAsNull()
    {
        Assert.Null(IndexSnapshot.Load(System.IO.Path.Combine(_dir, "absent.vsnap"), 0));
    }

    [Fact]
    public void WrongMagicIsRefused()
    {
        File.WriteAllBytes(Path_, new byte[256]);
        Assert.Null(IndexSnapshot.Load(Path_, 0));
    }

    [Fact]
    public void WrongSchemaVersionIsRefused()
    {
        IndexSnapshot.Save(Sample(), JournalMark.None, Path_);

        byte[] bytes = File.ReadAllBytes(Path_);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 999);
        File.WriteAllBytes(Path_, bytes);

        // Reinterpreting an old layout as the current struct would produce an index that
        // looks fine and is nonsense. Refusing is the only safe answer.
        Assert.Null(IndexSnapshot.Load(Path_, 0));
    }

    [Fact]
    public void WrongEntrySizeIsRefused()
    {
        IndexSnapshot.Save(Sample(), JournalMark.None, Path_);

        byte[] bytes = File.ReadAllBytes(Path_);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 72);   // FileEntry grew
        File.WriteAllBytes(Path_, bytes);

        Assert.Null(IndexSnapshot.Load(Path_, 0));
    }

    [Fact]
    public void TruncatedFileIsRefused()
    {
        IndexSnapshot.Save(Sample(), JournalMark.None, Path_);

        byte[] bytes = File.ReadAllBytes(Path_);
        File.WriteAllBytes(Path_, bytes[..(bytes.Length / 2)]);

        Assert.Null(IndexSnapshot.Load(Path_, 0));
    }

    [Fact]
    public void AbsurdCountsAreRefused()
    {
        IndexSnapshot.Save(Sample(), JournalMark.None, Path_);

        byte[] bytes = File.ReadAllBytes(Path_);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), int.MaxValue);
        File.WriteAllBytes(Path_, bytes);

        // Without the sanity bound this would try to allocate 137 GB of FileEntry.
        Assert.Null(IndexSnapshot.Load(Path_, 0));
    }

    [Fact]
    public void SaveIsAtomicAndLeavesNoTemporaryBehind()
    {
        IndexSnapshot.Save(Sample(), JournalMark.None, Path_);

        Assert.True(File.Exists(Path_));
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public void SaveOverwritesAnExistingSnapshot()
    {
        IndexSnapshot.Save(Sample(), new JournalMark(1, 100), Path_);
        IndexSnapshot.Save(Sample(), new JournalMark(1, 200), Path_);

        Assert.Equal(200, IndexSnapshot.Load(Path_, 0)!.Journal.LastUsn);
    }

    [Fact]
    public void PathIsKeyedByVolumeSerialNotDriveLetter()
    {
        // Drive letters get reassigned. Reading D:'s index as E: would be worse than
        // having no snapshot at all.
        string a = IndexSnapshot.PathFor(0x1234ABCD, _dir);
        string b = IndexSnapshot.PathFor(0x9999FFFF, _dir);

        Assert.NotEqual(a, b);
        Assert.Contains("1234ABCD", a, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JournalMarkIsOnlyUsableWhenBothHalvesArePresent()
    {
        Assert.False(JournalMark.None.IsUsable);
        Assert.False(new JournalMark(0, 500).IsUsable);
        Assert.False(new JournalMark(42, 0).IsUsable);
        Assert.True(new JournalMark(42, 500).IsUsable);
    }
}

public class SnapshotDescriptionTests
{
    [Theory]
    [InlineData(IncrementalRefusal.NoSnapshot)]
    [InlineData(IncrementalRefusal.UnusableSnapshot)]
    [InlineData(IncrementalRefusal.NoJournal)]
    [InlineData(IncrementalRefusal.JournalReplaced)]
    [InlineData(IncrementalRefusal.JournalWrapped)]
    [InlineData(IncrementalRefusal.NeedsElevation)]
    public void EveryRefusalHasItsOwnExplanation(IncrementalRefusal refusal)
    {
        string text = SnapshotDescription.Refusal(refusal);

        // A missing key renders as "[key]" — that would ship a placeholder to the user.
        Assert.DoesNotContain("[", text, StringComparison.Ordinal);
        Assert.NotEmpty(text);
    }

    [Fact]
    public void RefusalsDoNotCollapseIntoTheSameSentence()
    {
        // "no snapshot yet" and "the journal was recreated" lead to different
        // conclusions; only one of them is something the user can act on.
        string[] texts =
        [
            SnapshotDescription.Refusal(IncrementalRefusal.NoSnapshot),
            SnapshotDescription.Refusal(IncrementalRefusal.NoJournal),
            SnapshotDescription.Refusal(IncrementalRefusal.JournalReplaced),
            SnapshotDescription.Refusal(IncrementalRefusal.JournalWrapped),
            SnapshotDescription.Refusal(IncrementalRefusal.NeedsElevation),
        ];

        Assert.Equal(texts.Length, texts.Distinct().Count());
    }

    [Fact]
    public void SuccessNeedsAnIndexNotJustTheAbsenceOfARefusal()
    {
        // Refusal == None is not enough: without an index there is nothing to show, and
        // treating that as success would surface an empty disk as a real result.
        var withoutIndex = new IncrementalResult(null, IncrementalRefusal.None, 47,
                                                 new JournalMark(1, 2), DateTime.UtcNow);

        Assert.False(withoutIndex.Succeeded);
    }

    [Fact]
    public void ChangeCountAppearsInTheSuccessText()
    {
        Assert.Contains("47", L.T("snapshot.used", 47, "5 min"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, "s")]
    [InlineData(600, "min")]
    [InlineData(36000, "h")]
    [InlineData(600000, "days")]
    public void AgeUsesACoarseUnit(int secondsAgo, string expectedUnit)
    {
        // The exact second is noise. What matters is whether the index is minutes or
        // weeks old.
        string age = SnapshotDescription.Age(DateTime.UtcNow.AddSeconds(-secondsAgo));

        Assert.Contains(expectedUnit, age, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgeOfAnUnknownSnapshotDoesNotThrow()
    {
        Assert.NotEmpty(SnapshotDescription.Age(DateTime.MinValue));
    }
}

/// <summary>
/// The USN record parser, exercised against synthetic buffers — the journal on a real
/// machine cannot be made to produce a specific record on demand.
/// </summary>
public class UsnRecordTests
{
    /// <summary>Builds a <c>USN_RECORD_V2</c> the way the driver lays one out.</summary>
    private static byte[] Build(string name, ulong frn, ulong parentFrn, long usn,
                               UsnReason reason, NtfsFileAttributes attributes,
                               ushort majorVersion = 2)
    {
        int nameBytes = name.Length * 2;
        int nameOffset = UsnLayout.MinimumRecordSize;
        int length = nameOffset + nameBytes;

        byte[] record = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(UsnLayout.RecordLength), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(UsnLayout.MajorVersion), majorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(UsnLayout.MinorVersion), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(UsnLayout.FileReferenceNumber), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(UsnLayout.ParentFileReferenceNumber), parentFrn);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(UsnLayout.Usn), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(UsnLayout.Reason), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(UsnLayout.FileAttributes), (uint)attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(UsnLayout.FileNameLength), (ushort)nameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(UsnLayout.FileNameOffset), (ushort)nameOffset);
        Encoding.Unicode.GetBytes(name).CopyTo(record.AsSpan(nameOffset));

        return record;
    }

    [Fact]
    public void ParsesACreateRecord()
    {
        byte[] bytes = Build("render.mp4", frn: 0x0001_0000_0000_1234, parentFrn: 0x0002_0000_0000_0042,
                             usn: 5_000, UsnReason.FileCreate | UsnReason.Close,
                             NtfsFileAttributes.Archive);

        UsnRecord record = UsnJournal.Parse(bytes);

        Assert.True(record.IsValid);
        Assert.Equal("render.mp4", record.FileName.ToString());
        Assert.Equal(5_000, record.Usn);
        Assert.True((record.Reason & UsnReason.FileCreate) != 0);
        Assert.False(record.IsDirectory);
    }

    [Fact]
    public void RecordNumberIsTheLow48BitsOfTheFileReference()
    {
        // The high 16 bits are the sequence number, not part of the MFT record number.
        // Using the whole 64-bit value as an array index would be catastrophic.
        byte[] bytes = Build("a.txt", frn: 0xABCD_0000_0000_1234, parentFrn: 5, usn: 1,
                             UsnReason.FileCreate, NtfsFileAttributes.Archive);

        UsnRecord record = UsnJournal.Parse(bytes);

        Assert.Equal(0x1234u, record.RecordNumber);
    }

    [Fact]
    public void DirectoryAttributeIsRecognized()
    {
        byte[] bytes = Build("Videos", 100, 5, 1, UsnReason.FileCreate, NtfsFileAttributes.Directory);

        Assert.True(UsnJournal.Parse(bytes).IsDirectory);
    }

    [Fact]
    public void UnsupportedRecordVersionIsSkippedNotMisread()
    {
        // V3 and V4 records have a different layout after the header. Guessing would
        // corrupt the index in silence, so the parser declines them.
        byte[] bytes = Build("x.txt", 10, 5, 1, UsnReason.FileCreate,
                             NtfsFileAttributes.Archive, majorVersion: 3);

        Assert.False(UsnJournal.Parse(bytes).IsValid);
    }

    [Fact]
    public void TooShortBufferIsRefused()
    {
        Assert.False(UsnJournal.Parse(new byte[10]).IsValid);
    }

    [Fact]
    public void NameRunningPastTheRecordIsRefused()
    {
        byte[] bytes = Build("a.txt", 10, 5, 1, UsnReason.FileCreate, NtfsFileAttributes.Archive);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(UsnLayout.FileNameLength), 9999);

        Assert.False(UsnJournal.Parse(bytes).IsValid);
    }

    [Fact]
    public void OddNameLengthIsRefused()
    {
        // UTF-16 byte counts are even by definition; an odd one means a corrupt record.
        byte[] bytes = Build("ab", 10, 5, 1, UsnReason.FileCreate, NtfsFileAttributes.Archive);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(UsnLayout.FileNameLength), 3);

        Assert.False(UsnJournal.Parse(bytes).IsValid);
    }

    [Fact]
    public void IndexRelevantMaskCoversExistenceNameAndSize()
    {
        UsnReason mask = UsnReason.IndexRelevant;

        Assert.True((mask & UsnReason.FileCreate) != 0);
        Assert.True((mask & UsnReason.FileDelete) != 0);
        Assert.True((mask & UsnReason.RenameNewName) != 0);
        Assert.True((mask & UsnReason.DataExtend) != 0);

        // Security and EA changes never move a byte; asking for them would only multiply
        // the records to walk.
        Assert.True((mask & UsnReason.SecurityChange) == 0);
        Assert.True((mask & UsnReason.EaChange) == 0);
    }
}
