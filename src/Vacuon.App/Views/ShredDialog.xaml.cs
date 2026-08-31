using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core.Actions;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// Asks before overwriting files, and says what overwriting will not achieve.
/// <para>
/// ⚠️ This dialog exists more for the warning than for the confirmation. Overwriting works
/// on a spinning disk; on solid state it does not, and the honest thing is to say so before
/// somebody trusts it — not to print "securely deleted" afterwards. The doubts listed here
/// are <b>detected</b>, not boilerplate: the volume reports whether it has a seek penalty,
/// and the file's own attributes say whether writing over it would land somewhere else.
/// </para>
/// </summary>
public partial class ShredDialog : Window
{
    private bool _confirmed;

    private ShredDialog()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Loaded += (_, _) => CancelButton.Focus();
    }

    public static bool Confirm(Window owner, IReadOnlyList<string> paths, long bytes, ShredDoubt doubt)
    {
        var dialog = new ShredDialog { Owner = owner };

        dialog.Title = paths.Count == 1
            ? L.T("shred.titleOne")
            : L.T("shred.title", Format.Count(paths.Count));

        dialog.TitleText.Text = dialog.Title;
        dialog.BodyText.Text = L.T("shred.body");
        dialog.WarningText.Text = Warning(doubt);
        dialog.FooterText.Text = L.T("shred.footer") + "  ·  " + Format.Bytes(bytes);
        dialog.PathList.ItemsSource = paths;

        dialog.CancelButton.Content = L.T("delete.cancel");
        dialog.ConfirmButton.Content = L.T("shred.confirm");

        dialog.ShowDialog();
        return dialog._confirmed;
    }

    /// <summary>
    /// The doubts, spelled out. Each one is a reason the bytes may still be on the drive
    /// after this finishes, and each one was detected rather than assumed.
    /// </summary>
    private static string Warning(ShredDoubt doubt)
    {
        if (doubt == ShredDoubt.None) return L.T("shred.noDoubt");

        var lines = new List<string>(4);

        if (doubt.HasFlag(ShredDoubt.SolidState)) lines.Add(L.T("shred.doubtSsd"));
        if (doubt.HasFlag(ShredDoubt.MovesWhenWritten)) lines.Add(L.T("shred.doubtMoves"));
        if (doubt.HasFlag(ShredDoubt.MaybeResident)) lines.Add(L.T("shred.doubtResident"));
        if (doubt.HasFlag(ShredDoubt.ShadowCopies)) lines.Add(L.T("shred.doubtShadow"));

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
