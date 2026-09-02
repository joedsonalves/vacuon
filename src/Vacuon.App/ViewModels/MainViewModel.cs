using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.App.Views;
using Vacuon.Core;
using Vacuon.Core.Actions;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Cleanup;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Monitoring;
using Vacuon.Core.Optimization;
using Vacuon.Core.Preview;
using Vacuon.Core.Safety;
using Vacuon.Core.Scan;
using Vacuon.Core.Security;
using Vacuon.Core.Transfer;
using Vacuon.Core.Update;
using Vacuon.Native.Interop;

namespace Vacuon.App.ViewModels;

public enum Section { Dashboard, Explorer, Treemap, Cleanup, Duplicates, Similar, Quarantine, Monitor, Security, Optimize, Settings }

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
        PauseScanCommand = new RelayCommand(TogglePause, () => IsScanning);
        ScanVolumeCommand = new RelayCommand(async p => await ScanAsync(p as VolumeCardViewModel));
        StartWatchCommand = new RelayCommand(StartWatch);
        StopWatchCommand = new RelayCommand(Monitor.Stop);
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
        FindSimilarCommand = new RelayCommand(async () => await FindSimilarEitherWayAsync(),
                                             () => !IsFindingSimilar);
        QuarantineSimilarCommand = new RelayCommand(QuarantineSimilar);
        ClearSimilarSelectionCommand = new RelayCommand(ClearSimilarSelection);
        BeginEditCommand = new RelayCommand(BeginEdit, () => CanEditPreview && !IsEditing);
        BeginHexEditCommand = new RelayCommand(BeginHexEdit, _ => CanEditHex && !IsEditing);
        WatchForReleaseCommand = new RelayCommand(WatchForRelease, () => CanWatchForRelease);

        _pending.Settled += OnPendingSettled;
        SaveEditCommand = new RelayCommand(SaveEdit, () => IsEditing);
        CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        SelectAllAudioCommand = new RelayCommand(SelectAllAudio);
        ScanForJunkCommand = new RelayCommand(ScanForJunk);
        RunCleanupCommand = new RelayCommand(RunCleanup);
        SetCleanupProfileCommand = new RelayCommand(p =>
            CleanupProfileChoice = (p as string) switch
            {
                "deep" => CleanupProfile.Deep,
                "custom" => CleanupProfile.Custom,
                _ => CleanupProfile.Quick,
            });
        FindDuplicatesCommand = new RelayCommand(async () => await FindDuplicatesEitherWayAsync(),
                                                () => !IsFindingDuplicates);
        QuarantineDuplicatesCommand = new RelayCommand(QuarantineDuplicates);
        SelectAllDuplicatesCommand = new RelayCommand(SelectAllDuplicates);
        ClearDuplicateSelectionCommand = new RelayCommand(ClearDuplicateSelection);
        RefreshQuarantineCommand = new RelayCommand(RefreshQuarantine);
        RestoreBatchCommand = new RelayCommand(RestoreBatch);
        PurgeBatchCommand = new RelayCommand(PurgeBatch);
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
            Raise(nameof(IsTreemap));
            Raise(nameof(IsCleanup));
            Raise(nameof(IsDuplicates));
            Raise(nameof(IsSimilar));
            Raise(nameof(IsQuarantine));
            Raise(nameof(IsMonitor));
            Raise(nameof(IsSecurity));
            Raise(nameof(IsOptimize));
            Raise(nameof(IsSettings));
            Raise(nameof(ShowScanStatus));
        }
    }

    public bool IsDashboard => Section == Section.Dashboard;
    public bool IsExplorer => Section == Section.Explorer;
    public bool IsTreemap => Section == Section.Treemap;
    public bool IsCleanup => Section == Section.Cleanup;
    public bool IsDuplicates => Section == Section.Duplicates;
    public bool IsSimilar => Section == Section.Similar;
    public bool IsQuarantine => Section == Section.Quarantine;
    public bool IsMonitor => Section == Section.Monitor;
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

        LoadTrends();
    }

    /// <summary>
    /// Reads the stored free-space history and hands each card its trend.
    /// <para>
    /// One file read for every volume at once. Nothing is computed that the readings do not
    /// support — a volume with no history gets a trend that refuses and says why, which is
    /// what the card shows.
    /// </para>
    /// </summary>
    private void LoadTrends()
    {
        var history = new SpaceHistory();

        foreach (VolumeCardViewModel card in Volumes)
            card.Trend = SpaceTrend.Of(card.DriveLetter, history.Read(card.DriveLetter));
    }

    /// <summary>The tray icon, once the window has one. Null before the handle exists.</summary>
    public TrayService? Tray { get; set; }

    // ---------------- live monitor (M9) ----------------

    /// <summary>The live view of what a volume is doing right now.</summary>
    public MonitorViewModel Monitor { get; } = new();

    public ICommand StartWatchCommand { get; private set; } = null!;
    public ICommand StopWatchCommand { get; private set; } = null!;

    private void StartWatch()
    {
        if (SelectedVolume is null) return;

        Monitor.Start(SelectedVolume.DriveLetter);
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
            (PauseScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    // ---------------- pausar e retomar (F1.4) ----------------

    private ScanPause? _pause;

    public ICommand PauseScanCommand { get; }

    /// <summary>True while the scan is held rather than running.</summary>
    public bool IsScanPaused => _pause?.IsPaused == true;

    /// <summary>The button says what pressing it will do, not what the state is.</summary>
    public string PauseButtonText => L.T(IsScanPaused ? "scan.resume" : "scan.pause");

    /// <summary>
    /// Holds the scan, or lets it go again.
    /// <para>
    /// Cancelling a scan of two and a half million records throws away eleven seconds of
    /// disk. Pausing keeps them — which is the whole point, because the reason to stop is
    /// usually that something else needs the drive for a minute.
    /// </para>
    /// </summary>
    private void TogglePause()
    {
        _pause?.Toggle();

        Raise(nameof(IsScanPaused));
        Raise(nameof(PauseButtonText));

        if (IsScanPaused) StatusText = L.T("scan.paused", Format.Bytes(_bytesReadSoFar));
    }

    private long _bytesReadSoFar;
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
            _bytesReadSoFar = p.BytesRead;

            // While it is held, the line says so and stays saying so: a progress figure that
            // keeps being redrawn under a paused scan reads as a scan that is still running.
            if (IsScanPaused) return;

            StatusText = p.TotalBytes > 0
                ? L.T("scan.progress", Format.Percent(p.Percent), Format.Count(p.EntriesFound),
                      p.MegabytesPerSecond.ToString("N0", L.Culture))
                : L.T("scan.progressItems", Format.Count(p.RecordsParsed), Format.Duration(p.Elapsed));
        });

        try
        {
            // A fresh hold per scan: the one from last time may be sitting paused.
            _pause = new ScanPause();
            Raise(nameof(IsScanPaused));
            Raise(nameof(PauseButtonText));

            var options = new MftScanOptions { Progress = progress, Pause = _pause };
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
        // Where the person was looking, by path rather than by entry number: a fresh index
        // renumbers everything, so the old number would point at a stranger. Restored at the
        // end, because a scan is a refresh of what is on screen and not a trip back to the
        // top of the volume.
        string wasShowing = Mode == ListMode.Folder ? CurrentFolderPath : string.Empty;

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

        // Not awaited: the shadow-copy query is a second or two of somebody else's process,
        // and the panel filling in a moment later is better than a scan that waits for it.
        _ = RefreshSystemSpaceAsync();

        int again = wasShowing.Length > 0 ? index.FindEntry(wasShowing) : -1;

        // Gone since the last scan, or never here: the biggest files are the sane landing
        // place, and it is what the first scan of a volume shows anyway.
        if (again >= 0 && index.Entries[again].IsDirectory) NavigateToFolder(again);
        else ShowBiggestFiles();
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
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            UpdatePreview();
        }
    }

    // ================= painel de preview =================

    private string _previewTitle = string.Empty;
    public string PreviewTitle
    {
        get => _previewTitle;
        private set
        {
            if (!Set(ref _previewTitle, value)) return;
            Raise(nameof(HasNoPreviewTarget));
        }
    }

    /// <summary>Path of the image to show, or empty when the highlighted item is not one.</summary>
    private string _previewImagePath = string.Empty;
    public string PreviewImagePath
    {
        get => _previewImagePath;
        private set
        {
            if (!Set(ref _previewImagePath, value)) return;

            Raise(nameof(HasPreviewImage));
            Raise(nameof(PreviewImageSource));

            // While comparing, a newly highlighted picture fills the second half rather
            // than replacing the one being compared against.
            if (_isComparing && value.Length > 0)
            {
                _compareImagePath = value;
                _compareTitle = _previewTitle;

                Raise(nameof(CompareImageSource));
                Raise(nameof(HasCompareTarget));
                Raise(nameof(CompareCaption));
            }
        }
    }

    public bool HasPreviewImage => _previewImagePath.Length > 0;

    /// <summary>True when nothing is highlighted, so the pane can say what it is for.</summary>
    public bool HasNoPreviewTarget => _previewTitle.Length == 0;

    /// <summary>Text or hex dump of the highlighted file.</summary>
    private string _previewText = string.Empty;
    public string PreviewText
    {
        get => _previewText;
        private set
        {
            if (!Set(ref _previewText, value)) return;
            Raise(nameof(IsPreviewMonospaced));
        }
    }

    private bool _previewIsHex;
    public bool IsPreviewMonospaced => _previewIsHex;

    /// <summary>Media facts, one per line, or empty when the shell knows nothing.</summary>
    private string _previewFacts = string.Empty;
    public string PreviewFacts { get => _previewFacts; private set => Set(ref _previewFacts, value); }

    private string _previewNote = string.Empty;
    public string PreviewNote { get => _previewNote; private set => Set(ref _previewNote, value); }

    // ---------------- zoom and side-by-side (M3, F6.4 and F6.14) ----------------

    /// <summary>
    /// The decoded picture, rather than a path for the Image to resolve.
    /// <para>
    /// Decoded here and cached because the compare view shows two at once and the zoom
    /// control re-renders on every wheel notch; handing a path to two Image elements would
    /// decode the same file twice and again on every layout pass.
    /// </para>
    /// </summary>
    public BitmapSource? PreviewImageSource => Decode(_previewImagePath);

    private double _previewZoom = 1;

    /// <summary>Whole percent: a tenth of a percent of zoom is not information.</summary>
    public string PreviewZoomText =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:N0}%", _previewZoom * 100);

    public void ReportZoom(double zoom)
    {
        _previewZoom = zoom;
        Raise(nameof(PreviewZoomText));
    }

    private bool _isComparing;

    /// <summary>
    /// Whether the pane is showing two files at once.
    /// <para>
    /// The question it exists for is the one the list cannot answer: of these two copies,
    /// which is the better one? Two thumbnails in a column look identical, and the difference
    /// is exactly what someone is trying to see before deleting one.
    /// </para>
    /// </summary>
    public bool IsComparing
    {
        get => _isComparing;
        set
        {
            if (!Set(ref _isComparing, value)) return;

            // Entering compare pins whatever is on screen as the left-hand side, so picking
            // the next file fills the other half instead of replacing the first.
            _compareImagePath = value ? _previewImagePath : string.Empty;
            _compareTitle = value ? _previewTitle : string.Empty;

            Raise(nameof(CompareImageSource));
            Raise(nameof(HasCompareTarget));
            Raise(nameof(CompareCaption));
        }
    }

    private string _compareImagePath = string.Empty;
    private string _compareTitle = string.Empty;

    public BitmapSource? CompareImageSource => Decode(_compareImagePath);

    public bool HasCompareTarget => _compareImagePath.Length > 0;

    public string CompareCaption => _compareImagePath.Length == 0
        ? string.Empty
        : L.T("preview.compareCaption", _previewTitle, _compareTitle);

    /// <summary>
    /// Decodes a picture once and keeps it, so panning and zooming do not re-read the disk.
    /// <para>
    /// One slot, not a cache: the pane shows at most two files and the second is pinned
    /// separately. A dictionary here would hold every picture ever previewed in memory for
    /// the life of the window.
    /// </para>
    /// </summary>
    private static BitmapSource? Decode(string path)
    {
        if (path.Length == 0) return null;

        try
        {
            var image = new BitmapImage();

            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;   // so the file is not left open
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException
                                      or ArgumentException or UnauthorizedAccessException)
        {
            // A picture Windows cannot decode shows as nothing rather than taking the pane
            // down. The facts panel above still says what the file is.
            return null;
        }
    }

    /// <summary>
    /// Fills the preview pane for whatever is highlighted.
    /// <para>
    /// Everything here reads at most the first 64 KiB or asks the shell for metadata it
    /// already has. Nothing decodes video and nothing walks a folder, because this runs on
    /// every arrow-key press through a list of a million rows.
    /// </para>
    /// </summary>
    private void UpdatePreview()
    {
        FileRowViewModel? row = _selectedRow;

        CanEditPreview = false;
        CanEditHex = false;
        if (!IsEditing) EditStatus = string.Empty;

        PreviewImagePath = string.Empty;
        PreviewText = string.Empty;
        PreviewFacts = string.Empty;
        PreviewNote = string.Empty;
        _previewIsHex = false;

        if (row is null)
        {
            PreviewTitle = string.Empty;
            return;
        }

        PreviewTitle = row.Name;

        string path = row.FullPath;
        if (path.Length == 0 || row.IsDirectory) return;

        string category = FileCategories.Of(row.Name.AsSpan());

        // Media facts first: they answer "which copy do I keep" for exactly the files whose
        // content a text preview could not help with.
        MediaInfo media = MediaProbe.Read(path);
        if (!media.IsEmpty) PreviewFacts = DescribeMedia(media);

        if (category == FileCategories.Image)
        {
            // WPF decodes through WIC, so whatever Windows can open — including HEIC and
            // WebP where the codec is installed — shows up here without extra code.
            PreviewImagePath = path;
            return;
        }

        // Video and audio have no still to show; the facts above are the preview.
        if (category is FileCategories.Video or FileCategories.Audio) return;

        // Byte a byte serve para qualquer arquivo que caiba — inclusive um .exe, que é o
        // caso em que alguém quer isso. O que decide não é o tipo, é o tamanho e a lista de
        // caminhos protegidos.
        CanEditHex = row.LogicalSize <= FileEditor.MaxEditableBytesAsHex && !ProtectedPaths.IsProtected(path);

        PreviewContent content = FilePreview.Read(path);

        switch (content.Kind)
        {
            case PreviewKind.Text:
                PreviewText = content.Text;

                // Offered only for what can actually be written: the same guards the editor
                // enforces, asked before the button appears rather than after it is pressed.
                CanEditPreview = content.FileBytes <= FileEditor.MaxEditableBytes
                                 && !ProtectedPaths.IsProtected(path);
                break;

            case PreviewKind.Binary:
                _previewIsHex = true;
                PreviewText = content.Text;
                break;

            default:
                PreviewNote = L.T("preview.nothing");
                return;
        }

        Raise(nameof(IsPreviewMonospaced));

        if (content.Truncated)
            PreviewNote = L.T("preview.truncated", Format.Bytes(content.BytesRead),
                              Format.Bytes(content.FileBytes));
    }

    // ================= edicao no painel de previa (F6.15) =================

    private EditableFile? _loaded;
    private FileEditor.EditableBytes? _loadedBytes;
    private string _editingPath = string.Empty;

    private bool _isEditingHex;

    /// <summary>
    /// Whether the editor is working on bytes rather than on text.
    /// <para>
    /// ⚠️ A different tier from editing text, and the screen says so before the first
    /// keystroke. Changing a byte of a program is not the kind of edit a quarantine helps
    /// with: the file keeps its name, its size and its place, and only stops working. There
    /// is no undo, and the app does not pretend there is.
    /// </para>
    /// </summary>
    public bool IsEditingHex
    {
        get => _isEditingHex;
        private set
        {
            if (!Set(ref _isEditingHex, value)) return;
            Raise(nameof(IsEditingText));
        }
    }

    public bool IsEditingText => _isEditing && !_isEditingHex;

    private readonly PendingSaves _pending = new();
    private readonly PendingSaveStore _pendingStore = new();
    private CancellationTokenSource? _pendingCts;

    private byte[]? _refusedContent;
    private string _refusedPath = string.Empty;

    private bool _canWatchForRelease;

    /// <summary>
    /// Whether there is an edit that a locked file refused, waiting to be offered a watch.
    /// <para>
    /// Telling somebody to close the program and do the work again is the app handing back a
    /// problem it is better placed to solve: it already knows the path, it already holds the
    /// bytes, and it can watch.
    /// </para>
    /// </summary>
    public bool CanWatchForRelease
    {
        get => _canWatchForRelease;
        private set => Set(ref _canWatchForRelease, value);
    }

    private bool _isWatchingForRelease;

    public bool IsWatchingForRelease
    {
        get => _isWatchingForRelease;
        private set => Set(ref _isWatchingForRelease, value);
    }

    private bool _canEditHex;

    /// <summary>Whether the bytes of what is selected could be opened at all.</summary>
    public bool CanEditHex
    {
        get => _canEditHex;
        private set => Set(ref _canEditHex, value);
    }

    private bool _isEditing;

    /// <summary>
    /// Whether the preview pane is an editor right now.
    /// <para>
    /// ⚠️ While this is true the file list and the tree are disabled. Not to be strict: the
    /// alternative is a person typing into a box, clicking another file, and losing what
    /// they wrote to a screen that gave no sign it was about to. Save or Cancel are one
    /// click away, and the dedicated window exists for edits long enough to want the list
    /// back.
    /// </para>
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (!Set(ref _isEditing, value)) return;
            Raise(nameof(IsNotEditing));
        }
    }

    public bool IsNotEditing => !_isEditing;

    private string _editorText = string.Empty;

    public string EditorText
    {
        get => _editorText;
        set
        {
            if (!Set(ref _editorText, value)) return;
            Raise(nameof(IsEditorDirty));
        }
    }

    /// <summary>Whether what is on screen differs from what is on disk.</summary>
    public bool IsEditorDirty =>
        (_loaded is not null && !string.Equals(_editorText, _loaded.Text, StringComparison.Ordinal))
        || (_loadedBytes is not null && !string.Equals(_editorText, _loadedBytes.Dump, StringComparison.Ordinal));

    private string _editStatus = string.Empty;

    public string EditStatus
    {
        get => _editStatus;
        private set => Set(ref _editStatus, value);
    }

    private bool _canEditPreview;

    /// <summary>Whether the Edit button has anything to offer for what is selected.</summary>
    public bool CanEditPreview
    {
        get => _canEditPreview;
        private set => Set(ref _canEditPreview, value);
    }

    public ICommand BeginEditCommand { get; private set; } = null!;
    public ICommand BeginHexEditCommand { get; private set; } = null!;

    /// <summary>The file the preview is showing, for the dialogs that need to name it.</summary>
    public string SelectedPath => _selectedRow?.FullPath ?? string.Empty;
    public ICommand SaveEditCommand { get; private set; } = null!;
    public ICommand CancelEditCommand { get; private set; } = null!;

    /// <summary>
    /// Opens the selected file for editing.
    /// <para>
    /// ⚠️ This reads the file <b>again, whole</b>, rather than editing the preview text. The
    /// preview holds the first 64 KiB; saving that back would write those bytes over the
    /// whole file. <see cref="FileEditor"/> refuses anything that does not fit rather than
    /// opening part of it, and the reason is shown here.
    /// </para>
    /// </summary>
    private void BeginEdit()
    {
        FileRowViewModel? row = _selectedRow;
        if (row is null || row.IsDirectory) return;

        EditableFile file = FileEditor.Load(row.FullPath);

        if (!file.CanEdit)
        {
            EditStatus = file.Outcome switch
            {
                EditLoadOutcome.TooBig => L.T("edit.tooBig", Format.Bytes(file.Bytes),
                                              Format.Bytes(FileEditor.MaxEditableBytes)),
                EditLoadOutcome.NotText => L.T("edit.notText"),
                EditLoadOutcome.Protected => L.T("edit.protected"),
                _ => L.T("edit.unreadable"),
            };

            return;
        }

        _loaded = file;
        _editingPath = row.FullPath;
        EditorText = file.Text;
        EditStatus = string.Empty;
        IsEditing = true;

        Raise(nameof(IsEditorDirty));
    }

    /// <summary>
    /// Opens the selected file byte by byte, after the person has been told what that means.
    /// </summary>
    /// <param name="confirmed">
    /// Whether the warning was shown and accepted. The view model refuses without it rather
    /// than trusting the caller to have asked — the one guard that must not be bypassable by
    /// wiring a button straight to the command.
    /// </param>
    private void BeginHexEdit(object? confirmed)
    {
        if (confirmed is not true) return;

        FileRowViewModel? row = _selectedRow;
        if (row is null || row.IsDirectory) return;

        FileEditor.EditableBytes file = FileEditor.LoadBytes(row.FullPath);

        if (!file.CanEdit)
        {
            EditStatus = file.Outcome switch
            {
                EditLoadOutcome.TooBig => L.T("edit.hexTooBig", Format.Bytes(file.FileBytes),
                                              Format.Bytes(FileEditor.MaxEditableBytesAsHex)),
                EditLoadOutcome.Protected => L.T("edit.protected"),
                _ => L.T("edit.unreadable"),
            };

            return;
        }

        _loadedBytes = file;
        _loaded = null;
        _editingPath = row.FullPath;
        EditorText = file.Dump;
        EditStatus = L.T("edit.hexOpen");
        IsEditingHex = true;
        IsEditing = true;

        Raise(nameof(IsEditorDirty));
    }

    /// <summary>
    /// Writes the dump back as bytes.
    /// </summary>
    /// <remarks>
    /// The dump is parsed before anything is written, and a dump that no longer holds the
    /// same number of bytes is refused. Growing or shrinking a file this way moves every
    /// offset after the edit, which in an executable breaks it in a way that looks like
    /// nothing until it is run.
    /// </remarks>
    private void SaveHexEdit()
    {
        if (_loadedBytes is null || _editingPath.Length == 0) return;

        HexParse parsed = HexDump.Parse(EditorText, _loadedBytes.Bytes.Length);

        if (!parsed.Succeeded)
        {
            EditStatus = parsed.Outcome == HexParseOutcome.BadDigit
                ? L.T("edit.hexBadDigit", Format.Count(parsed.Line))
                : L.T("edit.hexLengthChanged", Format.Count(_loadedBytes.Bytes.Length));

            return;
        }

        SaveResult result = FileEditor.SaveBytes(_editingPath, parsed.Bytes);

        if (result.Succeeded)
        {
            _loadedBytes = _loadedBytes with { Bytes = parsed.Bytes, Dump = EditorText };
            EditStatus = L.T("edit.saved");
            Raise(nameof(IsEditorDirty));
            return;
        }

        Remember(result, _editingPath, parsed.Bytes);
    }

    private void SaveEdit()
    {
        if (IsEditingHex) { SaveHexEdit(); return; }

        if (_loaded is null || _editingPath.Length == 0) return;

        SaveResult result = FileEditor.Save(_editingPath, EditorText, _loaded);

        if (result.Succeeded)
        {
            // The file on disk is now what is on screen, so the editor is clean without
            // being closed: saving is not the same act as finishing.
            _loaded = _loaded with { Text = EditorText };
            EditStatus = L.T("edit.saved");
            Raise(nameof(IsEditorDirty));

            UpdatePreview();
            return;
        }

        Remember(result, _editingPath, FileEditor.BytesFor(EditorText, _loaded));
    }

    /// <summary>
    /// Says why the save failed and, when it was the lock, keeps the bytes so the wait can be
    /// offered.
    /// </summary>
    private void Remember(SaveResult result, string path, byte[] content)
    {
        EditStatus = result.Outcome switch
        {
            SaveOutcome.Protected => L.T("edit.protected"),
            SaveOutcome.InUse => L.T("edit.inUse", Describe(result.Holders)),
            _ => L.T("edit.saveFailed", result.Message ?? string.Empty),
        };

        if (result.Outcome != SaveOutcome.InUse) return;

        _refusedPath = path;
        _refusedContent = content;
        CanWatchForRelease = true;
    }

    public ICommand WatchForReleaseCommand { get; private set; } = null!;

    /// <summary>
    /// Edits from an earlier session that were never written.
    /// </summary>
    /// <remarks>
    /// ⚠️ Asked at startup rather than written at startup. The file may have been changed by
    /// something else in the meantime, and writing over that without a word would be the app
    /// making a decision that belongs to the person — the reason the dialog offers to open
    /// the edit before committing to it.
    /// </remarks>
    public IReadOnlyList<StoredSave> WaitingFromLastTime() => _pendingStore.Load();

    /// <summary>Writes the edits from an earlier session, or gives up on them.</summary>
    public int ResumePending()
    {
        int written = 0;

        foreach (StoredSave entry in _pendingStore.Load())
        {
            byte[]? content = _pendingStore.Content(entry);
            if (content is null) continue;

            _pending.Queue(entry.Path, content);
        }

        written = _pending.TryAll();

        if (_pending.Count > 0)
        {
            IsWatchingForRelease = true;

            _pendingCts?.Cancel();
            _pendingCts = new CancellationTokenSource();

            _ = _pending.WatchAsync(TimeSpan.FromSeconds(2), _pendingCts.Token);
        }

        return written;
    }

    public void DiscardPending()
    {
        _pending.Clear();
        _pendingStore.Clear();
        IsWatchingForRelease = false;
    }

    /// <summary>
    /// Opens the waiting edit in the editor so it can be looked at before it is written.
    /// </summary>
    /// <returns><c>false</c> when the file it belongs to is no longer there.</returns>
    public bool ReviewPending(StoredSave entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        byte[]? content = _pendingStore.Content(entry);
        if (content is null || !System.IO.File.Exists(entry.Path)) return false;

        EditableFile file = FileEditor.Load(entry.Path);
        if (!file.CanEdit) return false;

        _loaded = file;
        _loadedBytes = null;
        IsEditingHex = false;
        _editingPath = entry.Path;

        // What is shown is the edit that was waiting, over what the file holds now — which is
        // exactly the comparison somebody needs to decide whether it still applies.
        EditorText = System.Text.Encoding.UTF8.GetString(content);
        EditStatus = L.T("pending.reviewing", System.IO.Path.GetFileName(entry.Path));
        IsEditing = true;

        Raise(nameof(IsEditorDirty));
        return true;
    }

    /// <summary>
    /// Waits for the file to be let go, then writes the edit that was refused.
    /// </summary>
    /// <remarks>
    /// ⚠️ The bytes queued are the ones that were refused, not whatever the editor holds when
    /// the file is finally released. Somebody who kept typing while waiting would otherwise
    /// get a version they never asked to save — and one they may have been in the middle of.
    /// </remarks>
    private void WatchForRelease()
    {
        if (_refusedContent is null || _refusedPath.Length == 0) return;

        _pending.Queue(_refusedPath, _refusedContent);

        // ⚠️ Kept on disk as well, so closing Vacuon does not throw the edit away. The wait
        // is for somebody to close another program, which can easily outlast this session.
        _pendingStore.Keep(_refusedPath, _refusedContent);

        CanWatchForRelease = false;
        IsWatchingForRelease = true;
        EditStatus = L.T("edit.watching", System.IO.Path.GetFileName(_refusedPath));

        _pendingCts?.Cancel();
        _pendingCts = new CancellationTokenSource();

        _ = _pending.WatchAsync(TimeSpan.FromSeconds(2), _pendingCts.Token);
    }

    private void OnPendingSettled(object? sender, PendingSaveResult result)
    {
        // The watch runs on a pool thread; everything it touches here is on screen.
        App.Current?.Dispatcher.Invoke(() =>
        {
            IsWatchingForRelease = _pending.Count > 0;

            // Settled either way: written, or refused for a reason waiting will not change.
            // Leaving it on disk would offer it again at the next launch.
            _pendingStore.Forget(result.Save.Path);

            EditStatus = result.Outcome switch
            {
                SaveOutcome.Saved => L.T("edit.savedAfterWait",
                                         System.IO.Path.GetFileName(result.Save.Path)),
                SaveOutcome.Protected => L.T("edit.protected"),
                _ when result.Message == "gone" =>
                    L.T("edit.waitedAndGone", System.IO.Path.GetFileName(result.Save.Path)),
                _ => L.T("edit.saveFailed", result.Message ?? string.Empty),
            };
        });
    }

    private static string Describe(IReadOnlyList<FileHolder> holders)
    {
        if (holders.Count == 0) return string.Empty;

        var names = new List<string>(holders.Count);
        foreach (FileHolder holder in holders) names.Add(holder.Name);

        return string.Join(", ", names);
    }

    private void CancelEdit()
    {
        // A espera continua: cancelar a edição é fechar o editor, não desistir do que já foi
        // aceito para ser escrito quando o arquivo liberar.
        _refusedContent = null;
        _refusedPath = string.Empty;
        CanWatchForRelease = false;

        _loaded = null;
        _loadedBytes = null;
        IsEditingHex = false;
        _editingPath = string.Empty;
        EditorText = string.Empty;
        EditStatus = string.Empty;
        IsEditing = false;
    }

    private static string DescribeMedia(MediaInfo media)
    {
        var lines = new List<string>(6);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) lines.Add($"{L.T(key)}: {value}");
        }

        Add("media.duration", media.Duration?.ToString(@"hh\:mm\:ss"));
        Add("media.resolution", media.Dimensions is null
            ? null
            : $"{media.Dimensions} ({media.ResolutionLabel})");
        Add("media.frameRate", media.FrameRate is null
            ? null
            : L.T("media.fps", media.FrameRate.Value.ToString("N3").TrimEnd('0').TrimEnd('.', ',')));
        Add("media.videoCodec", media.VideoCodec);
        Add("media.camera", media.CameraModel);
        Add("media.dateTaken", media.DateTaken?.ToString("yyyy-MM-dd HH:mm"));
        Add("media.location", media.LocationText);
        Add("media.altitude", media.AltitudeText);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Entry behind <see cref="CurrentFolderPath"/>, or -1 outside folder mode.</summary>
    private int _currentFolderIndex = -1;

    /// <summary>
    /// Lists <paramref name="entryIndex"/> and opens the tree down to it, so the two halves
    /// of the screen agree about where you are.
    /// </summary>
    /// <param name="highlight">
    /// An entry inside that folder to select, or -1. This is what makes pasting a full file
    /// path land on the file rather than merely in the right folder.
    /// </param>
    public void NavigateToFolder(int entryIndex, int highlight = -1)
    {
        VolumeIndex? index = Index;
        if (index is null || entryIndex < 0 || entryIndex >= index.Entries.Length) return;

        // Selecting the node is what calls ShowFolder — going through the tree keeps the
        // selection, the breadcrumb and the list in one state instead of three.
        if (Root is not null && Root.Reveal(Ancestry(index, entryIndex)) is null) ShowFolder(entryIndex);
        else if (Root is null) ShowFolder(entryIndex);

        // ⚠️ And again when the node was already the selected one. Selecting what is
        // selected changes nothing, so the setter that lists the folder never runs — which
        // is why typing a letter back into a folder path left the list empty: the path
        // resolved, the tree was already there, and nobody rebuilt the rows. The guard is
        // on what is actually on screen, not on the selection, so this costs nothing when
        // the list is already showing that folder.
        if (Mode != ListMode.Folder || _currentFolderIndex != entryIndex) ShowFolder(entryIndex);

        if (highlight >= 0) SelectRowByEntry(highlight);
    }

    /// <summary>
    /// The chain from just below the root down to <paramref name="entryIndex"/>, root first.
    /// </summary>
    private static List<int> Ancestry(VolumeIndex index, int entryIndex)
    {
        var chain = new List<int>();
        int root = index.RootIndex;
        int current = entryIndex;

        // Same 512 ceiling GetFullPath uses: real depth never approaches it, and a corrupt
        // MFT with a parent cycle must not spin here.
        for (int guard = 0; guard < 512 && current != root; guard++)
        {
            chain.Add(current);

            uint parent = index.Entries[current].ParentIndex;
            if (parent >= (uint)index.Entries.Length || parent == (uint)current) break;

            current = (int)parent;
        }

        chain.Reverse();
        return chain;
    }

    public void ShowFolder(int entryIndex)
    {
        if (Index is null) return;

        Mode = ListMode.Folder;
        _currentFolderIndex = entryIndex;
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

        // A path in the box is a request to go there, not to look for files whose name
        // contains a backslash. Answered from the scan already in memory, so pasting a
        // folder from Explorer lists it without touching the disk.
        int pathScope = -1;

        if (SearchText.Length > 0)
        {
            PathQueryResult path = PathQuery.Resolve(SearchText, Index);

            switch (path.Outcome)
            {
                case PathQueryOutcome.Folder:
                case PathQueryOutcome.File:
                    // A file resolves to the folder holding it, with the file highlighted:
                    // people paste a full file path far more often than they mean to.
                    int folder = path.Outcome == PathQueryOutcome.Folder
                        ? path.EntryIndex
                        : (int)Index.Entries[path.EntryIndex].ParentIndex;

                    bool otherFilters = MinSizeBytes > 0 || MinAgeDays > 0 || ExtensionFilter.Length > 0;

                    if (!otherFilters)
                    {
                        NavigateToFolder(folder,
                            path.Outcome == PathQueryOutcome.File ? path.EntryIndex : -1);
                        return;
                    }

                    // With a size or age filter also set, the path is a place to search
                    // rather than a place to list — which is the useful reading of
                    // "everything over 1 GB under this folder".
                    pathScope = folder;
                    break;

                case PathQueryOutcome.OtherVolume:
                    StatusText = L.T("search.otherVolume", path.Path, Index.Volume.Root);
                    break;

                case PathQueryOutcome.NotFound:
                    StatusText = L.T("search.pathNotFound", path.Path);
                    break;
            }
        }

        Mode = ListMode.Search;

        VolumeIndex index = Index;

        // A resolved path narrows the search to itself; the text is then a place, not a
        // name, and matching file names against it would find nothing.
        string query = pathScope >= 0 ? string.Empty : SearchText;
        string[] extensions = ExtensionFilter
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToArray();

        // A folder has no extension, so an extension filter is an implicit "files only".
        bool includeFolders = SearchFolders && extensions.Length == 0;

        // -1 means the whole volume. A path typed into the box beats the tree selection:
        // it is the more specific thing the person just asked for.
        int scope = pathScope >= 0
            ? pathScope
            : SearchInSelectedFolder && SelectedFolder is not null
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

    // ---------------- gallery (M3, F6.13) ----------------

    private bool _isGallery;

    /// <summary>
    /// Whether the file list is a grid of pictures instead of rows.
    /// <para>
    /// The rows answer "how big" and the grid answers "which one". Choosing between four
    /// copies of a photograph by reading four file names is the case this exists for, and it
    /// is exactly the case where a column of names tells you nothing.
    /// </para>
    /// </summary>
    public bool IsGallery
    {
        get => _isGallery;
        set
        {
            if (!Set(ref _isGallery, value)) return;
            Raise(nameof(IsListMode));
        }
    }

    public bool IsListMode => !_isGallery;

    // ---------------- notification area (F8.3, F8.4) ----------------

    // ---------------- version and updates ----------------

    private string _updateNoticeText = string.Empty;

    /// <summary>
    /// One line, and only when there is something to say: a version newer than this one is
    /// on offer. Silence is the answer the rest of the time — a "you are up to date" banner
    /// on every start is a line people stop reading, and then they stop reading the other one.
    /// </summary>
    public string UpdateNoticeText
    {
        get => _updateNoticeText;
        private set => Set(ref _updateNoticeText, value);
    }

    /// <summary>Opens the About window, which owns the version comparison and the hash.</summary>
    public void ShowAbout(Window owner) => Views.AboutWindow.Show(owner, _settings);

    /// <summary>
    /// Asks winget for a newer version, in the background, if the setting allows it.
    /// <para>
    /// Fired once at start and never awaited: the answer takes a second or two of somebody
    /// else's process, and nothing on screen waits for it.
    /// </para>
    /// </summary>
    public void StartUpdateCheck()
    {
        if (!_settings.CheckUpdatesOnStart) return;

        _ = Task.Run(async () =>
        {
            UpdateStatus status = await UpdateCheck.QueryAsync().ConfigureAwait(false);
            if (status.Outcome != UpdateOutcome.Available || status.Available is null) return;

            Application.Current?.Dispatcher.Invoke(() =>
                UpdateNoticeText = L.T("settings.updateAvailable", status.Available));
        });
    }

    public bool ShowTrayIcon
    {
        get => _settings.ShowTrayIcon;
        set
        {
            if (_settings.ShowTrayIcon == value) return;
            _settings.ShowTrayIcon = value;
            _settings.Save();
            Raise();

            // Takes effect now. A setting that needs a restart to be believed is one people
            // toggle twice and then distrust.
            Tray?.ApplySettings();
        }
    }

    public bool NotifyOnLowSpace
    {
        get => _settings.NotifyOnLowSpace;
        set { if (_settings.NotifyOnLowSpace == value) return; _settings.NotifyOnLowSpace = value; _settings.Save(); Raise(); }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { if (_settings.CloseToTray == value) return; _settings.CloseToTray = value; _settings.Save(); Raise(); }
    }

    /// <summary>
    /// The low-space threshold, in whole gibibytes.
    /// <para>
    /// Typed in the unit people think in. A box holding 10737418240 invites a typo that turns
    /// a warning off by three orders of magnitude without looking wrong.
    /// </para>
    /// </summary>
    public int LowSpaceThresholdGiB
    {
        get => (int)(_settings.LowSpaceThresholdBytes / (1024L * 1024 * 1024));
        set
        {
            long bytes = Math.Clamp(value, 1, 4096) * 1024L * 1024 * 1024;

            if (_settings.LowSpaceThresholdBytes == bytes) return;

            _settings.LowSpaceThresholdBytes = bytes;
            _settings.Save();
            Raise();
        }
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
    /// Raised when the list should scroll to a row and highlight it. Only the view can do
    /// that — the highlight lives in the ListView, not in the model.
    /// </summary>
    public event Action<FileRowViewModel>? RowRevealRequested;

    /// <summary>
    /// Highlights the row standing for <paramref name="entryIndex"/>, if it is on screen.
    /// <para>
    /// Silent when it is not. Pasting a path whose folder holds ten thousand files lists the
    /// folder truncated, and the file asked for may genuinely not be among the rows built —
    /// saying nothing beats selecting the wrong one.
    /// </para>
    /// </summary>
    private void SelectRowByEntry(int entryIndex)
    {
        foreach (FileRowViewModel row in _rows)
        {
            if (row.EntryIndex != entryIndex) continue;

            SelectedRow = row;
            RowRevealRequested?.Invoke(row);
            return;
        }
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

    /// <summary>
    /// The paths a drag would carry: the same selection Delete and Move act on.
    /// <para>
    /// Deliberately the effective selection rather than the highlighted row, so dragging does
    /// not quietly disagree with the buttons about what "the selection" means.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SelectedPaths()
    {
        var paths = new List<string>();

        if (Index is null) return paths;

        foreach (int entry in EffectiveEntries())
        {
            string path = Index.GetFullPath(entry);
            if (path.Length > 0) paths.Add(path);
        }

        return paths;
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

        // The Recycle Bin stays with the shell: robocopy cannot recycle, and a faster delete
        // that quietly erased instead of recycling would be the app doing something other
        // than what the button says. Permanent removal of a folder is the case the mirror
        // trick actually wins — one threaded walk instead of a recursion per directory.
        bool worthTheEngine = mode == DeleteMode.Permanent
                           && plan.Results.Any(r => r.Succeeded && r.IsDirectory);

        DeleteReport report = worthTheEngine
            ? RunDeleteThroughTransfer(owner, paths)
            : service.Execute(paths, mode);

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

    // ================= mover =================

    /// <summary>
    /// Asks for a destination folder, plans the move, confirms it, runs it, and puts the
    /// index back in agreement with the disk.
    /// <para>
    /// This is the one batch action in the app that destroys nothing, and it exists because
    /// triaging a folder by hand — open a video, decide, move it — was impossible with a
    /// highlight that a double click wipes out. It acts on the ticked basket, which does
    /// not care how many files were opened in between.
    /// </para>
    /// </summary>
    public void MoveSelection(Window owner)
    {
        VolumeIndex? index = Index;
        List<int> selected = EffectiveEntries();

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
            StatusText = L.T("move.nothingSelected");
            return;
        }

        string? destination = AskForFolder();
        if (destination is null) return;

        List<string> paths = [.. byPath.Keys];
        var service = new MoveService();

        MoveReport plan = service.Plan(paths, destination);

        // A destination the service refuses never reaches the dialog: there is nothing to
        // confirm, only something to say.
        if (plan.Verdict != DestinationVerdict.Ok)
        {
            StatusText = L.T(plan.Verdict switch
            {
                DestinationVerdict.Missing => "move.destinationMissing",
                DestinationVerdict.NotAFolder => "move.destinationNotAFolder",
                _ => "move.destinationProtected",
            });
            return;
        }

        if (!MoveDialog.Confirm(owner, plan)) return;

        // A move that stays on the volume is a rename: instant, undoable from Explorer, and
        // nothing a copy engine could make faster. One that crosses volumes is a full copy
        // followed by a delete, which is the case worth thirty-two threads and a window that
        // says how long is left — so only that one is handed to the transfer engine.
        bool leaveJunction = MoveDialog.LeaveJunctionBehind;

        MoveReport report = plan.CrossVolume
            ? RunMoveThroughTransfer(owner, paths, plan)
            : service.Execute(paths, plan.Destination);

        // ⚠️ After the move, never before: a junction created first would be the thing the
        // copy engine walked into, and it would copy the folder into itself.
        if (leaveJunction) LeaveJunctions(report);

        ApplyMove(report, byPath);
    }

    /// <summary>
    /// Puts a junction where each moved folder was, pointing at where it went (F5.11/F7.10).
    /// <para>
    /// This is what makes "these 240 GB can go to the D:" a real offer rather than a promise
    /// to fix a hundred broken paths afterwards. A junction is not a shortcut: anything that
    /// opens the old path lands on the new folder in the file system, below the level where
    /// a program could tell the difference.
    /// </para>
    /// <para>
    /// ⚠️ Folders only, and only across volumes — the two cases a junction answers. And it
    /// runs <b>after</b> the move: a junction put in place first is a folder that the copy
    /// engine would walk into and copy into itself.
    /// </para>
    /// </summary>
    private void LeaveJunctions(MoveReport report)
    {
        int made = 0;
        var failed = new List<string>();

        foreach (MoveResult result in report.Results)
        {
            if (!result.Succeeded || !result.IsDirectory || !result.CrossVolume) continue;

            string landed = Path.Combine(report.Destination, result.FinalName);
            if (!Directory.Exists(landed)) continue;

            if (Junction.Create(result.Source, landed)) made++;
            else failed.Add(result.Source);
        }

        if (made == 0 && failed.Count == 0) return;

        var parts = new List<string>(2);

        if (made > 0)
        {
            parts.Add(made == 1
                ? L.T("move.junctionsMadeOne")
                : L.T("move.junctionsMade", Format.Count(made)));
        }

        if (failed.Count > 0)
        {
            parts.Add(L.T("move.junctionsFailed", Format.Count(failed.Count)));
            LastFailures = [.. failed];
        }

        StatusText += "  ·  " + string.Join("  ·  ", parts);
    }

    // ================= sobrescrever (F7.6) =================

    /// <summary>
    /// Overwrites the ticked files and removes them (F7.6).
    /// <para>
    /// ⚠️ The doubts are worked out first and shown before anybody agrees to anything, and
    /// they are <b>detected</b> rather than boilerplate: the volume already told the scan
    /// whether it has a seek penalty, and each file's attributes say whether writing over it
    /// would land somewhere else. On solid state this does not erase anything, and saying
    /// that afterwards would be saying it too late.
    /// </para>
    /// </summary>
    public async void ShredSelection(Window owner)
    {
        VolumeIndex? index = Index;
        List<string> paths = [.. BasketPaths(out _).Where(File.Exists)];

        if (paths.Count == 0)
        {
            StatusText = L.T("move.nothingSelected");
            return;
        }

        bool solidState = index is not null && !index.Volume.IncursSeekPenalty;
        bool shadows = _shadowCopiesPresent;

        ShredDoubt doubt = ShredDoubt.None;
        long bytes = 0;

        foreach (string path in paths)
        {
            var info = new FileInfo(path);
            bytes += info.Length;
            doubt |= ShredService.DoubtsAbout(info, solidState, shadows);
        }

        if (!ShredDialog.Confirm(owner, paths, bytes, doubt)) return;

        List<ShredResult> results = await Task.Run(() =>
        {
            var done = new List<ShredResult>(paths.Count);
            foreach (string path in paths) done.Add(ShredService.Shred(path, solidState, shadows));
            return done;
        });

        int gone = 0;
        long shredded = 0;
        var failed = new List<string>();

        foreach (ShredResult result in results)
        {
            if (result.Succeeded)
            {
                gone++;
                shredded += result.Bytes;
                continue;
            }

            failed.Add($"{result.Path} — {result.Message ?? result.Outcome.ToString()}");
        }

        StatusText = gone == 0
            ? L.T("shred.nothing")
            : doubt != ShredDoubt.None
                ? L.T("shred.doneUncertain", Format.Count(gone), Format.Bytes(shredded))
                : gone == 1
                    ? L.T("shred.doneOne", Format.Count(gone), Format.Bytes(shredded))
                    : L.T("shred.done", Format.Count(gone), Format.Bytes(shredded));

        if (failed.Count > 0) LastFailures = [.. failed];

        if (gone > 0)
        {
            DropDeletedRows();
            RefreshAggregates();
            LoadVolumes();
        }
    }

    /// <summary>Whether this volume holds shadow copies, from the last time it was asked.</summary>
    private bool _shadowCopiesPresent;

    // ================= compactar (F7.11) =================

    /// <summary>
    /// Stores the ticked items compressed by NTFS.
    /// <para>
    /// The one action in this app that gives space back without removing anything: the files
    /// stay where they are, keep their length, and keep opening. Logs, text and code halve;
    /// anything already compressed gains nothing, and the report says so rather than
    /// rounding a zero up into a success.
    /// </para>
    /// <para>
    /// ⚠️ The figure is measured — clusters before, clusters after — never the assumed ratio
    /// the candidate list carries, which is a number from somebody else's disk.
    /// </para>
    /// </summary>
    public async void CompressSelection()
    {
        List<string> paths = BasketPaths(out _);

        if (paths.Count == 0)
        {
            StatusText = L.T("move.nothingSelected");
            return;
        }

        StatusText = L.T("compress.working");

        List<CompressResult> results = await Task.Run(() =>
        {
            var done = new List<CompressResult>(paths.Count);
            foreach (string path in paths) done.Add(CompressionService.Compress(path));
            return done;
        });

        int touched = 0;
        long freed = 0;
        var refused = new List<string>();

        foreach (CompressResult result in results)
        {
            if (result.Succeeded)
            {
                touched++;
                freed += result.Freed;
                continue;
            }

            if (result.Outcome != CompressOutcome.Unchanged)
                refused.Add($"{result.Path} — {result.Message ?? result.Outcome.ToString()}");
        }

        StatusText = touched == 0
            ? L.T("compress.nothing")
            : refused.Count > 0
                ? L.T("compress.partial", Format.Count(touched), Format.Bytes(freed), Format.Count(refused.Count))
                : freed <= 0
                    ? L.T("compress.gainedNothing", Format.Count(touched))
                    : touched == 1
                        ? L.T("compress.doneOne", Format.Count(touched), Format.Bytes(freed))
                        : L.T("compress.done", Format.Count(touched), Format.Bytes(freed));

        if (refused.Count > 0) LastFailures = [.. refused];

        // The clusters really did come back, and the volume cards still quote the figure the
        // scan took. The index is not touched: the files are all still there, same length.
        if (freed != 0) LoadVolumes();
    }

    // ================= copiar =================

    /// <summary>
    /// Copies the ticked items into another folder, through robocopy, with a window that
    /// shows the transfer as it happens.
    /// <para>
    /// The list had no copy at all before this: the only ways out of a selection were to
    /// move it, quarantine it or delete it. Sorting a drive usually starts by copying the
    /// keepers somewhere safe, and having to leave the app to do that is how a session ends.
    /// </para>
    /// <para>
    /// No confirmation dialog, unlike a move or a delete. A copy destroys nothing and takes
    /// nothing away, and the folder picker is already an explicit choice; the transfer window
    /// names the destination, the count and the bytes before the first file lands, and stops
    /// on one click. What it does have to say is the renames — nothing is ever overwritten,
    /// so a name the destination already uses goes in as "name (2)".
    /// </para>
    /// </summary>
    public void CopySelection(Window owner)
    {
        VolumeIndex? index = Index;
        List<string> paths = BasketPaths(out _);

        if (paths.Count == 0)
        {
            StatusText = L.T("move.nothingSelected");
            return;
        }

        string? destination = AskForFolder("transfer.pickCopyFolder");
        if (destination is null) return;

        var service = new FileTransferService();
        TransferPlan plan = service.Plan(paths, destination, TransferKind.Copy, MeasureFromIndex(index));

        if (plan.IsEmpty)
        {
            StatusText = L.T("transfer.nothingToCopy");
            return;
        }

        TransferReport? report = TransferWindow.Run(owner, plan);
        if (report is null) return;

        // A copy adds bytes to the destination volume and takes none from this one, so
        // nothing here is allowed to say "freed". What it does add is files this scan has
        // never seen, and until now they went in at the next full scan — which meant copying
        // a folder into the folder you were looking at changed nothing on screen.
        int planted = 0;
        bool complete = true;

        if (index is not null)
        {
            foreach (TransferItemResult result in report.Results)
            {
                if (!result.Succeeded || result.Item.Destination.Length == 0) continue;

                GraftResult graft = IndexGraft.AddTree(index, result.Item.Destination);
                planted += graft.Added;
                if (!graft.Complete) complete = false;
            }
        }

        StatusText = report.FailedCount > 0
            ? L.T("transfer.copiedPartial", Format.Count(report.DoneCount),
                  Format.Bytes(report.BytesTransferred), Format.Count(report.FailedCount))
            : L.T("transfer.copied", Format.Count(report.DoneCount),
                  Format.Bytes(report.BytesTransferred), plan.Destination);

        if (report.FailedCount > 0)
        {
            LastFailures = [.. report.Failures.Select(
                f => $"{f.Item.Source} — {f.Message ?? L.T("delete.outcomeFailed")}")];
        }

        if (planted > 0)
        {
            // The tree grows its new folders, the totals count the new bytes, and the list
            // relists itself when the copy landed in the folder being shown.
            Root?.Resync();
            RefreshAggregates();

            if (Mode == ListMode.Folder && _currentFolderIndex >= 0) ShowFolder(_currentFolderIndex);
        }

        // Said out loud rather than left to be noticed: a tree too big to plant on the spot
        // leaves the list behind the disk until the next scan.
        if (!complete) StatusText += "  ·  " + L.T("transfer.listBehind");

        // Free space on the destination volume moved, and the Dashboard cards still quote
        // the figure the scan took.
        LoadVolumes();
    }

    /// <summary>Full paths of the ticked basket, with the index positions they came from. Not the same as SelectedPaths(), which answers for the rows merely highlighted.</summary>
    private List<string> BasketPaths(out Dictionary<string, int> byPath)
    {
        VolumeIndex? index = Index;
        List<int> selected = EffectiveEntries();

        byPath = new Dictionary<string, int>(selected.Count, StringComparer.OrdinalIgnoreCase);

        if (index is not null)
        {
            foreach (int i in selected)
            {
                if (i < 0 || i >= index.Entries.Length || !index.Entries[i].IsInUse) continue;

                string path = index.GetFullPath(i);
                if (path.Length > 0) byPath[path.TrimEnd('\\')] = i;
            }
        }

        return [.. byPath.Keys];
    }

    /// <summary>
    /// Folder sizes and file counts straight out of the index instead of a fresh walk.
    /// <para>
    /// The transfer plan needs a byte total to divide a percentage into, and measuring a
    /// folder of two million files by enumerating it would take longer than the copy. The
    /// scan already has that number.
    /// </para>
    /// </summary>
    private static Func<string, TransferMeasurement>? MeasureFromIndex(VolumeIndex? index)
    {
        if (index is null) return null;

        return path =>
        {
            int entry = index.FindEntry(path);
            if (entry < 0) return new TransferMeasurement(0, 1);

            if (!index.Entries[entry].IsDirectory)
                return new TransferMeasurement(index.Entries[entry].LogicalSize, 1);

            // A folder is worth every file under it, on both counts. The subtree totals are
            // already built, so this is two array reads rather than a walk of the tree.
            return new TransferMeasurement(
                index.GetSubtreeSize(entry),
                Math.Max(1, index.GetSubtreeFileCount(entry)));
        };
    }

    /// <summary>
    /// Runs a cross-volume move through the transfer engine and hands back the same
    /// <see cref="MoveReport"/> the shell path produces, so nothing downstream has to know
    /// which engine did the work.
    /// </summary>
    private MoveReport RunMoveThroughTransfer(Window owner, List<string> paths, MoveReport plan)
    {
        var service = new FileTransferService();

        // Both planners walk DeleteService.Collapse in the same order and call the same
        // MoveService.FreeName, so the destination names in this plan are the ones the
        // confirmation dialog just showed.
        TransferPlan transfer = service.Plan(paths, plan.Destination, TransferKind.Move, MeasureFromIndex(Index));

        TransferReport? report = TransferWindow.Run(owner, transfer);

        if (report is null)
            return new MoveReport([], plan.Destination, DestinationVerdict.Ok, plan.CrossVolume, WasDryRun: false);

        var results = new List<MoveResult>(report.Results.Count);

        foreach (TransferItemResult result in report.Results)
        {
            results.Add(new MoveResult(
                result.Item.Source,
                result.Succeeded ? result.Item.Destination : result.Item.Source,
                result.Outcome switch
                {
                    TransferOutcome.Done => MoveOutcome.Moved,
                    TransferOutcome.Blocked => MoveOutcome.Blocked,
                    TransferOutcome.NotFound => MoveOutcome.NotFound,
                    TransferOutcome.IntoItself => MoveOutcome.IntoItself,
                    TransferOutcome.AlreadyThere => MoveOutcome.AlreadyThere,
                    _ => MoveOutcome.Failed,
                },
                result.Succeeded ? result.Item.Bytes : 0,
                result.Item.IsDirectory,
                result.Message));
        }

        return new MoveReport(results, plan.Destination, DestinationVerdict.Ok, plan.CrossVolume, WasDryRun: false);
    }

    /// <summary>
    /// Runs a permanent deletion through the transfer engine, reporting it as the
    /// <see cref="DeleteReport"/> the rest of the app already knows how to apply.
    /// </summary>
    private DeleteReport RunDeleteThroughTransfer(Window owner, List<string> paths)
    {
        var service = new FileTransferService();
        TransferPlan transfer = service.Plan(paths, string.Empty, TransferKind.Delete, MeasureFromIndex(Index));

        TransferReport? report = TransferWindow.Run(owner, transfer);

        if (report is null) return new DeleteReport([], DeleteMode.Permanent, WasDryRun: false);

        var results = new List<DeleteResult>(report.Results.Count);

        foreach (TransferItemResult result in report.Results)
        {
            results.Add(new DeleteResult(
                result.Item.Source,
                result.Outcome switch
                {
                    TransferOutcome.Done => DeleteOutcome.Deleted,
                    TransferOutcome.Blocked => DeleteOutcome.Blocked,
                    TransferOutcome.NotFound => DeleteOutcome.NotFound,
                    _ => DeleteOutcome.Failed,
                },
                result.Item.Bytes,
                result.Item.IsDirectory,
                result.Message));
        }

        return new DeleteReport(results, DeleteMode.Permanent, WasDryRun: false);
    }

    /// <summary>
    /// Sets the ticked items aside, reversibly.
    /// <para>
    /// The index treatment is the same as a move that stays on the volume, and for the same
    /// reason: the bytes did not go anywhere. Marking them deleted would drop them out of
    /// the volume total and claim back space the disk never returned.
    /// </para>
    /// </summary>
    public void QuarantineSelection(Window owner)
    {
        VolumeIndex? index = Index;
        List<int> selected = EffectiveEntries();

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
            StatusText = L.T("move.nothingSelected");
            return;
        }

        List<string> paths = [.. byPath.Keys];
        var service = new QuarantineService();

        QuarantineReport plan = service.Plan(paths);
        if (!QuarantineDialog.Confirm(owner, plan)) return;

        QuarantineReport report = service.Execute(paths);

        ApplyQuarantine(report, byPath);
    }

    private void ApplyQuarantine(QuarantineReport report, Dictionary<string, int> byPath)
    {
        VolumeIndex? index = Index;

        var moved = new HashSet<int>();
        int unplaced = 0;

        if (index is not null && report.BatchFolders.Count > 0)
        {
            // One batch folder per volume; the list view only ever shows one volume, so the
            // first is the one these rows went into.
            int destination = MoveTarget.Locate(index, report.BatchFolders[0]);

            foreach (QuarantineResult result in report.Results)
            {
                if (!result.Succeeded) continue;
                if (!byPath.TryGetValue(result.Path.TrimEnd('\\'), out int entry)) continue;

                moved.Add(entry);

                // Re-parent, never MarkDeleted: the clusters are still allocated and the
                // volume total must not fall.
                if (destination < 0 ||
                    !index.MarkMoved(entry, destination, Path.GetFileName(result.Path).AsSpan()))
                {
                    unplaced++;
                }
            }
        }

        if (report.FailedCount > 0) LastFailures = [.. report.Failures.Select(Describe)];

        ReportQuarantine(report, unplaced);

        // Nothing was freed, so the removal passed on is empty by construction.
        AfterMove(moved, default);
    }

    private void ReportQuarantine(QuarantineReport report, int unplaced)
    {
        string size = Format.Bytes(report.BytesHeld);

        string headline = report.FailedCount > 0
            ? L.T("quarantine.donePartial", Format.Count(report.QuarantinedCount),
                  Format.Count(report.FailedCount))
            : report.QuarantinedCount == 1
                ? L.T("quarantine.doneOne", size)
                : L.T("quarantine.done", Format.Count(report.QuarantinedCount), size);

        var parts = new List<string>(2) { headline };

        if (unplaced > 0) parts.Add(L.T("move.staleIndex", Format.Count(unplaced)));

        StatusText = string.Join(" · ", parts);
    }

    /// <summary>
    /// Brings the index in line with what the disk just did.
    /// <para>
    /// Two different truths, and telling them apart is the whole job. Within one volume the
    /// item only changed parent — re-parenting it keeps the totals right, because the bytes
    /// never left. Across volumes it really is gone from here, so it leaves the index the
    /// same way a delete does, and that space really was freed.
    /// </para>
    /// </summary>
    private void ApplyMove(MoveReport report, Dictionary<string, int> byPath)
    {
        VolumeIndex? index = Index;
        if (index is null) return;

        // Resolved on the first item that needs it: a batch that only left the volume has
        // nothing to place, and locating the folder would read the disk for nothing.
        int destinationEntry = -1;
        bool destinationResolved = false;

        var moved = new HashSet<int>();
        Removal left = default;
        int unplaced = 0;

        foreach (MoveResult result in report.Results)
        {
            if (!result.Succeeded) continue;
            if (!byPath.TryGetValue(result.Source.TrimEnd('\\'), out int entry)) continue;

            moved.Add(entry);

            if (result.CrossVolume)
            {
                // Gone from this volume for real: its clusters are free here now.
                left += index.MarkDeleted(entry);
                continue;
            }

            if (!destinationResolved)
            {
                destinationEntry = MoveTarget.Locate(index, report.Destination);
                destinationResolved = true;
            }

            if (destinationEntry < 0 ||
                !index.MarkMoved(entry, destinationEntry, result.FinalName.AsSpan()))
            {
                unplaced++;
            }
        }

        if (report.FailedCount > 0) LastFailures = [.. report.Failures.Select(Describe)];

        ReportMove(report, left, unplaced);
        AfterMove(moved, left);
    }

    /// <summary>
    /// Says what happened — and, for a move that stayed on the volume, what did not.
    /// <para>
    /// "42 items moved · 30 GiB" reads like 30 GiB came back. On one volume not a single
    /// byte did: the files are the same size in the same place on the platter, under a
    /// different name in a different directory. The sentence says so.
    /// </para>
    /// </summary>
    private void ReportMove(MoveReport report, Removal left, int unplaced)
    {
        string destination = report.Destination;
        string count = Format.Count(report.MovedCount);
        string size = Format.Bytes(report.Bytes);

        string headline = report.FailedCount > 0
            ? L.T("move.donePartial", count, destination, size, Format.Count(report.FailedCount))
            : report.MovedCount == 1
                ? L.T("move.doneOne", destination, size)
                : L.T("move.done", count, destination, size);

        var parts = new List<string>(3) { headline };

        if (report.MovedCount > 0)
        {
            // Only the MFT read measures allocation; without it the logical size is the
            // honest figure, exactly as the delete report does it.
            long freed = HasRealAllocation ? left.BytesOnDisk : left.LogicalBytes;

            parts.Add(report.CrossVolume
                ? L.T("move.freed", Format.Bytes(freed))
                : L.T("move.freedNothing"));
        }

        int renamed = report.Renames.Count();
        if (renamed > 0) parts.Add(L.T("move.renamed", Format.Count(renamed)));

        // The index could not place them, so the list is now behind the disk for those
        // items. Saying it is the only alternative to showing them where they no longer are.
        if (unplaced > 0) parts.Add(L.T("move.staleIndex", Format.Count(unplaced)));

        StatusText = string.Join(" · ", parts);
    }

    /// <summary>
    /// Refreshes the list and the tree around the items that just changed place.
    /// </summary>
    private void AfterMove(HashSet<int> moved, Removal left)
    {
        VolumeIndex? index = Index;
        if (index is null || moved.Count == 0) return;

        // Untick before the rows are rebuilt: a row reads its tick from the basket as it is
        // created, so clearing afterwards would leave a ticked-looking row over an empty
        // basket — and the action bar would offer to act on it.
        foreach (int entry in moved) _basket.Remove(entry);

        int dropped = 0;

        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            int entry = _rows[i].EntryIndex;
            if (!moved.Contains(entry)) continue;

            bool stillHere = index.Entries[entry].IsInUse
                          && (Mode != ListMode.Folder
                              || index.Entries[entry].ParentIndex == (uint)_currentFolderIndex);

            if (!stillHere)
            {
                _rows.RemoveAt(i);
                dropped++;
                continue;
            }

            // Same entry, new name and new path. The row caches both, so it is rebuilt
            // rather than nudged — and it reads its tick back from the basket on the way.
            _rows[i] = new FileRowViewModel(index, entry, this);
        }

        if (dropped > 0) TotalMatches -= dropped;
        PublishRows();

        _basket.RemoveWhere(i => !index.Entries[i].IsInUse);
        _listSelection.RemoveAll(r => !index.Entries[r.EntryIndex].IsInUse);

        if (SelectedRow is not null && !index.Entries[SelectedRow.EntryIndex].IsInUse)
            SelectedRow = _listSelection.Count > 0 ? _listSelection[0] : null;

        Root?.Resync();
        RaiseSelectionChanged();
        RefreshAggregates();

        // Only a move across volumes changes free space; on one volume the cards are
        // already right and re-reading them would say the same thing.
        if (!left.IsEmpty) LoadVolumes();
    }

    /// <summary>
    /// The folder picker, opened on the folder being listed.
    /// <para>
    /// It allows creating a folder on the spot, which is how sorting usually starts — and
    /// why <see cref="MoveTarget"/> has to be able to adopt a folder younger than the scan.
    /// </para>
    /// </summary>
    /// <param name="titleKey">
    /// Which verb the dialog's own title uses. The picker was hard-coded to say "move the
    /// selection into" and then got reused by the copy, so copying a thousand files opened a
    /// window announcing a move — the app describing an action it was not about to take.
    /// </param>
    private string? AskForFolder(string titleKey = "move.pickFolder")
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L.T(titleKey),
            Multiselect = false,
        };

        string start = CurrentFolderPath.Length > 0 ? CurrentFolderPath : Index?.Volume.Root ?? string.Empty;
        if (start.Length > 0 && Directory.Exists(start)) dialog.InitialDirectory = start;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>Human-readable reason a single item did not move.</summary>
    private static string Describe(QuarantineResult result)
    {
        string reason = result.Outcome switch
        {
            QuarantineOutcome.Blocked => result.Message ?? L.T("delete.outcomeBlocked"),
            QuarantineOutcome.NotFound => L.T("delete.outcomeNotFound"),
            QuarantineOutcome.InUse => L.T("quarantine.outcomeInUse"),
            QuarantineOutcome.AccessDenied => L.T("quarantine.outcomeAccessDenied"),
            // Worth its own sentence: nothing was moved, and the reason is the volume, not
            // the file. "Failed" would send someone looking at the wrong thing.
            QuarantineOutcome.NoQuarantineOnVolume =>
                L.T("quarantine.noRoom", Path.GetPathRoot(result.Path) ?? result.Path),
            _ => result.Message ?? L.T("quarantine.outcomeFailed"),
        };

        return $"{result.Path} — {reason}";
    }

    private static string Describe(MoveResult result)
    {
        string reason = result.Outcome switch
        {
            MoveOutcome.Blocked => result.Message ?? L.T("delete.outcomeBlocked"),
            MoveOutcome.NotFound => L.T("delete.outcomeNotFound"),
            MoveOutcome.InUse => L.T("delete.outcomeInUse"),
            MoveOutcome.AccessDenied => L.T("delete.outcomeAccessDenied"),
            MoveOutcome.IntoItself => L.T("move.outcomeIntoItself"),
            MoveOutcome.AlreadyThere => L.T("move.outcomeAlreadyThere"),
            _ => result.Message ?? L.T("delete.outcomeFailed"),
        };

        return $"{result.Source} — {reason}";
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

    // ================= imagens parecidas =================

    public ObservableCollection<SimilarGroupViewModel> SimilarGroups { get; } = [];

    private bool _isFindingSimilar;
    public bool IsFindingSimilar
    {
        get => _isFindingSimilar;
        private set
        {
            if (!Set(ref _isFindingSimilar, value)) return;
            (FindSimilarCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _similarStatusText = L.T("similar.needScan");
    public string SimilarStatusText
    {
        get => _similarStatusText;
        private set => Set(ref _similarStatusText, value);
    }

    private string _similarSelectionText = string.Empty;
    public string SimilarSelectionText
    {
        get => _similarSelectionText;
        private set => Set(ref _similarSelectionText, value);
    }

    private bool _hasSimilarSelection;
    public bool HasSimilarSelection
    {
        get => _hasSimilarSelection;
        private set => Set(ref _hasSimilarSelection, value);
    }

    /// <summary>
    /// Re-reads whether a scan exists, so the pane stops telling someone to scan a drive
    /// they already scanned. The status text was set once at construction and never
    /// revisited, which is only correct until the first scan finishes.
    /// </summary>
    public void RefreshSimilarStatus()
    {
        if (SimilarGroups.Count > 0 || IsFindingSimilar) return;

        if (SimilarByAudio)
        {
            // The picture scope counts pictures. Saying it here would answer a question the
            // screen is not asking.
            if (AudioGroups.Count == 0) SimilarStatusText = L.T("similar.audioHint");
            return;
        }

        VolumeIndex? index = Index;

        if (index is null)
        {
            SimilarStatusText = L.T("similar.needScan");
            return;
        }

        // Free, and said before the expensive part: unlike exact duplicates, every
        // candidate here has to be decoded, and on a full disk that is minutes.
        SimilarScope scope = new NearDuplicateFinder().Scope(index, new NearDuplicateOptions());

        SimilarStatusText = scope.Candidates == 0
            ? L.T("similar.scopeNone")
            : L.T("similar.scope", Format.Count(scope.Candidates),
                  Format.Bytes(scope.CandidateBytes));
    }

    // ---------------- parecidos por som (F4.7) ----------------

    private bool _similarByAudio;

    /// <summary>
    /// Whether the search listens to audio instead of looking at pictures.
    /// <para>
    /// The same question about a different sense. A FLAC and an MP3 of one track have nothing
    /// in common as bytes and everything in common as music, and the exact search — which is
    /// right about bytes — cannot see it.
    /// </para>
    /// </summary>
    public bool SimilarByAudio
    {
        get => _similarByAudio;
        set
        {
            if (!Set(ref _similarByAudio, value)) return;

            SimilarGroups.Clear();
            AudioGroups.Clear();
            Raise(nameof(HasAudioGroups));
            Raise(nameof(SimilarByPictures));

            Raise(nameof(SimilarSubtitle));
            Raise(nameof(SimilarFindLabel));

            SimilarStatusText = _similarByAudio ? L.T("similar.audioHint") : string.Empty;
            if (!_similarByAudio) RefreshSimilarStatus();
        }
    }

    public bool SimilarByPictures
    {
        get => !_similarByAudio;
        set { if (value) SimilarByAudio = false; }
    }

    public ObservableCollection<AudioMatchGroupViewModel> AudioGroups { get; } = [];

    public bool HasAudioGroups => AudioGroups.Count > 0;

    /// <summary>The warning that belongs to every audio match, shown once above the list.</summary>
    public string AudioNoLinkNote => L.T("similar.audioNoLink");

    /// <summary>
    /// The heading and the button follow the mode.
    /// <para>
    /// Leaving them saying "pictures" while the screen is set to audio would be the app
    /// describing something it is not about to do.
    /// </para>
    /// </summary>
    public string SimilarSubtitle =>
        L.T(_similarByAudio ? "similar.subtitleAudio" : "similar.subtitle");

    public string SimilarFindLabel =>
        L.T(_similarByAudio ? "similar.findAudio" : "similar.find");

    /// <summary>
    /// Looks for recordings that are the same music in different files.
    /// <para>
    /// ⚠️ Nothing found here may be turned into a hard link, and the screen says so: a link
    /// gives two names to one set of bytes, and these files only sound alike. Pointing one
    /// path at the other's bytes would replace somebody's file rather than remove a copy.
    /// </para>
    /// </summary>
    private async Task FindAudioMatchesAsync()
    {
        VolumeIndex? index = Index;
        if (index is null) return;

        _similarCts?.Cancel();
        _similarCts = new CancellationTokenSource();
        CancellationToken token = _similarCts.Token;

        IsFindingSimilar = true;
        AudioGroups.Clear();
        Raise(nameof(HasAudioGroups));

        var progress = new Progress<DuplicateProgress>(p =>
            SimilarStatusText = L.T("similar.audioWorking", Format.Count(p.FilesDone),
                                    Format.Count(p.FilesTotal)));

        try
        {
            AudioMatchReport report = await Task.Run(
                () => AudioDuplicateFinder.Find(index, AudioFingerprint.MatchThreshold, progress, token),
                token);

            foreach (AudioMatchGroup group in report.Groups)
                AudioGroups.Add(new AudioMatchGroupViewModel(group, UpdateSimilarSelection));

            var parts = new List<string>(2)
            {
                report.GroupCount == 0
                    ? L.T("similar.audioNone")
                    : report.GroupCount == 1
                        ? L.T("similar.audioSummaryOne", Format.Bytes(report.RecoverableBytes))
                        : L.T("similar.audioSummary", Format.Count(report.GroupCount),
                              Format.Bytes(report.RecoverableBytes)),
            };

            // Files Windows cannot decode are said out loud rather than quietly missing: a
            // search that skipped half the library in silence would be worse than one that
            // found nothing.
            if (report.Unreadable > 0)
                parts.Add(L.T("similar.audioUnreadable", Format.Count(report.Unreadable)));

            SimilarStatusText = string.Join("  ·  ", parts);
        }
        catch (OperationCanceledException)
        {
            SimilarStatusText = L.T("similar.cancelled");
        }
        finally
        {
            IsFindingSimilar = false;
            Raise(nameof(HasAudioGroups));
            UpdateSimilarSelection();
        }
    }

    private CancellationTokenSource? _similarCts;

    public void CancelSimilarSearch() => _similarCts?.Cancel();

    public ICommand FindSimilarCommand { get; private set; } = null!;
    public ICommand QuarantineSimilarCommand { get; private set; } = null!;
    public ICommand ClearSimilarSelectionCommand { get; private set; } = null!;
    public ICommand SelectAllAudioCommand { get; private set; } = null!;

    /// <summary>Ticks every redundant copy in every group. The keepers have no tick to reach.</summary>
    private void SelectAllAudio()
    {
        foreach (AudioMatchGroupViewModel group in AudioGroups) group.SelectAll();
    }

    /// <summary>Pictures or audio, whichever the screen is set to.</summary>
    private Task FindSimilarEitherWayAsync() =>
        SimilarByAudio ? FindAudioMatchesAsync() : FindSimilarAsync();

    private async Task FindSimilarAsync()
    {
        VolumeIndex? index = Index;

        if (index is null)
        {
            SimilarStatusText = L.T("similar.needScan");
            return;
        }

        IsFindingSimilar = true;
        SimilarStatusText = L.T("dup.searching");
        SimilarGroups.Clear();
        SimilarSelectionText = string.Empty;
        HasSimilarSelection = false;

        _similarCts?.Dispose();
        _similarCts = new CancellationTokenSource();
        CancellationToken token = _similarCts.Token;

        var progress = new Progress<DuplicateProgress>(p =>
            SimilarStatusText = L.T("similar.progress",
                                    Format.Count(p.FilesDone), Format.Count(p.FilesTotal)));

        try
        {
            SimilarReport report = await Task.Run(
                () => new NearDuplicateFinder().Find(index, new NearDuplicateOptions(),
                                                     progress, token),
                token);

            foreach (SimilarGroup group in report.Groups.Take(200))
                SimilarGroups.Add(new SimilarGroupViewModel(group, UpdateSimilarSelection));

            var parts = new List<string>(5);

            // Said first, because everything after it describes a partial answer.
            if (report.WasCancelled) parts.Add(L.T("similar.cancelled"));

            parts.AddRange(new[]
            {
                report.Groups.Count == 0
                    ? L.T("similar.none")
                    : report.Groups.Count == 1
                        ? L.T("similar.summaryOne", Format.Bytes(report.RecoverableBytes))
                        : L.T("similar.summary", Format.Count(report.Groups.Count),
                              Format.Bytes(report.RecoverableBytes)),

                L.T("similar.fingerprinted", Format.Count(report.ImagesFingerprinted)),
            });

            // Both kinds of "not compared" are named. A report that only counts what it
            // examined reads as though the rest had been examined and found unique.
            if (report.ImagesSkipped > 0)
                parts.Add(L.T("similar.skipped", Format.Count(report.ImagesSkipped)));

            if (report.ImagesBelowMinimum > 0)
                parts.Add(L.T("similar.belowMinimum", Format.Count(report.ImagesBelowMinimum)));

            SimilarStatusText = string.Join(" · ", parts);

            // Thumbnails after the list exists, so the groups appear immediately and fill in.
            foreach (SimilarGroupViewModel group in SimilarGroups)
                await group.LoadThumbnailsAsync(_thumbnails);
        }
        catch (OperationCanceledException)
        {
            // Only reachable if the task itself was cancelled before Find could return a
            // partial report; the finder handles the usual case and hands back what it has.
            SimilarStatusText = L.T("similar.cancelled");
        }
        finally
        {
            IsFindingSimilar = false;
        }
    }

    private void UpdateSimilarSelection()
    {
        int count = 0;
        long bytes = 0;

        foreach (SimilarGroupViewModel group in SimilarGroups)
        {
            foreach (SimilarVersionViewModel version in group.Versions)
            {
                if (!version.IsChecked) continue;
                count++;
                bytes += version.Image.Bytes;
            }
        }

        foreach (AudioMatchGroupViewModel group in AudioGroups)
        {
            foreach (AudioCopyViewModel copy in group.Copies)
            {
                if (!copy.IsChecked) continue;
                count++;
                bytes += copy.RecoverableBytes;
            }
        }

        HasSimilarSelection = count > 0;
        SimilarSelectionText = count == 0
            ? string.Empty
            : L.T("dup.selectedCount", Format.Count(count), Format.Bytes(bytes));
    }

    private void ClearSimilarSelection()
    {
        foreach (SimilarGroupViewModel group in SimilarGroups)
            foreach (SimilarVersionViewModel version in group.Versions)
                version.IsChecked = false;

        foreach (AudioMatchGroupViewModel group in AudioGroups)
            foreach (AudioCopyViewModel copy in group.Copies)
                copy.IsChecked = false;

        UpdateSimilarSelection();
    }

    private void QuarantineSimilar(object? parameter)
    {
        if (parameter is not Window owner) return;

        var paths = new List<string>();

        foreach (SimilarGroupViewModel group in SimilarGroups)
            foreach (SimilarVersionViewModel version in group.Versions)
                if (version.IsChecked) paths.Add(version.Path);

        foreach (AudioMatchGroupViewModel group in AudioGroups)
            foreach (AudioCopyViewModel copy in group.Copies)
                if (copy.IsChecked) paths.Add(copy.Path);

        if (paths.Count == 0) return;

        var service = new QuarantineService();
        QuarantineReport plan = service.Plan(paths, "similar");

        if (!QuarantineDialog.Confirm(owner, plan)) return;

        QuarantineReport report = service.Execute(paths, "similar");

        ReportQuarantine(report, 0);

        if (report.FailedCount > 0) LastFailures = [.. report.Failures.Select(Describe)];

        if (SimilarByAudio)
        {
            // Searching again would decode every file on the volume a second time — minutes,
            // to learn what the run just did. The moved copies are dropped from the list
            // instead, and a group with nothing redundant left is no longer a group.
            DropQuarantinedAudio(report);
            return;
        }

        _ = FindSimilarAsync();
    }

    private void DropQuarantinedAudio(QuarantineReport report)
    {
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (QuarantineResult result in report.Results)
            if (result.Succeeded) gone.Add(result.Path);

        for (int i = AudioGroups.Count - 1; i >= 0; i--)
        {
            AudioMatchGroupViewModel group = AudioGroups[i];

            List<AudioTrack> left = [.. group.Group.Tracks.Where(t => !gone.Contains(t.Path))];

            if (left.Count == group.Group.Tracks.Count) continue;

            if (left.Count < 2) { AudioGroups.RemoveAt(i); continue; }

            AudioGroups[i] = new AudioMatchGroupViewModel(
                new AudioMatchGroup(left, group.Group.Similarity), UpdateSimilarSelection);
        }

        Raise(nameof(HasAudioGroups));
        UpdateSimilarSelection();
    }

    // ================= limpeza por regras =================

    public ObservableCollection<CleanupCategoryViewModel> CleanupCategories { get; } = [];

    private CleanupPlan? _cleanupPlan;

    private CleanupProfile _cleanupProfile = CleanupProfile.Quick;
    public CleanupProfile CleanupProfileChoice
    {
        get => _cleanupProfile;
        set
        {
            if (!Set(ref _cleanupProfile, value)) return;
            Raise(nameof(IsQuickProfile));
            Raise(nameof(IsDeepProfile));
            Raise(nameof(IsCustomProfile));
            ScanForJunk();
        }
    }

    public bool IsQuickProfile => _cleanupProfile == CleanupProfile.Quick;
    public bool IsDeepProfile => _cleanupProfile == CleanupProfile.Deep;
    public bool IsCustomProfile => _cleanupProfile == CleanupProfile.Custom;

    private string _cleanupStatusText = string.Empty;
    public string CleanupStatusText
    {
        get => _cleanupStatusText;
        private set => Set(ref _cleanupStatusText, value);
    }

    private string _cleanupSelectionText = string.Empty;
    public string CleanupSelectionText
    {
        get => _cleanupSelectionText;
        private set => Set(ref _cleanupSelectionText, value);
    }

    private bool _hasCleanupSelection;
    public bool HasCleanupSelection
    {
        get => _hasCleanupSelection;
        private set => Set(ref _hasCleanupSelection, value);
    }

    public ICommand ScanForJunkCommand { get; private set; } = null!;
    public ICommand RunCleanupCommand { get; private set; } = null!;
    public ICommand SetCleanupProfileCommand { get; private set; } = null!;

    /// <summary>
    /// What the tray menu's "quick cleanup" does: selects the profile and builds the plan.
    /// <para>
    /// It stops there. A menu item that ran a cleanup would be this app deleting on one
    /// click from a corner of the screen, with no chance to see what was matched — the
    /// opposite of every other route into the same operation.
    /// </para>
    /// </summary>
    public void StartQuickCleanupScan()
    {
        CleanupProfileChoice = CleanupProfile.Quick;
        ScanForJunk();
    }

    /// <summary>
    /// Builds the plan. Reads the disk and changes nothing — the button that changes things
    /// is a different one, and it can only act on what this produced.
    /// </summary>
    public void ScanForJunk()
    {
        RuleCatalog.CatalogLoad catalog = RuleCatalog.LoadWithProblems();

        CleanupPlan plan = new RuleEngine().Plan(
            catalog.Rules, _cleanupProfile, IsElevated);

        _cleanupPlan = plan;

        CleanupCategories.Clear();

        // Group by category, keeping the biggest rule first inside each.
        var byCategory = new Dictionary<string, List<CleanupRuleViewModel>>(StringComparer.Ordinal);

        foreach (RulePlan rule in plan.Rules)
        {
            // A rule the profile excluded is not this screen's business — the profile
            // buttons already say what is being run.
            if (rule.Skipped == RuleSkipReason.RiskAboveProfile) continue;

            if (!byCategory.TryGetValue(rule.Rule.Category, out List<CleanupRuleViewModel>? list))
                byCategory[rule.Rule.Category] = list = [];

            list.Add(new CleanupRuleViewModel(rule, UpdateCleanupSelection));
        }

        foreach ((string category, List<CleanupRuleViewModel> rules) in byCategory)
            CleanupCategories.Add(new CleanupCategoryViewModel(category, rules));

        // Everything that can run starts ticked: the plan is the proposal, and the screen
        // shows it in full before anything happens.
        foreach (CleanupCategoryViewModel category in CleanupCategories)
            foreach (CleanupRuleViewModel rule in category.Rules)
                rule.IsChecked = rule.CanRun;

        var parts = new List<string>(3);

        parts.Add(plan.FileCount == 0
            ? L.T("cleanup.planNothing")
            : L.T("cleanup.planSummary", Format.Count(plan.FileCount),
                  Format.Bytes(plan.MatchedBytes),
                  Format.Count(plan.Rules.Count(r => r.WillDoSomething))));

        int needElevation = plan.Rules.Count(r => r.Skipped == RuleSkipReason.NeedsElevation);
        if (needElevation > 0)
            parts.Add(L.T("cleanup.needsElevationNote", Format.Count(needElevation)));

        // A broken rules.json is said out loud, never swallowed.
        foreach (string problem in catalog.Problems)
            parts.Add(L.T("cleanup.catalogProblem", problem));

        CleanupStatusText = string.Join(" · ", parts);
        UpdateCleanupSelection();
    }

    private void UpdateCleanupSelection()
    {
        long bytes = 0;
        int files = 0;
        int rules = 0;

        foreach (CleanupCategoryViewModel category in CleanupCategories)
        {
            foreach (CleanupRuleViewModel rule in category.Rules)
            {
                if (!rule.IsChecked) continue;
                rules++;
                files += rule.Plan.Matches.Count;
                bytes += rule.Plan.Bytes;
            }
        }

        HasCleanupSelection = rules > 0;
        CleanupSelectionText = rules == 0
            ? string.Empty
            : L.T("cleanup.planSummary", Format.Count(files), Format.Bytes(bytes),
                  Format.Count(rules));
    }

    /// <summary>Carries out only the ticked rules, through the disposal the caller chose.</summary>
    private bool _restorePointFirst;

    /// <summary>
    /// Whether a system restore point is asked for before a cleanup runs (F7.8).
    /// <para>
    /// Off by default, and not out of timidity: it costs up to a minute, and on most
    /// machines most of the time Windows declines it anyway. Somebody who wants one should
    /// get one; somebody who does not should not wait a minute for nothing.
    /// </para>
    /// </summary>
    public bool RestorePointFirst
    {
        get => _restorePointFirst;
        set => Set(ref _restorePointFirst, value);
    }

    public string RestorePointLabel => L.T("cleanup.restorePoint");
    public string RestorePointHint => L.T("cleanup.restorePointHint");

    private async void RunCleanup(object? parameter)
    {
        if (_cleanupPlan is null) return;

        var ticked = new List<RulePlan>();

        foreach (CleanupCategoryViewModel category in CleanupCategories)
            foreach (CleanupRuleViewModel rule in category.Rules)
                if (rule.IsChecked) ticked.Add(rule.Plan);

        if (ticked.Count == 0) return;

        // The plan handed to Execute contains exactly the ticked rules, so nothing that was
        // shown-but-unticked can be swept up by accident.
        var plan = new CleanupPlan(ticked, _cleanupProfile);

        CleanupDisposal disposal = (parameter as string) switch
        {
            "permanent" => CleanupDisposal.Permanent,
            "recycle" => CleanupDisposal.RecycleBin,
            _ => CleanupDisposal.Quarantine,
        };

        string? restoreNote = null;

        if (RestorePointFirst)
        {
            StatusText = L.T("cleanup.restoreWorking");

            RestorePointResult point = await Task.Run(
                () => RestorePointService.Create("Vacuon — cleanup"));

            // ⚠️ Anything but a point that really appeared stops the cleanup. The whole
            // reason to ask for one is to have a way back; going ahead without it, after
            // being told to get one, would be doing the risky half of what was asked.
            if (!point.Succeeded)
            {
                StatusText = L.T(point.Outcome switch
                {
                    RestorePointOutcome.NothingHappened => "cleanup.restoreSilent",
                    RestorePointOutcome.Unavailable => "cleanup.restoreUnavailable",
                    _ => "cleanup.restoreFailed",
                });
                return;
            }

            restoreNote = L.T("cleanup.restoreMade", point.SequenceAfter,
                              Format.Duration(point.Took));
        }

        var engine = new RuleEngine();
        CleanupReport report = engine.Execute(plan, disposal);

        var parts = new List<string>(4)
        {
            report.Failed > 0
                ? L.T("cleanup.donePartial", Format.Count(report.Handled), Format.Count(report.Failed))
                : disposal switch
                {
                    CleanupDisposal.Permanent => L.T("cleanup.donePermanent",
                        Format.Count(report.Handled), Format.Bytes(report.Bytes)),
                    CleanupDisposal.RecycleBin => L.T("cleanup.doneRecycle",
                        Format.Count(report.Handled), Format.Bytes(report.Bytes)),
                    _ => L.T("cleanup.doneQuarantine",
                        Format.Count(report.Handled), Format.Bytes(report.Bytes)),
                },
        };

        if (restoreNote is not null) parts.Add(restoreNote);

        // The Windows tools run after the files, and each reports its own measured gain.
        var tools = new SystemTools();

        foreach (RulePlan rule in plan.SystemTools)
        {
            ToolResult result = tools.Run(rule.Rule.Tool!);

            parts.Add(!result.Succeeded
                ? L.T("cleanup.toolFailed", result.Error ?? $"exit {result.ExitCode}")
                : result.FreedBytesMeasured
                    ? L.T("cleanup.toolFreed", Format.Bytes(result.FreedBytes))
                    : L.T("cleanup.toolNoMeasure"));
        }

        CleanupStatusText = string.Join(" · ", parts);
        StatusText = parts[0];

        if (report.Failed > 0) LastFailures = [.. report.Failures];

        // What is gone is gone; the list has to be rebuilt from the disk.
        ScanForJunk();
    }

    // ================= duplicados =================

    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = [];

    private const int MaxGroupsShown = 300;

    private bool _isFindingDuplicates;
    public bool IsFindingDuplicates
    {
        get => _isFindingDuplicates;
        private set
        {
            if (!Set(ref _isFindingDuplicates, value)) return;
            (FindDuplicatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _duplicateStatusText = L.T("dup.needScan");
    public string DuplicateStatusText
    {
        get => _duplicateStatusText;
        private set => Set(ref _duplicateStatusText, value);
    }

    private string _duplicateSelectionText = string.Empty;
    public string DuplicateSelectionText
    {
        get => _duplicateSelectionText;
        private set => Set(ref _duplicateSelectionText, value);
    }

    public bool HasDuplicateSelection => _duplicateSelectedBytes >= 0 && _duplicateSelectedCount > 0;

    private int _duplicateSelectedCount;
    private long _duplicateSelectedBytes;

    public ICommand FindDuplicatesCommand { get; private set; } = null!;
    public ICommand QuarantineDuplicatesCommand { get; private set; } = null!;
    public ICommand SelectAllDuplicatesCommand { get; private set; } = null!;
    public ICommand ClearDuplicateSelectionCommand { get; private set; } = null!;

    private CancellationTokenSource? _duplicateCts;

    private string _duplicateScopeText = string.Empty;
    public string DuplicateScopeText
    {
        get => _duplicateScopeText;
        private set => Set(ref _duplicateScopeText, value);
    }

    /// <summary>
    /// Runs stage 1 and says what reading the rest would cost, before any of it happens.
    /// <para>
    /// It is free — every size is already in the index — and on the machine this was
    /// developed on it comes back with 769 k candidates holding 102 GiB. Starting that
    /// read on a button press with no figure on screen would be asking for a consent
    /// nobody was given the information to give.
    /// </para>
    /// </summary>
    public async void MeasureDuplicateScope()
    {
        // ⚠️ A search already running owns this text, and this method used to take it away.
        // It runs on every arrival at the tab, so leaving a running search to look at
        // something else and coming back replaced "2.140 of 11.176" with the figures from
        // before the button was pressed — the run looked like it had never started, until
        // the next progress tick put the number back seconds later.
        if (IsFindingDuplicates) return;

        DuplicateNotice = string.Empty;

        VolumeIndex? index = Index;

        if (index is null)
        {
            DuplicateStatusText = L.T("dup.needScan");
            DuplicateScopeText = string.Empty;
            return;
        }

        if (DuplicatesByFolder)
        {
            await MeasureFolderScopeAsync(index);
            return;
        }

        DuplicateScope scope = new DuplicateFinder().Scope(index, new DuplicateOptions());

        if (scope.CandidateFiles == 0)
        {
            DuplicateStatusText = L.T("dup.scopeNone");
            DuplicateScopeText = string.Empty;
            return;
        }

        DuplicateStatusText = L.T("dup.scope",
                                  Format.Count(scope.CandidateFiles),
                                  Format.Count(scope.SizeBuckets),
                                  Format.Bytes(scope.CandidateBytes));
        DuplicateScopeText = L.T("dup.scopeNote");

        // Said every time, not only the first: without a cache, every run pays again.
        DuplicateNotice = L.T("dup.fileRunNotice");
    }

    /// <summary>
    /// The folder scope, worked out off the UI thread.
    /// <para>
    /// Unlike the file one, which is 187 ms of dictionary work, this walks every subtree
    /// twice and takes about five seconds on a real disk. Running it where the file one runs
    /// would freeze the window for five seconds every time somebody opened the tab.
    /// </para>
    /// </summary>
    private async Task MeasureFolderScopeAsync(VolumeIndex index)
    {
        DuplicateStatusText = L.T("dup.measuring");
        DuplicateScopeText = string.Empty;

        DuplicateFolderScope scope = await Task.Run(() => DuplicateFolderFinder.Scope(index));

        if (IsFindingDuplicates) return;

        if (scope.Candidates == 0)
        {
            DuplicateStatusText = L.T("dup.folderScopeNone");
            DuplicateScopeText = string.Empty;
            return;
        }

        DuplicateStatusText = L.T("dup.folderScope",
                                  Format.Count(scope.Candidates),
                                  Format.Count(scope.FoldersConsidered),
                                  Format.Bytes(scope.CandidateBytes));

        // Without this the second run announces the same 110 GiB as the first, when the
        // true figure is almost none of it. Worded as "already read once" rather than
        // "will not be read": whether the entries still hold is only known once the run
        // walks the trees.
        DuplicateScopeText = scope.Remembered > 0
            ? L.T("dup.scopeNote") + "  " + L.T("dup.folderRemembered", Format.Count(scope.Remembered))
            : L.T("dup.scopeNote");

        DuplicateNotice = scope.Remembered == 0 ? L.T("dup.firstRunNotice") : string.Empty;
    }

    private string _duplicateNotice = string.Empty;

    /// <summary>
    /// The warning beside the button about what this run is going to cost.
    /// <para>
    /// ⚠️ <b>The two modes get different sentences, and the difference is not cosmetic.</b>
    /// The folder search remembers a tree's signature between runs, so "the next ones take
    /// seconds" is a promise it can keep — measured, 52,5 min then 13,7 s. The file search
    /// has no cache and reads the same bytes every time. Printing the folder sentence on the
    /// file mode would be the app promising something it has no way to deliver, which is the
    /// house rule in its plainest form.
    /// </para>
    /// <para>
    /// The folder notice disappears once the cache holds something, because the scope line
    /// then says how many folders were read before. The file notice does not disappear:
    /// every run really does cost the same.
    /// </para>
    /// </summary>
    public string DuplicateNotice
    {
        get => _duplicateNotice;
        private set
        {
            if (!Set(ref _duplicateNotice, value)) return;
            Raise(nameof(HasDuplicateNotice));
        }
    }

    public bool HasDuplicateNotice => _duplicateNotice.Length > 0;

    public void CancelDuplicateSearch() => _duplicateCts?.Cancel();

    private double _duplicateProgress;

    /// <summary>
    /// How far along the search is, 0 to 1, for the thin bar under the counter.
    /// <para>
    /// It measures files or folders done against the total the stages produced — not bytes,
    /// which nothing knows in advance, and not time, which nothing can honestly predict.
    /// </para>
    /// </summary>
    public double DuplicateProgressValue
    {
        get => _duplicateProgress;
        private set => Set(ref _duplicateProgress, value);
    }

    private bool _hasDuplicateProgress;

    public bool HasDuplicateProgress
    {
        get => _hasDuplicateProgress;
        private set => Set(ref _hasDuplicateProgress, value);
    }

    private void ShowProgress(DuplicateProgress p)
    {
        if (p.FilesTotal <= 0)
        {
            HasDuplicateProgress = false;
            return;
        }

        DuplicateProgressValue = Math.Clamp((double)p.FilesDone / p.FilesTotal, 0, 1);
        HasDuplicateProgress = true;
    }

    /// <summary>Files or folders, whichever the screen is set to.</summary>
    private Task FindDuplicatesEitherWayAsync() =>
        DuplicatesByFolder ? FindDuplicateFoldersAsync() : FindDuplicatesAsync();

    private async Task FindDuplicatesAsync()
    {
        VolumeIndex? index = Index;

        if (index is null)
        {
            DuplicateStatusText = L.T("dup.needScan");
            return;
        }

        IsFindingDuplicates = true;
        DuplicateStatusText = L.T("dup.searching");
        DuplicateScopeText = string.Empty;
        DuplicateGroups.Clear();
        RaiseDuplicateGroups();
        ResetDuplicateSelection();

        _duplicateCts?.Dispose();
        _duplicateCts = new CancellationTokenSource();
        CancellationToken token = _duplicateCts.Token;

        var progress = new Progress<DuplicateProgress>(p =>
        {
            ShowProgress(p);
            DuplicateStatusText = L.T("dup.progress",
                                      Format.Count(p.FilesDone),
                                      Format.Count(p.FilesTotal),
                                      Format.Count(p.GroupsFound));
        });

        try
        {
            DuplicateReport report = await Task.Run(
                () => new DuplicateFinder().Find(index, new DuplicateOptions(), progress, token),
                token);

            foreach (DuplicateGroup group in report.Groups.Take(MaxGroupsShown))
                DuplicateGroups.Add(new DuplicateGroupViewModel(group, UpdateDuplicateSelection));

            RaiseDuplicateGroups();

            string headline = report.GroupCount == 0
                ? L.T("dup.none")
                : report.GroupCount == 1
                    ? L.T("dup.summaryOne", Format.Bytes(report.RecoverableBytes))
                    : L.T("dup.summary", Format.Count(report.GroupCount),
                          Format.Bytes(report.RecoverableBytes));

            var parts = new List<string>(3) { headline };

            if (report.GroupCount > MaxGroupsShown)
                parts.Add(L.T("dup.groupsShown", Format.Count(MaxGroupsShown),
                              Format.Count(report.GroupCount)));

            // Hardlinked copies are identical and free nothing. Saying so keeps the
            // recoverable figure above from reading as though it covered every copy listed.
            if (report.HardLinkedCopies > 0)
                parts.Add(L.T("dup.hardlinkNote", Format.Count(report.HardLinkedCopies)));

            if (report.UnreadableFiles > 0)
                parts.Add(L.T("dup.unreadable", Format.Count(report.UnreadableFiles)));

            DuplicateStatusText = string.Join(" · ", parts);
        }
        catch (OperationCanceledException)
        {
            // Whatever was confirmed before stopping is real and stays on screen; a partial
            // answer is still an answer, as long as it is labelled as one.
            DuplicateStatusText = L.T("dup.cancelled");
        }
        finally
        {
            IsFindingDuplicates = false;
            HasDuplicateProgress = false;
        }
    }

    private void UpdateDuplicateSelection()
    {
        int count = 0;
        long bytes = 0;

        foreach (DuplicateGroupViewModel group in DuplicateGroups)
        {
            foreach (DuplicateCopyViewModel copy in group.Copies)
            {
                if (!copy.IsChecked) continue;
                count++;
                bytes += copy.RecoverableBytes;
            }
        }

        foreach (DuplicateFolderGroupViewModel group in DuplicateFolderGroups)
        {
            foreach (DuplicateFolderCopyViewModel copy in group.Copies)
            {
                if (!copy.IsChecked) continue;
                count++;
                bytes += copy.RecoverableBytes;
            }
        }

        _duplicateSelectedCount = count;
        _duplicateSelectedBytes = bytes;

        // The size shown is what the disk would give back, so a selection made entirely of
        // hardlinks reads as zero rather than as the sum of their apparent sizes.
        DuplicateSelectionText = count == 0
            ? string.Empty
            : L.T("dup.selectedCount", Format.Count(count), Format.Bytes(bytes));

        Raise(nameof(HasDuplicateSelection));
    }

    private void ResetDuplicateSelection()
    {
        _duplicateSelectedCount = 0;
        _duplicateSelectedBytes = 0;
        DuplicateSelectionText = string.Empty;
        Raise(nameof(HasDuplicateSelection));
    }

    /// <summary>
    /// Ticks every redundant copy. It cannot reach a keeper: keepers are not in
    /// <see cref="DuplicateGroupViewModel.Copies"/> and have no tick to set.
    /// </summary>
    /// <summary>Tells the bulk buttons the list they act on has changed.</summary>
    private void RaiseDuplicateGroups() => Raise(nameof(HasDuplicateGroups));

    private void SelectAllDuplicates()
    {
        foreach (DuplicateGroupViewModel group in DuplicateGroups)
            foreach (DuplicateCopyViewModel copy in group.Copies)
                copy.IsChecked = true;

        foreach (DuplicateFolderGroupViewModel group in DuplicateFolderGroups)
            group.SelectAll();

        UpdateDuplicateSelection();
    }

    private void ClearDuplicateSelection()
    {
        foreach (DuplicateGroupViewModel group in DuplicateGroups)
            foreach (DuplicateCopyViewModel copy in group.Copies)
                copy.IsChecked = false;

        foreach (DuplicateFolderGroupViewModel group in DuplicateFolderGroups)
            foreach (DuplicateFolderCopyViewModel copy in group.Copies)
                copy.IsChecked = false;

        UpdateDuplicateSelection();
    }

    /// <summary>Sets the ticked copies aside, through the same quarantine as everything else.</summary>
    // ---------------- space that is not a file (F1.9) ----------------

    /// <summary>
    /// The page file, the swap file, the hibernation file and the shadow copies.
    /// <para>
    /// This is the answer to "the folders add up to 300 GB and Windows says 340". Without
    /// it the app's own cross-check reports a gap it cannot explain, which is worse than
    /// not checking.
    /// </para>
    /// </summary>
    public ObservableCollection<SystemSpaceRow> SystemSpaceRows { get; } = [];

    public bool HasSystemSpace => SystemSpaceRows.Count > 0;

    public string SystemSpaceTitle => L.T("system.title");
    public string SystemSpaceHint => L.T("system.hint");

    /// <summary>
    /// Fills the panel: the files from the index, the shadow copies from the system.
    /// <para>
    /// ⚠️ The files are read from the <b>index</b>, not from the disk, and that is not an
    /// optimisation. <c>pagefile.sys</c> and <c>hiberfil.sys</c> cannot be opened at all —
    /// asking the file system for their size comes back as an error, which is how a machine
    /// with 12.7 GiB of hibernation file gets reported as having none. The MFT states it
    /// plainly.
    /// </para>
    /// </summary>
    private async Task RefreshSystemSpaceAsync()
    {
        VolumeIndex? index = Index;
        if (index is null) return;

        List<SystemSpaceItem> files = SystemSpace.InIndex(index);
        string root = index.Volume.Root;

        // Off the UI thread: this one asks the system and takes a moment.
        SystemSpaceItem shadow = await Task.Run(() => SystemSpace.ShadowCopies(root));

        SystemSpaceRows.Clear();

        foreach (SystemSpaceItem item in files) SystemSpaceRows.Add(new SystemSpaceRow(item));
        SystemSpaceRows.Add(new SystemSpaceRow(shadow));

        // Remembered because the shred warning needs it, and asking again there would mean
        // another second of somebody else's process in the middle of a confirmation.
        _shadowCopiesPresent = shadow.IsKnown && shadow.Bytes > 0;

        Raise(nameof(HasSystemSpace));
    }

    /// <summary>True once a search has produced something, so the bulk buttons can appear.</summary>
    public bool HasDuplicateGroups => DuplicateGroups.Count > 0 || DuplicateFolderGroups.Count > 0;

    // ---------------- duplicados por pasta (F4.8) ----------------

    private bool _duplicatesByFolder;

    /// <summary>
    /// Whether the search compares whole folders instead of individual files.
    /// <para>
    /// The same question asked at a different size. A backup taken twice is four thousand
    /// duplicate files and one duplicate folder, and only one of those two answers is a
    /// thing somebody can act on in an afternoon.
    /// </para>
    /// </summary>
    public bool DuplicatesByFolder
    {
        get => _duplicatesByFolder;
        set
        {
            if (!Set(ref _duplicatesByFolder, value)) return;

            // The two lists answer different questions; showing one under the other's
            // heading would be worse than showing nothing.
            DuplicateGroups.Clear();
            DuplicateFolderGroups.Clear();
            RaiseDuplicateGroups();
            UpdateDuplicateSelection();

            Raise(nameof(DuplicatesByFile));
            MeasureDuplicateScope();
        }
    }

    public bool DuplicatesByFile
    {
        get => !_duplicatesByFolder;
        set { if (value) DuplicatesByFolder = false; }
    }

    public ObservableCollection<DuplicateFolderGroupViewModel> DuplicateFolderGroups { get; } = [];

    public bool HasDuplicateFolders => DuplicateFolderGroups.Count > 0;

    /// <summary>
    /// Looks for whole folders that are identical.
    /// <para>
    /// Stage one costs nothing and rules out most of the volume: two folders can only be
    /// identical if they hold the same number of files and the same number of bytes, and the
    /// index already knows both. Only what survives that is read.
    /// </para>
    /// </summary>
    private async Task FindDuplicateFoldersAsync()
    {
        VolumeIndex? index = Index;
        if (index is null) return;

        _duplicateCts?.Cancel();
        _duplicateCts = new CancellationTokenSource();
        CancellationToken token = _duplicateCts.Token;

        IsFindingDuplicates = true;
        DuplicateNotice = string.Empty;
        DuplicateFolderGroups.Clear();
        RaiseDuplicateGroups();

        var progress = new Progress<DuplicateProgress>(p =>
        {
            ShowProgress(p);
            DuplicateStatusText = L.T("dup.progress", Format.Count(p.FilesDone),
                                      Format.Count(p.FilesTotal), Format.Count(p.GroupsFound));
        });

        try
        {
            DuplicateFolderReport report = await Task.Run(
                () => DuplicateFolderFinder.Find(index, DuplicateFolderFinder.MinimumBytes,
                                                 KeepPreference.Oldest, progress, token),
                token);

            foreach (DuplicateFolderGroup group in report.Groups.Take(MaxGroupsShown))
                DuplicateFolderGroups.Add(new DuplicateFolderGroupViewModel(group, UpdateDuplicateSelection));

            var parts = new List<string>(2)
            {
                report.GroupCount == 0
                    ? L.T("dup.folderNone")
                    : report.GroupCount == 1
                        ? L.T("dup.folderSummaryOne", Format.Bytes(report.RecoverableBytes))
                        : L.T("dup.folderSummary", Format.Count(report.GroupCount),
                              Format.Bytes(report.RecoverableBytes)),
            };

            // Said because it explains the clock: the same search that took half an hour
            // the first time takes minutes the second, and a number that changes that much
            // without a reason on screen reads as the app having skipped something.
            if (report.FromCache > 0)
                parts.Add(L.T("dup.folderFromCache", Format.Count(report.FromCache),
                              Format.Count(report.FoldersHashed)));

            DuplicateStatusText = string.Join("  ·  ", parts);
        }
        catch (OperationCanceledException)
        {
            DuplicateStatusText = L.T("dup.cancelled");
        }
        finally
        {
            IsFindingDuplicates = false;
            HasDuplicateProgress = false;
            RaiseDuplicateGroups();
            Raise(nameof(HasDuplicateFolders));
            UpdateDuplicateSelection();
        }
    }

    /// <summary>
    /// Replaces every ticked copy with a second name for the copy its group keeps (PRD F4.4).
    /// <para>
    /// The space comes back and the path keeps working, which is the difference between this
    /// and deleting: nothing that opens the old path has to know anything changed. What it
    /// is not is a shortcut — a <c>.lnk</c> is a little file something has to follow, and a
    /// program handed one instead of the file it asked for simply fails.
    /// </para>
    /// <para>
    /// ⚠️ Runs off the UI thread: each link reads both files through to the end before it
    /// touches anything, so a group of large copies is seconds of disk, not microseconds of
    /// bookkeeping.
    /// </para>
    /// </summary>
    public async void LinkDuplicates(Window owner)
    {
        var pairs = new List<(string Keeper, string Copy)>();
        long bytes = 0;

        foreach (DuplicateGroupViewModel group in DuplicateGroups)
        {
            foreach (DuplicateCopyViewModel copy in group.Copies)
            {
                if (!copy.IsChecked) continue;

                pairs.Add((group.KeeperPath, copy.Path));
                bytes += copy.RecoverableBytes;
            }
        }

        if (pairs.Count == 0) return;

        if (!LinkDialog.Confirm(owner, [.. pairs.Select(p => p.Copy)], bytes)) return;

        List<LinkResult> results = await Task.Run(() =>
        {
            var done = new List<LinkResult>(pairs.Count);
            foreach ((string keeper, string copy) in pairs) done.Add(HardLinkService.Replace(keeper, copy));
            return done;
        });

        ReportLinks(results);

        // The groups on screen describe a disk that has changed, so the listing is rebuilt
        // from it rather than patched to look right.
        _ = FindDuplicatesAsync();
    }

    /// <summary>
    /// What the links did — and, separately, what they refused to do.
    /// </summary>
    private void ReportLinks(List<LinkResult> results)
    {
        int linked = 0;
        long freed = 0;
        var refused = new List<string>();

        foreach (LinkResult result in results)
        {
            if (result.Succeeded)
            {
                linked++;
                freed += result.BytesFreed;
                continue;
            }

            refused.Add($"{result.Path} — {result.Message ?? Describe(result.Outcome)}");
        }

        DuplicateStatusText = linked == 0
            ? L.T("dup.linkNothing")
            : refused.Count > 0
                ? L.T("dup.linkPartial", Format.Count(linked), Format.Bytes(freed), Format.Count(refused.Count))
                : linked == 1
                    ? L.T("dup.linkDoneOne", Format.Count(linked), Format.Bytes(freed))
                    : L.T("dup.linkDone", Format.Count(linked), Format.Bytes(freed));

        LastFailures = [.. refused];

        // Space really did come back on that volume, and the cards still quote the figure
        // the scan took.
        if (freed > 0) LoadVolumes();
    }

    private static string Describe(LinkOutcome outcome) => L.T(outcome switch
    {
        LinkOutcome.Blocked => "dup.outcomeBlocked",
        LinkOutcome.ContentChanged => "dup.outcomeChanged",
        LinkOutcome.DifferentVolumes => "dup.outcomeVolumes",
        LinkOutcome.AlreadyLinked => "dup.outcomeAlready",
        LinkOutcome.NotFound => "delete.outcomeNotFound",
        _ => "delete.outcomeFailed",
    });

    private void QuarantineDuplicates(object? parameter)
    {
        if (parameter is not Window owner) return;

        var paths = new List<string>();

        foreach (DuplicateGroupViewModel group in DuplicateGroups)
            foreach (DuplicateCopyViewModel copy in group.Copies)
                if (copy.IsChecked) paths.Add(copy.Path);

        // Folders too, when the search was run that way: a whole tree is one thing to set
        // aside, which is the entire reason for asking the question at folder size.
        foreach (DuplicateFolderGroupViewModel group in DuplicateFolderGroups)
            foreach (DuplicateFolderCopyViewModel copy in group.Copies)
                if (copy.IsChecked) paths.Add(copy.Path);

        if (paths.Count == 0) return;

        var service = new QuarantineService();
        QuarantineReport plan = service.Plan(paths, "duplicates");

        if (!QuarantineDialog.Confirm(owner, plan)) return;

        QuarantineReport report = service.Execute(paths, "duplicates");

        ReportQuarantine(report, 0);

        if (report.FailedCount > 0) LastFailures = [.. report.Failures.Select(Describe)];

        // Whatever was set aside is no longer where the group says it is, so the listing is
        // rebuilt from the disk rather than patched in place.
        _ = FindDuplicatesAsync();
    }

    // ================= quarentena =================

    public ObservableCollection<QuarantineBatchViewModel> QuarantineBatches { get; } = [];

    private string _quarantineStatusText = L.T("quarantine.emptyAll");
    public string QuarantineStatusText
    {
        get => _quarantineStatusText;
        private set => Set(ref _quarantineStatusText, value);
    }

    private string _quarantineHeldText = string.Empty;
    public string QuarantineHeldText
    {
        get => _quarantineHeldText;
        private set => Set(ref _quarantineHeldText, value);
    }

    public bool HasQuarantine => QuarantineBatches.Count > 0;

    public ICommand RefreshQuarantineCommand { get; private set; } = null!;
    public ICommand RestoreBatchCommand { get; private set; } = null!;
    public ICommand PurgeBatchCommand { get; private set; } = null!;

    /// <summary>
    /// Reads every fixed volume's quarantine. Batches holding nothing are left out rather
    /// than listed as empty: a restored batch is not a thing the user still has.
    /// </summary>
    public void RefreshQuarantine()
    {
        var service = new QuarantineService();
        QuarantineBatches.Clear();

        long held = 0;

        foreach (VolumeInfo volume in VolumeProbe.EnumerateFixedVolumes())
        {
            foreach (QuarantineBatch batch in service.ListBatches(volume.DriveLetter + ":\\"))
            {
                (long bytes, int count) = service.Held(batch);
                if (count == 0) continue;

                QuarantineBatches.Add(new QuarantineBatchViewModel(batch, bytes, count));
                held += bytes;
            }
        }

        QuarantineStatusText = QuarantineBatches.Count == 0
            ? L.T("quarantine.emptyAll")
            : L.T("quarantine.held", Format.Bytes(held));

        QuarantineHeldText = QuarantineBatches.Count == 0
            ? string.Empty
            : L.T("quarantine.heldExplain");

        Raise(nameof(HasQuarantine));
    }

    private void RestoreBatch(object? parameter)
    {
        if (parameter is not QuarantineBatchViewModel row) return;

        IReadOnlyList<RestoreResult> results = new QuarantineService().Restore(row.Batch);

        int restored = results.Count(r => r.Succeeded);
        int failed = results.Count - restored;

        QuarantineStatusText = failed > 0
            ? L.T("quarantine.restorePartial", Format.Count(restored), Format.Count(failed))
            : restored == 1
                ? L.T("quarantine.restoreDoneOne")
                : L.T("quarantine.restoreDone", Format.Count(restored));

        if (failed > 0)
            LastFailures = [.. results.Where(r => !r.Succeeded).Select(Describe)];

        RefreshQuarantine();

        // The files are back under their original names, so anything on screen that was
        // showing them from the quarantine is now behind the disk.
        StatusText = QuarantineStatusText;
    }

    private void PurgeBatch(object? parameter)
    {
        if (parameter is not QuarantineBatchViewModel row) return;

        // The only irreversible step in the whole quarantine, so it asks — and it quotes
        // what is really in there, not what the manifest set out to hold.
        MessageBoxResult answer = MessageBox.Show(
            L.T("quarantine.purgeBody", Format.Bytes(row.HeldBytes)),
            L.T("quarantine.purgeTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        long freed = new QuarantineService().Purge(row.Batch);

        // "Freed" is honest here and nowhere else in this screen.
        QuarantineStatusText = freed > 0
            ? L.T("quarantine.purgeDone", Format.Bytes(freed))
            : L.T("quarantine.purgeNothing");

        RefreshQuarantine();
        StatusText = QuarantineStatusText;
    }

    private static string Describe(RestoreResult result)
    {
        string reason = L.T(result.Outcome switch
        {
            RestoreOutcome.MissingFromQuarantine => "quarantine.outcomeMissing",
            RestoreOutcome.OriginalPathTaken => "quarantine.outcomeTaken",
            RestoreOutcome.InUse => "quarantine.outcomeInUse",
            RestoreOutcome.AccessDenied => "quarantine.outcomeAccessDenied",
            _ => "quarantine.outcomeFailed",
        });

        return $"{result.OriginalPath} — {reason}";
    }

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
        Monitor.Dispose();
    }
}
