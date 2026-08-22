using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core.Actions;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// Confirmation for setting items aside.
/// <para>
/// No acknowledgement checkbox, and no red anywhere: this is the reversible action, and
/// dressing it like the permanent delete would teach people to click through the one that
/// is not reversible. What the dialog does insist on saying is that the space does not
/// come back yet — that is the only surprise the action holds.
/// </para>
/// </summary>
public partial class QuarantineDialog : Window
{
    private sealed record Row(string Path, string SizeText);

    private const int MaxListed = 200;

    private QuarantineDialog()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <param name="plan">Result of <see cref="QuarantineService.Plan"/> — never a live run.</param>
    public static bool Confirm(Window owner, QuarantineReport plan)
    {
        var dialog = new QuarantineDialog { Owner = owner };
        dialog.Populate(plan);
        return dialog.ShowDialog() == true;
    }

    private void Populate(QuarantineReport plan)
    {
        List<QuarantineResult> movable = [.. plan.Results.Where(r => r.Succeeded)];

        long bytes = movable.Sum(r => r.Bytes);
        int folders = movable.Count(r => r.IsDirectory);
        int files = movable.Count - folders;

        Title = L.T("quarantine.title");
        ModeBadgeText.Text = L.T("quarantine.button").ToUpperInvariant();

        TitleText.Text = movable.Count == 1
            ? L.T("quarantine.headerOne")
            : L.T("quarantine.header", Format.Count(movable.Count));

        var parts = new List<string>(2);
        if (folders > 0) parts.Add(folders == 1 ? L.T("delete.folderOne") : L.T("delete.folders", Format.Count(folders)));
        if (files > 0) parts.Add(files == 1 ? L.T("delete.fileOne") : L.T("delete.files", Format.Count(files)));

        Summary.Text = movable.Count == 0
            ? L.T("quarantine.emptyAll")
            : $"{string.Join(" · ", parts)} · {L.T("quarantine.held", Format.Bytes(bytes))}";

        HeldNote.Text = L.T("quarantine.heldExplain");
        BodyNote.Text = L.T("quarantine.body");

        Items.ItemsSource = movable
            .Take(MaxListed)
            .Select(r => new Row(r.Path, r.IsDirectory && r.Bytes == 0 ? "—" : Format.Bytes(r.Bytes)))
            .ToList();

        MoreItems.Text = movable.Count > MaxListed
            ? L.T("list.truncated", Format.Count(MaxListed), Format.Count(movable.Count))
            : string.Empty;

        int blocked = plan.Blocked.Count();
        if (blocked > 0)
        {
            NoticeBox.Visibility = Visibility.Visible;
            NoticeText.Text = L.T("quarantine.blockedHeader", Format.Count(blocked));
        }

        ConfirmButton.Content = L.T("quarantine.confirm");
        CancelButton.Content = L.T("delete.cancel");
        ConfirmButton.IsEnabled = movable.Count > 0;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
