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
/// What a call to <see cref="VolumeIndex.MarkDeleted"/> took out of the index.
/// <para>
/// The bytes are summed from the entries that were removed, not from what the delete
/// itself claimed — the two are measured by different code, and only this one saw the
/// whole subtree.
/// </para>
/// </summary>
public readonly record struct Removal(int Entries, long LogicalBytes, long BytesOnDisk)
{
    public bool IsEmpty => Entries == 0;

    public static Removal operator +(Removal a, Removal b) =>
        new(a.Entries + b.Entries, a.LogicalBytes + b.LogicalBytes, a.BytesOnDisk + b.BytesOnDisk);
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
    public IReadOnlyDictionary<int, long> AdsBytes { get; private set; }

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

    /// <summary>
    /// Same index, refreshed volume figures.
    /// <para>
    /// Free space moves without any journal record, so an incrementally updated index
    /// must re-read it instead of reporting the number from when the snapshot was taken.
    /// The heavy arrays are shared, not copied.
    /// </para>
    /// </summary>
    public VolumeIndex WithVolume(VolumeInfo volume) =>
        new(Entries, Names, volume, Strategy, AdsBytes);

    /// <summary>Swaps the Alternate Data Stream side table after a delta.</summary>
    public void ReplaceAdsTable(IReadOnlyDictionary<int, long> table)
    {
        AdsBytes = table;
        InvalidateAggregates();
    }

    /// <summary>
    /// Drops the cached subtree sizes and the child index.
    /// <para>
    /// Both are derived from the entry array, so any mutation makes them wrong. Clearing
    /// them costs one lazy rebuild; leaving them would show sizes for files that no
    /// longer exist.
    /// </para>
    /// </summary>
    public void InvalidateAggregates()
    {
        _subtreeSize = null;
        _subtreeSizeOnDisk = null;
        _subtreeCount = null;
        _childStart = null;
        _childList = null;
    }

    /// <summary>Espaço total em disco: fluxo principal + Alternate Data Streams.</summary>
    public long GetSizeOnDisk(int index) => Entries[index].AllocatedSize + GetAdsBytes(index);

    /// <summary>
    /// Takes an entry and everything below it out of the index.
    /// <para>
    /// Called after a delete really happened on disk. Dropping the row from the list on
    /// screen is not enough: every view is derived from <see cref="Entries"/>, so an item
    /// removed from the list alone walks straight back in the moment the folder is
    /// reopened, the search is rerun, or the biggest-files view is rebuilt.
    /// </para>
    /// <para>
    /// An entry is freed by emptying its name — the same condition the scanner uses for an
    /// unused MFT record. Nothing is compacted and no index shifts, so the entry numbers
    /// held by rows and tree nodes elsewhere stay valid.
    /// </para>
    /// </summary>
    /// <returns>
    /// What left the index, measured from the entries themselves. Callers report this
    /// instead of the figure the shell guessed while deleting.
    /// </returns>
    public Removal MarkDeleted(int index) => MarkDeleted([index]);

    /// <summary>
    /// Takes several entries, and everything below each of them, out of the index in one pass.
    /// <para>
    /// ⚠️ Callers with more than one item must use this rather than call
    /// <see cref="MarkDeleted(int)"/> in a loop. Freeing a folder needs the child index to
    /// find what is inside, and every call drops it on the way out — so the next folder
    /// rebuilt it over the whole volume. Measured on a real index of 3.7 M records: 500
    /// folders took 24.08 s one call at a time, one rebuild each, with the window frozen.
    /// </para>
    /// <para>
    /// An entry listed twice, or listed beside an ancestor that is also listed, is freed and
    /// counted once: whichever reaches it first takes it, and the other finds it gone.
    /// </para>
    /// </summary>
    public Removal MarkDeleted(ReadOnlySpan<int> indices)
    {
        // Built once, before anything is freed: the walk reads children from it, and a
        // rebuild halfway would be built from entries this method has already emptied.
        // Only a folder needs it. A file has no children, and asking anyway rebuilds the
        // child index of the whole volume to learn that.
        foreach (int start in indices)
        {
            if (start < 0 || start >= Entries.Length) continue;
            if (!Entries[start].IsInUse || !Entries[start].IsDirectory) continue;

            BuildChildIndex();
            break;
        }

        int removed = 0;
        long logical = 0;
        long onDisk = 0;
        var pending = new Stack<int>();

        foreach (int start in indices)
        {
            if (start < 0 || start >= Entries.Length) continue;
            if (!Entries[start].IsInUse) continue;

            // The root is refused by ProtectedPaths and cannot be deleted on disk. Emptying
            // the whole index because of a bug upstream would be far worse than doing nothing.
            if (start == RootIndex) continue;

            pending.Push(start);

            while (pending.Count > 0)
            {
                int current = pending.Pop();
                ref FileEntry entry = ref Entries[current];
                if (!entry.IsInUse) continue;

                if (entry.IsDirectory)
                {
                    foreach (int child in GetChildren(current))
                        if (Entries[child].IsInUse) pending.Push(child);
                }
                else
                {
                    logical += entry.LogicalSize;

                    // A file with another name keeps its clusters under that name: dropping
                    // this entry is not freeing them, and the figure a caller reports as freed
                    // must not say it was.
                    if (entry.HardLinkCount <= 1) onDisk += GetSizeOnDisk(current);
                }

                entry.NameLength = 0;
                removed++;
            }
        }

        if (removed > 0) InvalidateAggregates();
        return new Removal(removed, logical, onDisk);
    }

    /// <summary>
    /// Re-parents an entry — and, when the destination folder already held that name,
    /// renames it too.
    /// <para>
    /// Called after a move really happened on disk, and only when the move stayed on this
    /// volume. Marking the entry deleted instead would be the easy way out and a lie twice
    /// over: the file is still there, and the volume total would drop by its size while the
    /// free space on disk did not move a byte.
    /// </para>
    /// <para>
    /// The subtree needs no visit. Everything below points at its own parent, so a folder
    /// that changes parent takes its whole subtree with it, exactly as NTFS does.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when the move could not be represented; the index is untouched.</returns>
    public bool MarkMoved(int index, int newParentIndex, ReadOnlySpan<char> newName)
    {
        if (index < 0 || index >= Entries.Length || !Entries[index].IsInUse) return false;
        if (index == RootIndex) return false;

        if (newParentIndex < 0 || newParentIndex >= Entries.Length) return false;
        if (!Entries[newParentIndex].IsInUse || !Entries[newParentIndex].IsDirectory) return false;

        // A folder made its own descendant's child would build a ring, and every walk up
        // the parent chain — GetFullPath, the subtree sizes, the delete — would spin
        // until its guard fired.
        if (newParentIndex == index || IsDescendant(newParentIndex, index)) return false;

        bool renamed = newName.Length > 0 && !newName.SequenceEqual(GetName(index));

        // Read the name before appending: Append can grow the blob, and the span from
        // GetName points into the buffer it just replaced.
        int offset = renamed ? Names.Append(newName) : 0;

        ref FileEntry entry = ref Entries[index];
        entry.ParentIndex = (uint)newParentIndex;

        if (renamed)
        {
            entry.NameOffset = offset;
            entry.NameLength = (ushort)newName.Length;
        }

        InvalidateAggregates();
        return true;
    }

    /// <summary>
    /// One name more, or one name fewer, for a file the index already holds — a hard link
    /// made to it, or one of its links removed. The entry stays where it is: it is one
    /// record, credited once whatever its names (see <see cref="BuildSubtreeSizes"/>), so no
    /// total moves. What moves is the count the duplicate search reads to know that removing
    /// a name frees nothing.
    /// </summary>
    public void ChangeNameCount(int index, int by)
    {
        if (index < 0 || index >= Entries.Length) return;

        ref FileEntry e = ref Entries[index];
        if (!e.IsInUse || e.IsDirectory) return;

        e.HardLinkCount = (ushort)Math.Clamp(e.HardLinkCount + by, 1, ushort.MaxValue);

        if (e.HardLinkCount > 1) e.Flags |= EntryFlags.HardLinked;
        else e.Flags &= ~EntryFlags.HardLinked;
    }

    /// <summary>
    /// Puts a directory that exists on disk but not in this index into it — at its real
    /// MFT record number, never at an invented one.
    /// <para>
    /// Needed because the obvious way to use a move is to create the destination folder on
    /// the spot, which leaves it younger than the scan. The record number comes from the
    /// file system itself (the file id), so a later journal delta about that record lands
    /// on the same entry rather than on a stranger.
    /// </para>
    /// </summary>
    /// <returns>The entry index, or -1 when the slot is out of range or already in use.</returns>
    public int AddDirectory(int recordNumber, int parentIndex, ReadOnlySpan<char> name)
    {
        if (recordNumber <= 0 || recordNumber >= Entries.Length) return -1;
        if (name.Length == 0 || name.Length > ushort.MaxValue) return -1;
        if (parentIndex < 0 || parentIndex >= Entries.Length) return -1;
        if (!Entries[parentIndex].IsInUse || !Entries[parentIndex].IsDirectory) return -1;

        // Occupied means the index disagrees with the disk about that record. Overwriting
        // would hide the disagreement; refusing lets the caller say the scan is stale.
        if (Entries[recordNumber].IsInUse) return -1;

        int offset = Names.Append(name);

        Entries[recordNumber] = new FileEntry
        {
            RecordNumber = (uint)recordNumber,
            ParentIndex = (uint)parentIndex,
            NameOffset = offset,
            NameLength = (ushort)name.Length,
            Flags = EntryFlags.Directory,
            HardLinkCount = 1,
        };

        InvalidateAggregates();
        return recordNumber;
    }

    /// <summary>
    /// Puts a file that exists on disk but not in this index into it, at its real MFT record
    /// number — the companion to <see cref="AddDirectory"/>, and it exists for the same
    /// reason: a copy writes files younger than the scan, and a list that cannot show them
    /// is a list that disagrees with the disk.
    /// </summary>
    /// <returns>The entry index, or -1 when the slot is out of range or already in use.</returns>
    public int AddFile(int recordNumber, int parentIndex, ReadOnlySpan<char> name,
                       long logicalSize, long allocatedSize,
                       DateTime lastWriteUtc, DateTime createdUtc,
                       bool hidden = false, bool system = false)
    {
        if (recordNumber <= 0 || recordNumber >= Entries.Length) return -1;
        if (name.Length == 0 || name.Length > ushort.MaxValue) return -1;
        if (parentIndex < 0 || parentIndex >= Entries.Length) return -1;
        if (!Entries[parentIndex].IsInUse || !Entries[parentIndex].IsDirectory) return -1;

        // Same rule as AddDirectory: an occupied slot means the index and the disk disagree
        // about that record, and overwriting would hide the disagreement.
        if (Entries[recordNumber].IsInUse) return -1;

        int offset = Names.Append(name);

        EntryFlags flags = EntryFlags.None;
        if (hidden) flags |= EntryFlags.Hidden;
        if (system) flags |= EntryFlags.System;

        Entries[recordNumber] = new FileEntry
        {
            RecordNumber = (uint)recordNumber,
            ParentIndex = (uint)parentIndex,
            NameOffset = offset,
            NameLength = (ushort)name.Length,
            Flags = flags,
            LogicalSize = logicalSize,
            AllocatedSize = allocatedSize,
            LastWriteUtc = lastWriteUtc.ToFileTimeUtc(),
            LastAccessUtc = lastWriteUtc.ToFileTimeUtc(),
            CreatedUtc = createdUtc.ToFileTimeUtc(),
            HardLinkCount = 1,
        };

        InvalidateAggregates();
        return recordNumber;
    }

    /// <summary>Is <paramref name="candidate"/> somewhere below <paramref name="ancestor"/>?</summary>
    private bool IsDescendant(int candidate, int ancestor)
    {
        int root = RootIndex;
        int current = candidate;

        for (int guard = 0; guard < 512; guard++)
        {
            if (current == ancestor) return true;
            if (current == root) return false;

            uint parent = Entries[current].ParentIndex;
            if (parent >= (uint)Entries.Length || parent == (uint)current) return false;

            current = (int)parent;
        }

        return false;
    }

    /// <summary>
    /// The entry a full path names, or -1 when this index has never heard of it.
    /// <para>
    /// Walks down from the root comparing one name at a time. No dictionary of paths is
    /// kept anywhere — a million of them is exactly the cost the flat index exists to
    /// avoid — and a lookup this shallow costs one pass over each folder on the way.
    /// </para>
    /// </summary>
    public int FindEntry(ReadOnlySpan<char> path)
    {
        int root = RootIndex;

        string rootPath = GetFullPath(root);
        if (rootPath.Length == 0) return -1;

        ReadOnlySpan<char> rest = path.Trim().TrimEnd('\\');
        ReadOnlySpan<char> prefix = rootPath.AsSpan().TrimEnd('\\');

        if (rest.Length < prefix.Length) return -1;
        if (!rest[..prefix.Length].Equals(prefix, StringComparison.OrdinalIgnoreCase)) return -1;

        rest = rest[prefix.Length..];
        if (rest.Length > 0 && rest[0] != '\\') return -1;

        int current = root;

        while (rest.Length > 0)
        {
            rest = rest[1..];   // the separator

            int cut = rest.IndexOf('\\');
            ReadOnlySpan<char> component = cut < 0 ? rest : rest[..cut];
            rest = cut < 0 ? default : rest[cut..];

            if (component.Length == 0) continue;

            int found = -1;

            foreach (int child in GetChildren(current))
            {
                if (!Entries[child].IsInUse) continue;
                if (!GetName(child).Equals(component, StringComparison.OrdinalIgnoreCase)) continue;

                found = child;
                break;
            }

            if (found < 0) return -1;
            current = found;
        }

        return current;
    }

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

            // ⚠️ A file with several names is ONE record, and the MFT read keeps one entry
            // per record — so it is credited once, to the folder of the name the entry
            // holds, and that is the "once" this was always meant to be. It used to be
            // credited to nobody, which skipped every one of them: measured on the snapshot
            // of my C: of 10 September, 268,790 such files and 10.48 GiB of clusters left out
            // of the volume and of every folder holding one — 3.38 GiB of it under my own
            // profile — while the logical total, two properties down, counted them.
            long logical = e.LogicalSize;
            long physical = GetSizeOnDisk(i);

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

    // --- índice de filhos (CSR) -------------------------------------------
    private int[]? _childStart;   // childStart[i]..childStart[i+1] delimita os filhos de i
    private int[]? _childList;

    /// <summary>
    /// How many times the child index has been built over the whole entry array.
    /// <para>
    /// For the tests, which hold this to a handful across an operation that touches
    /// thousands of entries. Each build is two passes over every record on the volume — 55 ms
    /// on 3.7 M — and a loop that triggers one per item is a freeze that no profiler run on a
    /// small test index would ever show.
    /// </para>
    /// </summary>
    internal int ChildIndexBuilds { get; private set; }

    /// <summary>
    /// Constrói o índice de filhos em duas passadas O(n), no formato CSR
    /// (dois arrays planos, como matriz esparsa).
    /// <para>
    /// Um <c>Dictionary&lt;int, List&lt;int&gt;&gt;</c> seria o caminho óbvio e custaria
    /// centenas de MB em um volume com 2,8 milhões de entradas: um objeto
    /// <c>List</c> por diretório, mais o overhead de bucket do dicionário. Aqui são
    /// dois <c>int[]</c> — cerca de 23 MB para o mesmo volume.
    /// </para>
    /// </summary>
    public void BuildChildIndex()
    {
        if (_childStart is not null) return;

        ChildIndexBuilds++;

        int n = Entries.Length;
        var counts = new int[n + 1];
        int root = RootIndex;

        // 1ª passada: quantos filhos cada diretório tem.
        for (int i = 0; i < n; i++)
        {
            if (!Entries[i].IsInUse || i == root) continue;

            uint parent = Entries[i].ParentIndex;
            if (parent >= (uint)n || parent == (uint)i) continue;

            counts[parent + 1]++;
        }

        // Soma de prefixo transforma as contagens em deslocamentos.
        for (int i = 0; i < n; i++) counts[i + 1] += counts[i];

        var list = new int[counts[n]];
        var cursor = new int[n];

        // 2ª passada: coloca cada filho na fatia do pai.
        for (int i = 0; i < n; i++)
        {
            if (!Entries[i].IsInUse || i == root) continue;

            uint parent = Entries[i].ParentIndex;
            if (parent >= (uint)n || parent == (uint)i) continue;

            list[counts[parent] + cursor[parent]++] = i;
        }

        _childStart = counts;
        _childList = list;
    }

    /// <summary>Filhos diretos de uma entrada, como fatia do array compartilhado.</summary>
    public ReadOnlySpan<int> GetChildren(int index)
    {
        BuildChildIndex();

        if (index < 0 || index >= Entries.Length) return default;

        int from = _childStart![index];
        int to = _childStart[index + 1];
        return _childList!.AsSpan(from, to - from);
    }

    /// <summary>Quantos filhos diretos, sem materializar nada.</summary>
    public int GetChildCount(int index)
    {
        BuildChildIndex();
        if (index < 0 || index >= Entries.Length) return 0;
        return _childStart![index + 1] - _childStart[index];
    }

    /// <summary>Esta entrada tem ao menos um subdiretório? Decide se a árvore mostra a seta.</summary>
    public bool HasChildDirectories(int index)
    {
        foreach (int child in GetChildren(index))
            if (Entries[child].IsDirectory) return true;
        return false;
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

                // Once per record, names or not: see BuildSubtreeSizes.
                total += GetSizeOnDisk(i);
            }
            return total;
        }
    }

    /// <summary>
    /// How the measured total compares with what the filesystem reports as used.
    /// <para>
    /// The two can never match exactly — see <see cref="Reconciliation"/> — but they must
    /// stay in the same neighbourhood. This check exists because they once did not: a
    /// sparse Alternate Data Stream was counted at its logical size, and the app cheerfully
    /// printed "758 GiB on disk" for a 476 GiB volume, right above the correct used figure.
    /// Nothing complained, because nothing was comparing them.
    /// </para>
    /// </summary>
    public Reconciliation CheckAgainstFileSystem()
    {
        long measured = TotalBytesOnDisk;
        long reported = Volume.UsedBytes;

        return new Reconciliation(measured, reported, Strategy);
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
