using System.Windows;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;
using Vacuon.Core.Localization;
using Vacuon.Native.Interop;

namespace Vacuon.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    public MainWindow()
    {
        InitializeComponent();

        _model = new MainViewModel(App.Settings);
        DataContext = _model;

        // Read-only, in the background, and only if the setting allows: it runs winget and
        // reads what it printed. Nothing is downloaded and nothing waits on it.
        Loaded += (_, _) =>
        {
            _model.StartUpdateCheck();
            AskAboutPendingSaves();
        };

        // A barra de título é do Windows, não do WPF: sem sincronizar, o tema escuro
        // fica com uma faixa branca no topo.
        SourceInitialized += (_, _) =>
        {
            ApplyTitleBar();

            // After the handle exists: the tray icon addresses a window, and the message
            // hook needs a source to hang from.
            _tray = new TrayService(this, App.Settings);
            _tray.QuickCleanupRequested += OnQuickCleanupFromTray;
            _tray.Attach();

            _model.Tray = _tray;

            // Shell menu messages have to reach the extension that owns the entry. Without
            // this, an owner-drawn entry is measured by nobody and the menu misbehaves — and
            // it misbehaves inside a window procedure, where a fault is a fail-fast that no
            // exception handler in this process will ever see.
            System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(OnShellMenuMessage);
        };

        ThemeManager.Changed += ApplyTitleBar;
        L.Changed += RefreshHeader;

        Closing += OnClosing;

        Closed += (_, _) =>
        {
            ThemeManager.Changed -= ApplyTitleBar;
            L.Changed -= RefreshHeader;

            _tray?.Dispose();
            _model.Dispose();
        };
    }

    private nint OnShellMenuMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (!ShellContextMenu.HandleMenuMessage(msg, wParam, lParam, out nint result)) return 0;

        handled = true;
        return result;
    }

    private void RefreshHeader() => HeaderTitle.Text = L.T(_headerKey);

    private void ApplyTitleBar() =>
        TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

    /// <summary>
    /// Navigates and sets the header. The title comes from the SAME key the sidebar
    /// uses — hardcoding it here left the header in Portuguese while the rest of the
    /// interface had already switched to English.
    /// </summary>
    private void Navigate(Section section, string titleKey)
    {
        _model.Section = section;
        _headerKey = titleKey;
        HeaderTitle.Text = L.T(titleKey);
    }

    private string _headerKey = "nav.dashboard";

    private TrayService? _tray;

    /// <summary>
    /// Closing the window ends the app, unless someone asked otherwise.
    /// <para>
    /// The opposite default — closing to the tray — leaves a process running that the person
    /// believes they closed, and they find it days later wondering what put it there. The
    /// setting exists because some people do want it; it is off until they say so.
    /// </para>
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Settings.CloseToTray || _tray is null || !App.Settings.ShowTrayIcon) return;

        e.Cancel = true;
        Hide();
    }

    private void OnQuickCleanupFromTray()
    {
        Navigate(Section.Cleanup, "nav.cleanup");
        _model.StartQuickCleanupScan();
    }

    private void OnNavDashboard(object sender, RoutedEventArgs e) =>
        Navigate(Section.Dashboard, "nav.dashboard");

    private void OnNavExplorer(object sender, RoutedEventArgs e) =>
        Navigate(Section.Explorer, "nav.explorer");

    private void OnNavTreemap(object sender, RoutedEventArgs e) =>
        Navigate(Section.Treemap, "nav.treemap");

    private void OnNavCleanup(object sender, RoutedEventArgs e) =>
        Navigate(Section.Cleanup, "nav.cleanup");

    private void OnNavDuplicates(object sender, RoutedEventArgs e) =>
        Navigate(Section.Duplicates, "nav.duplicates");

    private void OnNavSimilar(object sender, RoutedEventArgs e) =>
        Navigate(Section.Similar, "nav.similar");

    private void OnNavQuarantine(object sender, RoutedEventArgs e) =>
        Navigate(Section.Quarantine, "nav.quarantine");

    private void OnNavMonitor(object sender, RoutedEventArgs e) =>
        Navigate(Section.Monitor, "nav.monitor");

    private void OnNavSecurity(object sender, RoutedEventArgs e) =>
        Navigate(Section.Security, "nav.security");

    private void OnNavOptimize(object sender, RoutedEventArgs e) =>
        Navigate(Section.Optimize, "nav.optimize");

    private void OnNavSettings(object sender, RoutedEventArgs e) =>
        Navigate(Section.Settings, "nav.settings");

    private void OnToggleTheme(object sender, RoutedEventArgs e) => _model.ToggleTheme();

    /// <summary>
    /// Asks about edits that never got written before the app was last closed.
    /// <para>
    /// ⚠️ Asked, not written. The file may have been changed by something else since, and
    /// writing over that without a word would be the app deciding something that belongs to
    /// the person — which is why one of the three ways out is to open the edit and look at it
    /// first.
    /// </para>
    /// </summary>
    private void AskAboutPendingSaves()
    {
        IReadOnlyList<Vacuon.Core.Preview.StoredSave> waiting = _model.WaitingFromLastTime();
        if (waiting.Count == 0) return;

        switch (Views.PendingSaveDialog.Ask(this, waiting))
        {
            case Views.PendingChoice.Save:
                _model.ResumePending();
                break;

            case Views.PendingChoice.Review:
                // The Explorer is where the editor lives, so the review has to happen there.
                _model.Section = ViewModels.Section.Explorer;
                _model.ReviewPending(waiting[0]);
                break;

            case Views.PendingChoice.Discard:
                _model.DiscardPending();
                break;

            default:
                // Closing the window keeps them: the edit already outlived one close, and a
                // stray Escape should not be what finally loses it.
                break;
        }
    }
}
