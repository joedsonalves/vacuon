using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.Core.Actions;
using Vacuon.App.Views;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Preview;
using Vacuon.Core.Scan;
using Vacuon.Core.Security;
using Vacuon.Native.Interop;

namespace Vacuon.App.ViewModels;

public enum Section { Dashboard, Explorer, Security, Settings }

/// <summary>Modo de listagem do Explorer.</summary>
public enum ListMode
{
    /// <summary>Conteúdo da pasta selecionada na árvore.</summary>
    Folder,
    /// <summary>Maiores arquivos do volume inteiro.</summary>
    BiggestFiles,
    /// <summary>Maiores pastas do volume inteiro.</summary>
    BiggestFolders,
    /// <summary>Resultado da busca e dos filtros.</summary>
    Search,
    /// <summary>Arquivos marcados pelas heurísticas de suspeita.</summary>
    Suspicious,
}

public sealed class MainViewModel : Observable, IDisposable
{
    /// <summary>
    /// Teto de linhas materializadas. A virtualização do WPF cuida dos contêineres,
    /// mas criar 2,8 milhões de ViewModels custaria mais que a varredura inteira —
    /// e ninguém rola uma lista de 2,8 milhões de itens. Quando o corte morde, a
    /// interface informa quantos itens ficaram de fora.
    /// </summary>
    private const int MaxRows = 5000;

