using System.Buffers.Binary;

namespace Vacuon.Native.Ntfs;

/// <summary>Um trecho contíguo de clusters. <see cref="Lcn"/> negativo indica trecho esparso.</summary>
public readonly record struct DataRun(long Lcn, long ClusterCount)
{
    public bool IsSparse => Lcn < 0;
}

/// <summary>
/// Decodificador da lista de data runs de um atributo não residente.
/// <para>
/// É por causa disto que a MFT não pode ser lida como um bloco contíguo: em disco
/// usado ela é fragmentada, e quem ignora os runs perde arquivos silenciosamente.
/// </para>
/// </summary>
public static class DataRunList
{
    /// <summary>
    /// Decodifica os runs a partir do início da lista dentro do atributo.
    /// Formato de cada run: 1 byte de cabeçalho (nibble baixo = tamanho do campo
    /// "contagem", nibble alto = tamanho do campo "deslocamento"), seguido dos dois
    /// campos. O deslocamento é <b>relativo ao LCN do run anterior</b> e com sinal.
    /// </summary>
    public static List<DataRun> Decode(ReadOnlySpan<byte> runList)
    {
        var runs = new List<DataRun>(16);
        int pos = 0;
        long currentLcn = 0;

        while (pos < runList.Length)
        {
            byte header = runList[pos++];
            if (header == 0) break; // fim da lista

            int countSize = header & 0x0F;
            int offsetSize = (header >> 4) & 0x0F;

            if (countSize == 0 || countSize > 8 || offsetSize > 8) break;
            if (pos + countSize + offsetSize > runList.Length) break;

            long count = ReadUnsigned(runList.Slice(pos, countSize));
            pos += countSize;

            if (offsetSize == 0)
            {
                // Sem deslocamento = trecho esparso (buraco). Ocupa VCN, não ocupa disco.
                runs.Add(new DataRun(-1, count));
                continue;
            }

            long delta = ReadSigned(runList.Slice(pos, offsetSize));
            pos += offsetSize;

            currentLcn += delta;
            if (currentLcn < 0) break; // lista corrompida

            runs.Add(new DataRun(currentLcn, count));
        }

        return runs;
    }

    /// <summary>Extrai a lista de runs do atributo $DATA não residente de um registro.</summary>
    public static List<DataRun> FromNonResidentAttribute(ReadOnlySpan<byte> attribute)
    {
        int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[NtfsLayout.NonResDataRunsOffset..]);
        if (runsOffset <= 0 || runsOffset >= attribute.Length) return [];
        return Decode(attribute[runsOffset..]);
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
            value = (value << 8) | bytes[i];
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
            value = (value << 8) | bytes[i];

        // Extensão de sinal a partir do bit mais alto do último byte lido.
        int bits = bytes.Length * 8;
        if (bits < 64 && (value & (1L << (bits - 1))) != 0)
            value |= -1L << bits;

        return value;
    }
}
