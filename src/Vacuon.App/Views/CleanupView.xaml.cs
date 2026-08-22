using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The rule-based cleanup screen — milestone M5.
/// <para>
/// The three disposal buttons are the whole design: reversible first and primary, permanent
/// last and red. This is the screen most likely to be used by someone in a hurry with a full
/// disk, so the easy button has to be the one they can undo.
/// </para>
/// </summary>
public partial class CleanupView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public CleanupView()
    {
        InitializeComponent();

        // Planning reads the disk and changes nothing, so it can run on arrival. What it
        // cannot do is act — that needs one of the buttons at the bottom.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && Model?.CleanupCategories.Count == 0) Model.ScanForJunk();
        };
    }

    private void OnProfileQuick(object sender, RoutedEventArgs e) => SetProfile("quick");
    private void OnProfileDeep(object sender, RoutedEventArgs e) => SetProfile("deep");
    private void OnProfileCustom(object sender, RoutedEventArgs e) => SetProfile("custom");

    private void SetProfile(string profile) =>
        Model?.SetCleanupProfileCommand.Execute(profile);

    private void OnQuarantine(object sender, RoutedEventArgs e) => Run("quarantine");
    private void OnRecycle(object sender, RoutedEventArgs e) => Run("recycle");

    private void OnPermanent(object sender, RoutedEventArgs e)
    {
        // The only one of the three with no way back, so it is the only one that asks.
        MessageBoxResult answer = MessageBox.Show(
            Model?.CleanupSelectionText ?? string.Empty,
            Vacuon.Core.Localization.L.T("cleanup.toPermanent"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer == MessageBoxResult.OK) Run("permanent");
    }

    private void Run(string disposal) => Model?.RunCleanupCommand.Execute(disposal);
}
