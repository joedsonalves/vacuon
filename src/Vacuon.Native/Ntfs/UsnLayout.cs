using System.Runtime.InteropServices;

namespace Vacuon.Native.Ntfs;

/// <summary>Why the NTFS change journal recorded an entry.</summary>
[Flags]
public enum UsnReason : uint
{
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,

    /// <summary>
    /// Everything the index cares about: existence, name, and size.
    /// <para>
    /// Security, EA and object-id changes are deliberately excluded — they never move a
    /// byte, and asking for them would multiply the number of records to walk.
    /// </para>
    /// </summary>
    IndexRelevant = FileCreate | FileDelete | RenameOldName | RenameNewName
                  | DataOverwrite | DataExtend | DataTruncation
                  | NamedDataOverwrite | NamedDataExtend | NamedDataTruncation
                  | HardLinkChange | CompressionChange | ReparsePointChange,
}

/// <summary>
/// <c>USN_JOURNAL_DATA_V0</c> — the journal's identity and valid range.
/// <para>
/// <see cref="UsnJournalID"/> and <see cref="FirstUsn"/> are what make an incremental
/// update trustworthy: a different journal id means the journal was deleted and
/// recreated, and a <c>FirstUsn</c> above the snapshot's mark means the records we
/// needed were already purged. Either way the only honest answer is a full rescan.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct UsnJournalData
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary><c>READ_USN_JOURNAL_DATA_V0</c> — the request for a batch of records.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ReadUsnJournalData
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
}

/// <summary>One parsed <c>USN_RECORD_V2</c>. The name is a slice of the caller's buffer.</summary>
public ref struct UsnRecord
{
    public bool IsValid;
    public long Usn;
    public ulong FileReferenceNumber;
    public ulong ParentFileReferenceNumber;
    public UsnReason Reason;
    public NtfsFileAttributes Attributes;
    public ReadOnlySpan<char> FileName;

    public readonly bool IsDirectory => (Attributes & NtfsFileAttributes.Directory) != 0;

    /// <summary>Record number in the MFT — the low 48 bits of the file reference.</summary>
    public readonly uint RecordNumber => (uint)(FileReferenceNumber & 0x0000FFFFFFFFFFFFUL);

    public readonly uint ParentRecordNumber => (uint)(ParentFileReferenceNumber & 0x0000FFFFFFFFFFFFUL);
}

/// <summary>Offsets inside <c>USN_RECORD_V2</c>.</summary>
public static class UsnLayout
{
    public const int RecordLength = 0x00;
    public const int MajorVersion = 0x04;
    public const int MinorVersion = 0x06;
    public const int FileReferenceNumber = 0x08;
    public const int ParentFileReferenceNumber = 0x10;
    public const int Usn = 0x18;
    public const int TimeStamp = 0x20;
    public const int Reason = 0x28;
    public const int SourceInfo = 0x2C;
    public const int SecurityId = 0x30;
    public const int FileAttributes = 0x34;
    public const int FileNameLength = 0x38;
    public const int FileNameOffset = 0x3A;

    /// <summary>Smallest possible V2 record: header plus a one-character name.</summary>
    public const int MinimumRecordSize = 0x3C;

    /// <summary>
    /// The read output begins with the USN to continue from, then the records.
    /// </summary>
    public const int NextUsnPrefixSize = sizeof(long);
}
