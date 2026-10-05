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
        //
        // ⚠️ "Visible" is not "arrived". This view reports itself visible while the main window
        // is first built, before the section bindings have collapsed it, so the plan used to
        // run on every launch - on the window's thread, before the window appeared: measured,
        // the window took 7.6 to 7.8 s to show instead of 0.8. Only the Cleanup section being
        // the one on screen means somebody came here.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && Model is { IsCleanup: true, CleanupCategories.Count: 0, IsCleanupBusy: false })
                Model.ScanForJunk();
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
