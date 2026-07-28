using System.Windows.Media.Imaging;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
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
    private readonly ISelectionSink? _selection;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _isChecked;

    public FileRowViewModel(VolumeIndex index, int entryIndex, ISelectionSink? selection = null)
    {
        _index = index;
        _entryIndex = entryIndex;
        _selection = selection;

        // Straight into the field: reading the basket back is a restore, not a new tick,
        // and routing it through the setter would push it right back where it came from.
        _isChecked = selection?.IsChecked(entryIndex) ?? false;

        ref FileEntry entry = ref index.Entries[entryIndex];

        Name = index.GetName(entryIndex).ToString();
        IsDirectory = entry.IsDirectory;
        LogicalSize = IsDirectory ? index.GetSubtreeSize(entryIndex) : entry.LogicalSize;
        SizeOnDisk = IsDirectory ? index.GetSubtreeSizeOnDisk(entryIndex) : index.GetSizeOnDisk(entryIndex);
        Modified = entry.LastWrite;
        Accessed = entry.LastAccess;
        Flags = entry.Flags;
        CategoryKey = IsDirectory ? "badge.folder" : FileCategories.Of(Name.AsSpan());
        ChildCount = IsDirectory ? index.GetSubtreeFileCount(entryIndex) : 0;
    }

    public int EntryIndex => _entryIndex;

    /// <summary>
    /// Ticked for a batch action.
    /// <para>
    /// The tick lives in the view model's basket, not in this row, so it survives the list
    /// being refilled. Changing folder, sorting a column or running another search rebuilds
    /// every row here — a selection that lived in the rows would die with them, which is
    /// exactly what made gathering files across folders impossible before.
    /// </para>
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value)) return;
            _selection?.SetChecked(_entryIndex, value);
        }
    }

    /// <summary>
    /// Adopts the basket's answer without telling it back.
    /// <para>
    /// The same entry can be on screen twice — a subfolder is a row in the list and a node
    /// in the tree at the same time. Ticking one has to show on the other, and routing that
    /// through the normal setter would bounce it back to the basket it just came from.
    /// </para>
    /// </summary>
    internal void SyncChecked(bool value)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        Raise(nameof(IsChecked));
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public long LogicalSize { get; }
    public long SizeOnDisk { get; }
    public DateTime Modified { get; }
    public DateTime Accessed { get; }
    public EntryFlags Flags { get; }
    /// <summary>Chave estável — a cor da categoria depende dela, não do texto exibido.</summary>
    public string CategoryKey { get; }

    /// <summary>Nome da categoria no idioma ativo.</summary>
    public string Category => L.T(CategoryKey);
    public int ChildCount { get; }

    /// <summary>Materializado sob demanda: montar o caminho de tudo é caro e inútil.</summary>
    private string? _fullPath;
    public string FullPath => _fullPath ??= _index.GetFullPath(_entryIndex);

    public string SizeText => Format.Bytes(LogicalSize);
    public string SizeOnDiskText => SizeOnDisk > 0 ? Format.Bytes(SizeOnDisk) : "—";
    public string ModifiedText => Format.DateOrDash(Modified);
    public string DetailText => IsDirectory
        ? L.T("list.fileCount", Format.Count(ChildCount))
        : Category;

    /// <summary>Marcadores que mudam a decisão de apagar. Vazio quando não há nenhum.</summary>
    public string Badges
    {
        get
        {
            var parts = new List<string>(3);
            if ((Flags & EntryFlags.CloudPlaceholder) != 0) parts.Add(L.T("badge.cloud"));
            if ((Flags & EntryFlags.HardLinked) != 0) parts.Add(L.T("badge.hardlink"));
            if ((Flags & EntryFlags.Compressed) != 0) parts.Add(L.T("badge.compressed"));
            if ((Flags & EntryFlags.HasAds) != 0) parts.Add(L.T("badge.ads"));
            if ((Flags & EntryFlags.Suspicious) != 0) parts.Add(L.T("badge.suspicious"));
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

    /// <summary>
    /// Reavalia os textos derivados de tradução. Chamado na troca de idioma — os
    /// valores em si (tamanho, datas) não mudam, só a forma de escrevê-los.
    /// </summary>
    public void RaiseLocalizedText()
    {
        Raise(nameof(Category));
        Raise(nameof(DetailText));
        Raise(nameof(Badges));
        Raise(nameof(SizeText));
        Raise(nameof(SizeOnDiskText));
        Raise(nameof(ModifiedText));
    }

    /// <summary>Permite recarregar quando o usuário muda o tamanho do ícone.</summary>
    public void ResetThumbnail()
    {
        _thumbnailRequested = false;
        Thumbnail = null;
    }
}
