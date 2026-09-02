using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Vacuon.Core.Preview;

namespace Vacuon.App.Views;

/// <summary>
/// A hex dump with its parts told apart by colour.
/// <para>
/// The dump is a wall of two-character groups that all look alike, and what somebody is
/// actually doing with it is finding the shape of a file: where the header ends, where the
/// padding starts, which stretch is text. Colour answers those at a glance.
/// </para>
/// <para>
/// Same split as <see cref="SyntaxTextBlock"/>: <see cref="HexSpans"/> decides what each
/// stretch is and knows nothing about brushes, and this maps a kind to a colour from the
/// theme that is already loaded.
/// </para>
/// </summary>
public sealed class HexTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HexTextBlock),
            new PropertyMetadata(string.Empty, OnChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HexTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        string dump = SourceText ?? string.Empty;
        if (dump.Length == 0) return;

        List<HexSpan> spans = HexSpans.Classify(dump);

        if (spans.Count == 0)
        {
            Inlines.Add(new Run(dump));
            return;
        }

        int at = 0;

        foreach (HexSpan span in spans)
        {
            // Whatever sits between two spans — the separators, the line breaks — is written
            // plain rather than dropped. A dump missing its spacing is not a dump.
            if (span.Start > at) Inlines.Add(new Run(dump[at..span.Start]));

            Inlines.Add(new Run(dump.Substring(span.Start, span.Length))
            {
                Foreground = BrushFor(span.Kind),
            });

            at = span.Start + span.Length;
        }

        if (at < dump.Length) Inlines.Add(new Run(dump[at..]));
    }

    /// <summary>
    /// The colour for each kind, taken from the theme so both themes stay readable.
    /// </summary>
    /// <remarks>
    /// Zeros and the dots that stand in for unprintable bytes are deliberately the muted
    /// colour: they are the majority of most files and they are the part carrying no
    /// information. Dimming them is what makes the rest stand out — the same reasoning that
    /// put the folder colours on the treemap.
    /// </remarks>
    private Brush BrushFor(HexKind kind)
    {
        string key = kind switch
        {
            HexKind.Offset => "Text.Muted",
            HexKind.Zero => "Text.Muted",
            HexKind.Printable => "Syntax.String",
            HexKind.Other => "Syntax.Number",
            HexKind.Ascii => "Text.Primary",
            _ => "Text.Muted",
        };

        return TryFindResource(key) as Brush ?? Foreground;
    }
}
