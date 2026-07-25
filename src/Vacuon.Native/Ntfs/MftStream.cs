using System.Runtime.Versioning;
using Vacuon.Native.Interop;

namespace Vacuon.Native.Ntfs;

/// <summary>
/// Fluxo sequencial virtual sobre os data runs da $MFT.
/// <para>
/// Esconde a fragmentação: quem consome lê bytes em sequência como se a MFT fosse
/// contígua. Também resolve o caso de borda em que um registro de 1024 B cruza a
/// fronteira de dois runs (possível quando o cluster é de 512 B).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftStream(VolumeDevice device, IReadOnlyList<DataRun> runs, uint bytesPerCluster, long validDataLength)
{
    private int _runIndex;
    private long _offsetInRun;

    /// <summary>Total de bytes válidos da MFT (além disso é área alocada mas não escrita).</summary>
    public long Length { get; } = validDataLength;

    /// <summary>Posição lógica atual dentro da MFT.</summary>
    public long Position { get; private set; }

    /// <summary>
    /// Preenche o buffer com os próximos bytes lógicos da MFT.
    /// Trechos esparsos viram zeros (e serão descartados como registros inválidos).
    /// </summary>
    /// <returns>Bytes efetivamente preenchidos; 0 quando chega ao fim.</returns>
    public int Read(Span<byte> buffer)
    {
        int filled = 0;

        while (filled < buffer.Length && Position < Length && _runIndex < runs.Count)
        {
            DataRun run = runs[_runIndex];
            long runBytes = run.ClusterCount * bytesPerCluster;
            long remainingInRun = runBytes - _offsetInRun;

            if (remainingInRun <= 0)
            {
                _runIndex++;
                _offsetInRun = 0;
                continue;
            }

            long remainingLogical = Length - Position;
            int chunk = (int)Math.Min(Math.Min(remainingInRun, remainingLogical), buffer.Length - filled);

            if (run.IsSparse)
            {
                buffer.Slice(filled, chunk).Clear();
            }
            else
            {
                long physical = run.Lcn * bytesPerCluster + _offsetInRun;
                int read = device.ReadAt(physical, buffer.Slice(filled, chunk));
                if (read < chunk)
                {
                    // Leitura curta: setor com defeito ou fim inesperado do dispositivo.
                    buffer.Slice(filled + read, chunk - read).Clear();
                }
            }

            filled += chunk;
            _offsetInRun += chunk;
            Position += chunk;
        }

        return filled;
    }

    /// <summary>
    /// Lê o registro FILE de número <paramref name="recordNumber"/> diretamente,
    /// sem perturbar a posição sequencial. Usado para resolver registros de extensão
    /// referenciados por $ATTRIBUTE_LIST.
    /// </summary>
    public bool ReadRecordAt(long recordNumber, uint bytesPerRecord, Span<byte> buffer)
    {
        long logical = recordNumber * bytesPerRecord;
        if (logical + bytesPerRecord > Length) return false;

        long remaining = logical;
        foreach (DataRun run in runs)
        {
            long runBytes = run.ClusterCount * bytesPerCluster;
            if (remaining < runBytes)
            {
                if (run.IsSparse) return false;

                // Se o registro cruzar a fronteira do run, o caminho sequencial trata;
                // aqui só o caso contíguo interessa.
                if (remaining + bytesPerRecord > runBytes) return false;

                long physical = run.Lcn * bytesPerCluster + remaining;
                return device.ReadAt(physical, buffer[..(int)bytesPerRecord]) == bytesPerRecord;
            }
            remaining -= runBytes;
        }

        return false;
    }
}
