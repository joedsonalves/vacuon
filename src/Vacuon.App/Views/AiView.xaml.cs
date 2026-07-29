using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The AI components tab.
/// <para>
/// Deliberately not part of the Security tab. That one states, in the interface and in the
/// CLI, that it changed no key — and this is the only place in Vacuon that writes to the
/// registry. Keeping them apart is what keeps that statement true.
/// </para>
/// </summary>
public partial class AiView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public AiView() => InitializeComponent();

    private static AiComponentRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as AiComponentRowViewModel;

    private async void OnTurnOff(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (RowOf(sender) is not { } row || model is null) return;

        await model.TurnOffAsync(row);
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (RowOf(sender) is not { } row || model is null) return;

        await model.UndoAsync(row);
    }

    /// <summary>
    /// Opens Microsoft's own page for this control, so the claim can be checked against the
    /// source rather than taken on Vacuon's word.
    /// </summary>
    private void OnOpenDocs(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        try
        {
            Process.Start(new ProcessStartInfo(row.DocumentationUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException)
        {
        }
    }
}
