namespace Vacuon.Native.Ntfs;

/// <summary>
/// Deslocamentos e constantes do formato on-disk do NTFS.
/// Referência: documentação do formato + linux-ntfs project.
/// </summary>
public static class NtfsLayout
{
    /// <summary>Assinatura "FILE" no início de todo registro da MFT.</summary>
    public const uint FileRecordMagic = 0x454C4946; // 'F','I','L','E' little-endian

    /// <summary>Assinatura "BAAD": registro que o chkdsk marcou como corrompido.</summary>
    public const uint BaadRecordMagic = 0x44414142;

    // ---- FILE_RECORD_SEGMENT_HEADER ----
    public const int RecMagic = 0x00;
    public const int RecUsaOffset = 0x04;
    public const int RecUsaCount = 0x06;
    public const int RecLsn = 0x08;
    public const int RecSequenceNumber = 0x10;
    public const int RecHardLinkCount = 0x12;
    public const int RecFirstAttributeOffset = 0x14;
    public const int RecFlags = 0x16;
    public const int RecUsedSize = 0x18;
    public const int RecAllocatedSize = 0x1C;
    public const int RecBaseFileRecord = 0x20;
    public const int RecNextAttributeId = 0x28;
    public const int RecRecordNumber = 0x2C; // XP+

    public const ushort RecFlagInUse = 0x0001;
    public const ushort RecFlagDirectory = 0x0002;

    // ---- ATTRIBUTE_RECORD_HEADER (comum) ----
    public const int AttrType = 0x00;
    public const int AttrLength = 0x04;
    public const int AttrNonResident = 0x08;
    public const int AttrNameLength = 0x09;
    public const int AttrNameOffset = 0x0A;
    public const int AttrFlags = 0x0C;
    public const int AttrId = 0x0E;

    // residente
    public const int ResValueLength = 0x10;
    public const int ResValueOffset = 0x14;

    // não residente
    public const int NonResStartVcn = 0x10;
    public const int NonResLastVcn = 0x18;
    public const int NonResDataRunsOffset = 0x20;
    public const int NonResCompressionUnit = 0x22;
    public const int NonResAllocatedSize = 0x28;
    public const int NonResRealSize = 0x30;
    public const int NonResInitializedSize = 0x38;

    /// <summary>
    /// Only present when the attribute is compressed or sparse, and then it — not
    /// <see cref="NonResAllocatedSize"/> — is the real number of bytes on disk.
    /// <para>
    /// For those attributes 0x28 holds the size of the run space as if nothing had been
    /// compressed or punched out. $BadClus:$Bad is sized to the entire volume and occupies
    /// nothing; trusting 0x28 for it credits the whole disk twice.
    /// </para>
    /// </summary>
    public const int NonResCompressedSize = 0x40;

    public const ushort AttrFlagCompressed = 0x0001;
    public const ushort AttrFlagEncrypted = 0x4000;
    public const ushort AttrFlagSparse = 0x8000;

    // ---- $FILE_NAME (0x30) ----
    public const int FnParentRef = 0x00;
    public const int FnCreationTime = 0x08;
    public const int FnModifiedTime = 0x10;
    public const int FnMftChangedTime = 0x18;
    public const int FnAccessTime = 0x20;
    public const int FnAllocatedSize = 0x28;
    public const int FnRealSize = 0x30;
    public const int FnFlags = 0x38;
    public const int FnNameLength = 0x40;
    public const int FnNameType = 0x41;
    public const int FnName = 0x42;

    // ---- $STANDARD_INFORMATION (0x10) ----
    public const int SiCreationTime = 0x00;
    public const int SiModifiedTime = 0x08;
    public const int SiMftChangedTime = 0x10;
    public const int SiAccessTime = 0x18;
    public const int SiFileAttributes = 0x20;

    // ---- $ATTRIBUTE_LIST (0x20) entry ----
    public const int AlType = 0x00;
    public const int AlRecordLength = 0x04;
    public const int AlNameLength = 0x06;
    public const int AlNameOffset = 0x07;
    public const int AlStartVcn = 0x08;
    public const int AlBaseFileRef = 0x10;
    public const int AlAttributeId = 0x18;

    /// <summary>Número do registro da MFT do diretório raiz (".").</summary>
    public const int RootDirectoryRecord = 5;

    /// <summary>Os 16 primeiros registros são metafiles do NTFS ($MFT, $LogFile, $Bitmap...).</summary>
    public const int FirstUserRecord = 16;
}

public enum NtfsAttributeType : uint
{
    StandardInformation = 0x10,
    AttributeList = 0x20,
    FileName = 0x30,
    ObjectId = 0x40,
    SecurityDescriptor = 0x50,
    VolumeName = 0x60,
    VolumeInformation = 0x70,
    Data = 0x80,
    IndexRoot = 0x90,
    IndexAllocation = 0xA0,
    Bitmap = 0xB0,
    ReparsePoint = 0xC0,
    EaInformation = 0xD0,
    Ea = 0xE0,
    LoggedUtilityStream = 0x100,
    End = 0xFFFFFFFF,
}

/// <summary>Namespace do nome em $FILE_NAME. O namespace DOS puro é ignorado (8.3 duplicaria entradas).</summary>
public enum NtfsNameType : byte
{
    Posix = 0,
    Win32 = 1,
    Dos = 2,
    Win32AndDos = 3,
}

/// <summary>Atributos de arquivo do Win32, como aparecem em $STANDARD_INFORMATION.</summary>
[Flags]
public enum NtfsFileAttributes : uint
{
    None = 0,
    ReadOnly = 0x00000001,
    Hidden = 0x00000002,
    System = 0x00000004,
    Directory = 0x00000010,
    Archive = 0x00000020,
    Device = 0x00000040,
    Normal = 0x00000080,
    Temporary = 0x00000100,
    SparseFile = 0x00000200,
    ReparsePoint = 0x00000400,
    Compressed = 0x00000800,
    Offline = 0x00001000,
    NotContentIndexed = 0x00002000,
    Encrypted = 0x00004000,
    IntegrityStream = 0x00008000,
    Virtual = 0x00010000,
    NoScrubData = 0x00020000,
    RecallOnOpen = 0x00040000,
    RecallOnDataAccess = 0x00400000,
}
