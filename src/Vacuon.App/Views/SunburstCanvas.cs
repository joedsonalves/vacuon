using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vacuon.Core.Analyzers;

namespace Vacuon.App.Views;

/// <summary>One wedge, with what it stands for.</summary>
public sealed record SunburstSlice(SunburstWedge Wedge, int EntryIndex, string Name, long Bytes, string CategoryKey);

/// <summary>
/// The sunburst (PRD F2.8): the same disk the treemap draws, told by angle and depth.
/// <para>
/// A treemap answers "which of these is biggest". A sunburst answers "how does this break
/// down, level by level" — several levels at once, which is the question somebody has about
/// a folder they do not recognise. Neither replaces the other, so this sits beside it.
/// </para>
/// <para>
/// ⚠️ <b>One <see cref="DrawingVisual"/>, like the treemap</b>, and for the same measured
/// reason: a shape per wedge does not survive the number of wedges a real volume produces.
/// The geometry is built once per layout and painted whole.
/// </para>
/// </summary>
public sealed class SunburstCanvas : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private readonly VisualCollection _children;

    private IReadOnlyList<SunburstSlice> _slices = [];
    private int _ringCount = 1;
    private int _hoverIndex = -1;

    public SunburstCanvas()
    {
        _children = new VisualCollection(this) { _visual };
        ClipToBounds = true;

        // Same honesty as the treemap: a picture is not readable by ear, and the list has
        // the same numbers item by item. Naming the canvas and stopping there would be
        // worse than saying where the way in actually is.
        SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                 Vacuon.Core.Localization.L.T("a11y.treemap"));

        Focusable = true;

        IsVisibleChanged += OnBecameVisible;
    }

    /// <summary>
    /// Draws again once there is somewhere to draw.
    /// <para>
    /// ⚠️ <b>A collapsed element reads <c>ActualWidth == 0</c>, and coming back from
    /// collapsed raises no <c>SizeChanged</c>.</b> Measured: showing the canvas, hiding it,
    /// redrawing while hidden and showing it again ends with a blank picture — the redraw
    /// wiped the visual because it had no size, and nothing ever told it to try again.
    /// So a redraw that cannot happen is remembered, and the moment the element is visible
    /// again it is done, at <see cref="DispatcherPriority.Loaded"/> because the visibility
    /// change runs before layout has given it a size.
    /// </para>
    /// </summary>
    private bool _pending;

    private void OnBecameVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !_pending) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Render));
    }

    public event EventHandler<SunburstSlice>? SliceActivated;

    public event EventHandler<SunburstSlice?>? SliceHovered;

    public void SetSlices(IReadOnlyList<SunburstSlice> slices, int ringCount)
    {
        _slices = slices;
        _ringCount = Math.Max(1, ringCount);
        _hoverIndex = -1;

        Render();
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Render();
    }

    private double Radius => Math.Max(0, (Math.Min(ActualWidth, ActualHeight) / 2) - 8);

    private Point Centre => new(ActualWidth / 2, ActualHeight / 2);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int hit = HitTest(e.GetPosition(this));
        if (hit == _hoverIndex) return;

        _hoverIndex = hit;
        SliceHovered?.Invoke(this, hit >= 0 ? _slices[hit] : null);
        Render();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverIndex < 0) return;

        _hoverIndex = -1;
        SliceHovered?.Invoke(this, null);
        Render();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        int hit = HitTest(e.GetPosition(this));
        if (hit >= 0) SliceActivated?.Invoke(this, _slices[hit]);
    }

    private int HitTest(Point point)
    {
        if (_slices.Count == 0 || Radius <= 0) return -1;

        Point centre = Centre;
        var wedges = new SunburstWedge[_slices.Count];
        for (int i = 0; i < _slices.Count; i++) wedges[i] = _slices[i].Wedge;

        return Sunburst.HitTest(wedges, point.X - centre.X, point.Y - centre.Y, _ringCount, Radius);
    }

    private void Render()
    {
        // Before RenderOpen, never after: RenderOpen wipes the visual, so returning from
        // inside the using block is what leaves the blank circle behind.
        if (_slices.Count > 0 && Radius <= 0)
        {
            _pending = true;
            return;
        }

        _pending = false;

        using DrawingContext context = _visual.RenderOpen();

        if (_slices.Count == 0 || Radius <= 0) return;

        Point centre = Centre;
        double radius = Radius;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);

        for (int i = 0; i < _slices.Count; i++)
        {
            SunburstSlice slice = _slices[i];

            // A sliver thinner than an eyelash cannot be clicked, and a volume produces
            // thousands of them.
            if (!slice.Wedge.IsVisible()) continue;

            double inner = Sunburst.RingInnerRadius(slice.Wedge.Ring, _ringCount, radius);
            double outer = Sunburst.RingOuterRadius(slice.Wedge.Ring, _ringCount, radius);

            Geometry geometry = WedgeGeometry(centre, inner, outer, slice.Wedge);
            context.DrawGeometry(BrushFor(slice.CategoryKey, i == _hoverIndex), pen, geometry);
        }
    }

    /// <summary>
    /// The ring segment: out along one edge, round the outside, back down the other, round
    /// the inside.
    /// </summary>
    private static Geometry WedgeGeometry(Point centre, double inner, double outer, SunburstWedge wedge)
    {
        Point At(double radius, double angle) =>
            new(centre.X + (radius * Math.Sin(angle)), centre.Y - (radius * Math.Cos(angle)));

        bool large = wedge.SweepAngle > Math.PI;

        var figure = new PathFigure { StartPoint = At(inner, wedge.StartAngle), IsClosed = true, IsFilled = true };

        figure.Segments.Add(new LineSegment(At(outer, wedge.StartAngle), true));
        figure.Segments.Add(new ArcSegment(At(outer, wedge.EndAngle), new Size(outer, outer), 0,
                                           large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(inner, wedge.EndAngle), true));
        figure.Segments.Add(new ArcSegment(At(inner, wedge.StartAngle), new Size(inner, inner), 0,
                                           large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }

    private Brush BrushFor(string categoryKey, bool hovered)
    {
        string key = categoryKey switch
        {
            FileCategories.Video => "Cat.Video",
            FileCategories.Image => "Cat.Image",
            FileCategories.Audio => "Cat.Audio",
            FileCategories.Document => "Cat.Document",
            FileCategories.Archive or FileCategories.Installer => "Cat.Archive",
            FileCategories.Code or FileCategories.Build => "Cat.Code",
            FileCategories.Disk or FileCategories.Database => "Cat.Disk",
            FileCategories.Executable => "Cat.Binary",
            FileCategories.Log => "Cat.Log",
            _ => "Cat.Other",
        };

        var brush = TryFindResource(key) as Brush ?? Brushes.Gray;
        if (!hovered) return brush;

        // The hovered wedge is the same colour, lifted — a different hue would say it is a
        // different kind of thing.
        var lifted = brush.Clone();
        lifted.Opacity = 0.75;
        lifted.Freeze();

        return lifted;
    }
}
