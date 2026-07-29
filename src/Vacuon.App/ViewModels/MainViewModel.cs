using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.Core;
using Vacuon.Core.Actions;
using Vacuon.App.Views;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Optimization;
using Vacuon.Core.Preview;
using Vacuon.Core.Scan;
using Vacuon.Core.Security;
using Vacuon.Native.Interop;

namespace Vacuon.App.ViewModels;

public enum Section { Dashboard, Explorer, Security, Optimize, Settings }

/// <summary>
/// The two panels inside Optimize.
/// <para>
/// They share a section because they share the one thing that sets them apart from the rest
/// of the app: everywhere else reads, and these two write.
/// </para>
/// </summary>
public enum OptimizePanel { Ai, Startup, Memory }

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

/// <summary>Which column the file list is ordered by.</summary>
public enum RowSortKey { Size, Name, Modified, Path }

public sealed class MainViewModel : Observable, ISelectionSink, IDisposable
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
        SelectAllCommand = new RelayCommand(SelectAllListed);
        InvertSelectionCommand = new RelayCommand(InvertListedSelection);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        OpenRecycleBinCommand = new RelayCommand(OpenRecycleBin);
        RunAiScanCommand = new RelayCommand(async () => await RunAiScanAsync(), () => !IsAiScanning);
        RunStartupScanCommand = new RelayCommand(async () => await RunStartupScanAsync(), () => !IsStartupScanning);
        RunMemoryScanCommand = new RelayCommand(async () => await RunMemoryScanAsync());
        TrimWorkingSetsCommand = new RelayCommand(async () => await TrimWorkingSetsAsync());

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
            Raise(nameof(IsOptimize));
            Raise(nameof(IsSettings));
            Raise(nameof(ShowScanStatus));
        }
    }

    public bool IsDashboard => Section == Section.Dashboard;
    public bool IsExplorer => Section == Section.Explorer;
    public bool IsSecurity => Section == Section.Security;
    public bool IsOptimize => Section == Section.Optimize;

    private OptimizePanel _panel = OptimizePanel.Ai;
    public OptimizePanel Panel
    {
        get => _panel;
        set
        {
            if (!Set(ref _panel, value)) return;
            Raise(nameof(IsAiPanel));
            Raise(nameof(IsStartupPanel));
            Raise(nameof(IsMemoryPanel));
        }
    }

    public bool IsAiPanel => _panel == OptimizePanel.Ai;
    public bool IsStartupPanel => _panel == OptimizePanel.Startup;
    public bool IsMemoryPanel => _panel == OptimizePanel.Memory;
    public bool IsSettings => Section == Section.Settings;

    /// <summary>
    /// The header subtitle carries the scan status, which only means something on the two
    /// screens that scan. Elsewhere it sat under the title telling the reader to pick a drive
    /// on a page with no drives on it.
    /// </summary>
    public bool ShowScanStatus => IsDashboard || IsExplorer;

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
    public static string AppVersion => AppInfo.Version;

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
            nameof(SelectionSummaryText), nameof(SelectionDetailText), nameof(RecycledText),
            nameof(AiStatusText), nameof(AiJournalNoteText),
            nameof(HeaderName), nameof(HeaderSize), nameof(HeaderModified), nameof(HeaderPath),
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

    /// <summary>
    /// Set when the scan's own numbers contradict each other. Empty the rest of the time —
    /// a banner that is always up is a banner nobody reads.
    /// </summary>
    private string _warningText = string.Empty;
    public string WarningText
    {
        get => _warningText;
        private set { Set(ref _warningText, value); Raise(nameof(HasWarning)); }
    }

    public bool HasWarning => _warningText.Length > 0;

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

    private async Task ScanAsync(VolumeCardViewModel? volume = null, bool forceFullScan = false)
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

            // Refresh prefers a snapshot plus the journal delta; it falls back to a full
            // scan on its own and reports which path it took.
            ScanResult result = await Task.Run(
                () => orchestrator.Refresh(volume.DriveLetter, StrategyPreference.Auto,
                                           allowSnapshot: !forceFullScan, token), token);

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

        string source = result.Incremental is not null
            ? $" · {SnapshotDescription.Describe(result.Incremental)}"
            : string.Empty;

        StatusText = L.T("scan.summary", Format.Count(files), Format.Duration(elapsed),
                         strategy + fallback + source);

        // Cross-check the measured total against what the volume reports as used. Only the
        // impossible direction is surfaced: telling someone "97% of the used space is
        // accounted for" every single scan is noise they would learn to ignore, and then it
        // would be ignored on the one scan where it said something.
        Reconciliation check = HasRealAllocation
            ? index.CheckAgainstFileSystem()
            : default;

        WarningText = check.IsImpossible ? check.Describe() : string.Empty;

        // A fresh index means the old entry numbers mean nothing. Anything still ticked
        // would point at whatever now sits at that record.
        _basket.Clear();
        _listSelection.Clear();
        RaiseSelectionChanged();

        // Árvore
        Root = new FolderNodeViewModel(index, index.RootIndex, this, index.Volume.Root);
        Root.IsExpanded = true;
        Root.IsSelected = true;
        Raise(nameof(RootNodes));

        RefreshAggregates();

        LoadVolumes();
        ShowBiggestFiles();
    }

    /// <summary>
    /// Recomputes everything derived from the index: the totals line and the three
    /// breakdowns in the sidebar.
    /// <para>
    /// Runs after a scan and again after a delete. Each one is a linear pass over the entry
    /// array — cheap enough to redo, and far better than leaving a breakdown on screen that
    /// still counts files the user just deleted.
    /// </para>
    /// </summary>
    private void RefreshAggregates()
    {
        VolumeIndex? index = Index;
        if (index is null) return;

        SummaryText = HasRealAllocation
            ? L.T("scan.logicalAndDisk", Format.Bytes(index.TotalLogicalBytes),
                  Format.Bytes(index.TotalBytesOnDisk), Format.Bytes(index.TotalSlackBytes))
            : L.T("scan.logicalOnly", Format.Bytes(index.TotalLogicalBytes));

        Extensions.Clear();
        foreach (ExtensionBucket bucket in SizeAnalyzer.ByExtension(index, 12))
            Extensions.Add(bucket);

        SizeBuckets.Clear();
        foreach (SizeBucket bucket in SizeAnalyzer.BySizeRange(index))
            if (bucket.Count > 0) SizeBuckets.Add(bucket);

        AgeBuckets.Clear();
        foreach (AgeBucket bucket in SizeAnalyzer.ByAge(index, DateTime.UtcNow))
            if (bucket.Count > 0) AgeBuckets.Add(bucket);
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

    /// <summary>
    /// The same rows, in a plain list.
    /// <para>
    /// Sorting happens here and the result is republished, so the row objects survive a
    /// re-sort — with them the thumbnails already decoded and the ticks already placed.
    /// Sorting the ObservableCollection in place would fire one notification per move.
    /// </para>
    /// </summary>
    private readonly List<FileRowViewModel> _rows = [];

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
        _rows.Clear();

        if (Index is null)
        {
            Rows.Clear();
            TotalMatches = 0;
            return;
        }

        foreach (int index in indices)
        {
            if (_rows.Count >= MaxRows) break;
            _rows.Add(new FileRowViewModel(Index, index, this));
        }

        // Everything that fills this list hands it over biggest-first. That is a sort by
        // size, descending — so say so, instead of letting the header arrow claim otherwise.
        _sortKey = RowSortKey.Size;
        _sortDescending = true;
        RaiseSortState();

        TotalMatches = total;
        SelectedRow = null;
        PublishRows();
    }

    /// <summary>Moves the working list into the bound collection, in its current order.</summary>
    private void PublishRows()
    {
        Rows.Clear();
        foreach (FileRowViewModel row in _rows) Rows.Add(row);

        Raise(nameof(TruncationText));
        Raise(nameof(IsTruncated));
    }

    // ================= ordenação =================

    private RowSortKey _sortKey = RowSortKey.Size;
    private bool _sortDescending = true;

    public RowSortKey SortKey => _sortKey;
    public bool SortDescending => _sortDescending;

    // Column headers carry their own sort arrow. Built here rather than in XAML so the
    // localized label and the arrow stay one string and refresh together.
    public string HeaderName => Header("column.name", RowSortKey.Name);
    public string HeaderSize => Header("column.size", RowSortKey.Size);
    public string HeaderModified => Header("column.modified", RowSortKey.Modified);
    public string HeaderPath => Header("column.path", RowSortKey.Path);

    private string Header(string key, RowSortKey column) =>
        column == _sortKey ? $"{L.T(key)}  {(_sortDescending ? '▾' : '▴')}" : L.T(key);

    private void RaiseSortState()
    {
        Raise(nameof(SortKey));
        Raise(nameof(SortDescending));
        Raise(nameof(HeaderName));
        Raise(nameof(HeaderSize));
        Raise(nameof(HeaderModified));
        Raise(nameof(HeaderPath));
    }

    /// <summary>
    /// Reorders the listed rows. Clicking the active column again reverses it.
    /// </summary>
    public void SortBy(RowSortKey key)
    {
        if (key == _sortKey)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = key;

            // Open each column on the order that answers the question it is asked: the
            // biggest file, the oldest file — but names and paths read A to Z.
            _sortDescending = key is RowSortKey.Size or RowSortKey.Modified;
        }

        SortRows();
        RaiseSortState();
        PublishRows();
    }

    private void SortRows()
    {
        Comparison<FileRowViewModel> ascending = _sortKey switch
        {
            RowSortKey.Name => static (a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),

            RowSortKey.Modified => static (a, b) => a.Modified.CompareTo(b.Modified),

            RowSortKey.Path => static (a, b) =>
                string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase),

            _ => static (a, b) => a.LogicalSize.CompareTo(b.LogicalSize),
        };

        // Ties keep folders above files: a folder and a file of the same size are not
        // equally interesting when the point is finding what to delete.
        Comparison<FileRowViewModel> comparison = (a, b) =>
        {
            int order = ascending(a, b);
            if (order != 0) return _sortDescending ? -order : order;
            return b.IsDirectory.CompareTo(a.IsDirectory);
        };

        _rows.Sort(comparison);
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

    /// <summary>
    /// Restricts the search to the folder selected in the tree.
    /// <para>
    /// Searching a whole volume for "render" answers a different question than searching one
    /// project folder for it, and the second is the one asked while clearing space.
    /// </para>
    /// </summary>
    private bool _searchInSelectedFolder;
    public bool SearchInSelectedFolder
    {
        get => _searchInSelectedFolder;
        set { if (Set(ref _searchInSelectedFolder, value)) ApplySearch(); }
    }

    /// <summary>
    /// Includes folders in the results, sized by their whole subtree.
    /// <para>
    /// On by default, and for a long time not possible at all: the scan loop skipped every
    /// directory, so a folder could never be found by name — the one thing you want when
    /// hunting for <c>node_modules</c> or an old build output.
    /// </para>
    /// </summary>
    private bool _searchFolders = true;
    public bool SearchFolders
    {
        get => _searchFolders;
        set { if (Set(ref _searchFolders, value)) ApplySearch(); }
    }

    public ICommand ClearFiltersCommand { get; }

    private void ClearFilters()
    {
        _searchText = string.Empty;
        _minSizeBytes = 0;
        _minAgeDays = 0;
        _extensionFilter = string.Empty;
        _searchInSelectedFolder = false;

        Raise(nameof(SearchText));
        Raise(nameof(MinSizeBytes));
        Raise(nameof(MinAgeDays));
        Raise(nameof(ExtensionFilter));
        Raise(nameof(SearchInSelectedFolder));

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

        VolumeIndex index = Index;
        string query = SearchText;
        string[] extensions = ExtensionFilter
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToArray();

        // A folder has no extension, so an extension filter is an implicit "files only".
        bool includeFolders = SearchFolders && extensions.Length == 0;

        // -1 means the whole volume.
        int scope = SearchInSelectedFolder && SelectedFolder is not null
            ? SelectedFolder.EntryIndex
            : -1;

        DateTime cutoff = MinAgeDays > 0 ? DateTime.UtcNow.AddDays(-MinAgeDays) : DateTime.MaxValue;

        if (includeFolders) index.BuildSubtreeSizes();

        var matches = new List<(int Index, long Size)>();

        // Passada linear sobre o array plano. Sem LINQ e sem materializar caminho:
        // é o que mantém a busca abaixo de 100 ms em um índice de milhões de entradas.
        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry entry = ref index.Entries[i];
            if (!entry.IsInUse) continue;

            bool isFolder = entry.IsDirectory;
            if (isFolder && !includeFolders) continue;

            // A folder stands for everything under it, so that is the size to filter on —
            // an empty-looking directory holding 40 GB must not be filtered out as small.
            long size = isFolder ? index.GetSubtreeSize(i) : entry.LogicalSize;

            if (MinSizeBytes > 0 && size < MinSizeBytes) continue;

            if (MinAgeDays > 0)
            {
                DateTime written = entry.LastWrite;
                if (written == DateTime.MinValue || written > cutoff) continue;
            }

            ReadOnlySpan<char> name = index.GetName(i);

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

            // Ancestry last on purpose: it is the only test that walks, and by here only a
            // handful of entries are still candidates.
            if (scope >= 0 && !IsUnder(index, i, scope)) continue;

            matches.Add((i, size));
        }

        matches.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        Fill(matches.Select(m => m.Index), matches.Count);
    }

    /// <summary>Is <paramref name="entryIndex"/> inside <paramref name="ancestor"/>?</summary>
    private static bool IsUnder(VolumeIndex index, int entryIndex, int ancestor)
    {
        if (entryIndex == ancestor) return false;

        int root = index.RootIndex;
        int current = entryIndex;

        // Same 512 ceiling GetFullPath uses: real depth never approaches it, and a corrupt
        // MFT with a parent cycle must not spin here.
        for (int guard = 0; guard < 512; guard++)
        {
            uint parent = index.Entries[current].ParentIndex;
            if (parent >= (uint)index.Entries.Length || parent == (uint)current) return false;

            current = (int)parent;
            if (current == ancestor) return true;
            if (current == root) return false;
        }

        return false;
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

    // ================= seleção =================

    /// <summary>
    /// The batch, by entry index.
    /// <para>
    /// Keyed on the index and nothing else: it costs four bytes an item, it survives every
    /// rebuild of the list and the tree, and the index itself still answers for the name,
    /// the size and the full path. Ticks in the file list and ticks in the folder tree land
    /// in the same set, because to the person deleting they are one selection.
    /// </para>
    /// </summary>
    private readonly HashSet<int> _basket = [];

    /// <summary>Rows highlighted in the list. Fed by the view (SelectedItems is not bindable).</summary>
    private readonly List<FileRowViewModel> _listSelection = [];

    /// <summary>Suppresses per-item notifications while a whole batch is being ticked.</summary>
    private bool _selectionBatch;

    void ISelectionSink.SetChecked(int entryIndex, bool isChecked)
    {
        bool changed = isChecked ? _basket.Add(entryIndex) : _basket.Remove(entryIndex);
        if (!changed || _selectionBatch) return;

        MirrorCheck(entryIndex, isChecked);
        RaiseSelectionChanged();
    }

    /// <summary>
    /// Shows one entry's tick on the other pane.
    /// <para>
    /// In folder mode a subfolder is a row in the list and a node in the tree at once. They
    /// are two objects for one entry, and a checkbox that only moves on the half you clicked
    /// makes the batch look like it lost an item.
    /// </para>
    /// </summary>
    private void MirrorCheck(int entryIndex, bool isChecked)
    {
        foreach (FileRowViewModel row in _rows)
        {
            if (row.EntryIndex != entryIndex) continue;
            row.SyncChecked(isChecked);
            break;
        }

        Root?.SyncChecked(entryIndex, isChecked);
    }

    bool ISelectionSink.IsChecked(int entryIndex) => _basket.Contains(entryIndex);

    public ICommand SelectAllCommand { get; }
    public ICommand InvertSelectionCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    /// <summary>Ticks everything currently listed — never the whole volume.</summary>
    private void SelectAllListed() =>
        InBatch(() => { foreach (FileRowViewModel row in _rows) row.IsChecked = true; });

    private void InvertListedSelection() =>
        InBatch(() => { foreach (FileRowViewModel row in _rows) row.IsChecked = !row.IsChecked; });

    /// <summary>Empties the basket — the listed rows, the tree ticks, and the rest.</summary>
    public void ClearSelection() => InBatch(() =>
    {
        foreach (FileRowViewModel row in _rows) row.IsChecked = false;
        Root?.ClearChecks();

        // Whatever was ticked in a folder that is no longer on screen has no row and no
        // node left to clear it through; it only exists here.
        _basket.Clear();
    });

    private void InBatch(Action action)
    {
        _selectionBatch = true;
        try { action(); }
        finally { _selectionBatch = false; }

        // The tree was not the one being ticked, so it has to catch up in one pass.
        Root?.SyncAllChecks();
        RaiseSelectionChanged();
    }

    /// <summary>
    /// What an action would act on: the ticked basket, or — when nothing is ticked — whatever
    /// is highlighted in the list right now.
    /// <para>
    /// Ctrl-clicking three rows and pressing Del has to keep working. The basket is there for
    /// gathering across folders, not as a new toll gate in front of the old gesture.
    /// </para>
    /// </summary>
    private List<int> EffectiveEntries()
    {
        if (_basket.Count > 0) return [.. _basket];

        var entries = new List<int>(_listSelection.Count);
        foreach (FileRowViewModel row in _listSelection) entries.Add(row.EntryIndex);
        return entries;
    }

    public int SelectedCount => _basket.Count > 0 ? _basket.Count : _listSelection.Count;
    public bool HasSelection => SelectedCount > 0;

    /// <summary>True while the selection is the persistent one rather than the highlight.</summary>
    public bool HasBasket => _basket.Count > 0;

    /// <summary>
    /// Bytes the selection stands for. A folder counts its whole subtree, and an item whose
    /// parent is also selected counts nothing — it is already inside that total.
    /// </summary>
    public long SelectedBytes
    {
        get
        {
            VolumeIndex? index = Index;
            if (index is null) return 0;

            long total = 0;

            foreach (int i in EffectiveEntries())
            {
                if (i < 0 || i >= index.Entries.Length) continue;

                ref FileEntry entry = ref index.Entries[i];
                if (!entry.IsInUse) continue;
                if (HasSelectedAncestor(index, i)) continue;

                total += entry.IsDirectory ? index.GetSubtreeSize(i) : entry.LogicalSize;
            }

            return total;
        }
    }

    /// <summary>
    /// Is some ancestor of this entry also in the basket?
    /// <para>
    /// Without this, ticking a folder and then a file inside it would report the file's bytes
    /// twice — a number nobody measured, on the one screen where the number decides what gets
    /// destroyed.
    /// </para>
    /// </summary>
    private bool HasSelectedAncestor(VolumeIndex index, int entryIndex)
    {
        if (_basket.Count == 0) return false;

        int root = index.RootIndex;
        int current = entryIndex;

        for (int guard = 0; guard < 512; guard++)
        {
            if (current == root) return false;

            uint parent = index.Entries[current].ParentIndex;
            if (parent >= (uint)index.Entries.Length || parent == (uint)current) return false;

            current = (int)parent;
            if (_basket.Contains(current)) return true;
        }

        return false;
    }

    public string SelectionSummaryText => SelectedCount == 0
        ? string.Empty
        : L.T("delete.selectionCount", Format.Count(SelectedCount), Format.Bytes(SelectedBytes));

    public string SelectionDetailText => SelectedCount == 1 && _listSelection.Count == 1
        ? $"{_listSelection[0].FullPath}\n{Format.Bytes(_listSelection[0].LogicalSize)} · {_listSelection[0].ModifiedText}"
        : SelectionSummaryText;

    /// <summary>Called by the view whenever the list highlight changes.</summary>
    public void SetListSelection(IEnumerable<FileRowViewModel> rows)
    {
        _listSelection.Clear();
        _listSelection.AddRange(rows);

        // The highlight drives the single-item actions whether or not a basket exists.
        SelectedRow = _listSelection.Count > 0 ? _listSelection[0] : null;

        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(HasBasket));
        Raise(nameof(SelectedBytes));
        Raise(nameof(SelectionSummaryText));
        Raise(nameof(SelectionDetailText));
    }

    // ================= exclusão =================

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
        VolumeIndex? index = Index;
        List<int> selected = EffectiveEntries();

        // Path back to entry. Pruning the index afterwards by matching strings would be
        // guessing at which row a result belongs to; this knows, because it built the path.
        var byPath = new Dictionary<string, int>(selected.Count, StringComparer.OrdinalIgnoreCase);

        if (index is not null)
        {
            foreach (int i in selected)
            {
                if (i < 0 || i >= index.Entries.Length || !index.Entries[i].IsInUse) continue;

                string path = index.GetFullPath(i);
                if (path.Length > 0) byPath[path.TrimEnd('\\')] = i;
            }
        }

        if (byPath.Count == 0)
        {
            StatusText = L.T("delete.nothingSelected");
            return;
        }

        List<string> paths = [.. byPath.Keys];
        var service = new DeleteService();

        // Plan first, always. The dialog shows exactly what will happen, including the
        // items the protection list refuses to touch.
        DeleteReport plan = service.Plan(paths, mode);

        if (!DeleteDialog.Confirm(owner, plan, mode)) return;

        DeleteReport report = service.Execute(paths, mode);

        // Take out of the index exactly what the disk reported as gone, and measure the
        // result from the entries themselves — the whole subtree included.
        Removal removed = default;

        foreach (DeleteResult result in report.Results)
        {
            if (!result.Succeeded) continue;
            if (byPath.TryGetValue(result.Path.TrimEnd('\\'), out int entry))
                removed += index!.MarkDeleted(entry);
        }

        if (report.FailedCount > 0) LastFailures = [.. report.Failures.Select(Describe)];

        ReportDeletion(report, removed, mode);
        AfterDeletion(removed);
    }

    /// <summary>
    /// Says what happened — and, for the Recycle Bin, what did not.
    /// <para>
    /// A move to the bin frees nothing. The bytes sit in <c>$Recycle.Bin</c> until it is
    /// emptied, so a status line reading "3 GiB freed" would be the app asserting a figure
    /// the disk plainly disagrees with. It says "still occupied" instead, and offers to open
    /// the bin.
    /// </para>
    /// </summary>
    private void ReportDeletion(DeleteReport report, Removal removed, DeleteMode mode)
    {
        // Only the MFT read measures allocation. Without it the logical size is the honest
        // figure to quote, and the summary line already says the same thing.
        long bytes = HasRealAllocation ? removed.BytesOnDisk : removed.LogicalBytes;

        string count = Format.Count(report.DeletedCount);
        string size = Format.Bytes(bytes);
        bool one = report.DeletedCount == 1;

        if (mode == DeleteMode.RecycleBin)
        {
            RecycledBytes += bytes;
            HasRecycled = report.DeletedCount > 0 || HasRecycled;

            StatusText = report.FailedCount > 0
                ? L.T("delete.recycledPartial", count, size, Format.Count(report.FailedCount))
                : one ? L.T("delete.recycledOne", size)
                      : L.T("delete.recycled", count, size);

            return;
        }

        StatusText = report.FailedCount > 0
            ? L.T("delete.donePartial", count, size, Format.Count(report.FailedCount))
            : one ? L.T("delete.doneOne", size)
                  : L.T("delete.done", count, size);
    }

    /// <summary>
    /// Brings every view back in line with an index that just lost entries.
    /// </summary>
    private void AfterDeletion(Removal removed)
    {
        if (removed.IsEmpty) return;

        if (!_anythingDeleted)
        {
            _anythingDeleted = true;
            Raise(nameof(FooterText));
        }

        DropDeletedRows();
        Root?.PruneDeleted();
        RefreshAggregates();

        // Free space moved, and the cards on the Dashboard still quote the figure from the
        // scan. Re-reading it is one call per volume.
        LoadVolumes();
    }

    /// <summary>Bytes moved to the Recycle Bin this session and not yet reclaimed.</summary>
    private long _recycledBytes;
    public long RecycledBytes
    {
        get => _recycledBytes;
        private set { Set(ref _recycledBytes, value); Raise(nameof(RecycledText)); }
    }

    private bool _hasRecycled;
    public bool HasRecycled { get => _hasRecycled; private set => Set(ref _hasRecycled, value); }

    public string RecycledText => L.T("delete.stillInBin", Format.Bytes(RecycledBytes));

    public ICommand OpenRecycleBinCommand { get; }

    private void OpenRecycleBin()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shell:RecycleBinFolder") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            StatusText = L.T("status.cannotOpenRecycleBin");
        }
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
    /// Drops the rows whose entries no longer exist.
    /// <para>
    /// Asking the index is exact and costs one array read per row. The previous version
    /// compared path strings, which meant a row survived whenever the two spellings of a
    /// path disagreed — and, worse, that the row was the only thing removed at all.
    /// </para>
    /// </summary>
    private void DropDeletedRows()
    {
        VolumeIndex? index = Index;
        if (index is null) return;

        int dropped = 0;

        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (index.Entries[_rows[i].EntryIndex].IsInUse) continue;

            _rows.RemoveAt(i);
            dropped++;
        }

        // Subtract rather than assign: on a truncated list the real total is still larger
        // than what is on screen, and claiming otherwise would understate the match count.
        if (dropped > 0) TotalMatches -= dropped;

        PublishRows();

        // A tick left pointing at a freed entry would keep the action bar up for something
        // that no longer exists.
        _basket.RemoveWhere(i => !index.Entries[i].IsInUse);
        _listSelection.RemoveAll(r => !index.Entries[r.EntryIndex].IsInUse);

        if (SelectedRow is not null && !index.Entries[SelectedRow.EntryIndex].IsInUse)
            SelectedRow = _listSelection.Count > 0 ? _listSelection[0] : null;

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

    // ================= componentes de IA =================

    public ObservableCollection<AiComponentRowViewModel> AiComponents { get; } = [];

    private bool _isAiScanning;
    public bool IsAiScanning
    {
        get => _isAiScanning;
        private set
        {
            if (!Set(ref _isAiScanning, value)) return;
            (RunAiScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private bool _hasAiRun;
    public bool HasAiRun { get => _hasAiRun; private set => Set(ref _hasAiRun, value); }

    private string _aiStatusText = L.T("ai.prompt");
    public string AiStatusText { get => _aiStatusText; private set => Set(ref _aiStatusText, value); }

    public string AiJournalNoteText => L.T("ai.journalNote", PolicyJournal.DefaultPath);

    public ICommand RunAiScanCommand { get; private set; } = null!;

    private async Task RunAiScanAsync()
    {
        IsAiScanning = true;
        AiStatusText = L.T("ai.running");

        try
        {
            AiScanReport report = await Task.Run(() => new AiComponentScanner().Scan());

            AiComponents.Clear();
            foreach (AiComponentStatus status in report.Items)
                AiComponents.Add(new AiComponentRowViewModel(status));

            HasAiRun = true;

            // Zero running is a real answer, and it gets its own sentence rather than being
            // dressed up as a saving that is not there.
            AiStatusText = report.MeasuredBytes > 0
                ? L.T("ai.summary", report.Items.Count, report.OnCount, Format.Bytes(report.MeasuredBytes))
                : L.T("ai.summaryNothingRunning", report.Items.Count, report.OnCount);
        }
        finally
        {
            IsAiScanning = false;
        }
    }

    /// <summary>Switches one component off, then re-reads the machine to show what stuck.</summary>
    public async Task TurnOffAsync(AiComponentRowViewModel row)
    {
        SwitchResult result = await Task.Run(() => new AiComponentSwitch().TurnOff(row.Component));
        row.Outcome = Describe(result, undone: false);
        await RefreshAiRowsAsync();
    }

    public async Task UndoAsync(AiComponentRowViewModel row)
    {
        SwitchResult result = await Task.Run(() => new AiComponentSwitch().Undo(row.Component));
        row.Outcome = Describe(result, undone: true);
        await RefreshAiRowsAsync();
    }

    /// <summary>
    /// Re-reads every component after a change.
    /// <para>
    /// The state shown always comes from a fresh read of the machine, never from assuming the
    /// write did what it was asked to.
    /// </para>
    /// </summary>
    private async Task RefreshAiRowsAsync()
    {
        AiScanReport report = await Task.Run(() => new AiComponentScanner().Scan());

        foreach (AiComponentStatus status in report.Items)
        {
            foreach (AiComponentRowViewModel row in AiComponents)
            {
                if (row.Component.Id != status.Component.Id) continue;
                row.Status = status;
                break;
            }
        }

        AiStatusText = report.MeasuredBytes > 0
            ? L.T("ai.summary", report.Items.Count, report.OnCount, Format.Bytes(report.MeasuredBytes))
            : L.T("ai.summaryNothingRunning", report.Items.Count, report.OnCount);
    }

    private static string Describe(SwitchResult result, bool undone) => result.Outcome switch
    {
        SwitchOutcome.Applied => L.T(undone ? "ai.outcomeUndone" : "ai.outcomeApplied"),
        SwitchOutcome.NoChange => L.T("ai.outcomeNoChange"),
        SwitchOutcome.NeedsElevation => L.T("ai.outcomeNeedsElevation"),
        SwitchOutcome.NotConfirmed => L.T("ai.outcomeNotConfirmed"),
        SwitchOutcome.NotActionable => L.T("ai.outcomeNotActionable"),
        _ => L.T("ai.outcomeFailed", result.Message ?? string.Empty),
    };

    // ================= inicialização do Windows =================

    public ObservableCollection<StartupRowViewModel> StartupItems { get; } = [];

    private bool _isStartupScanning;
    public bool IsStartupScanning
    {
        get => _isStartupScanning;
        private set
        {
            if (!Set(ref _isStartupScanning, value)) return;
            (RunStartupScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private bool _hasStartupRun;
    public bool HasStartupRun { get => _hasStartupRun; private set => Set(ref _hasStartupRun, value); }

    private string _startupStatusText = L.T("startup.prompt");
    public string StartupStatusText { get => _startupStatusText; private set => Set(ref _startupStatusText, value); }

    public ICommand RunStartupScanCommand { get; private set; } = null!;

    private async Task RunStartupScanAsync()
    {
        IsStartupScanning = true;
        StartupStatusText = L.T("startup.running");

        try
        {
            StartupReport report = await Task.Run(() => new StartupScanner().Scan());

            StartupItems.Clear();

            // Enabled first, and the heaviest of those at the top: the list is read to find
            // what to switch off, not to browse alphabetically.
            foreach (StartupEntry e in report.Entries
                         .OrderByDescending(e => e.IsEnabled)
                         .ThenByDescending(e => e.MeasuredBytes)
                         .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                StartupItems.Add(new StartupRowViewModel(e));
            }

            HasStartupRun = true;
            StartupStatusText = Summarize(report);
        }
        finally
        {
            IsStartupScanning = false;
        }
    }

    private static string Summarize(StartupReport report) => report.MeasuredBytes > 0
        ? L.T("startup.summary", report.Entries.Count, report.EnabledCount, Format.Bytes(report.MeasuredBytes))
        : L.T("startup.summaryNothingRunning", report.Entries.Count, report.EnabledCount);

    /// <summary>Switches one startup program, then re-reads the list to show what stuck.</summary>
    public async Task SetStartupEnabledAsync(StartupRowViewModel row, bool enabled)
    {
        SwitchOutcome outcome = await Task.Run(() => new StartupSwitch().SetEnabled(row.Entry, enabled));

        row.Outcome = outcome switch
        {
            SwitchOutcome.Applied => L.T(enabled ? "startup.outcomeEnabled" : "startup.outcomeDisabled"),
            SwitchOutcome.NeedsElevation => L.T("startup.outcomeNeedsElevation"),
            SwitchOutcome.NotConfirmed => L.T("startup.outcomeNotConfirmed"),
            _ => L.T("startup.outcomeFailed"),
        };

        await RefreshStartupRowsAsync();
    }

    /// <summary>
    /// Re-reads every entry after a change, so the state shown always comes from the machine
    /// rather than from assuming the write did what it was asked.
    /// </summary>
    private async Task RefreshStartupRowsAsync()
    {
        StartupReport report = await Task.Run(() => new StartupScanner().Scan());

        foreach (StartupEntry fresh in report.Entries)
        {
            foreach (StartupRowViewModel row in StartupItems)
            {
                if (row.Entry.Name != fresh.Name || row.Entry.Source != fresh.Source) continue;
                row.Entry = fresh;
                break;
            }
        }

        StartupStatusText = Summarize(report);
    }

    // ================= memória =================

    public ObservableCollection<MemoryRowViewModel> MemoryRows { get; } = [];

    private bool _hasMemoryRun;
    public bool HasMemoryRun { get => _hasMemoryRun; private set => Set(ref _hasMemoryRun, value); }

    private string _memoryStatusText = L.T("memory.prompt");
    public string MemoryStatusText { get => _memoryStatusText; private set => Set(ref _memoryStatusText, value); }

    private string _memoryTotals = string.Empty;
    public string MemoryTotals { get => _memoryTotals; private set => Set(ref _memoryTotals, value); }

    private string _memoryCompressed = string.Empty;
    public string MemoryCompressed { get => _memoryCompressed; private set => Set(ref _memoryCompressed, value); }

    private string _memoryStartupShare = string.Empty;
    public string MemoryStartupShare
    {
        get => _memoryStartupShare;
        private set { Set(ref _memoryStartupShare, value); Raise(nameof(HasStartupShare)); }
    }

    public bool HasStartupShare => _memoryStartupShare.Length > 0;

    private string _trimOutcome = string.Empty;
    public string TrimOutcome
    {
        get => _trimOutcome;
        private set { Set(ref _trimOutcome, value); Raise(nameof(HasTrimOutcome)); }
    }

    public bool HasTrimOutcome => _trimOutcome.Length > 0;

    public ICommand RunMemoryScanCommand { get; private set; } = null!;
    public ICommand TrimWorkingSetsCommand { get; private set; } = null!;

    private async Task RunMemoryScanAsync(string? keepStatus = null)
    {
        MemoryStatusText = keepStatus ?? L.T("memory.running");

        MemoryReport report = await Task.Run(() => new MemoryScanner().Scan(20));
        MemoryReading r = report.Reading;

        MemoryRows.Clear();
        foreach (ProcessMemory p in report.TopProcesses)
        {
            MemoryRows.Add(new MemoryRowViewModel(p)
            {
                Share = r.TotalBytes > 0 ? (double)p.PrivateBytes / r.TotalBytes : 0,
            });
        }

        MemoryTotals = $"{L.T("memory.total")} {Format.Bytes(r.TotalBytes)}  ·  " +
                       $"{L.T("memory.inUse")} {Format.Bytes(r.InUseBytes)}  ·  " +
                       $"{L.T("memory.available")} {Format.Bytes(r.AvailableBytes)}";

        // A result worth reading survives the refresh that follows it.
        MemoryStatusText = keepStatus ?? L.T("memory.load", r.LoadPercent);

        MemoryCompressed = r.CompressedBytes > 0
            ? $"{L.T("memory.compressed")}: {Format.Bytes(r.CompressedBytes)}"
            : string.Empty;

        // Only worth saying when there is something to say — and it points at the one screen
        // that can actually remove the cost for good.
        MemoryStartupShare = report.FromStartupBytes > 0
            ? L.T("memory.fromStartupTotal", Format.Bytes(report.FromStartupBytes))
            : string.Empty;

        HasMemoryRun = true;
    }

    /// <summary>
    /// Closes a program from the memory list.
    /// <para>
    /// Unlike the trim below, this really does free memory — so the wording may say freed.
    /// Both figures are reported: what the process was holding, and how much the machine's
    /// available memory actually rose. They are rarely the same number, and showing only the
    /// flattering one would be the arithmetic this app exists not to do.
    /// </para>
    /// </summary>
    public async Task CloseProcessAsync(MemoryRowViewModel row)
    {
        row.IsArmed = false;
        row.IsClosing = true;

        try
        {
            TerminateResult result = await Task.Run(() => new ProcessTerminator().CloseByName(row.Name));

            if (result.Succeeded)
            {
                string held = Format.Bytes(result.HeldBytes);
                string rose = Format.Bytes(Math.Max(0, result.AvailableRoseBytes));

                // Gone means gone: the row leaves the list, which is the visible confirmation
                // that the process really died rather than a message claiming it did.
                MemoryRows.Remove(row);

                MemoryStatusText = result.ClosedCount == result.AttemptedCount
                    ? L.T("memory.closed", held, rose)
                    : L.T("memory.closedPartial", result.ClosedCount, result.AttemptedCount, held, rose);

                await RunMemoryScanAsync(keepStatus: MemoryStatusText);
                return;
            }

            // Still there: the row stays, carrying the reason.
            row.Outcome = L.T(result.Outcome switch
            {
                TerminateOutcome.Protected => "memory.closeProtected",
                TerminateOutcome.AccessDenied => "memory.closeDenied",
                TerminateOutcome.StillRunning => "memory.closeStillRunning",
                TerminateOutcome.NotFound => "memory.closeNotFound",
                _ => "memory.closeFailed",
            });
        }
        finally
        {
            row.IsClosing = false;
        }
    }

    /// <summary>
    /// Empties every working set, and reports what really happened.
    /// <para>
    /// The result is worded as a movement, not as a saving, and a negative movement is shown
    /// as a negative — which is the case other utilities never put on screen.
    /// </para>
    /// </summary>
    private async Task TrimWorkingSetsAsync()
    {
        TrimResult result = await Task.Run(() => new WorkingSetTrimmer().TrimAll());

        TrimOutcome = result.MovedBytes >= 0
            ? L.T("memory.trimResult", result.ProcessesTouched, Format.Bytes(result.MovedBytes))
            : L.T("memory.trimResultNegative", result.ProcessesTouched, Format.Bytes(-result.MovedBytes));

        await RunMemoryScanAsync();
    }

    public void Dispose()
    {
        ThemeManager.Changed -= OnThemeChanged;
        _scanCts?.Dispose();
        _thumbnailCts.Dispose();
        _thumbnails.Dispose();
    }
}
