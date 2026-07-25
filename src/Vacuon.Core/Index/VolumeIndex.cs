using System.Text;
using Vacuon.Native.Ntfs;

namespace Vacuon.Core.Index;

public sealed record VolumeInfo(
    char DriveLetter,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    uint BytesPerCluster,
    bool IncursSeekPenalty)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public string Root => $"{DriveLetter}:\\";
}

/// <summary>
/// O índice completo de um volume: arrays planos indexados pelo número do registro da MFT.
/// </summary>
public sealed class VolumeIndex
{
    /// <summary>Indexado por número do registro da MFT. Entradas com NameLength 0 estão livres.</summary>
    public FileEntry[] Entries { get; }

    public NameBlob Names { get; }
    public VolumeInfo Volume { get; }
    public DateTime ScannedAtUtc { get; }
    public ScanStrategy Strategy { get; }

    /// <summary>Tamanho total agregado por diretório (próprio + descendentes). Preenchido sob demanda.</summary>
    private long[]? _subtreeSize;
    private long[]? _subtreeSizeOnDisk;
    private int[]? _subtreeCount;

    /// <summary>
    /// Bytes em Alternate Data Streams, por índice de entrada.
    /// <para>
    /// Tabela lateral de propósito: ADS existe em uma fração mínima dos arquivos, então
    /// carregar o campo em todas as entradas seria pagar 8 MB por milhão de arquivos
    /// para guardar zeros.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, long> AdsBytes { get; }

