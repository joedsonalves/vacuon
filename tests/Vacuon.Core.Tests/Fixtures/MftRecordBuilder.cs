using System.Buffers.Binary;
using System.Text;
using Vacuon.Native.Ntfs;

namespace Vacuon.Core.Tests.Fixtures;

/// <summary>
/// Constrói registros FILE sintéticos com os mesmos casos de borda que existem em
/// disco real: fixups, nomes 8.3 duplicados, $DATA residente e não residente, ADS.
/// <para>
/// Testar o parser contra um volume de verdade seria não-determinístico e exigiria
/// elevação no CI. Um construtor de registros resolve os dois problemas.
/// </para>
/// </summary>
public sealed class MftRecordBuilder
{
    public const int RecordSize = 1024;
    public const int SectorSize = 512;

    private readonly List<byte[]> _attributes = [];

    public uint RecordNumber { get; set; }
    public uint BaseRecordNumber { get; set; }
    public ushort HardLinkCount { get; set; } = 1;
    public bool InUse { get; set; } = true;
    public bool IsDirectory { get; set; }

    public MftRecordBuilder WithStandardInformation(
        DateTime created, DateTime modified, DateTime accessed, NtfsFileAttributes attributes)
    {
        byte[] value = new byte[0x48];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(NtfsLayout.SiCreationTime), created.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(NtfsLayout.SiModifiedTime), modified.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(NtfsLayout.SiMftChangedTime), modified.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(NtfsLayout.SiAccessTime), accessed.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(NtfsLayout.SiFileAttributes), (uint)attributes);

        _attributes.Add(BuildResident(NtfsAttributeType.StandardInformation, value, name: null));
        return this;
    }

    public MftRecordBuilder WithFileName(string name, uint parentRecord, NtfsNameType nameType)
    {
        byte[] value = new byte[NtfsLayout.FnName + name.Length * 2];

        // Referência do pai: 48 bits de número + 16 bits de sequência.
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(NtfsLayout.FnParentRef),
            parentRecord | (1UL << 48));

        value[NtfsLayout.FnNameLength] = (byte)name.Length;
        value[NtfsLayout.FnNameType] = (byte)nameType;
        Encoding.Unicode.GetBytes(name).CopyTo(value.AsSpan(NtfsLayout.FnName));

        _attributes.Add(BuildResident(NtfsAttributeType.FileName, value, name: null));
        return this;
    }

    public MftRecordBuilder WithResidentData(byte[] content)
    {
        _attributes.Add(BuildResident(NtfsAttributeType.Data, content, name: null));
        return this;
    }

    public MftRecordBuilder WithNonResidentData(long logicalSize, long allocatedSize,
                                                string? streamName = null, ushort flags = 0,
                                                long startVcn = 0, byte[]? dataRuns = null)
    {
        _attributes.Add(BuildNonResident(NtfsAttributeType.Data, logicalSize, allocatedSize,
                                         streamName, flags, startVcn, dataRuns));
        return this;
    }

    public byte[] Build()
    {
        byte[] record = new byte[RecordSize];

        int usaOffset = 0x30;
        int sectors = RecordSize / SectorSize;
        int usaCount = sectors + 1;
        int attributesOffset = Align8(usaOffset + usaCount * 2);

        BinaryPrimitives.WriteUInt32LittleEndian(record, NtfsLayout.FileRecordMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(NtfsLayout.RecUsaOffset), (ushort)usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(NtfsLayout.RecUsaCount), (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(NtfsLayout.RecSequenceNumber), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(NtfsLayout.RecHardLinkCount), HardLinkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(NtfsLayout.RecFirstAttributeOffset), (ushort)attributesOffset);

        ushort flags = 0;
        if (InUse) flags |= NtfsLayout.RecFlagInUse;
        if (IsDirectory) flags |= NtfsLayout.RecFlagDirectory;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(NtfsLayout.RecFlags), flags);

        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(NtfsLayout.RecBaseFileRecord),
            BaseRecordNumber == 0 ? 0UL : BaseRecordNumber | (1UL << 48));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(NtfsLayout.RecRecordNumber), RecordNumber);

        int offset = attributesOffset;
        foreach (byte[] attribute in _attributes)
        {
            attribute.CopyTo(record.AsSpan(offset));
            offset += attribute.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), (uint)NtfsAttributeType.End);
        offset += 8;

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(NtfsLayout.RecUsedSize), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(NtfsLayout.RecAllocatedSize), RecordSize);

        ApplyOutgoingFixups(record, usaOffset, sectors);
        return record;
    }

    /// <summary>
    /// Faz o que o NTFS faz ao gravar: guarda os dois últimos bytes de cada setor no
    /// array de fixup e escreve o USN no lugar deles.
    /// </summary>
    private static void ApplyOutgoingFixups(byte[] record, int usaOffset, int sectors)
    {
        const ushort usn = 0x1234;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset), usn);

        for (int i = 0; i < sectors; i++)
        {
            int tail = (i + 1) * SectorSize - 2;
            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(tail));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset + (i + 1) * 2), original);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(tail), usn);
        }
    }

    private static byte[] BuildResident(NtfsAttributeType type, byte[] value, string? name)
    {
        int nameBytes = name is null ? 0 : name.Length * 2;
        int nameOffset = 0x18;
        int valueOffset = Align8(nameOffset + nameBytes);
        int length = Align8(valueOffset + value.Length);

        byte[] attribute = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(NtfsLayout.AttrType), (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(NtfsLayout.AttrLength), (uint)length);
        attribute[NtfsLayout.AttrNonResident] = 0;
        attribute[NtfsLayout.AttrNameLength] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(NtfsLayout.AttrNameOffset), (ushort)nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(NtfsLayout.ResValueLength), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(NtfsLayout.ResValueOffset), (ushort)valueOffset);

        if (name is not null) Encoding.Unicode.GetBytes(name).CopyTo(attribute.AsSpan(nameOffset));
        value.CopyTo(attribute.AsSpan(valueOffset));

        return attribute;
    }

    private static byte[] BuildNonResident(NtfsAttributeType type, long logicalSize, long allocatedSize,
                                           string? name, ushort flags, long startVcn, byte[]? dataRuns)
    {
        dataRuns ??= [0x00];
        int nameBytes = name is null ? 0 : name.Length * 2;
        int nameOffset = 0x40;
        int runsOffset = Align8(nameOffset + nameBytes);
        int length = Align8(runsOffset + dataRuns.Length);

        byte[] attribute = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(NtfsLayout.AttrType), (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(NtfsLayout.AttrLength), (uint)length);
        attribute[NtfsLayout.AttrNonResident] = 1;
        attribute[NtfsLayout.AttrNameLength] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(NtfsLayout.AttrNameOffset), (ushort)nameOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(NtfsLayout.AttrFlags), flags);

        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(NtfsLayout.NonResStartVcn), startVcn);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(NtfsLayout.NonResLastVcn), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(NtfsLayout.NonResDataRunsOffset), (ushort)runsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(NtfsLayout.NonResAllocatedSize), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(NtfsLayout.NonResRealSize), logicalSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(NtfsLayout.NonResInitializedSize), logicalSize);

        if (name is not null) Encoding.Unicode.GetBytes(name).CopyTo(attribute.AsSpan(nameOffset));
        dataRuns.CopyTo(attribute.AsSpan(runsOffset));

        return attribute;
    }

    private static int Align8(int value) => (value + 7) & ~7;
}
