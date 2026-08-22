using System.Windows;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;
using Vacuon.Core.Localization;

namespace Vacuon.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    public MainWindow()
    {
        InitializeComponent();

        _model = new MainViewModel(App.Settings);
        DataContext = _model;

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
}