    public VolumeIndex(FileEntry[] entries, NameBlob names, VolumeInfo volume, ScanStrategy strategy,
                       IReadOnlyDictionary<int, long>? adsBytes = null)
    {
        Entries = entries;
        Names = names;
        Volume = volume;
        Strategy = strategy;
        AdsBytes = adsBytes ?? new Dictionary<int, long>();
        ScannedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Bytes de ADS deste item, ou 0 se não tiver.</summary>
    public long GetAdsBytes(int index) => AdsBytes.TryGetValue(index, out long bytes) ? bytes : 0;

    /// <summary>Espaço total em disco: fluxo principal + Alternate Data Streams.</summary>
    public long GetSizeOnDisk(int index) => Entries[index].AllocatedSize + GetAdsBytes(index);

    public int Capacity => Entries.Length;

    /// <summary>
    /// Índice da raiz da árvore. Na MFT é o registro 5 (o diretório "."); numa
    /// travessia por API é a entrada 0, que guarda o caminho completo do escopo.
    /// </summary>
    public int RootIndex => Strategy == ScanStrategy.Mft ? NtfsLayout.RootDirectoryRecord : 0;

    public int FileCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].IsInUse && !Entries[i].IsDirectory) n++;
            return n;
        }
    }

    public int DirectoryCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].IsInUse && Entries[i].IsDirectory) n++;
            return n;
        }
    }

    public ReadOnlySpan<char> GetName(int index)
    {
        ref FileEntry e = ref Entries[index];
        return Names.Get(e.NameOffset, e.NameLength);
    }

    /// <summary>
    /// Materializa o caminho completo subindo pelos índices de pai.
    /// <para>
    /// Deliberadamente sob demanda: construir 1 M de strings de caminho durante a
    /// varredura é o segundo maior gargalo depois da própria leitura do disco.
    /// </para>
    /// </summary>
    public string GetFullPath(int index)
    {
        if (index < 0 || index >= Entries.Length || !Entries[index].IsInUse)
            return string.Empty;

        int root = RootIndex;

        // Sobe até a raiz coletando os índices. Profundidade real raramente passa de 40;
        // o teto de 512 é uma trava contra ciclo em MFT corrompida.
        Span<int> stack = stackalloc int[512];
        int depth = 0;
        int current = index;

        while (depth < stack.Length)
        {
            if (current == root) break;

            stack[depth++] = current;

            uint parent = Entries[current].ParentIndex;
            if (parent >= Entries.Length || parent == current) break;
            if (!Entries[(int)parent].IsInUse) break;

            current = (int)parent;
        }

        var sb = new StringBuilder(260);

        if (Strategy == ScanStrategy.Mft)
        {
            sb.Append(Volume.DriveLetter).Append(":\\");
        }
        else
        {
            // Na travessia por API a raiz carrega o caminho completo do escopo.
            sb.Append(GetName(root));
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
        }

        for (int i = depth - 1; i >= 0; i--)
        {
            sb.Append(GetName(stack[i]));
            if (i > 0) sb.Append('\\');
        }

        if (Entries[index].IsDirectory && depth > 0) sb.Append('\\');
        return sb.ToString();
    }

    /// <summary>
    /// Agrega tamanho e contagem por subárvore em uma passada O(n × profundidade).
    /// Resultado fica em cache; chamar de novo é grátis.
    /// </summary>
    public void BuildSubtreeSizes()
    {
        if (_subtreeSize is not null) return;

        var size = new long[Entries.Length];
        var onDisk = new long[Entries.Length];
        var count = new int[Entries.Length];

        for (int i = 0; i < Entries.Length; i++)
        {
            ref FileEntry e = ref Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;

            // Hardlink: o conteúdo ocupa disco uma única vez. Creditar N vezes faria
            // pastas como WinSxS "ocuparem" o triplo do real.
            long logical = e.LogicalSize;
            long physical = e.HardLinkCount > 1 ? 0 : GetSizeOnDisk(i);

            int current = i;
            int guard = 0;
            while (guard++ < 512)
            {
                size[current] += logical;
                onDisk[current] += physical;
                count[current]++;

                uint parent = Entries[current].ParentIndex;
                if (parent >= Entries.Length || parent == current) break;
                current = (int)parent;
            }
        }

        _subtreeSize = size;
        _subtreeSizeOnDisk = onDisk;
        _subtreeCount = count;
    }

    public long GetSubtreeSize(int index)
    {
        BuildSubtreeSizes();
        return _subtreeSize![index];
    }

    public long GetSubtreeSizeOnDisk(int index)
    {
        BuildSubtreeSizes();
        return _subtreeSizeOnDisk![index];
    }

    public int GetSubtreeFileCount(int index)
    {
        BuildSubtreeSizes();
        return _subtreeCount![index];
    }

    /// <summary>Soma lógica de todos os arquivos, com hardlinks contados uma vez.</summary>
    public long TotalLogicalBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                ref FileEntry e = ref Entries[i];
                if (e.IsInUse && !e.IsDirectory) total += e.LogicalSize;
            }
            return total;
        }
    }

    public long TotalBytesOnDisk
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                ref FileEntry e = ref Entries[i];
                if (!e.IsInUse || e.IsDirectory) continue;
                if (e.HardLinkCount > 1) continue; // contabilizado uma vez só
                total += GetSizeOnDisk(i);
            }
            return total;
        }
    }

    /// <summary>Desperdício entre o tamanho lógico e o múltiplo de cluster ocupado.</summary>
    public long TotalSlackBytes
    {
        get
        {
            long slack = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                ref FileEntry e = ref Entries[i];
                if (!e.IsInUse || e.IsDirectory || e.AllocatedSize == 0) continue;
                if ((e.Flags & (EntryFlags.Compressed | EntryFlags.Sparse)) != 0) continue;
                long diff = e.AllocatedSize - e.LogicalSize;
                if (diff > 0) slack += diff;
            }
            return slack;
        }
    }
}

public enum ScanStrategy
{
    /// <summary>Leitura bruta da MFT. Exige NTFS e elevação. 3–8 s por milhão de arquivos.</summary>
    Mft,
    /// <summary>Travessia pela API do Windows. Funciona em qualquer filesystem, 10–40× mais lenta.</summary>
    Win32Walk,
}
