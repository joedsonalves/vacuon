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
        SourceInitialized += (_, _) => ApplyTitleBar();
        ThemeManager.Changed += ApplyTitleBar;
        L.Changed += RefreshHeader;

        Closed += (_, _) =>
        {
            ThemeManager.Changed -= ApplyTitleBar;
            L.Changed -= RefreshHeader;
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

    private void OnNavDashboard(object sender, RoutedEventArgs e) =>
        Navigate(Section.Dashboard, "nav.dashboard");

    private void OnNavExplorer(object sender, RoutedEventArgs e) =>
        Navigate(Section.Explorer, "nav.explorer");

    private void OnNavSecurity(object sender, RoutedEventArgs e) =>
        Navigate(Section.Security, "nav.security");

    private void OnNavSettings(object sender, RoutedEventArgs e) =>
        Navigate(Section.Settings, "nav.settings");

    private void OnToggleTheme(object sender, RoutedEventArgs e) => _model.ToggleTheme();
}