    private readonly ThumbnailService _thumbnails = new();
    private readonly AppSettings _settings;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource _thumbnailCts = new();

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;

        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning);
        CancelScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        ScanVolumeCommand = new RelayCommand(async p => await ScanAsync(p as VolumeCardViewModel));
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
        OpenCommand = new RelayCommand(OpenSelected);
        RevealCommand = new RelayCommand(RevealSelected);
        CopyPathCommand = new RelayCommand(CopySelectedPath);
        RunSecurityScanCommand = new RelayCommand(async () => await RunSecurityScanAsync(), () => !IsSecurityScanning);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        ThemeManager.Changed += OnThemeChanged;

        LoadVolumes();
    }

    // ================= navegação =================

    private Section _section = Section.Dashboard;
    public Section Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            Raise(nameof(IsDashboard));
            Raise(nameof(IsExplorer));
            Raise(nameof(IsSecurity));
            Raise(nameof(IsSettings));
        }
    }

    public bool IsDashboard => Section == Section.Dashboard;
    public bool IsExplorer => Section == Section.Explorer;
    public bool IsSecurity => Section == Section.Security;
    public bool IsSettings => Section == Section.Settings;

    // ================= elevação =================

    public bool IsElevated => ElevationService.IsElevated;

    public string ElevationText =>
        L.T(IsElevated ? "elevation.elevatedHint" : "elevation.notElevatedHint");

    public bool AlwaysRunAsAdministrator
    {
        get => _settings.AlwaysRunAsAdministrator;
        set
        {
            if (_settings.AlwaysRunAsAdministrator == value) return;
            _settings.AlwaysRunAsAdministrator = value;
            _settings.Save();
            Raise();
            Raise(nameof(AlwaysAdminHintText));
        }
    }

    public string AlwaysAdminHintText =>
        L.T(AlwaysRunAsAdministrator ? "elevation.alwaysOn" : "elevation.alwaysOff");

    private void RestartElevated()
    {
        if (!ElevationService.RelaunchElevated())
            StatusText = L.T("elevation.declined");
    }

    // ================= temas =================

    public ThemeChoice Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            _settings.Save();
            ThemeManager.Apply(value);
            Raise();
        }
    }

    public bool IsDarkTheme => ThemeManager.Effective == ThemeChoice.Dark;

    // ================= idioma =================

    /// <summary>Versão exibida no rodapé e nas Configurações.</summary>
    public static string AppVersion => "0.3.0";

    /// <summary>
    /// Footer tag. It claims "nothing was deleted" only while that is still true —
    /// leaving the claim up after a delete would be the app lying about itself.
    /// </summary>
    public string FooterText => _anythingDeleted
        ? L.T("app.footerAfterDelete", AppVersion)
        : L.T("app.footer", AppVersion);

    private bool _anythingDeleted;
    public string VersionTitleText => L.T("settings.versionTitle", AppVersion);
    public string PrivacyNoteText => L.T("settings.privacyNote", AppSettings.FilePath);

    public AppLanguage Language
    {
        get => _settings.Language;
        set
        {
            if (_settings.Language == value) return;
            _settings.Language = value;
            _settings.Save();

            // L.Use dispara L.Changed; a ponte reescreve os recursos S.* e o XAML
            // se atualiza sozinho. Aqui só falta avisar as propriedades do próprio VM.
            L.Use(value);
            Raise();
            RaiseLocalizedProperties();
        }
    }

    /// <summary>
    /// Reavalia todo texto que o VM produz em código.
    /// <para>
    /// O XAML se resolve pelos recursos <c>S.*</c>, mas o que passa por
    /// <see cref="L.T"/> aqui dentro precisa de um empurrão explícito.
    /// </para>
    /// </summary>
    private void RaiseLocalizedProperties()
    {
        foreach (string name in new[]
        {
            nameof(FooterText), nameof(VersionTitleText), nameof(PrivacyNoteText),
            nameof(ElevationText), nameof(AlwaysAdminHintText), nameof(ThemeToggleTooltip),
            nameof(ModeText), nameof(TruncationText), nameof(SummaryText),
            nameof(SecurityStatusText), nameof(IconSizeOptions), nameof(SelectedIconSizeOption),
        })
        {
            Raise(name);
        }

        // Cartões de volume e linhas da lista recalculam o texto ao serem relidos.
        LoadVolumes();
        foreach (FileRowViewModel row in Rows) row.RaiseLocalizedText();
    }

    /// <summary>
    /// Botão de alternância rápida no cabeçalho. Glifos da fonte de ícones do Windows
    /// (E706 = brilho, E708 = lua): os equivalentes Unicode soltos não existem em
    /// Segoe UI Variable e saem como círculo vazio.
    /// </summary>
    public string ThemeToggleGlyph => IsDarkTheme ? "" : "";
    public string ThemeToggleTooltip =>
        L.T(IsDarkTheme ? "theme.switchToLight" : "theme.switchToDark");

    public void ToggleTheme() => Theme = IsDarkTheme ? ThemeChoice.Light : ThemeChoice.Dark;

    private void OnThemeChanged()
    {
        Raise(nameof(IsDarkTheme));
        Raise(nameof(ThemeToggleGlyph));
        Raise(nameof(ThemeToggleTooltip));
    }

    // ================= volumes =================

    public ObservableCollection<VolumeCardViewModel> Volumes { get; } = [];

    private VolumeCardViewModel? _selectedVolume;
    public VolumeCardViewModel? SelectedVolume
    {
        get => _selectedVolume;
        set => Set(ref _selectedVolume, value);
    }

    private void LoadVolumes()
    {
        Volumes.Clear();
        foreach (VolumeInfo volume in VolumeProbe.EnumerateFixedVolumes())
            Volumes.Add(new VolumeCardViewModel(volume));

        SelectedVolume = Volumes.FirstOrDefault(v =>
                             _settings.LastVolume is not null &&
                             v.Header.StartsWith(_settings.LastVolume, StringComparison.OrdinalIgnoreCase))
                         ?? Volumes.FirstOrDefault();
    }

    // ================= varredura =================

    private VolumeIndex? _index;
    public VolumeIndex? Index
    {
        get => _index;
        private set => Set(ref _index, value);
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            (ScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private string _statusText = L.T("scan.prompt");
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _summaryText = string.Empty;
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

    private bool _hasScanned;
    public bool HasScanned { get => _hasScanned; private set => Set(ref _hasScanned, value); }

    /// <summary>
    /// A travessia pela API não expõe <c>AllocatedSize</c>. A interface esconde as
    /// colunas de "em disco" nesse caso, em vez de repetir o tamanho lógico e deixar
    /// o usuário achar que mediu.
    /// </summary>
    private bool _hasRealAllocation;
    public bool HasRealAllocation { get => _hasRealAllocation; private set => Set(ref _hasRealAllocation, value); }

    public ICommand ScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand ScanVolumeCommand { get; }
    public ICommand RestartElevatedCommand { get; }

    private async Task ScanAsync(VolumeCardViewModel? volume = null)
    {
        volume ??= SelectedVolume;
        if (volume is null || IsScanning) return;

        SelectedVolume = volume;
        _settings.LastVolume = volume.Header;
        _settings.Save();

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;

        IsScanning = true;
        Progress = 0;
        HasScanned = false;
        StatusText = L.T("scan.running", volume.Header);
        Section = Section.Explorer;

        var stopwatch = Stopwatch.StartNew();

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Percent;
            StatusText = p.TotalBytes > 0
                ? L.T("scan.progress", Format.Percent(p.Percent), Format.Count(p.EntriesFound),
                      p.MegabytesPerSecond.ToString("N0", L.Culture))
                : L.T("scan.progressItems", Format.Count(p.RecordsParsed), Format.Duration(p.Elapsed));
        });

        try
        {
            var options = new MftScanOptions { Progress = progress };
            var orchestrator = new ScanOrchestrator(options);

            ScanResult result = await Task.Run(
                () => orchestrator.ScanVolume(volume.DriveLetter, StrategyPreference.Auto, token), token);

            stopwatch.Stop();
            ApplyScanResult(result, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("scan.cancelled");
        }
        catch (VolumeAccessException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsScanning = false;
            Progress = 0;
        }
    }

    private void ApplyScanResult(ScanResult result, TimeSpan elapsed)
    {
        Index = result.Index;
        HasRealAllocation = result.StrategyUsed == ScanStrategy.Mft;
        HasScanned = true;

        VolumeIndex index = result.Index;
        int files = index.FileCount;

        string strategy = L.T(result.StrategyUsed == ScanStrategy.Mft
            ? "scan.strategyMft" : "scan.strategyWalk");

        string fallback = result.FallbackReason is null ? string.Empty : $" — {result.FallbackReason}";

        StatusText = L.T("scan.summary", Format.Count(files), Format.Duration(elapsed), strategy + fallback);

        SummaryText = HasRealAllocation
            ? L.T("scan.logicalAndDisk", Format.Bytes(index.TotalLogicalBytes),
                  Format.Bytes(index.TotalBytesOnDisk), Format.Bytes(index.TotalSlackBytes))
            : L.T("scan.logicalOnly", Format.Bytes(index.TotalLogicalBytes));

        // Árvore
        Root = new FolderNodeViewModel(index, index.RootIndex, index.Volume.Root);
        Root.IsExpanded = true;
        Root.IsSelected = true;
        Raise(nameof(RootNodes));

        Extensions.Clear();
        foreach (ExtensionBucket bucket in SizeAnalyzer.ByExtension(index, 12))
            Extensions.Add(bucket);

        SizeBuckets.Clear();
        foreach (SizeBucket bucket in SizeAnalyzer.BySizeRange(index))
            if (bucket.Count > 0) SizeBuckets.Add(bucket);

        AgeBuckets.Clear();
        foreach (AgeBucket bucket in SizeAnalyzer.ByAge(index, DateTime.UtcNow))
            if (bucket.Count > 0) AgeBuckets.Add(bucket);

        LoadVolumes();
        ShowBiggestFiles();
    }

    // ================= árvore =================

    private FolderNodeViewModel? _root;
    public FolderNodeViewModel? Root
    {
        get => _root;
        private set => Set(ref _root, value);
    }

    public IEnumerable<FolderNodeViewModel> RootNodes => Root is null ? [] : [Root];

    private FolderNodeViewModel? _selectedFolder;
    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!Set(ref _selectedFolder, value)) return;
            if (value is not null) ShowFolder(value.EntryIndex);
        }
    }

    // ================= lista =================

    public ObservableCollection<FileRowViewModel> Rows { get; } = [];

    private ListMode _mode = ListMode.Folder;
    public ListMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(ModeText));
        }
    }

    public string ModeText => Mode switch
    {
        ListMode.BiggestFiles => L.T("list.biggestFiles"),
        ListMode.BiggestFolders => L.T("list.biggestFolders"),
        ListMode.Search => L.T("list.searchResults"),
        ListMode.Suspicious => L.T("list.suspicious"),
        _ => CurrentFolderPath,
    };

    private string _currentFolderPath = string.Empty;
    public string CurrentFolderPath
    {
        get => _currentFolderPath;
        private set { Set(ref _currentFolderPath, value); Raise(nameof(ModeText)); }
    }

    private int _totalMatches;
    public int TotalMatches { get => _totalMatches; private set { Set(ref _totalMatches, value); Raise(nameof(TruncationText)); Raise(nameof(IsTruncated)); } }

    public bool IsTruncated => TotalMatches > Rows.Count;

    public string TruncationText => IsTruncated
        ? L.T("list.truncated", Format.Count(Rows.Count), Format.Count(TotalMatches))
        : L.T("list.itemCount", Format.Count(Rows.Count));

    /// <summary>
    /// The single "current" row, kept so the one-item actions (open, reveal, copy path)
    /// keep working. The multi-selection lives in the delete section below.
    /// </summary>
    private FileRowViewModel? _selectedRow;
    public FileRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    public void ShowFolder(int entryIndex)
    {
        if (Index is null) return;

        Mode = ListMode.Folder;
        CurrentFolderPath = Index.GetFullPath(entryIndex);

        var children = new List<(int Index, long Size)>();
        foreach (int child in Index.GetChildren(entryIndex))
        {
            ref FileEntry entry = ref Index.Entries[child];
            if (!_settings.ShowHiddenAndSystem &&
                (entry.Flags & (EntryFlags.Hidden | EntryFlags.System)) != 0) continue;

            long size = entry.IsDirectory ? Index.GetSubtreeSize(child) : entry.LogicalSize;
            children.Add((child, size));
        }

        children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        Fill(children.Select(c => c.Index), children.Count);
    }

    public void ShowBiggestFiles()
    {
        if (Index is null) return;
        Mode = ListMode.BiggestFiles;

        List<SizedItem> top = SizeAnalyzer.TopFiles(Index, _settings.TopItemCount);
        Fill(top.Select(t => t.Index), top.Count);
    }

    public void ShowBiggestFolders()
    {
        if (Index is null) return;
        Mode = ListMode.BiggestFolders;

        List<SizedItem> top = SizeAnalyzer.TopFolders(Index, _settings.TopItemCount);
        Fill(top.Select(t => t.Index), top.Count);
    }

    public void ShowSuspicious()
    {
        if (Index is null) return;
        Mode = ListMode.Suspicious;

        List<SuspiciousFile> found = new SuspiciousFileAnalyzer().Analyze(Index, MaxRows);
        Fill(found.Select(f => f.Index), found.Count);

        if (found.Count == 0)
            StatusText = L.T("status.noSuspicious");
    }

    private void Fill(IEnumerable<int> indices, int total)
    {
        CancelPendingThumbnails();
        Rows.Clear();

        if (Index is null) return;

        int added = 0;
        foreach (int index in indices)
        {
            if (added >= MaxRows) break;
            Rows.Add(new FileRowViewModel(Index, index));
            added++;
        }

        TotalMatches = total;
        Raise(nameof(TruncationText));
        SelectedRow = null;
    }

    // ================= busca e filtros =================

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplySearch(); }
    }

    private long _minSizeBytes;
    public long MinSizeBytes { get => _minSizeBytes; set { if (Set(ref _minSizeBytes, value)) ApplySearch(); } }

    private int _minAgeDays;
    public int MinAgeDays { get => _minAgeDays; set { if (Set(ref _minAgeDays, value)) ApplySearch(); } }

    private string _extensionFilter = string.Empty;
    public string ExtensionFilter { get => _extensionFilter; set { if (Set(ref _extensionFilter, value)) ApplySearch(); } }

    public ICommand ClearFiltersCommand { get; }

    private void ClearFilters()
    {
        _searchText = string.Empty;
        _minSizeBytes = 0;
        _minAgeDays = 0;
        _extensionFilter = string.Empty;

        Raise(nameof(SearchText));
        Raise(nameof(MinSizeBytes));
        Raise(nameof(MinAgeDays));
        Raise(nameof(ExtensionFilter));

        ShowBiggestFiles();
    }

    private void ApplySearch()
    {
        if (Index is null) return;

        bool hasQuery = SearchText.Length > 0
                     || MinSizeBytes > 0
                     || MinAgeDays > 0
                     || ExtensionFilter.Length > 0;

        if (!hasQuery)
        {
            ShowBiggestFiles();
            return;
        }

        Mode = ListMode.Search;

        string query = SearchText;
        string[] extensions = ExtensionFilter
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToArray();

        DateTime cutoff = MinAgeDays > 0 ? DateTime.UtcNow.AddDays(-MinAgeDays) : DateTime.MaxValue;

        var matches = new List<(int Index, long Size)>();

        // Passada linear sobre o array plano. Sem LINQ e sem materializar caminho:
        // é o que mantém a busca abaixo de 100 ms em um índice de milhões de entradas.
        for (int i = 0; i < Index.Entries.Length; i++)
        {
            ref FileEntry entry = ref Index.Entries[i];
            if (!entry.IsInUse || entry.IsDirectory) continue;

            if (MinSizeBytes > 0 && entry.LogicalSize < MinSizeBytes) continue;

            if (MinAgeDays > 0)
            {
                DateTime written = entry.LastWrite;
                if (written == DateTime.MinValue || written > cutoff) continue;
            }

            ReadOnlySpan<char> name = Index.GetName(i);

            if (query.Length > 0 &&
                name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (extensions.Length > 0)
            {
                bool matchesExtension = false;
                foreach (string extension in extensions)
                {
                    if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        matchesExtension = true;
                        break;
                    }
                }
                if (!matchesExtension) continue;
            }

            matches.Add((i, entry.LogicalSize));
        }

        matches.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        Fill(matches.Select(m => m.Index), matches.Count);
    }

    // ================= agregados =================

    public ObservableCollection<ExtensionBucket> Extensions { get; } = [];
    public ObservableCollection<SizeBucket> SizeBuckets { get; } = [];
    public ObservableCollection<AgeBucket> AgeBuckets { get; } = [];

    // ================= miniaturas =================

    public ThumbnailSize IconSize
    {
        get => _settings.IconSize;
        set
        {
            if (_settings.IconSize == value) return;
            _settings.IconSize = value;
            _settings.Save();
            Raise();
            Raise(nameof(IconPixels));
            Raise(nameof(RowHeight));
            ReloadThumbnails();
        }
    }

    public IReadOnlyList<IconSizeOption> IconSizeOptions => IconSizeOption.All;

    public IconSizeOption? SelectedIconSizeOption
    {
        get => IconSizeOption.All.FirstOrDefault(o => o.Size == IconSize);
        set { if (value is not null) IconSize = value.Size; }
    }

    public double IconPixels => (int)IconSize;

    /// <summary>
    /// Altura da linha acompanha o ícone, com piso para o texto respirar. Sem o piso,
    /// o modo 16 px cortaria as duas linhas de texto.
    /// </summary>
    public double RowHeight => Math.Max(38, IconPixels + 10);

    public bool ContentThumbnails
    {
        get => _settings.ContentThumbnails;
        set
        {
            if (_settings.ContentThumbnails == value) return;
            _settings.ContentThumbnails = value;
            _settings.Save();
            Raise();
            ReloadThumbnails();
        }
    }

    public bool ShowSizeOnDisk
    {
        get => _settings.ShowSizeOnDisk;
        set { if (_settings.ShowSizeOnDisk == value) return; _settings.ShowSizeOnDisk = value; _settings.Save(); Raise(); }
    }

    public bool ShowHiddenAndSystem
    {
        get => _settings.ShowHiddenAndSystem;
        set
        {
            if (_settings.ShowHiddenAndSystem == value) return;
            _settings.ShowHiddenAndSystem = value;
            _settings.Save();
            Raise();
            if (Mode == ListMode.Folder && SelectedFolder is not null) ShowFolder(SelectedFolder.EntryIndex);
        }
    }

    private void ReloadThumbnails()
    {
        CancelPendingThumbnails();
        foreach (FileRowViewModel row in Rows) row.ResetThumbnail();
    }

    private void CancelPendingThumbnails()
    {
        _thumbnailCts.Cancel();
        _thumbnailCts.Dispose();
        _thumbnailCts = new CancellationTokenSource();
    }

    /// <summary>Chamado pela View quando uma linha entra em tela.</summary>
    public async Task RequestThumbnailAsync(FileRowViewModel row)
    {
        try
        {
            await row.LoadThumbnailAsync(_thumbnails, IconSize, ContentThumbnails, _thumbnailCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ================= ações =================

    public ICommand OpenCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand CopyPathCommand { get; }

    private void OpenSelected()
    {
        if (SelectedRow is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(SelectedRow.FullPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            StatusText = L.T("status.cannotOpen", SelectedRow.Name);
        }
    }

    private void RevealSelected()
    {
        if (SelectedRow is null) return;

        try
        {
            Shell32.RevealInExplorer(SelectedRow.FullPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            StatusText = L.T("status.cannotReveal");
        }
    }

    private void CopySelectedPath()
    {
        if (SelectedRow is null) return;

        try
        {
            Clipboard.SetText(SelectedRow.FullPath);
            StatusText = L.T("status.pathCopied");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // A área de transferência pode estar travada por outro processo.
            StatusText = L.T("status.clipboardBusy");
        }
    }

    // ================= exclusão =================

    /// <summary>
    /// Extra paths selected in the folder tree. The tree and the list are separate
    /// selections, and a delete acts on both — selecting a folder on the left and files
    /// on the right is a normal way to work.
    /// </summary>
    private readonly List<string> _treeSelection = [];

    /// <summary>Rows selected in the file list. Fed by the view (SelectedItems is not bindable).</summary>
    private readonly List<FileRowViewModel> _listSelection = [];

    public int SelectedCount => _treeSelection.Count + _listSelection.Count;
    public bool HasSelection => SelectedCount > 0;

    public string SelectionSummaryText
    {
        get
        {
            if (SelectedCount == 0) return string.Empty;

            long bytes = _listSelection.Sum(r => r.LogicalSize);
            return L.T("delete.selectionCount", Format.Count(SelectedCount), Format.Bytes(bytes));
        }
    }

    public string SelectionDetailText => _listSelection.Count == 1 && _treeSelection.Count == 0
        ? $"{_listSelection[0].FullPath}\n{Format.Bytes(_listSelection[0].LogicalSize)} · {_listSelection[0].ModifiedText}"
        : SelectionSummaryText;

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void SetListSelection(IEnumerable<FileRowViewModel> rows)
    {
        _listSelection.Clear();
        _listSelection.AddRange(rows);
        RaiseSelectionChanged();
    }

    /// <summary>Called by the view whenever the tree selection changes.</summary>
    public void SetTreeSelection(IEnumerable<string> paths)
    {
        _treeSelection.Clear();
        _treeSelection.AddRange(paths);
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionSummaryText));
        Raise(nameof(SelectionDetailText));

        // SelectedRow keeps the single-item actions (open, reveal) working unchanged.
        SelectedRow = _listSelection.Count > 0 ? _listSelection[0] : null;
    }

    /// <summary>Every path the current selection covers, list and tree together.</summary>
    private List<string> SelectedPaths() =>
        [.. _treeSelection.Concat(_listSelection.Select(r => r.FullPath))];

    /// <summary>
    /// Plans a deletion, asks for confirmation, executes it and reports the outcome.
    /// </summary>
    /// <param name="mode">
    /// <see cref="DeleteMode.RecycleBin"/> for <c>Del</c>, <see cref="DeleteMode.Permanent"/>
    /// for <c>Shift+Del</c>.
    /// </param>
    /// <param name="owner">Owner window for the confirmation dialog.</param>
    public void DeleteSelection(DeleteMode mode, Window owner)
    {
        List<string> paths = SelectedPaths();

        if (paths.Count == 0)
        {
            StatusText = L.T("delete.nothingSelected");
            return;
        }

        var service = new DeleteService();

        // Plan first, always. The dialog shows exactly what will happen, including the
        // items the protection list refuses to touch.
        DeleteReport plan = service.Plan(paths, mode);

        if (!DeleteDialog.Confirm(owner, plan, mode)) return;

        DeleteReport report = service.Execute(paths, mode);

        StatusText = report.FailedCount == 0
            ? L.T("delete.done", Format.Count(report.DeletedCount), Format.Bytes(report.BytesFreed))
            : L.T("delete.donePartial", Format.Count(report.DeletedCount),
                  Format.Bytes(report.BytesFreed), Format.Count(report.FailedCount));

        if (report.FailedCount > 0) LastFailures = [.. report.Failures.Select(Describe)];

        if (report.DeletedCount > 0 && !_anythingDeleted)
        {
            _anythingDeleted = true;
            Raise(nameof(FooterText));
        }

        RemoveDeletedRows(report);
    }

    /// <summary>Human-readable reason a single item did not go.</summary>
    private static string Describe(DeleteResult result)
    {
        string reason = L.T(result.Outcome switch
        {
            DeleteOutcome.Blocked => "delete.outcomeBlocked",
            DeleteOutcome.NotFound => "delete.outcomeNotFound",
            DeleteOutcome.InUse => "delete.outcomeInUse",
            DeleteOutcome.AccessDenied => "delete.outcomeAccessDenied",
            _ => "delete.outcomeFailed",
        });

        return $"{result.Path} — {reason}";
    }

    private List<string> _lastFailures = [];
    public List<string> LastFailures
    {
        get => _lastFailures;
        private set { Set(ref _lastFailures, value); Raise(nameof(HasFailures)); }
    }

    public bool HasFailures => LastFailures.Count > 0;

    /// <summary>
    /// Drops the rows that are really gone.
    /// <para>
    /// The index in memory still describes the old disk — rescanning after every delete
    /// would cost a full traversal, so the list is pruned instead and the numbers in the
    /// sidebar stay as measured. They will be right again after the next scan.
    /// </para>
    /// </summary>
    private void RemoveDeletedRows(DeleteReport report)
    {
        var deleted = new HashSet<string>(
            report.Results.Where(r => r.Succeeded).Select(r => r.Path),
            StringComparer.OrdinalIgnoreCase);

        if (deleted.Count == 0) return;

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            string path = Rows[i].FullPath.TrimEnd('\\');

            bool gone = deleted.Contains(path)
                     || deleted.Any(d => path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));

            if (gone) Rows.RemoveAt(i);
        }

        _listSelection.Clear();
        _treeSelection.Clear();
        TotalMatches = Rows.Count;
        RaiseSelectionChanged();
    }

    // ================= segurança =================

    public ObservableCollection<SecurityFinding> Findings { get; } = [];

    private bool _isSecurityScanning;
    public bool IsSecurityScanning
    {
        get => _isSecurityScanning;
        private set
        {
            if (!Set(ref _isSecurityScanning, value)) return;
            (RunSecurityScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _securityStatusText = L.T("security.prompt");
    public string SecurityStatusText { get => _securityStatusText; private set => Set(ref _securityStatusText, value); }

    private bool _hasSecurityRun;
    public bool HasSecurityRun { get => _hasSecurityRun; private set => Set(ref _hasSecurityRun, value); }

    public ICommand RunSecurityScanCommand { get; }

    private async Task RunSecurityScanAsync()
    {
        IsSecurityScanning = true;
        SecurityStatusText = L.T("security.running");

        try
        {
            var scanner = new RegistryPersistenceScanner(new SecurityScanOptions());
            SecurityReport report = await Task.Run(() => scanner.Scan());

            Findings.Clear();
            foreach (SecurityFinding finding in report.Findings) Findings.Add(finding);

            HasSecurityRun = true;

            int flagged = report.CountAtLeast(Suspicion.Notable);

            string prefix = L.T("security.stats", report.LocationsInspected,
                                Format.Count(report.EntriesInspected), Format.Duration(report.Elapsed));

            SecurityStatusText = flagged switch
            {
                0 => L.T("security.noneFlagged", prefix),
                1 => L.T("security.oneFlagged", prefix),
                _ => L.T("security.manyFlagged", prefix, flagged),
            };

            if (!report.WasElevated) SecurityStatusText += L.T("security.notElevatedSuffix");
        }
        finally
        {
            IsSecurityScanning = false;
        }
    }

    public void Dispose()
    {
        ThemeManager.Changed -= OnThemeChanged;
        _scanCts?.Dispose();
        _thumbnailCts.Dispose();
        _thumbnails.Dispose();
    }
}
