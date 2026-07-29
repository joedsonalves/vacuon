using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The startup programs tab.
/// <para>
/// Like the AI components tab, it lives outside Security — that one promises it changed no
/// key, and this one writes.
/// </para>
/// </summary>
public partial class StartupView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public StartupView() => InitializeComponent();

    private static StartupRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as StartupRowViewModel;

    private async void OnDisable(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (RowOf(sender) is not { } row || model is null) return;

        await model.SetStartupEnabledAsync(row, enabled: false);
    }

    private async void OnEnable(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (RowOf(sender) is not { } row || model is null) return;

        await model.SetStartupEnabledAsync(row, enabled: true);
    }
}
