using Vacuon.Core.Actions;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Planting a freshly copied tree into the index on screen.
/// <para>
/// The graft runs on the UI thread the moment the transfer window closes, so its cost is a
/// freeze of the whole app. It used to rebuild the child index once per file — 55 ms each on
/// a real index of 3.7 M records, so a copy of 5,000 files that robocopy finished in 1.6 s
/// held the window for 258 s afterwards — and a test index of a few dozen entries rebuilds in
/// microseconds, so nothing timed could ever have caught it. These count the rebuilds
/// instead, which is the same on any machine.
/// </para>
/// <para>
/// The files are real and the walk is real; only the record numbers are stood in for. The
/// file system hands out numbers in the millions, and an index able to hold them would be a
/// quarter of a gigabyte per test.
/// </para>
/// </summary>
public class IndexGraftTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-graft-tests-" + Guid.NewGuid().ToString("N"));

    public IndexGraftTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // Links first, each by itself: the recursive delete reports a junction as an error
            // after removing it, and leaves the folder.
            if (Directory.Exists(_root))
            {
                foreach (string link in Links.FoldersBelow(_root)) Links.Remove(link);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// An MFT index holding the chain of folders down to <see cref="_root"/>, with room for
    /// <paramref name="room"/> more entries after it.
    /// </summary>
    private VolumeIndex IndexDownToRoot(int room, out int rootEntry, out int firstFree)
    {
        string[] parts = _root.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var names = new NameBlob(4096);
        var entries = new FileEntry[16 + parts.Length + room];

        int Add(int at, string name, int parent, bool directory, long size = 0)
        {
            entries[at] = new FileEntry
            {
                RecordNumber = (uint)at,
                ParentIndex = (uint)parent,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = directory ? EntryFlags.Directory : EntryFlags.None,
                LogicalSize = size,
                AllocatedSize = size,
                HardLinkCount = 1,
            };
            return at;
        }

        int current = Add(5, ".", 5, directory: true);

        // parts[0] is the drive, "C:"; the root entry already stands for it.
        for (int i = 1; i < parts.Length; i++) current = Add(15 + i, parts[i], current, directory: true);

        rootEntry = current;
        firstFree = 16 + parts.Length;

        var volume = new VolumeInfo(char.ToUpperInvariant(_root[0]), "Test", "NTFS",
                                    1L << 40, 1L << 39, 4096, false);

        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft);
    }

    /// <summary>A tree shaped like a real copy: a few folders, many files.</summary>
    private string CopiedTree(string name, int files, int perFolder = 250)
    {
        string tree = Path.Combine(_root, name);

        for (int f = 0; f < files; f++)
        {
            string folder = Path.Combine(tree, $"sub{f / perFolder:D2}");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"file{f:D4}.bin"), new byte[f % 7 + 1]);
        }

        return tree;
    }

    /// <summary>Small stand-in record numbers, one per path in the tree, handed out from <paramref name="first"/>.</summary>
    private static Dictionary<string, long> NumberTheTree(string tree, int first)
    {
        var records = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [tree] = first++ };

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (string path in Directory.EnumerateFileSystemEntries(tree, "*", options))
            records[path] = first++;

        return records;
    }

    [Fact]
    public void AThousandCopiedFiles_GoInWithoutRebuildingTheChildIndexPerFile()
    {
        string tree = CopiedTree("copy", files: 1000);
        VolumeIndex index = IndexDownToRoot(room: 1100, out _, out int firstFree);
        Dictionary<string, long> records = NumberTheTree(tree, firstFree);

        index.BuildChildIndex();
        int before = index.ChildIndexBuilds;

        GraftResult graft = IndexGraft.AddTree(index, tree,
            path => records.TryGetValue(path, out long record) ? record : -1);

        Assert.True(graft.Complete);
        Assert.Equal(records.Count, graft.Added);

        // The one lookup by path at the top may need a fresh child index; a thousand files
        // must not need a thousand. This was 1,005 before.
        Assert.True(index.ChildIndexBuilds - before <= 2,
                    $"{index.ChildIndexBuilds - before} child index builds for one graft");
    }

    [Fact]
    public void WhatWasPlanted_IsWhereTheDiskSaysAndWeighsWhatTheDiskSays()
    {
        string tree = CopiedTree("copy", files: 30, perFolder: 10);
        VolumeIndex index = IndexDownToRoot(room: 64, out int rootEntry, out int firstFree);
        Dictionary<string, long> records = NumberTheTree(tree, firstFree);

        long before = index.TotalLogicalBytes;

        IndexGraft.AddTree(index, tree, path => records.TryGetValue(path, out long record) ? record : -1);

        int planted = index.FindEntry(tree);
        Assert.Equal((int)records[tree], planted);
        Assert.Equal(rootEntry, (int)index.Entries[planted].ParentIndex);

        foreach ((string path, long record) in records)
        {
            Assert.Equal(record, index.FindEntry(path));

            if (File.Exists(path))
                Assert.Equal(new FileInfo(path).Length, index.Entries[record].LogicalSize);
        }

        // Every byte the copy wrote, and not one more.
        long written = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
                                .Sum(f => new FileInfo(f).Length);

        Assert.Equal(before + written, index.TotalLogicalBytes);
        Assert.Equal(30, index.GetSubtreeFileCount(planted));
    }

    [Fact]
    public void ALinkInTheCopiedTree_IsPlantedAsItself_AndNotEntered()
    {
        // The transfer carries links as links, so a copied tree can hold a junction. What is
        // behind it lives somewhere else, and the MFT lists none of it under the link — entering
        // would count that folder a second time, under a path it does not have.
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "precious.bin"), new byte[70_000]);

        string tree = Path.Combine(_root, "copy");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "mine.bin"), new byte[300]);
        string door = Path.Combine(tree, "door");
        Assert.True(Junction.Create(door, outside));

        VolumeIndex index = IndexDownToRoot(room: 32, out _, out int firstFree);
        long before = index.TotalLogicalBytes;

        long next = firstFree;
        var asked = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long RecordOf(string path) => asked.TryGetValue(path, out long record) ? record : asked[path] = next++;

        GraftResult graft = IndexGraft.AddTree(index, tree, RecordOf);

        Assert.True(graft.Complete);
        Assert.True(index.FindEntry(door) >= 0);
        Assert.DoesNotContain(asked.Keys, path => path.StartsWith(door + "\\", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, index.GetSubtreeFileCount(index.FindEntry(tree)));
        Assert.Equal(before + 300, index.TotalLogicalBytes);
    }

    [Fact]
    public void StaleFilesInTheSlots_AreClearedWithoutRebuildingTheChildIndexEither()
    {
        // NTFS recycles records fast, so a freshly copied file often lands on the record of
        // one the index still remembers. Clearing that occupant goes through MarkDeleted —
        // which used to ask for the children of a FILE, and rebuild the index to answer.
        string tree = CopiedTree("copy", files: 300);
        VolumeIndex index = IndexDownToRoot(room: 400, out int rootEntry, out int firstFree);
        Dictionary<string, long> records = NumberTheTree(tree, firstFree);

        int stale = 0;

        foreach (long record in records.Values)
        {
            if (record % 2 != 0) continue;

            index.AddFile((int)record, rootEntry, $"gone-{record}.tmp", 100, 4096,
                          DateTime.UtcNow, DateTime.UtcNow);
            stale++;
        }

        long before = index.TotalLogicalBytes;
        index.BuildChildIndex();
        int builds = index.ChildIndexBuilds;

        // The stale names resolve to nothing, as a deleted file's would.
        GraftResult graft = IndexGraft.AddTree(index, tree,
            path => records.TryGetValue(path, out long record) ? record : -1);

        Assert.True(graft.Complete);
        Assert.True(stale > 100);
        Assert.True(index.ChildIndexBuilds - builds <= 2,
                    $"{index.ChildIndexBuilds - builds} child index builds for {stale} stale slots");

        // The occupants left, and their bytes left with them.
        long written = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
                                .Sum(f => new FileInfo(f).Length);

        Assert.Equal(before - stale * 100L + written, index.TotalLogicalBytes);
    }

    [Fact]
    public void AFolderThatVanishesMidGraft_LeavesItIncomplete_AndDoesNotThrow()
    {
        // crash.log, 9 September: a DirectoryNotFoundException from the graft's enumerator
        // closed the app right after a copy. Here the folder goes away at the one moment that
        // reproduces it — after it has been planted, before it is opened.
        string tree = CopiedTree("copy", files: 30, perFolder: 10);
        VolumeIndex index = IndexDownToRoot(room: 64, out _, out int firstFree);
        Dictionary<string, long> records = NumberTheTree(tree, firstFree);

        string doomed = Path.Combine(tree, "sub01");

        long RecordOf(string path)
        {
            if (string.Equals(path, doomed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(doomed))
                Directory.Delete(doomed, recursive: true);

            return records.TryGetValue(path, out long record) ? record : -1;
        }

        GraftResult graft = IndexGraft.AddTree(index, tree, RecordOf);

        Assert.False(graft.Complete);

        // Everything that was still there went in.
        Assert.True(index.FindEntry(Path.Combine(tree, "sub00", "file0000.bin")) >= 0);
        Assert.True(index.FindEntry(Path.Combine(tree, "sub02", "file0029.bin")) >= 0);
    }

    [Fact]
    public void AFolderMadeAMomentAgo_IsAdoptedOverARecordAFileLeftBehind()
    {
        // "Move to" into a folder just made, or the quarantine's batch folder: the folder very
        // often gets the record of a file deleted since the scan. Locate used to give up, and
        // the list said it could not place what had moved.
        string folder = Path.Combine(_root, "nova");
        Directory.CreateDirectory(folder);

        VolumeIndex index = IndexDownToRoot(room: 8, out int rootEntry, out int firstFree);
        index.AddFile(firstFree, rootEntry, "apagado-depois.tmp", 500, 4096, DateTime.UtcNow, DateTime.UtcNow);

        long before = index.TotalLogicalBytes;

        // The folder's record is the stale file's; the stale file's own path resolves to nothing.
        int located = MoveTarget.Locate(index, folder,
            path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ? firstFree : -1);

        Assert.Equal(firstFree, located);
        Assert.True(index.Entries[located].IsDirectory);
        Assert.Equal(rootEntry, (int)index.Entries[located].ParentIndex);
        Assert.Equal(before - 500, index.TotalLogicalBytes);
    }

    [Fact]
    public void AFolderWhoseRecordIsStillReallyTaken_IsNotForcedIn()
    {
        // The disk decides: an occupant that is still that record is a real disagreement, and
        // papering over it would bury it.
        string folder = Path.Combine(_root, "nova");
        Directory.CreateDirectory(folder);

        VolumeIndex index = IndexDownToRoot(room: 8, out int rootEntry, out int firstFree);
        index.AddFile(firstFree, rootEntry, "ainda-aqui.tmp", 500, 4096, DateTime.UtcNow, DateTime.UtcNow);

        int located = MoveTarget.Locate(index, folder, _ => firstFree);

        Assert.Equal(-1, located);
        Assert.False(index.Entries[firstFree].IsDirectory);
        Assert.True(index.Entries[firstFree].IsInUse);
    }

    [Fact]
    public void AFileHasNoChildren_SoFreeingOneDoesNotAskForThem()
    {
        VolumeIndex index = IndexDownToRoot(room: 8, out int rootEntry, out int firstFree);
        index.AddFile(firstFree, rootEntry, "one.bin", 10, 4096, DateTime.UtcNow, DateTime.UtcNow);
        index.AddFile(firstFree + 1, rootEntry, "two.bin", 20, 4096, DateTime.UtcNow, DateTime.UtcNow);

        int builds = index.ChildIndexBuilds;

        Removal first = index.MarkDeleted(firstFree);
        Removal second = index.MarkDeleted(firstFree + 1);

        Assert.Equal(10, first.LogicalBytes);
        Assert.Equal(20, second.LogicalBytes);
        Assert.Equal(builds, index.ChildIndexBuilds);
    }
}
