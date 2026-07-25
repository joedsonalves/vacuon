using System.Runtime.InteropServices;

namespace Vacuon.Core.Index;

[Flags]
public enum EntryFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    Compressed = 1 << 1,
    Sparse = 1 << 2,
    Encrypted = 1 << 3,
    ReparsePoint = 1 << 4,
    Hidden = 1 << 5,
    System = 1 << 6,
    HasAds = 1 << 7,
    HardLinked = 1 << 8,
    /// <summary>Placeholder de nuvem (OneDrive). Ler hidrata; apagar remove da nuvem.</summary>
    CloudPlaceholder = 1 << 9,
    /// <summary>Registro existe mas nunca foi preenchido por um registro base válido.</summary>
    Orphan = 1 << 10,
    /// <summary>Marcado por uma heurística de segurança. Ver <c>SuspicionAnalyzer</c>.</summary>
    Suspicious = 1 << 11,
}

/// <summary>
/// A unidade fundamental do índice: 64 bytes exatos, alinhados à linha de cache.
/// <para>
/// 1 milhão de arquivos = 64 MB previsíveis, sem um único objeto no heap gerenciado
/// por arquivo. Um grafo de <c>class FileNode</c> com <c>Parent</c>/<c>Children</c>
/// custaria ~400 MB e manteria a Gen2 sofrendo durante toda a varredura.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FileEntry
{
    /// <summary>Número do registro na MFT. Identidade estável, sobrevive a renomeação.</summary>
    public uint RecordNumber;       //  4

    /// <summary>Índice do pai no mesmo array. Raiz aponta para si mesma.</summary>
    public uint ParentIndex;        //  4

    /// <summary>Posição do nome no blob compartilhado de caracteres.</summary>
    public int NameOffset;          //  4

    public ushort NameLength;       //  2
    public EntryFlags Flags;        //  2

    /// <summary>$DATA.DataSize — o tamanho que o Explorer mostra.</summary>
    public long LogicalSize;        //  8

    /// <summary>Tamanho realmente ocupado em disco (reflete compressão, esparsidade e cluster).</summary>
    public long AllocatedSize;      //  8

    public long LastWriteUtc;       //  8  FILETIME
    public long LastAccessUtc;      //  8
    public long CreatedUtc;         //  8

    public ushort HardLinkCount;    //  2
    private readonly ushort _pad0;  //  2
    private readonly uint _pad1;    //  4
                                    // = 64

    public readonly bool IsDirectory => (Flags & EntryFlags.Directory) != 0;
    public readonly bool IsInUse => NameLength > 0;

    /// <summary>
    /// Espaço ocupado pelo fluxo principal. Atributos residentes (arquivos minúsculos
    /// que cabem dentro do próprio registro da MFT) têm AllocatedSize 0 — nesse caso o
    /// custo é o registro da MFT, contabilizado à parte.
    /// <para>
    /// Os bytes de Alternate Data Stream NÃO entram aqui: eles vivem em uma tabela
    /// lateral no <c>VolumeIndex</c>, porque ADS é raro e um campo de 8 bytes em toda
    /// entrada custaria 8 MB por milhão de arquivos para servir a algumas centenas.
    /// Use <c>VolumeIndex.GetSizeOnDisk(index)</c> quando o total precisar incluir ADS.
    /// </para>
    /// </summary>
    public readonly long SizeOnDisk => AllocatedSize;

    public readonly DateTime LastWrite => FromFileTime(LastWriteUtc);
    public readonly DateTime LastAccess => FromFileTime(LastAccessUtc);
    public readonly DateTime Created => FromFileTime(CreatedUtc);

    private static DateTime FromFileTime(long ft)
    {
        // FILETIME fora de faixa aparece em registros corrompidos e em alguns metafiles.
        if (ft <= 0 || ft > 2650467743999999999L) return DateTime.MinValue;
        return DateTime.FromFileTimeUtc(ft);
    }
}
