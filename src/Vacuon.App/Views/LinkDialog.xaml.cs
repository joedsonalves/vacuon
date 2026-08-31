using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// Asks before turning copies into second names for the copy that stays.
/// <para>
/// The one thing this dialog exists to say is the thing that is easy to miss: afterwards
/// there is one file with two names, and writing through either name changes what the other
/// one reads. Everything else — the space, the paths that keep working — is the good part,
/// and the good part does not need a dialog.
/// </para>
/// </summary>
public partial class LinkDialog : Window
{
    private bool _confirmed;

    private LinkDialog()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        // Focus on Cancel: this is not reversible, so the key somebody presses without
        // reading should be the one that does nothing.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static bool Confirm(Window owner, IReadOnlyList<string> paths, long bytes)
    {
        var dialog = new LinkDialog { Owner = owner };

        dialog.Title = paths.Count == 1
            ? L.T("dup.linkTitleOne")
            : L.T("dup.linkTitle", Format.Count(paths.Count));

        dialog.TitleText.Text = dialog.Title;
        dialog.BodyText.Text = L.T("dup.linkBody");
        dialog.WarningText.Text = L.T("dup.linkWarning");
        dialog.FooterText.Text = L.T("dup.linkFooter") + "  ·  " + Format.Bytes(bytes);
        dialog.PathList.ItemsSource = paths;

        dialog.CancelButton.Content = L.T("delete.cancel");
        dialog.ConfirmButton.Content = L.T("dup.linkSelected");

        dialog.ShowDialog();
        return dialog._confirmed;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
