using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Vacuon.Native.Ntfs;

/// <summary>
/// Resultado do parse de um registro FILE da MFT. Struct puro, zero alocação —
/// o nome fica como fatia do buffer de origem, não como <c>string</c>.
/// </summary>
public ref struct ParsedMftRecord
{
    public bool IsValid;
    public bool InUse;
    public bool IsDirectory;

    /// <summary>Número deste registro (do cabeçalho, validado contra o índice calculado).</summary>
    public uint RecordNumber;

    /// <summary>Se != 0, este é um registro de extensão; os atributos pertencem ao registro base.</summary>
    public uint BaseRecordNumber;

    /// <summary>
    /// The link count straight from the record header (offset 0x12).
    /// <para>
    /// <b>This is not the number of hardlinks.</b> NTFS counts $FILE_NAME attributes here,
    /// and the DOS 8.3 alias is one of them — so any file with a long name reports 2.
    /// Use <see cref="NameCount"/> for the real thing.
    /// </para>
    /// </summary>
    public ushort StatedLinkCount;

    /// <summary>
    /// $FILE_NAME attributes that represent an actual directory entry: everything except a
    /// standalone DOS alias. This is the true hardlink count for this record.
    /// </summary>
    public ushort NameCount;

    public ushort SequenceNumber;

    // --- $FILE_NAME escolhido ---
    public bool HasName;
    public ReadOnlySpan<char> Name;
    public uint ParentRecordNumber;
    public NtfsNameType NameType;

    // --- $STANDARD_INFORMATION ---
    public bool HasStandardInfo;
    public long CreationTime;
    public long ModifiedTime;
    public long AccessTime;
    public NtfsFileAttributes FileAttributes;

    // --- $DATA sem nome ---
    public bool HasData;
    public long LogicalSize;
    public long AllocatedSize;

    // --- $DATA nomeado (Alternate Data Streams) ---
    public bool HasAds;
    public long AdsBytes;

    public bool HasAttributeList;
    public bool IsCompressed;
    public bool IsSparse;
    public bool IsEncrypted;
    public bool IsReparsePoint;
}

