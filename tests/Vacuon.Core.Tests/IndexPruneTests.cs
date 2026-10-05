using Vacuon.Core.Actions;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// What a delete that did not finish leaves the index believing.
/// <para>
/// A permanent delete of a folder with one file held open takes everything else and fails on
/// the folder. The index kept the whole folder, so the list went on showing files the disk no
/// longer had, and the totals went on counting them.
/// </para>
/// </summary>
public class IndexPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-prune-tests-" + Guid.NewGuid().ToString("N"));

    public IndexPruneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A walk index of <see cref="_root"/>: <c>keep\k.bin</c>, <c>gone\g1.bin</c>,
    /// <c>gone\g2.bin</c>, <c>a.bin</c> and <c>b.bin</c>, all of them on the disk too.
    /// </summary>
    private VolumeIndex Indexed()
    {
        var names = new NameBlob(256);
        var entries = new FileEntry[8];

        void Set(int i, string name, int parent, long size, bool dir = false)
        {
            entries[i] = new FileEntry
            {
                RecordNumber = (uint)i,
                ParentIndex = (uint)parent,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = dir ? EntryFlags.Directory : EntryFlags.None,
                LogicalSize = size,
                AllocatedSize = size,
                HardLinkCount = 1,
            };

            string path = i == 0 ? _root : Path.Combine(PathOf(parent), name);
            _paths[i] = path;

            if (i == 0) return;
            if (dir) Directory.CreateDirectory(path);
            else File.WriteAllBytes(path, new byte[size]);
        }

        Set(0, _root, 0, 0, dir: true);
        Set(1, "keep", 0, 0, dir: true);
        Set(2, "k.bin", 1, 10);
        Set(3, "gone", 0, 0, dir: true);
        Set(4, "g1.bin", 3, 20);
        Set(5, "g2.bin", 3, 30);
        Set(6, "a.bin", 0, 40);
        Set(7, "b.bin", 0, 50);

        var volume = new VolumeInfo(char.ToUpperInvariant(_root[0]), "Test", "NTFS", 1L << 40, 1L << 39, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Win32Walk);
    }

    private readonly Dictionary<int, string> _paths = [];

    private string PathOf(int entry) => _paths[entry];

    [Fact]
    public void WhatLeftTheDisk_LeavesTheIndex_AndNothingElseDoes()
    {
        VolumeIndex index = Indexed();

        // What a purge that stopped on one held file leaves: a whole subfolder gone, one
        // loose file gone, the rest still there.
        Directory.Delete(PathOf(3), recursive: true);
        File.Delete(PathOf(7));

        Removal removed = IndexPrune.Vanished(index, 0);

        Assert.Equal(4, removed.Entries);              // gone, g1, g2, b.bin
        Assert.Equal(100, removed.LogicalBytes);       // 20 + 30 + 50

        Assert.True(index.Entries[1].IsInUse);
        Assert.True(index.Entries[2].IsInUse);
        Assert.True(index.Entries[6].IsInUse);
        Assert.False(index.Entries[3].IsInUse);
        Assert.False(index.Entries[4].IsInUse);
        Assert.False(index.Entries[7].IsInUse);

        Assert.Equal(50, index.GetSubtreeSize(0));     // k.bin and a.bin
    }

    [Fact]
    public void AFolderThatIsGone_TakesItsSubtreeWithoutAskingAboutEachFile()
    {
        VolumeIndex index = Indexed();
        Directory.Delete(PathOf(3), recursive: true);

        int builds = index.ChildIndexBuilds;
        Removal removed = IndexPrune.Vanished(index, 3);

        Assert.Equal(3, removed.Entries);
        Assert.Equal(50, removed.LogicalBytes);
        Assert.True(index.ChildIndexBuilds - builds <= 1);
    }

    [Fact]
    public void NothingGone_NothingTaken()
    {
        VolumeIndex index = Indexed();

        Assert.True(IndexPrune.Vanished(index, 0).IsEmpty);
        Assert.Equal(150, index.GetSubtreeSize(0));
    }
}
