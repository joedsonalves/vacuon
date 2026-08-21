using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core.Actions;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// Confirmation for a move. Shows the destination, the plan, and the two things a move
/// can do that nobody expects: rename an item because the name was taken, and free
/// nothing at all when it never leaves the volume.
/// <para>
/// No acknowledgement checkbox here, unlike the permanent delete. A move destroys
/// nothing — the files are all still there afterwards, under a path the dialog printed
/// before it happened.
/// </para>
/// </summary>
public partial class MoveDialog : Window
{
    /// <summary>One row of the preview list.</summary>
    private sealed record Row(string Path, string SizeText, string RenamedTo);

    private const int MaxListed = 200;

    private MoveDialog()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Shows the dialog and returns <c>true</c> when the user confirmed.</summary>
    /// <param name="plan">Result of <see cref="MoveService.Plan"/> — never a live run.</param>
    public static bool Confirm(Window owner, MoveReport plan)
    {
        var dialog = new MoveDialog { Owner = owner };
        dialog.Populate(plan);
        return dialog.ShowDialog() == true;
    }

    private void Populate(MoveReport plan)
    {
        List<MoveResult> movable = [.. plan.Movable];

        long bytes = movable.Sum(r => r.Bytes);
        int folders = movable.Count(r => r.IsDirectory);
        int files = movable.Count - folders;

        Title = L.T("move.header");
        ModeBadgeText.Text = L.T("move.badge");

        TitleText.Text = movable.Count == 1
            ? L.T("move.titleOne")
            : L.T("move.title", movable.Count);

        DestinationText.Text = L.T("move.into", plan.Destination);

        // The honest line about space. A move inside one volume frees nothing, and this
        // is the app's only chance to say so before the user goes looking for the space.
        VolumeNote.Text = plan.CrossVolume ? L.T("move.crossVolume") : L.T("move.sameVolume");

        var parts = new List<string>(2);
        if (folders > 0) parts.Add(folders == 1 ? L.T("delete.folderOne") : L.T("delete.folders", Format.Count(folders)));
        if (files > 0) parts.Add(files == 1 ? L.T("delete.fileOne") : L.T("delete.files", Format.Count(files)));

        Summary.Text = movable.Count == 0
            ? L.T("move.allBlocked")
            : $"{string.Join(" · ", parts)} · {Format.Bytes(bytes)}";

        Items.ItemsSource = movable
            .Take(MaxListed)
            .Select(r => new Row(
                r.Source,
                r.IsDirectory && r.Bytes == 0 ? "—" : Format.Bytes(r.Bytes),
                r.Renamed ? $"→ {r.FinalName}" : string.Empty))
            .ToList();

        MoreItems.Text = movable.Count > MaxListed
            ? L.T("list.truncated", Format.Count(MaxListed), Format.Count(movable.Count))
            : string.Empty;

        // Blocked, renamed and already-there are three different things, and each one is
        // a surprise if it only shows up in the result. They share one box, in that order
        // of importance.
        var notices = new List<string>(3);

        int blocked = plan.Blocked.Count();
        int renamed = plan.Renames.Count();

        if (blocked > 0) notices.Add(L.T("move.blockedHeader", Format.Count(blocked)));
        if (renamed > 0) notices.Add(L.T("move.renameHeader", Format.Count(renamed)));
        if (plan.SkippedCount > 0) notices.Add(L.T("move.alreadyThereHeader", Format.Count(plan.SkippedCount)));

        if (notices.Count > 0)
        {
            NoticeBox.Visibility = Visibility.Visible;
            NoticeText.Text = string.Join("\n", notices);
        }

        BodyNote.Text = L.T("move.body");

        ConfirmButton.Content = L.T("move.confirm");
        CancelButton.Content = L.T("move.cancel");
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
