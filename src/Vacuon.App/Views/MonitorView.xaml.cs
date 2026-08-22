using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The live monitor — milestone M9, and until now the only part of it that had no screen.
/// <para>
/// Leaving the tab stops the watch. Reading the change journal holds the volume open and
/// polls it every few seconds; carrying on invisibly would mean the app doing work nobody
/// asked for, which is worse than making them press the button again.
/// </para>
/// </summary>
public partial class MonitorView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public MonitorView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Model?.Monitor.Stop();
    }
}
