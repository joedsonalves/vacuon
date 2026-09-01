using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The similar-pictures screen — milestone M8.
/// <para>
/// Every version in a group is shown as a thumbnail, side by side. That is the whole design:
/// these files really are different, so the app cannot simply assert they are the same
/// picture and expect to be believed. It shows them, states how many bits apart they are,
/// and lets the person decide.
/// </para>
/// </summary>
public partial class SimilarView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public SimilarView()
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Model?.RefreshSimilarStatus();
        };
    }

    private void OnStop(object sender, RoutedEventArgs e) => Model?.CancelSimilarSearch();

    private void OnSelectAudioGroup(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioMatchGroupViewModel group })
            group.SelectAll();
    }

    private void OnQuarantineSelected(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.QuarantineSimilarCommand.Execute(owner);
    }
}
