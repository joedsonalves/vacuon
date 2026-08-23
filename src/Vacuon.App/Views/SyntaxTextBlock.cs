using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Vacuon.Core.Preview;

namespace Vacuon.App.Views;

/// <summary>
/// The text preview, with comments, strings, numbers and keywords coloured — M3, F6.6.
/// <para>
/// The colouring is decided in <see cref="SyntaxSpans"/>, which returns positions and knows
/// nothing about brushes; this maps a kind to a colour from the theme already loaded. That
/// split is the rule the whole project is built on — the core never references UI — and it
/// pays off here, because the tokenizer is testable without a window.
/// </para>
/// <para>
/// A <c>TextBlock</c> of runs rather than a <c>RichTextBox</c>: the preview is read-only and
/// never edited, and a rich text box brings a document model, a caret and an undo stack to a
/// panel that needs none of them.
/// </para>
/// </summary>
public sealed class SyntaxTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(SyntaxTextBlock),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(SyntaxTextBlock),
            new PropertyMetadata(string.Empty, OnChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>Decides whether to colour at all — a log has no syntax to reveal.</summary>
    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    /// <summary>
    /// Above this many characters the text is shown plain.
    /// <para>
    /// Every coloured run is an inline element, and a hundred thousand of them makes the panel
    /// stutter for a file nobody is reading in a side pane anyway. The preview is already a
    /// truncated read; this is the same bargain one level down.
    /// </para>
    /// </summary>
    public const int MaximumColoured = 40_000;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SyntaxTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        string text = SourceText ?? string.Empty;

        if (text.Length == 0) return;

        if (text.Length > MaximumColoured || !SyntaxSpans.IsSource(FileName ?? string.Empty))
        {
            Inlines.Add(new Run(text));
            return;
        }

        IReadOnlyList<SyntaxSpan> spans = SyntaxSpans.Of(text);

        int at = 0;

        foreach (SyntaxSpan span in spans)
        {
            // Defensive, and cheap: a span that overlapped the previous one would otherwise
            // produce a negative length and throw while drawing a preview panel.
            if (span.Start < at || span.End > text.Length) continue;

            if (span.Start > at) Inlines.Add(new Run(text[at..span.Start]));

            Inlines.Add(new Run(text[span.Start..span.End]) { Foreground = BrushFor(span.Kind) });

            at = span.End;
        }

        if (at < text.Length) Inlines.Add(new Run(text[at..]));
    }

    /// <summary>
    /// The theme's own colours, looked up by key so light and dark both work and neither is
    /// hard-coded here.
    /// </summary>
    private Brush BrushFor(TokenKind kind)
    {
        string key = kind switch
        {
            TokenKind.Comment => "Syntax.Comment",
            TokenKind.String => "Syntax.String",
            TokenKind.Number => "Syntax.Number",
            TokenKind.Keyword => "Syntax.Keyword",
            _ => "Text.Secondary",
        };

        return TryFindResource(key) as Brush ?? Foreground;
    }
}
