using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vacuon.Core.Preview;

namespace Vacuon.App.Views;

/// <summary>
/// A text box that shows the colours of <see cref="SyntaxTextBlock"/> while it is being
/// typed into.
/// <para>
/// ⚠️ <b>Two controls, stacked, not one.</b> WPF has no coloured editable text box: a
/// <c>RichTextBox</c> is the only built-in one and it brings a document model, its own undo
/// stack and a re-colour of the whole flow on every keystroke. So the colouring is a
/// <see cref="SyntaxTextBlock"/> painted behind, and the real editor is an ordinary
/// <c>TextBox</c> in front with a <b>transparent foreground</b> — the caret, the selection
/// and every key belong to the box, and the colours show through it.
/// </para>
/// <para>
/// ⚠️ <b>The two only line up while their metrics are identical.</b> Same font, same size,
/// same padding, no wrapping, and the scroll offset of the box copied onto the block. Change
/// one of those on one of them and the text underneath drifts a little further with every
/// line — which reads as a rendering bug and is really a layout mismatch.
/// </para>
/// </summary>
public sealed class CodeEditor : Grid
{
    private readonly SyntaxTextBlock _colours = new();
    private readonly HexCanvas _hexColours = new();
    private readonly TextBox _box = new();
    private readonly ScrollViewer _behind = new();
    private readonly Grid _painters = new();

    private string _indent = Indentation.Default;

