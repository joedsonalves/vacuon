using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
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

    public string ElevationText => IsElevated
        ? "Administrador — leitura da MFT disponível"
        : "Sem elevação — a varredura vai usar a API do Windows (mais lenta)";

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

    public string AlwaysAdminHintText => AlwaysRunAsAdministrator
        ? "O Vacuon vai se relançar elevado a cada abertura. O Windows exibe o UAC — não há como suprimi-lo sem criar uma tarefa agendada, e isso não é feito às escondidas."
        : "Ligue para abrir sempre elevado. Sem elevação a leitura da MFT não existe, e a varredura passa de segundos para minutos.";

    private void RestartElevated()
    {
        if (!ElevationService.RelaunchElevated())
            StatusText = "Elevação recusada no UAC. O Vacuon continua funcionando, sem a leitura da MFT.";
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

    /// <summary>
    /// Botão de alternância rápida no cabeçalho. Glifos da fonte de ícones do Windows
    /// (E706 = brilho, E708 = lua): os equivalentes Unicode soltos não existem em
    /// Segoe UI Variable e saem como círculo vazio.
    /// </summary>
    public string ThemeToggleGlyph => IsDarkTheme ? "" : "";
    public string ThemeToggleTooltip => IsDarkTheme ? "Mudar para o tema claro" : "Mudar para o tema escuro";

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

    private string _statusText = "Escolha uma unidade e clique em Varrer.";
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
        StatusText = $"Varrendo {volume.Header}…";
        Section = Section.Explorer;

        var stopwatch = Stopwatch.StartNew();

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Percent;
            StatusText = p.TotalBytes > 0
                ? $"{Format.Percent(p.Percent)} · {Format.Count(p.EntriesFound)} itens · {p.MegabytesPerSecond:N0} MB/s"
                : $"{Format.Count(p.RecordsParsed)} itens · {Format.Duration(p.Elapsed)}";
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
            StatusText = "Varredura cancelada.";
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

        string strategy = result.StrategyUsed == ScanStrategy.Mft
            ? "leitura bruta da MFT"
            : "travessia pela API do Windows";

        string fallback = result.FallbackReason is null ? string.Empty : $" — {result.FallbackReason}";

        StatusText = $"{Format.Count(files)} arquivos em {Format.Duration(elapsed)} · {strategy}{fallback}";

        SummaryText = HasRealAllocation
            ? $"{Format.Bytes(index.TotalLogicalBytes)} lógicos · {Format.Bytes(index.TotalBytesOnDisk)} em disco · {Format.Bytes(index.TotalSlackBytes)} de folga de cluster"
            : $"{Format.Bytes(index.TotalLogicalBytes)} lógicos · tamanho em disco não medido (só a MFT expõe AllocatedSize)";

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
        ListMode.BiggestFiles => "Maiores arquivos do volume",
        ListMode.BiggestFolders => "Maiores pastas do volume",
        ListMode.Search => "Resultado da busca",
        ListMode.Suspicious => "Arquivos marcados pelas heurísticas",
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
        ? $"Mostrando {Format.Count(Rows.Count)} de {Format.Count(TotalMatches)} — refine a busca para ver o resto"
        : $"{Format.Count(Rows.Count)} itens";

    private FileRowViewModel? _selectedRow;
    public FileRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { Set(ref _selectedRow, value); Raise(nameof(HasSelection)); Raise(nameof(SelectionDetailText)); }
    }

    public bool HasSelection => SelectedRow is not null;

    public string SelectionDetailText => SelectedRow is null
        ? string.Empty
        : $"{SelectedRow.FullPath}\n{Format.Bytes(SelectedRow.LogicalSize)} · modificado em {SelectedRow.ModifiedText}";

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
            StatusText = "Nenhum arquivo bateu nas heurísticas. Isso é uma boa notícia.";
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
            StatusText = $"Não foi possível abrir: {SelectedRow.Name}";
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
            StatusText = "Não foi possível abrir a pasta de origem.";
        }
    }

    private void CopySelectedPath()
    {
        if (SelectedRow is null) return;

        try
        {
            Clipboard.SetText(SelectedRow.FullPath);
            StatusText = "Caminho copiado.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // A área de transferência pode estar travada por outro processo.
            StatusText = "A área de transferência está ocupada. Tente de novo.";
        }
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

    private string _securityStatusText = "Inspeciona 44 pontos do registro onde malware costuma se alojar. Somente leitura — nada é alterado.";
    public string SecurityStatusText { get => _securityStatusText; private set => Set(ref _securityStatusText, value); }

    private bool _hasSecurityRun;
    public bool HasSecurityRun { get => _hasSecurityRun; private set => Set(ref _hasSecurityRun, value); }

    public ICommand RunSecurityScanCommand { get; }

    private async Task RunSecurityScanAsync()
    {
        IsSecurityScanning = true;
        SecurityStatusText = "Inspecionando…";

        try
        {
            var scanner = new RegistryPersistenceScanner(new SecurityScanOptions());
            SecurityReport report = await Task.Run(() => scanner.Scan());

            Findings.Clear();
            foreach (SecurityFinding finding in report.Findings) Findings.Add(finding);

            HasSecurityRun = true;

            int flagged = report.CountAtLeast(Suspicion.Notable);

            string prefix = $"{report.LocationsInspected} locais e {Format.Count(report.EntriesInspected)} " +
                            $"entradas em {Format.Duration(report.Elapsed)}";

            SecurityStatusText = flagged switch
            {
                0 => $"{prefix} — nenhuma fugiu do padrão.",
                1 => $"{prefix} — 1 merece um olhar.",
                _ => $"{prefix} — {flagged} merecem um olhar.",
            };

            if (!report.WasElevated)
                SecurityStatusText += " Sem elevação, chaves protegidas de HKLM e as Tarefas Agendadas ficam de fora.";
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
