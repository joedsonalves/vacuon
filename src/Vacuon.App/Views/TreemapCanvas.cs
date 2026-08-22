using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vacuon.Core.Analyzers;

namespace Vacuon.App.Views;

/// <summary>One box on the map: a folder or a file, with what it takes on disk.</summary>
public sealed record TreemapNode(int EntryIndex, string Name, long Bytes, bool IsDirectory, string CategoryKey);

/// <summary>
/// Draws a squarified treemap.
/// <para>
/// A <see cref="FrameworkElement"/> with one <see cref="DrawingVisual"/> rather than a
/// Canvas full of Rectangles: at the sizes this has to handle, one element per box means
/// tens of thousands of live objects, each with its own layout, hit-test and template pass.
/// The PRD asks for 100 k rectangles; the retained-mode route never gets close.
/// </para>
/// <para>
/// Boxes below a couple of pixels are drawn but not outlined. A one-pixel border on a
/// two-pixel box is not a border, it is the whole box — the grid would eat the picture and
/// make small files look bigger than they are.
/// </para>
/// </summary>
public sealed class TreemapCanvas : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private readonly VisualCollection _children;

    private IReadOnlyList<TreemapNode> _nodes = [];
    private TreemapRect[] _rects = [];

    private int _hoverIndex = -1;

    public TreemapCanvas()
    {
        _children = new VisualCollection(this) { _visual };
        ClipToBounds = true;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>Raised when a box is clicked, with the node under the pointer.</summary>
    public event EventHandler<TreemapNode>? NodeActivated;

    /// <summary>Raised when the box under the pointer changes. Null means the pointer left.</summary>
    public event EventHandler<TreemapNode?>? NodeHovered;

    public void SetNodes(IReadOnlyList<TreemapNode> nodes)
    {
        _nodes = nodes;
        _hoverIndex = -1;
        Redraw();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Redraw();
    }

    private void Redraw()
    {
        using DrawingContext dc = _visual.RenderOpen();

        double width = ActualWidth;
        double height = ActualHeight;

        if (_nodes.Count == 0 || width <= 1 || height <= 1)
        {
            _rects = [];
            return;
        }

        var weights = new long[_nodes.Count];
        for (int i = 0; i < _nodes.Count; i++) weights[i] = _nodes[i].Bytes;

        _rects = new TreemapRect[_nodes.Count];
        Treemap.Layout(weights, new TreemapRect(0, 0, width, height), _rects);

        // One pen for every box: creating a Pen per rectangle is the allocation that turns
        // a 100 k redraw into a stutter. Frozen so WPF can share it across threads.
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);
        pen.Freeze();

        for (int i = 0; i < _rects.Length; i++)
        {
            TreemapRect r = _rects[i];
            if (r.Width <= 0 || r.Height <= 0) continue;

            Brush brush = BrushFor(_nodes[i].CategoryKey, i == _hoverIndex);

            // The outline is skipped on boxes too small to survive it.
            bool outline = r.Width > 3 && r.Height > 3;

            dc.DrawRectangle(brush, outline ? pen : null,
                             new Rect(r.X, r.Y, r.Width, r.Height));
        }
    }

    private Brush BrushFor(string categoryKey, bool hovered)
    {
        string resource = categoryKey switch
        {
            FileCategories.Video => "Cat.Video",
            FileCategories.Image => "Cat.Image",
            FileCategories.Audio => "Cat.Audio",
            FileCategories.Document => "Cat.Document",
            FileCategories.Archive or FileCategories.Installer => "Cat.Archive",
            FileCategories.Code or FileCategories.Build => "Cat.Code",
            // Virtual disks earn their own colour: on the machine this was built on, nine
            // .vhdx files hold 106 GiB, and folding them into grey hides the single
            // biggest thing on the volume.
            FileCategories.Disk or FileCategories.Database => "Cat.Disk",
            FileCategories.Executable => "Cat.Binary",
            FileCategories.Log => "Cat.Log",
            _ => "Cat.Other",
        };

        var brush = TryFindResource(resource) as SolidColorBrush;
        Color color = brush?.Color ?? Colors.Gray;

        if (hovered)
        {
            // Lighten rather than swap colour: the category must stay readable while hovered.
            color = Color.FromRgb(Lift(color.R), Lift(color.G), Lift(color.B));
        }

        var solid = new SolidColorBrush(color);
        solid.Freeze();
        return solid;
    }

    private static byte Lift(byte channel) => (byte)Math.Min(255, channel + 45);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Point p = e.GetPosition(this);
        int index = HitTest(p);

        if (index == _hoverIndex) return;

        _hoverIndex = index;
        Redraw();

        NodeHovered?.Invoke(this, index >= 0 ? _nodes[index] : null);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverIndex < 0) return;

        _hoverIndex = -1;
        Redraw();
        NodeHovered?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        int index = HitTest(e.GetPosition(this));
        if (index >= 0) NodeActivated?.Invoke(this, _nodes[index]);
    }

    /// <summary>
    /// Which box is under the point. Linear, because the rectangles tile the area and the
    /// first hit is the only hit — building a spatial index would be work to save a scan
    /// that only happens on pointer moves.
    /// </summary>
    private int HitTest(Point p)
    {
        for (int i = 0; i < _rects.Length; i++)
        {
            TreemapRect r = _rects[i];
            if (r.Width <= 0 || r.Height <= 0) continue;

            if (p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom) return i;
        }

        return -1;
    }
}
