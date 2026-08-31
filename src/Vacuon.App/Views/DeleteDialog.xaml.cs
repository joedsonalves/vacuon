using System.Windows;
using System.Windows.Media;
using Vacuon.App.Infra;
using Vacuon.Core.Actions;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// Confirmation for a deletion. Shows the plan — count, total size, and every path —
/// before anything is touched.
/// <para>
/// The permanent mode additionally requires ticking an acknowledgement box. That is
/// deliberate friction: a permanent delete has no undo at all, while both of the other
/// routes out of a folder — the Recycle Bin and the quarantine — do.
/// </para>
/// </summary>
public partial class DeleteDialog : Window
{
    /// <summary>One row of the preview list.</summary>
    private sealed record Row(string Path, string SizeText);

    private const int MaxListed = 200;

    private DeleteDialog()
    {
        InitializeComponent();

        // The caption bar belongs to Windows, not to WPF — a dialog needs the same
        // treatment as the main window or it shows a white strip in the dark theme.
        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        // Focus where the user must act: the acknowledgement box when the delete is
        // permanent, the Cancel button otherwise. Landing on Confirm by default would
        // make an accidental Enter destructive.
        Loaded += (_, _) =>
        {
            if (Acknowledge.Visibility == Visibility.Visible) Acknowledge.Focus();
            else CancelButton.Focus();
        };
    }

    /// <summary>
    /// Shows the dialog and returns <c>true</c> when the user confirmed.
    /// </summary>
    /// <param name="plan">Result of <see cref="DeleteService.Plan"/> — never a live run.</param>
    public static bool Confirm(Window owner, DeleteReport plan, DeleteMode mode)
    {
        var dialog = new DeleteDialog { Owner = owner };
        dialog.Populate(plan, mode);
        return dialog.ShowDialog() == true;
    }

    private void Populate(DeleteReport plan, DeleteMode mode)
    {
        bool permanent = mode == DeleteMode.Permanent;

        List<DeleteResult> deletable = [.. plan.Results.Where(r => r.Outcome == DeleteOutcome.Deleted)];
        List<DeleteResult> blocked = [.. plan.Blocked];

        long bytes = deletable.Sum(r => r.Bytes);
        int folders = deletable.Count(r => r.IsDirectory);
        int files = deletable.Count - folders;

        // TitleText is the heading inside the dialog; Title is the window caption.
        TitleText.Text = deletable.Count == 1
            ? L.T("delete.titleOne")
            : L.T("delete.title", deletable.Count);

        Title = permanent ? L.T("delete.permanentHeader") : L.T("delete.recycleHeader");

        ModeBadgeText.Text = permanent
            ? L.T("delete.permanentHeader").ToUpperInvariant()
            : L.T("delete.recycleHeader").ToUpperInvariant();

        // Amber for the bin, red for permanent — the badge is the fastest signal on
        // the dialog, so it must not say the same thing in both modes.
        ModeBadge.Background = (Brush)(TryFindResource(permanent ? "Risk.Danger" : "Risk.Notable")
                                       ?? Brushes.OrangeRed);

        Body.Text = permanent ? L.T("delete.permanentBody") : L.T("delete.recycleBody");

        var parts = new List<string>(2);
        if (folders > 0) parts.Add(folders == 1 ? L.T("delete.folderOne") : L.T("delete.folders", Format.Count(folders)));
        if (files > 0) parts.Add(files == 1 ? L.T("delete.fileOne") : L.T("delete.files", Format.Count(files)));

        Summary.Text = $"{string.Join(" · ", parts)} · {Format.Bytes(bytes)}";

        Items.ItemsSource = deletable
            .Take(MaxListed)
            .Select(r => new Row(r.Path, r.IsDirectory && r.Bytes == 0 ? "—" : Format.Bytes(r.Bytes)))
            .ToList();

        MoreItems.Text = deletable.Count > MaxListed
            ? L.T("list.truncated", Format.Count(MaxListed), Format.Count(deletable.Count))
            : string.Empty;

        if (blocked.Count > 0)
        {
            BlockedBox.Visibility = Visibility.Visible;
            BlockedHeader.Text = L.T("delete.blockedHeader", Format.Count(blocked.Count));

            // Show the reasons, deduplicated: five items blocked for the same reason
            // should read as one explanation, not five.
            BlockedDetail.Text = string.Join(" · ", blocked
                .Select(b => b.Message)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .Take(4));
        }

        if (!permanent)
        {
            QuotaWarning.Visibility = Visibility.Visible;
            QuotaWarning.Text = L.T("delete.quotaWarning");
        }

        if (permanent)
        {
            Acknowledge.Visibility = Visibility.Visible;
            Acknowledge.Content = L.T("delete.confirmPermanent");
        }

        ConfirmButton.Content = L.T("delete.confirm");
        CancelButton.Content = L.T("delete.cancel");

        // Nothing deletable, or permanent mode not yet acknowledged.
        ConfirmButton.IsEnabled = deletable.Count > 0 && !permanent;

        if (deletable.Count == 0)
        {
            Acknowledge.Visibility = Visibility.Collapsed;
            Summary.Text = L.T("delete.allBlocked");
        }

        _permanent = permanent;
        _hasDeletable = deletable.Count > 0;
    }

    private bool _permanent;
    private bool _hasDeletable;

    private void OnAcknowledgeChanged(object sender, RoutedEventArgs e) =>
        ConfirmButton.IsEnabled = _hasDeletable && (!_permanent || Acknowledge.IsChecked == true);

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
