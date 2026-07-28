using System.Runtime.InteropServices;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Xunit;

namespace Vacuon.Core.Tests;

public class FileEntryTests
{
    [Fact]
    public void FileEntry_IsExactlySixtyFourBytes()
    {
        // Contrato de memória do índice: 1 M de arquivos = 64 MB previsíveis.
        // Se este teste quebrar, alguém encostou no layout e o orçamento de RAM mudou.
        Assert.Equal(64, Marshal.SizeOf<FileEntry>());
    }

    [Fact]
    public void SizeOnDisk_CoversTheMainStreamOnly()
    {
        // ADS mora em tabela lateral justamente para não custar 8 bytes por arquivo.
        var entry = new FileEntry { AllocatedSize = 4096 };
        Assert.Equal(4096, entry.SizeOnDisk);
    }

    [Fact]
    public void Timestamps_OutOfRangeBecomeMinValue()
    {
        var entry = new FileEntry { LastWriteUtc = -5 };
        Assert.Equal(DateTime.MinValue, entry.LastWrite);
    }
}

public class NameBlobTests
{
    [Fact]
    public void Append_ReturnsOffsetsThatRoundTrip()
    {
        var blob = new NameBlob(8);

        int a = blob.Append("primeiro.txt");
        int b = blob.Append("segundo.mkv");

        Assert.Equal("primeiro.txt", blob.Get(a, 12).ToString());
        Assert.Equal("segundo.mkv", blob.Get(b, 11).ToString());
    }

    [Fact]
    public void Append_GrowsBeyondInitialCapacity()
    {
        var blob = new NameBlob(4);
        var offsets = new List<(int Offset, int Length)>();

        for (int i = 0; i < 1000; i++)
        {
            string name = $"arquivo-{i:D4}.dat";
            offsets.Add((blob.Append(name), name.Length));
        }

        Assert.Equal("arquivo-0999.dat", blob.Get(offsets[999].Offset, offsets[999].Length).ToString());
    }
}

