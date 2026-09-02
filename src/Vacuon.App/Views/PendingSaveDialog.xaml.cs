using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Preview;

namespace Vacuon.App.Views;

/// <summary>What the person chose to do with the edits that were still waiting.</summary>
public enum PendingChoice
{
    /// <summary>Leave them waiting. The next launch will ask again.</summary>
    Later,
    /// <summary>Write them now, if the files are free.</summary>
    Save,
    /// <summary>Open the first one in the editor before deciding.</summary>
    Review,
    /// <summary>Throw them away.</summary>
    Discard,
}

/// <summary>
/// Tells somebody, at the next launch, that an edit never got written — and gives them the
/// three ways out.
/// <para>
/// ⚠️ Closing this window is <b>Later</b>, not Discard. The edit outlived the app being
/// closed once; a stray Escape should not be what finally loses it. Throwing it away is a
/// button somebody has to press, and it says so.
/// </para>
/// </summary>
public partial class PendingSaveDialog : Window
{
    private PendingChoice _choice = PendingChoice.Later;

    private PendingSaveDialog()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Loaded += (_, _) => SaveButton.Focus();
    }

    public static PendingChoice Ask(Window owner, IReadOnlyList<StoredSave> waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);

        var dialog = new PendingSaveDialog { Owner = owner };

        dialog.Title = waiting.Count == 1
            ? L.T("pending.titleOne")
            : L.T("pending.title", Format.Count(waiting.Count));

        dialog.TitleText.Text = dialog.Title;
        dialog.BodyText.Text = L.T("pending.body");
        dialog.SaveList.ItemsSource = waiting;

        dialog.SaveButton.Content = L.T("pending.save");
        dialog.ReviewButton.Content = L.T("pending.review");
        dialog.DiscardButton.Content = L.T("pending.discard");

        dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnSave(object sender, RoutedEventArgs e) => Choose(PendingChoice.Save);

    private void OnReview(object sender, RoutedEventArgs e) => Choose(PendingChoice.Review);

    private void OnDiscard(object sender, RoutedEventArgs e) => Choose(PendingChoice.Discard);

    private void Choose(PendingChoice choice)
    {
        _choice = choice;
        Close();
    }
}
