using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

/// <summary>
/// Reads again what files occupy on the disk, after something changed how they are stored.
/// <para>
/// Compressing gives clusters back without changing a single length, so nothing in the index
/// moved: the list went on showing every compressed file at the size it had before, the
/// folders above them too, beside a status line saying how much had just come back. This
/// reads the clusters the file system now reports, for exactly the files that were touched.
/// </para>
/// <para>
/// Three steps, because the index is the window's: the files are listed on the window's
/// thread, which is where the child index is built and read; the disk is asked off it, one
/// call a file; and the answers are written back on the window's thread again.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class OnDiskRemeasure
{
    /// <summary>The files at and under <paramref name="roots"/>, with their paths.</summary>
    public static List<(int Entry, string Path)> FilesUnder(VolumeIndex index, IEnumerable<int> roots)
    {
        ArgumentNullException.ThrowIfNull(index);

        var files = new List<(int, string)>();
        var pending = new Stack<int>();

        foreach (int root in roots)
            if (root >= 0 && root < index.Entries.Length && index.Entries[root].IsInUse) pending.Push(root);

        while (pending.Count > 0)
        {
            int current = pending.Pop();

            if (index.Entries[current].IsDirectory)
            {
                foreach (int child in index.GetChildren(current))
                    if (index.Entries[child].IsInUse) pending.Push(child);

                continue;
            }

            string path = index.GetFullPath(current);
            if (path.Length > 0) files.Add((current, path));
        }

        return files;
    }

    /// <summary>What each file occupies now, and whether it is stored compressed. Any thread.</summary>
    /// <param name="clusterBytes">The volume's cluster, to round a plain file's length up to.</param>
    public static List<(int Entry, long OnDisk, bool Compressed)> Measure(
        IReadOnlyList<(int Entry, string Path)> files, uint clusterBytes)
    {
        var measured = new List<(int, long, bool)>(files.Count);

        foreach ((int entry, string path) in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;

                bool compressed = (info.Attributes & FileAttributes.Compressed) != 0;
                measured.Add((entry, OnDisk(path, info, clusterBytes), compressed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
            {
                // Unreadable now: the index keeps what it had rather than a guess.
            }
        }

        return measured;
    }

    /// <summary>Writes the measurements into the index, on the window's thread.</summary>
    /// <returns>Whether any entry changed, and the views need to read the index again.</returns>
    public static bool Apply(VolumeIndex index, IReadOnlyList<(int Entry, long OnDisk, bool Compressed)> measured)
    {
        ArgumentNullException.ThrowIfNull(index);

        bool changed = false;

        foreach ((int entry, long onDisk, bool compressed) in measured)
        {
            if (entry < 0 || entry >= index.Entries.Length) continue;

            ref FileEntry e = ref index.Entries[entry];
            if (!e.IsInUse || e.IsDirectory) continue;

            EntryFlags flags = compressed ? e.Flags | EntryFlags.Compressed : e.Flags & ~EntryFlags.Compressed;
            if (e.AllocatedSize == onDisk && e.Flags == flags) continue;

            e.AllocatedSize = onDisk;
            e.Flags = flags;
            changed = true;
        }

        if (changed) index.InvalidateAggregates();
        return changed;
    }

    /// <summary>
    /// The clusters a file holds: what the file system says for one stored compressed or
    /// sparse, and its length rounded up to a cluster for one that is not — which is what a
    /// plain file holds, and what <c>GetCompressedFileSize</c> would not round for it.
    /// </summary>
    internal static long OnDisk(string path, FileInfo info, uint clusterBytes)
    {
        if ((info.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            return Kernel32.CompressedSizeOf(path);

        long length = info.Length;
        return clusterBytes == 0 ? length : (length + clusterBytes - 1) / clusterBytes * clusterBytes;
    }
}
