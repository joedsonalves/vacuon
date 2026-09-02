using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// Asks before letting somebody change the bytes of a file, and says what that costs.
/// <para>
/// ⚠️ This is the same tier as "Delete forever", and for the same reason: there is no undo.
/// The quarantine does not help here — a file whose bytes were edited keeps its name, its
/// size and its place, and only stops working. An executable can go on launching and fail at
/// the one code path that was touched, which is worse than failing outright.
/// </para>
/// <para>
/// The confirm button stays disabled until the box is ticked, so the warning has to be read
/// past rather than clicked through. Same shape as the irreversible delete.
/// </para>
/// </summary>
public partial class HexWarningDialog : Window
{
    private bool _confirmed;

    private HexWarningDialog()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Loaded += (_, _) => CancelButton.Focus();
    }

    public static bool Confirm(Window owner, string path, bool executable)
    {
        var dialog = new HexWarningDialog { Owner = owner };

        dialog.Title = L.T("edit.hexWarnTitle");
        dialog.TitleText.Text = dialog.Title;
        dialog.PathText.Text = path;

        // An executable earns the sharper sentence: for a data file a wrong byte usually
        // shows up the moment something reads it, and for a program it may not show up until
        // the one path that was touched runs.
        dialog.WarningText.Text = executable
            ? L.T("edit.hexWarnExecutable")
            : L.T("edit.hexWarnBody");

        dialog.Understood.Content = L.T("edit.hexWarnUnderstood");
        dialog.CancelButton.Content = L.T("delete.cancel");
        dialog.ConfirmButton.Content = L.T("edit.hexWarnConfirm");

        dialog.ShowDialog();
        return dialog._confirmed;
    }

    private void OnUnderstood(object sender, RoutedEventArgs e) =>
        ConfirmButton.IsEnabled = Understood.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
