using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// The same edit, in a window of its own, for when the pane is too narrow to work in.
/// <para>
/// ⚠️ <b>A second view of one edit, not a second edit.</b> It binds to the same view model
/// the pane does, so the text, the status and Save are literally the same objects. Two
/// editors each holding their own copy of a file is how somebody ends up writing the older
/// one over the newer without either screen having lied to them.
/// </para>
/// </summary>
public partial class EditorWindow : Window
{
    private EditorWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Loaded += (_, _) => Editor.Box.Focus();
    }

    public static void Open(Window owner, MainViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var window = new EditorWindow
        {
            Owner = owner,
            DataContext = model,
            Title = model.PreviewTitle,
        };

        window.Show();
    }

    /// <summary>
    /// Ctrl+S saves.
    /// </summary>
    /// <remarks>
    /// Escape is deliberately not bound. In the pane it cancels, and cancelling from here
    /// would look like closing the window — two very different outcomes behind one key,
    /// with the destructive one being the surprise.
    /// </remarks>
    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            model.SaveEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Closes the window and leaves the edit open in the pane.
    /// </summary>
    /// <remarks>
    /// Not Cancel. Closing a window is something people do to get it out of the way, and
    /// having that throw away what they typed would be a trap. The pane still has Cancel,
    /// which says what it does.
    /// </remarks>
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        FindNext();
        e.Handled = true;
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();

    private void FindNext()
    {
        string needle = FindBox.Text;

        if (needle.Length == 0)
        {
            FindStatus.Text = string.Empty;
            return;
        }

        TextBox box = Editor.Box;
        string haystack = box.Text;
        int from = box.SelectionStart + Math.Max(1, box.SelectionLength);

        int at = haystack.IndexOf(needle, Math.Min(from, haystack.Length),
                                  StringComparison.OrdinalIgnoreCase);

        if (at < 0) at = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            FindStatus.Text = L.T("edit.findNone");
            return;
        }

        box.Focus();
        box.Select(at, needle.Length);
        box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(at) - 2));

        FindStatus.Text = L.T("edit.findCount", Count(haystack, needle).ToString("N0", L.Culture));
    }

    private static int Count(string haystack, string needle)
    {
        int total = 0;
        int at = 0;

        while ((at = haystack.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            total++;
            at += needle.Length;
        }

        return total;
    }
}