public class VolumeIndexTests
{
    /// <summary>
    /// Monta um índice pequeno no formato da MFT: a raiz é o registro 5.
    /// </summary>
    private static VolumeIndex BuildSample()
    {
        var names = new NameBlob(256);
        var entries = new FileEntry[16];

        void Set(int i, string name, uint parent, long size, bool dir = false, ushort links = 1)
        {
            entries[i] = new FileEntry
            {
                RecordNumber = (uint)i,
                ParentIndex = parent,
                NameOffset = names.Append(name),
                NameLength = (ushort)name.Length,
                Flags = dir ? EntryFlags.Directory : EntryFlags.None,
                LogicalSize = size,
                AllocatedSize = size,
                HardLinkCount = links,
            };
        }

        Set(5, ".", 5, 0, dir: true);            // raiz
        Set(6, "Videos", 5, 0, dir: true);
        Set(7, "render.mp4", 6, 1000);
        Set(8, "render_v2.mp4", 6, 2000);
        Set(9, "Documentos", 5, 0, dir: true);
        Set(10, "nota.txt", 9, 50);

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1_000_000, 500_000, 4096, false);
        return new VolumeIndex(entries, names, volume, ScanStrategy.Mft);
    }

    [Fact]
    public void GetFullPath_WalksUpToTheRoot()
    {
        VolumeIndex index = BuildSample();
        Assert.Equal(@"C:\Videos\render.mp4", index.GetFullPath(7));
        Assert.Equal(@"C:\Documentos\nota.txt", index.GetFullPath(10));
    }

    [Fact]
    public void GetFullPath_AppendsSeparatorForDirectories()
    {
        VolumeIndex index = BuildSample();
        Assert.Equal(@"C:\Videos\", index.GetFullPath(6));
    }

    [Fact]
    public void SubtreeSizes_AggregateUpTheTree()
    {
        VolumeIndex index = BuildSample();

        Assert.Equal(3000, index.GetSubtreeSize(6));   // Videos
        Assert.Equal(50, index.GetSubtreeSize(9));     // Documentos
        Assert.Equal(3050, index.GetSubtreeSize(5));   // raiz
        Assert.Equal(2, index.GetSubtreeFileCount(6));
    }

    [Fact]
    public void HardLinkedFile_CountsAgainstDiskOnlyOnce()
    {
        // Contar N vezes faria pastas como WinSxS parecerem ocupar o triplo do real.
        var names = new NameBlob(64);
        var entries = new FileEntry[8];

        entries[5] = new FileEntry
        {
            RecordNumber = 5, ParentIndex = 5,
            NameOffset = names.Append("."), NameLength = 1,
            Flags = EntryFlags.Directory,
        };
        entries[6] = new FileEntry
        {
            RecordNumber = 6, ParentIndex = 5,
            NameOffset = names.Append("compartilhado.dll"), NameLength = 17,
            LogicalSize = 1024, AllocatedSize = 4096, HardLinkCount = 3,
        };

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1000, 500, 4096, false);
        var index = new VolumeIndex(entries, names, volume, ScanStrategy.Mft);

        Assert.Equal(1024, index.TotalLogicalBytes);

        Assert.Equal(0, index.TotalBytesOnDisk);      // hardlink: não credita a ninguém
        Assert.Equal(0, index.GetSubtreeSizeOnDisk(5));
    }

    [Fact]
    public void ChildIndex_ListsDirectChildrenOnly()
    {
        VolumeIndex index = BuildSample();

        int[] videos = index.GetChildren(6).ToArray();
        Assert.Equal([7, 8], videos);

        int[] root = index.GetChildren(5).ToArray();
        Assert.Equal([6, 9], root);

        Assert.Empty(index.GetChildren(7).ToArray()); // arquivo não tem filhos
    }

    [Fact]
    public void ChildIndex_RootIsNotItsOwnChild()
    {
        // A raiz da MFT aponta para si mesma; incluí-la nos próprios filhos faria
        // a TreeView entrar em recursão infinita.
        VolumeIndex index = BuildSample();
        Assert.DoesNotContain(5, index.GetChildren(5).ToArray());
    }

    [Fact]
    public void HasChildDirectories_DistinguishesFoldersFromFiles()
    {
        VolumeIndex index = BuildSample();

        Assert.True(index.HasChildDirectories(5));   // raiz tem Videos e Documentos
        Assert.False(index.HasChildDirectories(6));  // Videos só tem arquivos
        Assert.Equal(2, index.GetChildCount(6));
    }

    [Fact]
    public void GetSizeOnDisk_AddsAlternateDataStreamsFromTheSideTable()
    {
        var names = new NameBlob(64);
        var entries = new FileEntry[8];

        entries[5] = new FileEntry
        {
            RecordNumber = 5, ParentIndex = 5,
            NameOffset = names.Append("."), NameLength = 1,
            Flags = EntryFlags.Directory,
        };
        entries[6] = new FileEntry
        {
            RecordNumber = 6, ParentIndex = 5,
            NameOffset = names.Append("baixado.exe"), NameLength = 11,
            LogicalSize = 1000, AllocatedSize = 4096,
            Flags = EntryFlags.HasAds, HardLinkCount = 1,
        };

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1000, 500, 4096, false);
        var index = new VolumeIndex(entries, names, volume, ScanStrategy.Mft,
                                    new Dictionary<int, long> { [6] = 8192 });

        Assert.Equal(4096, entries[6].SizeOnDisk);       // só o fluxo principal
        Assert.Equal(12288, index.GetSizeOnDisk(6));     // com o ADS
        Assert.Equal(12288, index.TotalBytesOnDisk);
    }

    [Fact]
    public void TopFiles_ReturnsLargestFirst()
    {
        VolumeIndex index = BuildSample();
        List<SizedItem> top = SizeAnalyzer.TopFiles(index, 2);

        Assert.Equal(2, top.Count);
        Assert.Equal(2000, top[0].LogicalSize);
        Assert.Equal(1000, top[1].LogicalSize);
    }

    [Fact]
    public void ByExtension_GroupsAndSortsBySize()
    {
        VolumeIndex index = BuildSample();
        List<ExtensionBucket> buckets = SizeAnalyzer.ByExtension(index, 10);

        Assert.Equal(".mp4", buckets[0].Extension);
        Assert.Equal(3000, buckets[0].TotalBytes);
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(FileCategories.Video, buckets[0].CategoryKey);
    }

    [Fact]
    public void MarkDeleted_RemovesTheFileFromEverythingDerivedFromTheIndex()
    {
        // The bug this guards: pruning the list on screen and leaving the index alone.
        // The row came back on the next folder open, search or biggest-files rebuild.
        VolumeIndex index = BuildSample();

        Removal gone = index.MarkDeleted(8);          // render_v2.mp4

        Assert.Equal(1, gone.Entries);
        Assert.Equal(2000, gone.LogicalBytes);
        Assert.Equal(2000, gone.BytesOnDisk);

        Assert.False(index.Entries[8].IsInUse);
        Assert.Equal([7], index.GetChildren(6).ToArray());
        Assert.Equal(1000, index.GetSubtreeSize(6));
        Assert.Equal(1050, index.GetSubtreeSize(5));
        Assert.Equal(1050, index.TotalLogicalBytes);
        Assert.Equal(string.Empty, index.GetFullPath(8));
        Assert.DoesNotContain(SizeAnalyzer.TopFiles(index, 10), t => t.Index == 8);
    }

    [Fact]
    public void MarkDeleted_TakesTheWholeSubtreeWithTheFolder()
    {
        VolumeIndex index = BuildSample();

        Removal gone = index.MarkDeleted(6);          // Videos, with both renders inside

        Assert.Equal(3, gone.Entries);                // the folder plus its two files
        Assert.Equal(3000, gone.LogicalBytes);

        Assert.False(index.Entries[7].IsInUse);
        Assert.False(index.Entries[8].IsInUse);
        Assert.Equal([9], index.GetChildren(5).ToArray());
        Assert.Equal(50, index.GetSubtreeSize(5));
        Assert.Equal(1, index.FileCount);
    }

    [Fact]
    public void MarkDeleted_IsIdempotentAndRefusesTheRoot()
    {
        VolumeIndex index = BuildSample();

        Assert.Equal(1, index.MarkDeleted(10).Entries);
        Assert.True(index.MarkDeleted(10).IsEmpty);   // already gone, nothing more to take

        // ProtectedPaths refuses the volume root, so this can only ever be a bug upstream —
        // and emptying the entire index would be a much worse answer than doing nothing.
        Assert.True(index.MarkDeleted(index.RootIndex).IsEmpty);
        Assert.True(index.Entries[index.RootIndex].IsInUse);
    }

    [Fact]
    public void MarkDeleted_DoesNotClaimToFreeAHardlinkedFilesClusters()
    {
        // The clusters were never credited to this name, so deleting it frees nothing.
        var names = new NameBlob(64);
        var entries = new FileEntry[8];

        entries[5] = new FileEntry
        {
            RecordNumber = 5, ParentIndex = 5,
            NameOffset = names.Append("."), NameLength = 1,
            Flags = EntryFlags.Directory,
        };
        entries[6] = new FileEntry
        {
            RecordNumber = 6, ParentIndex = 5,
            NameOffset = names.Append("compartilhado.dll"), NameLength = 17,
            LogicalSize = 1024, AllocatedSize = 4096, HardLinkCount = 3,
        };

        var volume = new VolumeInfo('C', "Teste", "NTFS", 1000, 500, 4096, false);
        var index = new VolumeIndex(entries, names, volume, ScanStrategy.Mft);

        Removal gone = index.MarkDeleted(6);

        Assert.Equal(1024, gone.LogicalBytes);
        Assert.Equal(0, gone.BytesOnDisk);
    }
}