/// <summary>
/// Parser dos registros FILE da MFT. Opera sobre <see cref="Span{T}"/> — nenhuma alocação
/// por registro, o que é o que permite varrer 1 M de arquivos sem pressão de GC.
/// </summary>
public static class MftRecordParser
{
    /// <summary>
    /// Aplica os fixups do Update Sequence Array.
    /// <para>
    /// Sem isso, os dois últimos bytes de CADA setor do registro vêm com o valor do USN
    /// em vez do conteúdo real — o parse "quase funciona", que é pior do que falhar.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> se o registro está inconsistente (USN não bate) e deve ser descartado.</returns>
    public static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 0x30) return false;

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecUsaOffset..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecUsaCount..]);

        // usaCount inclui o próprio USN; sobram usaCount-1 entradas, uma por setor.
        int sectors = usaCount - 1;
        if (sectors <= 0) return false;
        if (usaOffset + usaCount * 2 > record.Length) return false;
        if (sectors * bytesPerSector > record.Length) return false;

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);

        for (int i = 0; i < sectors; i++)
        {
            int tailOffset = (i + 1) * bytesPerSector - 2;
            ushort tail = BinaryPrimitives.ReadUInt16LittleEndian(record[tailOffset..]);

            // O rabo de cada setor tem que carregar o USN. Se não carrega, o registro
            // foi escrito parcialmente (torn write) e não é confiável.
            if (tail != usn) return false;

            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(record[(usaOffset + (i + 1) * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[tailOffset..], original);
        }

        return true;
    }

    /// <summary>
    /// Faz o parse de um registro já corrigido pelos fixups.
    /// </summary>
    /// <param name="record">Buffer do registro (já passou por <see cref="ApplyFixups"/>).</param>
    /// <param name="expectedRecordNumber">Índice calculado pela posição na MFT, usado para validar.</param>
    public static ParsedMftRecord Parse(ReadOnlySpan<byte> record, uint expectedRecordNumber)
    {
        var result = default(ParsedMftRecord);

        if (record.Length < 0x30) return result;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (magic != NtfsLayout.FileRecordMagic) return result; // inclui "BAAD" e registros zerados

        result.IsValid = true;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecFlags..]);
        result.InUse = (flags & NtfsLayout.RecFlagInUse) != 0;
        result.IsDirectory = (flags & NtfsLayout.RecFlagDirectory) != 0;
        result.StatedLinkCount = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecHardLinkCount..]);
        result.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecSequenceNumber..]);

        // O campo RecordNumber só existe a partir do XP. Se estiver zerado ou divergir,
        // o índice calculado pela posição é a fonte da verdade.
        uint stated = BinaryPrimitives.ReadUInt32LittleEndian(record[NtfsLayout.RecRecordNumber..]);
        result.RecordNumber = stated == expectedRecordNumber ? stated : expectedRecordNumber;

        ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(record[NtfsLayout.RecBaseFileRecord..]);
        result.BaseRecordNumber = (uint)(baseRef & 0x0000FFFFFFFFFFFFUL);

        if (!result.InUse) return result;

        int used = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[NtfsLayout.RecUsedSize..]);
        if (used <= 0 || used > record.Length) used = record.Length;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecFirstAttributeOffset..]);
        if (offset < 0x30 || offset >= used) return result;

        NtfsNameType bestNameType = (NtfsNameType)255;

        while (offset + 8 <= used)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == (uint)NtfsAttributeType.End) break;

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + NtfsLayout.AttrLength)..]);
            // Atributo com tamanho inválido corromperia o loop; abortar o registro é mais seguro
            // do que caminhar por lixo.
            if (length < 0x10 || offset + length > used) break;

            ReadOnlySpan<byte> attr = record.Slice(offset, length);
            bool nonResident = attr[NtfsLayout.AttrNonResident] != 0;
            byte nameLength = attr[NtfsLayout.AttrNameLength];
            ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr[NtfsLayout.AttrFlags..]);

            switch ((NtfsAttributeType)type)
            {
                case NtfsAttributeType.StandardInformation when !nonResident:
                    ParseStandardInformation(attr, ref result);
                    break;

                case NtfsAttributeType.FileName when !nonResident:
                    ParseFileName(attr, ref result, ref bestNameType);
                    break;

                case NtfsAttributeType.AttributeList:
                    result.HasAttributeList = true;
                    break;

                case NtfsAttributeType.ReparsePoint:
                    result.IsReparsePoint = true;
                    break;

                case NtfsAttributeType.Data:
                    ParseData(attr, nonResident, nameLength, attrFlags, ref result);
                    break;
            }

            offset += length;
        }

        return result;
    }

    private static void ParseStandardInformation(ReadOnlySpan<byte> attr, ref ParsedMftRecord result)
    {
        int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[NtfsLayout.ResValueOffset..]);
        int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[NtfsLayout.ResValueLength..]);
        if (valueOffset + valueLength > attr.Length || valueLength < 0x24) return;

        ReadOnlySpan<byte> v = attr.Slice(valueOffset, valueLength);
        result.HasStandardInfo = true;
        result.CreationTime = BinaryPrimitives.ReadInt64LittleEndian(v[NtfsLayout.SiCreationTime..]);
        result.ModifiedTime = BinaryPrimitives.ReadInt64LittleEndian(v[NtfsLayout.SiModifiedTime..]);
        result.AccessTime = BinaryPrimitives.ReadInt64LittleEndian(v[NtfsLayout.SiAccessTime..]);
        result.FileAttributes = (NtfsFileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(v[NtfsLayout.SiFileAttributes..]);
    }

    private static void ParseFileName(ReadOnlySpan<byte> attr, ref ParsedMftRecord result, ref NtfsNameType best)
    {
        int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[NtfsLayout.ResValueOffset..]);
        int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[NtfsLayout.ResValueLength..]);
        if (valueOffset + valueLength > attr.Length || valueLength < NtfsLayout.FnName) return;

        ReadOnlySpan<byte> v = attr.Slice(valueOffset, valueLength);

        int nameChars = v[NtfsLayout.FnNameLength];
        var nameType = (NtfsNameType)v[NtfsLayout.FnNameType];
        if (NtfsLayout.FnName + nameChars * 2 > v.Length) return;

        // Count the links while we are here, because the record header cannot tell us.
        // A standalone DOS alias is a second name for the SAME directory entry, not a
        // second link — counting it is what made 75% of a real volume look hardlinked.
        // Win32AndDos is one entry serving both namespaces, so it counts once.
        if (nameType != NtfsNameType.Dos && result.NameCount < ushort.MaxValue)
            result.NameCount++;

        // Um arquivo pode ter vários $FILE_NAME (um por hardlink, mais o 8.3).
        // Preferimos Win32/Win32AndDos; o namespace DOS puro é descartado para não
        // duplicar entradas com o nome curto (ex.: PROGRA~1).
        if (!IsBetterName(nameType, best)) return;

        best = nameType;
        result.HasName = true;
        result.NameType = nameType;
        result.Name = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(
            v.Slice(NtfsLayout.FnName, nameChars * 2));

        ulong parentRef = BinaryPrimitives.ReadUInt64LittleEndian(v[NtfsLayout.FnParentRef..]);
        result.ParentRecordNumber = (uint)(parentRef & 0x0000FFFFFFFFFFFFUL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBetterName(NtfsNameType candidate, NtfsNameType current)
    {
        // Ordem de preferência: Win32AndDos > Win32 > Posix > Dos
        static int Rank(NtfsNameType t) => t switch
        {
            NtfsNameType.Win32AndDos => 3,
            NtfsNameType.Win32 => 2,
            NtfsNameType.Posix => 1,
            NtfsNameType.Dos => 0,
            _ => -1,
        };

        return Rank(candidate) > Rank(current);
    }

    private static void ParseData(ReadOnlySpan<byte> attr, bool nonResident, byte nameLength,
                                  ushort attrFlags, ref ParsedMftRecord result)
    {
        long logical, allocated;

        bool compressedOrSparse =
            (attrFlags & (NtfsLayout.AttrFlagCompressed | NtfsLayout.AttrFlagSparse)) != 0;

        if (nonResident)
        {
            if (attr.Length < 0x38) return;
            allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[NtfsLayout.NonResAllocatedSize..]);
            logical = BinaryPrimitives.ReadInt64LittleEndian(attr[NtfsLayout.NonResRealSize..]);

            // Compressed or sparse: 0x28 is the run space measured as if nothing had been
            // compressed or punched out, and 0x40 is what is really on disk. Reading 0x28
            // for these is how a volume ends up reporting more occupied space than it has.
            if (compressedOrSparse && attr.Length >= 0x48)
                allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[NtfsLayout.NonResCompressedSize..]);

            // Só o fragmento com StartingVCN == 0 carrega os tamanhos reais;
            // fragmentos posteriores (arquivo muito fragmentado) trazem zeros.
            long startVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[NtfsLayout.NonResStartVcn..]);
            if (startVcn != 0) return;
        }
        else
        {
            int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[NtfsLayout.ResValueLength..]);
            logical = valueLength;
            // Atributo residente vive dentro do próprio registro da MFT: não consome cluster.
            allocated = 0;
        }

        if (logical < 0 || allocated < 0) return;

        if (nameLength == 0)
        {
            // $DATA sem nome = o conteúdo do arquivo.
            result.HasData = true;
            result.LogicalSize = logical;
            result.AllocatedSize = allocated;
            result.IsCompressed = (attrFlags & NtfsLayout.AttrFlagCompressed) != 0;
            result.IsSparse = (attrFlags & NtfsLayout.AttrFlagSparse) != 0;
            result.IsEncrypted = (attrFlags & NtfsLayout.AttrFlagEncrypted) != 0;
        }
        else
        {
            // $DATA nomeado = Alternate Data Stream. Invisível no Explorer, ocupa espaço real.
            result.HasAds = true;

            // The on-disk size computed above, never the logical size. Falling back to
            // logical when the allocation is 0 looks harmless — a resident stream reports
            // 0 — but a SPARSE stream also occupies ~nothing while claiming an enormous
            // logical size, and NTFS ships two of those on every volume: $BadClus:$Bad is
            // sized to the whole disk and $UsnJrnl:$J to the journal's whole range. On a
            // 476 GiB volume that fallback invented 568 GiB out of nothing.
            //
            // Resident named streams live inside the MFT record and cost no cluster, the
            // same treatment the unnamed $DATA above already gets.
            result.AdsBytes += allocated;
        }
    }
}
