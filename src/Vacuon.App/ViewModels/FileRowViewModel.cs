using System.Windows.Media.Imaging;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Preview;

namespace Vacuon.App.ViewModels;

/// <summary>
/// Uma linha da lista de arquivos.
/// <para>
/// Criada só para o que vai ser exibido — nunca para o índice inteiro. Materializar
/// 2,8 milhões destes seria o mesmo erro que o índice plano evita no núcleo.
/// </para>
/// </summary>
public sealed class FileRowViewModel : Observable
{
    private readonly VolumeIndex _index;
    private readonly int _entryIndex;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;

    public FileRowViewModel(VolumeIndex index, int entryIndex)
    {
        _index = index;
        _entryIndex = entryIndex;

        ref FileEntry entry = ref index.Entries[entryIndex];

        Name = index.GetName(entryIndex).ToString();
        IsDirectory = entry.IsDirectory;
        LogicalSize = IsDirectory ? index.GetSubtreeSize(entryIndex) : entry.LogicalSize;
        SizeOnDisk = IsDirectory ? index.GetSubtreeSizeOnDisk(entryIndex) : index.GetSizeOnDisk(entryIndex);
        Modified = entry.LastWrite;
        Accessed = entry.LastAccess;
        Flags = entry.Flags;
        Category = IsDirectory ? "Pasta" : FileCategories.Of(Name.AsSpan());
        ChildCount = IsDirectory ? index.GetSubtreeFileCount(entryIndex) : 0;
    }

    public int EntryIndex => _entryIndex;
    public string Name { get; }
    public bool IsDirectory { get; }
    public long LogicalSize { get; }
    public long SizeOnDisk { get; }
    public DateTime Modified { get; }
    public DateTime Accessed { get; }
    public EntryFlags Flags { get; }
    public string Category { get; }
    public int ChildCount { get; }

    /// <summary>Materializado sob demanda: montar o caminho de tudo é caro e inútil.</summary>
    private string? _fullPath;
    public string FullPath => _fullPath ??= _index.GetFullPath(_entryIndex);

    public string SizeText => Format.Bytes(LogicalSize);
    public string SizeOnDiskText => SizeOnDisk > 0 ? Format.Bytes(SizeOnDisk) : "—";
    public string ModifiedText => Format.DateOrDash(Modified);
    public string DetailText => IsDirectory
        ? $"{Format.Count(ChildCount)} arquivos"
        : Category;

    /// <summary>Marcadores que mudam a decisão de apagar. Vazio quando não há nenhum.</summary>
    public string Badges
    {
        get
        {
            var parts = new List<string>(3);
            if ((Flags & EntryFlags.CloudPlaceholder) != 0) parts.Add("nuvem");
            if ((Flags & EntryFlags.HardLinked) != 0) parts.Add("hardlink");
            if ((Flags & EntryFlags.Compressed) != 0) parts.Add("comprimido");
            if ((Flags & EntryFlags.HasAds) != 0) parts.Add("ADS");
            if ((Flags & EntryFlags.Suspicious) != 0) parts.Add("suspeito");
            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }
    }

    public bool HasBadges => Badges.Length > 0;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    /// <summary>
    /// Pede a miniatura uma única vez por linha. Chamado quando a linha entra em tela,
    /// não na construção da lista.
    /// </summary>
    public async Task LoadThumbnailAsync(ThumbnailService service, ThumbnailSize size,
                                         bool preferContent, CancellationToken cancellationToken)
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;

        bool wantsContent = preferContent && !IsDirectory && ThumbnailService.WantsContent(Name);

        BitmapSource? cached = service.GetCached(FullPath, size, wantsContent);
        if (cached is not null)
        {
            Thumbnail = cached;
            return;
        }

        BitmapSource? loaded = await service.GetAsync(FullPath, size, wantsContent, cancellationToken)
                                           .ConfigureAwait(true);
        if (loaded is not null) Thumbnail = loaded;
    }

    /// <summary>Permite recarregar quando o usuário muda o tamanho do ícone.</summary>
    public void ResetThumbnail()
    {
        _thumbnailRequested = false;
        Thumbnail = null;
    }
}
