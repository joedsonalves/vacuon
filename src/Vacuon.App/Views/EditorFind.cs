using System.Windows.Controls;
using Vacuon.App.ViewModels;
using Vacuon.Core.Localization;
using Vacuon.Core.Preview;

namespace Vacuon.App.Views;

/// <summary>
/// The Find behind both editors: the preview pane and the window it opens into.
/// <para>
/// ⚠️ <b>One implementation, because there is one edit.</b> This was two copies of the same
/// forty lines, one in each code-behind, and they had already drifted: the window's status
/// formatted its count differently from the pane's. Two copies of a search over one file is
/// the same mistake as two copies of the file.
/// </para>
/// <para>
/// What is searched for and what the count says live on the view model, so the two screens
/// cannot disagree about either. Only the caret and the scroll are asked of the box, because
/// those genuinely belong to whichever box the person is looking at.
/// </para>
/// </summary>
internal static class EditorFind
{
    /// <summary>How many lines of context to leave above a match when scrolling to it.</summary>
    private const int LinesAbove = 2;

    public static void Next(CodeEditor editor, MainViewModel model)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(model);

        TextBox box = editor.Box;
        string needle = model.EditFindText;

        if (needle.Length == 0)
        {
            model.EditFindStatus = string.Empty;
            editor.ClearMatch();
            return;
        }

        // Past the match that is selected, or from the caret when nothing is. It used to
        // always add one, which skipped a match sitting exactly under the caret — so the
        // first press of Enter jumped over the first occurrence in the file.
        int from = box.SelectionStart + box.SelectionLength;

        TextMatch match = FindInText.Next(box.Text, needle, from);

        if (!match.Found)
        {
            model.EditFindStatus = L.T("edit.findNone");
            editor.ClearMatch();
            return;
        }

        // ⚠️ The focus stays in the search box. Moving it into the editor is what made the
        // second press of Enter type a line break into the file instead of finding the next
        // occurrence — an edit nobody asked for, arriving through a key that was supposed to
        // only look at things.
        box.Select(match.Index, match.Length);
        box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(match.Index) - LinesAbove));

        // The selection alone is invisible while the box does not have the focus, so the
        // editor draws the match itself. See CodeEditor.HighlightMatch.
        editor.HighlightMatch(match.Index, match.Length);

        model.EditFindStatus = L.T("edit.findCount", match.Total.ToString("N0", L.Culture));
    }
}
