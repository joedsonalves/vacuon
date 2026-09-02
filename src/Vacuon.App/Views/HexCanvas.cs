using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Vacuon.Core.Preview;

namespace Vacuon.App.Views;

/// <summary>
/// A hex dump, coloured, drawn one screenful at a time.
/// <para>
/// ⚠️ <b>Only the lines on screen are drawn.</b> The dump of a one-megabyte file is 65 536
/// lines and about 180 000 pixels tall, and a window shows forty of them. What this replaced
/// was a <c>TextBlock</c> holding one <c>Run</c> per coloured stretch — 477 568 of them for a
/// 181 KiB executable, which took <b>12,4 s</b> to build and lay out, on the UI thread, before
/// the window could be touched again. Reading the file was 22 ms of that.
/// </para>
/// <para>
/// One <see cref="DrawingVisual"/>, redrawn on scroll, the way <c>TreemapCanvas</c> already
/// does it here for the same reason. Measured on that executable: <b>8,2 ms</b> per scroll
/// step, worst 25,9 ms — against <b>1 380 ms</b> for a <c>TextBlock</c> rebuilt with only the
/// forty-five visible lines in it, which is the comparison that matters. The cost was never
/// the text; it is the inline objects and the logical tree they hang in.
/// </para>
/// </summary>
public sealed class HexCanvas : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private readonly List<HexSpan> _spans = [];

    private int[] _lineStarts = [];
    private double _lineHeight = 14;
    private Typeface? _typeface;

    /// <summary>Set when a redraw could not happen, so becoming visible can pay it back.</summary>
    private bool _owed;

    public HexCanvas()
    {
        AddVisualChild(_visual);

        // ⚠️ Coming back from Collapsed does not raise SizeChanged, so nothing would ever
        // redraw it. Same trap the treemap and the sunburst were caught by; the vault note is
        // "RenderOpen apaga antes de desenhar".
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _owed) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Redraw);
        };
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HexCanvas),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure, OnSourceChanged));

    /// <summary>The whole dump. Only the visible part of it is ever drawn.</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (HexCanvas)d;

        canvas._lineStarts = HexSpans.LineStarts((string)(e.NewValue ?? string.Empty));
        canvas._verticalOffset = 0;
        canvas.InvalidateMeasure();
        canvas.Redraw();
    }

    private double _verticalOffset;

    /// <summary>
    /// How far down the dump the viewport is, in pixels.
    /// <para>
    /// ⚠️ <b>This only chooses which lines to draw; it never moves them.</b> The element is
    /// the full height of the dump and whatever scrolls it already translates it, so
    /// subtracting the offset from each line as well would move the text twice and it would
    /// slide out of the window at double speed.
    /// </para>
    /// </summary>
    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            if (Math.Abs(_verticalOffset - value) < 0.01) return;

            _verticalOffset = value;
            Redraw();
        }
    }

    /// <summary>
    /// How much of the dump is on screen, when this element is taller than what shows it.
    /// <para>
    /// Zero means "as tall as I am", which is the case when nothing is scrolling it.
    /// </para>
    /// </summary>
    public double ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            if (Math.Abs(_viewportHeight - value) < 0.01) return;

            _viewportHeight = value;
            Redraw();
        }
    }

    private double _viewportHeight;

    /// <summary>The height of one line, from the font this control was actually given.</summary>
    public double LineHeight
    {
        get
        {
            if (_typeface is null) ReadFont();
            return _lineHeight;
        }
    }

    /// <summary>How many lines the dump has.</summary>
    public int LineCount => _lineStarts.Length;

    protected override Size MeasureOverride(Size availableSize)
    {
        ReadFont();

        // The width is the width of a full line, measured rather than guessed at: inside a
        // scroller the available width is infinite, and returning something short would clip
        // the readable column off the right of every line.
        double width = LineWidth();

        if (!double.IsInfinity(availableSize.Width)) width = Math.Max(width, availableSize.Width);

        return new Size(width, _lineStarts.Length * _lineHeight);
    }

    /// <summary>
    /// How wide one full line renders.
    /// </summary>
    /// <remarks>
    /// Every full line of a dump is the same width — sixteen fixed columns — so the first one
    /// is the widest. The last line is the short one, which is why it is not the one measured.
    /// </remarks>
    private double LineWidth()
    {
        string dump = SourceText ?? string.Empty;

        if (_lineStarts.Length == 0 || _typeface is null) return 0;

        int end = HexSpans.LineEnd(dump, _lineStarts, 0);
        if (end <= 0) return 0;

        var text = new FormattedText(dump[..end], CultureInfo.InvariantCulture,
                                     FlowDirection.LeftToRight, _typeface,
                                     TextElement.GetFontSize(this), Brushes.Gray,
                                     VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return text.WidthIncludingTrailingWhitespace;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Redraw();
    }

    /// <summary>
    /// Takes the typeface and the line height from what this control was actually given.
    /// </summary>
    /// <remarks>
    /// The font is never set here. Family, size and foreground are inherited properties, so
    /// whatever the pane puts on this control reaches it and there is nothing for it to drift
    /// apart from — the rule <c>CodeEditor</c> already lives by.
    /// </remarks>
    private void ReadFont()
    {
        FontFamily family = TextElement.GetFontFamily(this);
        double size = TextElement.GetFontSize(this);

        _typeface = new Typeface(family, TextElement.GetFontStyle(this),
                                 TextElement.GetFontWeight(this), TextElement.GetFontStretch(this));

        _lineHeight = family.LineSpacing * size;
    }

    /// <summary>
    /// How tall the band on screen is.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="FrameworkElement.ActualHeight"/> is the wrong answer inside a
    /// scroller</b>, and it is the one that would be reached for. This element is the full
    /// height of the dump — 180 000 pixels for a 181 KiB file — so falling back to it would
    /// draw every line and bring back the freeze this class exists to remove. The hazard is
    /// one of ordering: a size change can arrive before the first scroll event, with nothing
    /// having said what the viewport is yet. So the scroller is asked directly, and its own
    /// height is only the last resort, for when there is no scroller at all.
    /// </remarks>
    private double VisibleHeight()
    {
        if (_viewportHeight > 0) return _viewportHeight;

        for (DependencyObject? at = VisualTreeHelper.GetParent(this);
             at is not null;
             at = VisualTreeHelper.GetParent(at))
        {
            if (at is System.Windows.Controls.ScrollViewer scroller && scroller.ViewportHeight > 0)
                return scroller.ViewportHeight;
        }

        return ActualHeight;
    }

    /// <summary>
    /// Redraws the band that is on screen.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The guard comes before <c>RenderOpen</c>, never after.</b> <c>RenderOpen</c>
    /// wipes the visual as it opens it, so giving up once it is open is exactly what leaves a
    /// blank rectangle behind. A redraw that cannot happen keeps the old picture and notes
    /// that it owes one.
    /// </remarks>
    public void Redraw()
    {
        string dump = SourceText ?? string.Empty;
        double height = VisibleHeight();

        if (dump.Length == 0 || _lineStarts.Length == 0 || height <= 0)
        {
            _owed = dump.Length > 0;
            return;
        }

        if (_typeface is null) ReadFont();
        if (_lineHeight <= 0) { _owed = true; return; }

        _owed = false;

        Brush muted = Colour("Text.Muted");
        Brush printable = Colour("Syntax.String");
        Brush other = Colour("Syntax.Number");
        Brush ascii = Colour("Text.Primary");

        int first = Math.Max(0, (int)(_verticalOffset / _lineHeight));
        int last = Math.Min(_lineStarts.Length - 1,
                            (int)((_verticalOffset + height) / _lineHeight) + 1);

        using DrawingContext dc = _visual.RenderOpen();

        for (int line = first; line <= last; line++)
        {
            int start = _lineStarts[line];
            int end = HexSpans.LineEnd(dump, _lineStarts, line);

            if (end <= start) continue;

            double y = line * _lineHeight;
            double x = 0;

            _spans.Clear();
            HexSpans.Line(dump, start, end, _spans);

            if (_spans.Count == 0)
            {
                Emit(dc, dump[start..end], muted, ref x, y);
                continue;
            }

            int at = start;

            foreach (HexSpan span in _spans)
            {
                // Whatever sits between two spans — the separators — is drawn plain rather
                // than dropped. A dump missing its spacing does not line up as a dump.
                if (span.Start > at) Emit(dc, dump[at..span.Start], muted, ref x, y);

                Emit(dc, dump.Substring(span.Start, span.Length), span.Kind switch
                {
                    HexKind.Printable => printable,
                    HexKind.Other => other,
                    HexKind.Ascii => ascii,
                    _ => muted,
                }, ref x, y);

                at = span.Start + span.Length;
            }

            if (at < end) Emit(dc, dump[at..end], muted, ref x, y);
        }
    }

    private void Emit(DrawingContext dc, string piece, Brush brush, ref double x, double y)
    {
        if (piece.Length == 0) return;

        var text = new FormattedText(piece, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     _typeface!, TextElement.GetFontSize(this), brush,
                                     VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(x, y));
        x += text.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// The colour for each kind, taken from the theme so both themes stay readable.
    /// </summary>
    /// <remarks>
    /// Zeros and the dots standing in for unprintable bytes are deliberately the muted
    /// colour: they are the majority of most files and the part carrying no information, so
    /// dimming them is what makes the rest stand out — the same reasoning that put the folder
    /// colours on the treemap.
    /// </remarks>
    private Brush Colour(string key) =>
        TryFindResource(key) as Brush ?? TextElement.GetForeground(this) ?? Brushes.Gray;
}
