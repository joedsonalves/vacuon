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
            return;
        }

        int from = box.SelectionStart + Math.Max(1, box.SelectionLength);

        TextMatch match = FindInText.Next(box.Text, needle, from);

        if (!match.Found)
        {
            model.EditFindStatus = L.T("edit.findNone");
            return;
        }

        box.Focus();
        box.Select(match.Index, match.Length);
        box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(match.Index) - LinesAbove));

        model.EditFindStatus = L.T("edit.findCount", match.Total.ToString("N0", L.Culture));
    }
}
