using Vacuon.Core.Index;
using Vacuon.Core.Localization;

namespace Vacuon.Core.Analyzers;

public readonly record struct SizedItem(int Index, long LogicalSize, long SizeOnDisk, int FileCount);

public readonly record struct ExtensionBucket(string Extension, long TotalBytes, int Count)
{
    /// <summary>Chave estável da categoria — use para cor e comparação.</summary>
    public string CategoryKey => FileCategories.Of(Extension);

    /// <summary>Nome da categoria no idioma ativo.</summary>
    public string Category => FileCategories.DisplayName(CategoryKey);

    /// <summary>
    /// Extensão como texto. Arquivos sem extensão carregam uma chave de tradução em
    /// vez de um literal, então este é o único lugar que precisa saber disso.
    /// </summary>
    public string DisplayExtension =>
        Extension == FileCategories.NoExtension ? L.T(FileCategories.NoExtension) : Extension;
}

/// <summary>Faixas de tamanho — responde "arquivos pequenos que atrapalham" (PRD F2.7).</summary>
public readonly record struct SizeBucket(string LabelKey, long MinBytes, long MaxBytes,
                                         int Count, long TotalBytes, long SlackBytes)
{
    /// <summary>Rótulo no idioma ativo. Resolvido na leitura, não na construção,
    /// para que trocar o idioma não exija revarrer o disco.</summary>
    public string Label => L.T(LabelKey);
}

/// <summary>Faixas de idade por último acesso/escrita (PRD F2.6).</summary>
public readonly record struct AgeBucket(string LabelKey, int Count, long TotalBytes)
{
    public string Label => L.T(LabelKey);
}

/// <summary>
/// Consultas de tamanho sobre o índice. Tudo em passada linear sobre arrays planos,
/// sem LINQ no caminho quente.
/// </summary>
public static class SizeAnalyzer
{
    public static List<SizedItem> TopFiles(VolumeIndex index, int take)
    {
        var heap = new MinHeap(take);

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;
            heap.Offer(new SizedItem(i, e.LogicalSize, e.SizeOnDisk, 1), e.LogicalSize);
        }

