using System.Buffers.Binary;

namespace Vacuon.Native.Ntfs;

/// <summary>
/// Localiza um atributo específico dentro de um registro FILE já corrigido pelos fixups.
/// Usado para achar o $DATA da própria $MFT — o registro que destrava todo o resto.
/// </summary>
public static class MftAttributeFinder
{
    /// <summary>
    /// Devolve a fatia do primeiro atributo do tipo pedido, sem nome (unnamed).
    /// </summary>
    public static bool TryFind(ReadOnlySpan<byte> record, NtfsAttributeType wanted,
                               out ReadOnlySpan<byte> attribute)
    {
        attribute = default;

        if (record.Length < 0x30) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != NtfsLayout.FileRecordMagic) return false;

        int used = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[NtfsLayout.RecUsedSize..]);
        if (used <= 0 || used > record.Length) used = record.Length;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecFirstAttributeOffset..]);
        if (offset < 0x30 || offset >= used) return false;

        while (offset + 8 <= used)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == (uint)NtfsAttributeType.End) break;

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + NtfsLayout.AttrLength)..]);
            if (length < 0x10 || offset + length > used) break;

            if (type == (uint)wanted && record[offset + NtfsLayout.AttrNameLength] == 0)
            {
                attribute = record.Slice(offset, length);
                return true;
            }

            offset += length;
        }

        return false;
    }
}
