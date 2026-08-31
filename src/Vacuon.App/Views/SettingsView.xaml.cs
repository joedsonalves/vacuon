using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        Window? owner = Window.GetWindow(this);
        if (owner is not null) model.ShowAbout(owner);
    }
}