        return heap.ToSortedDescending();
    }

    public static List<SizedItem> TopFolders(VolumeIndex index, int take, bool bySubtree = true)
    {
        index.BuildSubtreeSizes();
        var heap = new MinHeap(take);

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || !e.IsDirectory) continue;

            long size = bySubtree ? index.GetSubtreeSize(i) : OwnSize(index, i);
            if (size <= 0) continue;

            heap.Offer(new SizedItem(i, size, index.GetSubtreeSizeOnDisk(i), index.GetSubtreeFileCount(i)), size);
        }

        return heap.ToSortedDescending();
    }

    /// <summary>Tamanho dos arquivos diretamente dentro da pasta, sem descer.</summary>
    private static long OwnSize(VolumeIndex index, int folderIndex)
    {
        long total = 0;
        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (e.IsInUse && !e.IsDirectory && e.ParentIndex == folderIndex) total += e.LogicalSize;
        }
        return total;
    }

    public static List<ExtensionBucket> ByExtension(VolumeIndex index, int take)
    {
        var map = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;

            ReadOnlySpan<char> name = index.GetName(i);
            string ext = ExtractExtension(name);

            map.TryGetValue(ext, out (long Bytes, int Count) acc);
            map[ext] = (acc.Bytes + e.LogicalSize, acc.Count + 1);
        }

        var list = new List<ExtensionBucket>(map.Count);
        foreach ((string ext, (long bytes, int count)) in map)
            list.Add(new ExtensionBucket(ext, bytes, count));

        list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return list.Count > take ? list.GetRange(0, take) : list;
    }

    public static List<SizeBucket> BySizeRange(VolumeIndex index)
    {
        (string Key, long Max)[] ranges =
        [
            ("sizeBucket.empty",     0),
            ("sizeBucket.tiny",      4L * 1024),
            ("sizeBucket.small",     64L * 1024),
            ("sizeBucket.medium",    1024L * 1024),
            ("sizeBucket.large",     16L * 1024 * 1024),
            ("sizeBucket.big",       128L * 1024 * 1024),
            ("sizeBucket.huge",      1024L * 1024 * 1024),
            ("sizeBucket.giant",     8L * 1024 * 1024 * 1024),
            ("sizeBucket.colossal",  long.MaxValue),
        ];

        var counts = new int[ranges.Length];
        var totals = new long[ranges.Length];
        var slack = new long[ranges.Length];

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;

            int bucket = 0;
            while (bucket < ranges.Length - 1 && e.LogicalSize > ranges[bucket].Max) bucket++;

            counts[bucket]++;
            totals[bucket] += e.LogicalSize;

            if (e.AllocatedSize > e.LogicalSize && (e.Flags & (EntryFlags.Compressed | EntryFlags.Sparse)) == 0)
                slack[bucket] += e.AllocatedSize - e.LogicalSize;
        }

        var result = new List<SizeBucket>(ranges.Length);
        long min = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            result.Add(new SizeBucket(ranges[i].Key, min, ranges[i].Max, counts[i], totals[i], slack[i]));
            min = ranges[i].Max + 1;
        }
        return result;
    }

    public static List<AgeBucket> ByAge(VolumeIndex index, DateTime nowUtc)
    {
        (string Key, int Days)[] ranges =
        [
            ("ageBucket.week", 7),
            ("ageBucket.month", 30),
            ("ageBucket.quarter", 90),
            ("ageBucket.year", 365),
            ("ageBucket.twoYears", 730),
            ("ageBucket.older", int.MaxValue),
        ];

        var counts = new int[ranges.Length];
        var totals = new long[ranges.Length];

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;

            DateTime stamp = e.LastWrite;
            if (stamp == DateTime.MinValue) continue;

            double days = (nowUtc - stamp).TotalDays;
            int bucket = 0;
            while (bucket < ranges.Length - 1 && days > ranges[bucket].Days) bucket++;

            counts[bucket]++;
            totals[bucket] += e.LogicalSize;
        }

        var result = new List<AgeBucket>(ranges.Length);
        for (int i = 0; i < ranges.Length; i++)
            result.Add(new AgeBucket(ranges[i].Key, counts[i], totals[i]));
        return result;
    }

    internal static string ExtractExtension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return FileCategories.NoExtension;
        ReadOnlySpan<char> ext = name[(dot + 1)..];
        if (ext.Length > 12) return FileCategories.NoExtension; // "extensão" absurda = ponto no nome
        return string.Concat(".", ext).ToLowerInvariant();
    }

    /// <summary>Heap mínimo de tamanho fixo: top-N sem ordenar 1 milhão de itens.</summary>
    private sealed class MinHeap(int capacity)
    {
        private readonly List<(SizedItem Item, long Key)> _items = new(Math.Max(capacity, 1));
        private readonly int _capacity = Math.Max(capacity, 1);

        public void Offer(SizedItem item, long key)
        {
            if (_items.Count < _capacity)
            {
                _items.Add((item, key));
                if (_items.Count == _capacity) _items.Sort(static (a, b) => a.Key.CompareTo(b.Key));
                return;
            }

            if (key <= _items[0].Key) return;

            _items[0] = (item, key);
            // Reinserção linear: com N pequeno (100–1000) é mais rápido que um sift-down
            // genérico e mantém o código legível.
            int i = 0;
            while (i + 1 < _items.Count && _items[i].Key > _items[i + 1].Key)
            {
                (_items[i], _items[i + 1]) = (_items[i + 1], _items[i]);
                i++;
            }
        }

        public List<SizedItem> ToSortedDescending()
        {
            var copy = new List<(SizedItem Item, long Key)>(_items);
            copy.Sort(static (a, b) => b.Key.CompareTo(a.Key));

            var result = new List<SizedItem>(copy.Count);
            foreach ((SizedItem item, _) in copy) result.Add(item);
            return result;
        }
    }
}