public class FileCategoriesTests
{
    [Theory]
    [InlineData("filme.mkv", FileCategories.Video)]
    [InlineData("foto.HEIC", FileCategories.Image)]
    [InlineData("musica.flac", FileCategories.Audio)]
    [InlineData("relatorio.pdf", FileCategories.Document)]
    [InlineData("backup.7z", FileCategories.Archive)]
    [InlineData("maquina.vhdx", FileCategories.Disk)]
    [InlineData("desconhecido.qqq", FileCategories.Other)]
    public void Of_ClassifiesByExtension(string fileName, string expectedKey)
    {
        // Compara a CHAVE, não o texto exibido: o texto muda com o idioma e o teste
        // passaria a depender de qual tradução está ativa.
        Assert.Equal(expectedKey, FileCategories.Of(fileName.AsSpan()));
    }

    [Fact]
    public void DisplayName_UsesTheActiveLanguage()
    {
        try
        {
            L.Use(AppLanguage.English);
            Assert.Equal("Disk image / VM", FileCategories.DisplayNameOf("maquina.vhdx".AsSpan()));

            L.Use(AppLanguage.Portuguese);
            Assert.Equal("Imagem de disco / VM", FileCategories.DisplayNameOf("maquina.vhdx".AsSpan()));
        }
        finally
        {
            L.Use(AppLanguage.English);
        }
    }

    [Fact]
    public void ContentThumbnail_IsOfferedForVisualCategories()
    {
        // É isto que decide se a lista mostra um frame do vídeo ou só o ícone do tipo.
        Assert.True(FileCategories.HasContentThumbnail(FileCategories.Video));
        Assert.True(FileCategories.HasContentThumbnail(FileCategories.Image));
        Assert.False(FileCategories.HasContentThumbnail(FileCategories.Executable));
    }
}
