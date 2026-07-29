using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The memory panel: what is holding the RAM, measured, and what emptying working sets
/// really does.
/// </summary>
public partial class MemoryView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public MemoryView() => InitializeComponent();

    private static MemoryRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as MemoryRowViewModel;

    /// <summary>
    /// First click. Arms the row rather than closing anything — whatever is unsaved in that
    /// process is about to be lost, and one stray click should not be enough to do it.
    /// </summary>
    private void OnArmClose(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) row.IsArmed = true;
    }

    private void OnCancelClose(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) row.IsArmed = false;
    }

    private async void OnConfirmClose(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (RowOf(sender) is not { } row || model is null) return;

        await model.CloseProcessAsync(row);
    }
}
