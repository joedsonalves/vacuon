using System.Windows;
using System.Windows.Input;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;

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

    /// <summary>
    /// The same Find the pane runs, over the same term. See <see cref="EditorFind"/>.
    /// </summary>
    private void FindNext()
    {
        if (DataContext is not MainViewModel model) return;

        EditorFind.Next(Editor, model);
    }
}
