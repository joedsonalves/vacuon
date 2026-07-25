using System.Windows;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;

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

        Closed += (_, _) =>
        {
            ThemeManager.Changed -= ApplyTitleBar;
            _model.Dispose();
        };
    }

    private void ApplyTitleBar() =>
        TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

    private void Navigate(Section section, string title)
    {
        _model.Section = section;
        HeaderTitle.Text = title;
    }

    private void OnNavDashboard(object sender, RoutedEventArgs e) =>
        Navigate(Section.Dashboard, "Painel");

    private void OnNavExplorer(object sender, RoutedEventArgs e) =>
        Navigate(Section.Explorer, "Explorer");

    private void OnNavSecurity(object sender, RoutedEventArgs e) =>
        Navigate(Section.Security, "Segurança");

    private void OnNavSettings(object sender, RoutedEventArgs e) =>
        Navigate(Section.Settings, "Configurações");

    private void OnToggleTheme(object sender, RoutedEventArgs e) => _model.ToggleTheme();
}