    public CodeEditor()
    {
        // ⚠️ Neither half sets its own font. Font family, size and foreground are inherited
        // attached properties, so whatever the pane puts on this control reaches both — and
        // they cannot drift apart, which is the one thing that would break the alignment.
        _colours.TextWrapping = TextWrapping.NoWrap;
        _colours.Margin = new Thickness(2, 0, 0, 0);

        // ⚠️ Drawn, not built out of Runs. The dump of a one-megabyte file is 65 536 lines,
        // and the TextBlock this replaced took 12,4 s to build and lay out one of 181 KiB —
        // which is what "opening the byte editor froze the window" was.
        _hexColours.Margin = _colours.Margin;
        _hexColours.Visibility = Visibility.Collapsed;
        _hexColours.HorizontalAlignment = HorizontalAlignment.Left;
        _hexColours.VerticalAlignment = VerticalAlignment.Top;

        _painters.Children.Add(_colours);
        _painters.Children.Add(_hexColours);

        _behind.Content = _painters;
        _behind.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _behind.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _behind.IsHitTestVisible = false;
        _behind.Padding = new Thickness(4, 2, 0, 0);

        _box.AcceptsReturn = true;
        _box.AcceptsTab = true;
        _box.TextWrapping = TextWrapping.NoWrap;
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _box.Background = Brushes.Transparent;
        _box.Foreground = Brushes.Transparent;
        _box.BorderThickness = new Thickness(0);
        _box.Padding = new Thickness(4, 2, 0, 0);
        _box.UndoLimit = 200;

        _box.TextChanged += OnBoxChanged;
        _box.PreviewKeyDown += OnKeys;
        _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrolled));

        Children.Add(_behind);
        Children.Add(_box);
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(CodeEditor),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(CodeEditor),
            new PropertyMetadata(string.Empty, OnFileNameChanged));

    /// <summary>Decides whether there is syntax to colour at all — a log has none.</summary>
    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public static readonly DependencyProperty CaretBrushProperty =
        DependencyProperty.Register(nameof(CaretBrush), typeof(Brush), typeof(CodeEditor),
            new PropertyMetadata(Brushes.Black, (d, e) => ((CodeEditor)d)._box.CaretBrush = (Brush)e.NewValue));

    public Brush CaretBrush
    {
        get => (Brush)GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    public static readonly DependencyProperty IsHexProperty =
        DependencyProperty.Register(nameof(IsHex), typeof(bool), typeof(CodeEditor),
            new PropertyMetadata(false, OnIsHexChanged));

    /// <summary>
    /// Whether the text is a hex dump rather than source.
    /// <para>
    /// Two painters, one shown at a time. A dump and a snippet of code want different
    /// colours: one separates byte from byte, the other separates word from word, and running
    /// a code tokenizer over a dump would paint hex digits as numbers and tell nobody
    /// anything.
    /// </para>
    /// </summary>
    public bool IsHex
    {
        get => (bool)GetValue(IsHexProperty);
        set => SetValue(IsHexProperty, value);
    }

    private static void OnIsHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        bool hex = (bool)e.NewValue;

        editor._colours.Visibility = hex ? Visibility.Collapsed : Visibility.Visible;
        editor._hexColours.Visibility = hex ? Visibility.Visible : Visibility.Collapsed;

        editor.Repaint();
    }

    /// <summary>Gives the find box somewhere to select into.</summary>
    public TextBox Box => _box;

    private void Repaint()
    {
        if (IsHex) _hexColours.SourceText = _box.Text;
        else _colours.SourceText = _box.Text;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        var text = (string)(e.NewValue ?? string.Empty);

        if (!string.Equals(editor._box.Text, text, StringComparison.Ordinal)) editor._box.Text = text;

        editor.Repaint();
        editor._indent = Indentation.Detect(text);
    }

    private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeEditor)d)._colours.FileName = (string)(e.NewValue ?? string.Empty);

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        Repaint();
        if (!string.Equals(Text, _box.Text, StringComparison.Ordinal)) Text = _box.Text;
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        _behind.ScrollToVerticalOffset(e.VerticalOffset);
        _behind.ScrollToHorizontalOffset(e.HorizontalOffset);
    }

    /// <summary>
    /// Enter keeps the indentation of the line above; Tab indents and Shift+Tab takes it back.
    /// </summary>
    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
        {
            string lead = Indentation.LeadingWhitespaceAt(_box.Text, _box.SelectionStart);
            if (lead.Length == 0) return;

            Insert("\r\n" + lead);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Tab) return;

        bool back = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        // A selection spanning lines means the whole block moves, which is the only reason
        // anybody presses Tab with something selected.
        if (_box.SelectionLength > 0 && _box.SelectedText.Contains('\n'))
        {
            Shift(back);
            e.Handled = true;
            return;
        }

        if (back)
        {
            Outdent();
            e.Handled = true;
            return;
        }

        Insert(_indent);
        e.Handled = true;
    }

    private void Insert(string what)
    {
        int at = _box.SelectionStart;

        _box.SelectedText = what;
        _box.CaretIndex = at + what.Length;
        _box.SelectionLength = 0;
    }

    /// <summary>Moves every line the selection touches one level in or out.</summary>
    private void Shift(bool back)
    {
        string text = _box.Text;
        int start = text.LastIndexOf('\n', Math.Max(0, _box.SelectionStart - 1)) + 1;

        int end = _box.SelectionStart + _box.SelectionLength;
        int lineEnd = text.IndexOf('\n', Math.Min(end, Math.Max(0, text.Length - 1)));
        if (lineEnd < 0) lineEnd = text.Length;

        string block = text[start..lineEnd];
        var rebuilt = new System.Text.StringBuilder(block.Length + 64);
        string[] lines = block.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) rebuilt.Append('\n');

            string line = lines[i];

            if (!back) { rebuilt.Append(_indent).Append(line); continue; }

            if (line.StartsWith(_indent, StringComparison.Ordinal)) rebuilt.Append(line[_indent.Length..]);
            else if (line.StartsWith('\t')) rebuilt.Append(line[1..]);
            else rebuilt.Append(line.TrimStart(' '));
        }

        string replacement = rebuilt.ToString();

        _box.Select(start, block.Length);
        _box.SelectedText = replacement;
        _box.Select(start, replacement.Length);
    }

    private void Outdent()
    {
        string text = _box.Text;
        int start = text.LastIndexOf('\n', Math.Max(0, _box.SelectionStart - 1)) + 1;
        int caret = _box.SelectionStart;

        int remove = 0;

        if (text.Length > start && text[start] == '\t') remove = 1;
        else
        {
            while (remove < _indent.Length && start + remove < text.Length && text[start + remove] == ' ')
                remove++;
        }

        if (remove == 0) return;

        _box.Select(start, remove);
        _box.SelectedText = string.Empty;
        _box.CaretIndex = Math.Max(start, caret - remove);
    }
}
