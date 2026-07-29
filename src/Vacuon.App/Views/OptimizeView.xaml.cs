using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// Hosts the two panels that change this machine: AI components and startup programs.
/// <para>
/// They were separate sidebar entries until the sidebar grew to six. Grouping them is not
/// only tidiness — it puts everything that writes behind one door, which is exactly the line
/// the rest of the app is careful to stay on the other side of.
/// </para>
/// </summary>
public partial class OptimizeView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public OptimizeView() => InitializeComponent();

    private void OnShowAi(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.Panel = OptimizePanel.Ai;
    }

    private void OnShowStartup(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.Panel = OptimizePanel.Startup;
    }

    private void OnShowMemory(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.Panel = OptimizePanel.Memory;
    }
}
