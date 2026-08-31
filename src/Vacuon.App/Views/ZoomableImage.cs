using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// The preview image, with a wheel that zooms and a drag that pans — milestone M3, F6.4.
/// <para>
/// It exists for one question the fitted view cannot answer: <i>which of these two copies is
/// the sharper one?</i> Two versions of a photograph shrunk into a side panel look identical,
/// and the difference between them is exactly what somebody is trying to see before deleting
/// one of them.
/// </para>
/// <para>
/// Built on a <see cref="Image"/> with a transform rather than a ScrollViewer: panning has to
/// work while the picture is smaller than the panel too, and a scroll viewer has nothing to
/// scroll then.
/// </para>
/// </summary>
/// <remarks>
/// A Grid rather than a templated Control: a templated control wants a default style in a
/// Themes/Generic.xaml this project does not have, and would render as nothing at all while
/// compiling perfectly — the same silent-blank failure mode a missing converter caused here
/// once already.
/// </remarks>
public sealed class ZoomableImage : Grid
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ZoomableImage),
            new PropertyMetadata(null, OnSourceChanged));

    /// <summary>Raised whenever the zoom changes, so the view can show it.</summary>
    public event Action<double>? ZoomChanged;

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Beyond this the picture is bigger than any screen and pans out of reach.</summary>
    public const double MaximumZoom = 16.0;

    /// <summary>Below this it is a dot. Fitted is 1.0, so this still allows shrinking.</summary>
    public const double MinimumZoom = 0.2;

    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
    };

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _pan = new(0, 0);

    private Point _grabbedAt;
    private bool _dragging;

    public ZoomableImage()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var transforms = new TransformGroup();
        transforms.Children.Add(_scale);
        transforms.Children.Add(_pan);

        _image.RenderTransform = transforms;
        _image.RenderTransformOrigin = new Point(0.5, 0.5);

        Children.Add(_image);

        // Without a background the control has no hit-test surface where the picture is not,
        // so a drag that starts on empty space does nothing and the pan feels broken.
        Background = Brushes.Transparent;

        ClipToBounds = true;
        Focusable = true;

        // A screen reader gets a name, and the keyboard gets the reset. Zoom by wheel alone
        // would put the whole feature out of reach of anyone not using a mouse.
        AutomationProperties.SetName(this, L.T("preview.imageA11y"));
    }

    public double Zoom => _scale.ScaleX;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ZoomableImage)d;

        control._image.Source = (ImageSource?)e.NewValue;

        // A new picture starts fitted. Carrying the previous zoom over means opening a file
        // and seeing a corner of it with no clue why.
        control.Reset();
    }

    /// <summary>Back to fitted and centred.</summary>
    public void Reset()
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _pan.X = _pan.Y = 0;

        ZoomChanged?.Invoke(1);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        double target = Math.Clamp(_scale.ScaleX * factor, MinimumZoom, MaximumZoom);

        if (Math.Abs(target - _scale.ScaleX) < 0.0001) return;

        _scale.ScaleX = _scale.ScaleY = target;

        // Zoomed back out to fitted: recentre, because a pan left over from a closer look
        // otherwise leaves the fitted picture sitting off to one side.
        if (target <= 1.0) _pan.X = _pan.Y = 0;

        ZoomChanged?.Invoke(target);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        Focus();

        if (e.ClickCount == 2)
        {
            Reset();
            e.Handled = true;
            return;
        }

        _grabbedAt = e.GetPosition(this);
        _dragging = true;

        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging) return;

        Point now = e.GetPosition(this);

        _pan.X += now.X - _grabbedAt.X;
        _pan.Y += now.Y - _grabbedAt.Y;

        _grabbedAt = now;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        _dragging = false;

        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                Scale(1.2);
                e.Handled = true;
                break;

            case Key.Subtract or Key.OemMinus:
                Scale(1 / 1.2);
                e.Handled = true;
                break;

            case Key.D0 or Key.NumPad0 or Key.Escape:
                Reset();
                e.Handled = true;
                break;
        }
    }

    private void Scale(double factor)
    {
        double target = Math.Clamp(_scale.ScaleX * factor, MinimumZoom, MaximumZoom);

        _scale.ScaleX = _scale.ScaleY = target;
        if (target <= 1.0) _pan.X = _pan.Y = 0;

        ZoomChanged?.Invoke(target);
    }
}
