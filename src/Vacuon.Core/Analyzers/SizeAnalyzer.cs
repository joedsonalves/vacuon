using Vacuon.Core.Index;

namespace Vacuon.Core.Analyzers;

public readonly record struct SizedItem(int Index, long LogicalSize, long SizeOnDisk, int FileCount);

public readonly record struct ExtensionBucket(string Extension, long TotalBytes, int Count)
{
    public string Category => FileCategories.Of(Extension);
}

/// <summary>Faixas de tamanho — responde "arquivos pequenos que atrapalham" (PRD F2.7).</summary>
public readonly record struct SizeBucket(string Label, long MinBytes, long MaxBytes, int Count, long TotalBytes, long SlackBytes);

/// <summary>Faixas de idade por último acesso/escrita (PRD F2.6).</summary>
public readonly record struct AgeBucket(string Label, int Count, long TotalBytes);

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
        (string Label, long Max)[] ranges =
        [
            ("0 B (vazio)",     0),
            ("1 B – 4 KB",      4L * 1024),
            ("4 KB – 64 KB",    64L * 1024),
            ("64 KB – 1 MB",    1024L * 1024),
            ("1 MB – 16 MB",    16L * 1024 * 1024),
            ("16 MB – 128 MB",  128L * 1024 * 1024),
            ("128 MB – 1 GB",   1024L * 1024 * 1024),
            ("1 GB – 8 GB",     8L * 1024 * 1024 * 1024),
            ("acima de 8 GB",   long.MaxValue),
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
            result.Add(new SizeBucket(ranges[i].Label, min, ranges[i].Max, counts[i], totals[i], slack[i]));
            min = ranges[i].Max + 1;
        }
        return result;
    }

    public static List<AgeBucket> ByAge(VolumeIndex index, DateTime nowUtc)
    {
        (string Label, int Days)[] ranges =
        [
            ("últimos 7 dias", 7),
            ("7 – 30 dias", 30),
            ("30 – 90 dias", 90),
            ("90 dias – 1 ano", 365),
            ("1 – 2 anos", 730),
            ("mais de 2 anos", int.MaxValue),
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
            result.Add(new AgeBucket(ranges[i].Label, counts[i], totals[i]));
        return result;
    }

    internal static string ExtractExtension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return "(sem extensão)";
        ReadOnlySpan<char> ext = name[(dot + 1)..];
        if (ext.Length > 12) return "(sem extensão)"; // "extensão" absurda = provavelmente ponto no nome
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
