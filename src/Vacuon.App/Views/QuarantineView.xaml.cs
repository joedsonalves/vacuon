using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The quarantine screen — milestone M4, and the undo the rest of the app spent three
/// releases pointing at.
/// <para>
/// It reads the disk on every showing rather than caching a list. Batches are files in a
/// folder, and the CLI can restore or purge them between two visits to this tab; showing a
/// remembered list would offer to restore something that is not there any more.
/// </para>
/// </summary>
public partial class QuarantineView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public QuarantineView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Model?.RefreshQuarantine();
    }
}
