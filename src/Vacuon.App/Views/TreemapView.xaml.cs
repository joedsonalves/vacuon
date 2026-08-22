using System.Windows;
using System.Windows.Controls;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;

namespace Vacuon.App.Views;

/// <summary>
/// The treemap screen — milestone M7.
/// <para>
/// It draws one level at a time rather than the whole tree nested. A full nested treemap of
/// 2.4 million files spends most of its pixels on boxes too small to see, and the question
/// people actually arrive with is "what is big in here?", asked one folder at a time.
/// </para>
/// </summary>
public partial class TreemapView : UserControl
{
    /// <summary>
    /// Above this many boxes the rest are too small to matter and only cost time. The
    /// footer says when it happens, so the picture is never quietly incomplete.
    /// </summary>
    private const int MaxBoxes = 4000;

    private MainViewModel? Model => DataContext as MainViewModel;

    private int _currentFolder = -1;

    public TreemapView()
    {
        InitializeComponent();

        Map.NodeActivated += OnNodeActivated;
        Map.NodeHovered += OnNodeHovered;

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Reset();
        };
    }

    private void Reset()
    {
        VolumeIndex? index = Model?.Index;

        if (index is null)
        {
            EmptyText.Text = L.T("treemap.needScan");
            PathText.Text = string.Empty;
            StatsText.Text = string.Empty;
            Map.SetNodes([]);
            UpButton.IsEnabled = false;
            return;
        }

        _dominant.Clear();
        _currentFolder = index.RootIndex;
        Show(index, _currentFolder);
    }

    private void Show(VolumeIndex index, int folder)
    {
        var nodes = new List<TreemapNode>();

        foreach (int child in index.GetChildren(folder))
        {
            ref FileEntry entry = ref index.Entries[child];
            if (!entry.IsInUse) continue;

            long bytes = entry.IsDirectory
                ? index.GetSubtreeSizeOnDisk(child)
                : index.GetSizeOnDisk(child);

            if (bytes <= 0) continue;

            ReadOnlySpan<char> name = index.GetName(child);

            // A folder is charged with whatever category fills it, so the top level of a
            // volume — which is almost all folders — is not a sheet of grey.
            string category = entry.IsDirectory
                ? DominantOf(index, child)
                : FileCategories.Of(name);

            nodes.Add(new TreemapNode(child, name.ToString(), bytes, entry.IsDirectory, category));
        }

        // Biggest first: the layout assumes descending weights, and so does the eye.
        nodes.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

        int total = nodes.Count;
        if (nodes.Count > MaxBoxes) nodes.RemoveRange(MaxBoxes, nodes.Count - MaxBoxes);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Map.SetNodes(nodes);
        watch.Stop();

        EmptyText.Text = nodes.Count == 0 ? L.T("treemap.emptyFolder") : string.Empty;

        string path = index.GetFullPath(folder);
        PathText.Text = path.Length > 0 ? path : L.T("treemap.root");

        // Measured, like everything else the app puts on screen.
        var stats = new List<string>(2)
        {
            L.T("treemap.boxes", Format.Count(nodes.Count),
                Format.Count(watch.ElapsedMilliseconds)),
        };

        if (total > nodes.Count)
            stats.Add(L.T("treemap.tooManyToDraw", Format.Count(nodes.Count), Format.Count(total)));

        StatsText.Text = string.Join(" · ", stats);
        UpButton.IsEnabled = folder != index.RootIndex;
    }

    /// <summary>
    /// Dominant category of a folder, cached by entry index.
    /// <para>
    /// Each call walks that folder's subtree, so drawing one level costs one pass over the
    /// part of the index below it. The cache matters because going into a folder and back
    /// out again is the normal way this screen is used, and the answer cannot change while
    /// the index stands still.
    /// </para>
    /// </summary>
    private string DominantOf(VolumeIndex index, int folder)
    {
        if (_dominant.TryGetValue(folder, out string? cached)) return cached;

        string category = FolderCategory.Dominant(index, folder);
        _dominant[folder] = category;
        return category;
    }

    private readonly Dictionary<int, string> _dominant = [];

    private void OnNodeActivated(object? sender, TreemapNode node)
    {
        VolumeIndex? index = Model?.Index;
        if (index is null || !node.IsDirectory) return;

        _currentFolder = node.EntryIndex;
        Show(index, _currentFolder);
    }

    private void OnNodeHovered(object? sender, TreemapNode? node)
    {
        HoverText.Text = node is null
            ? string.Empty
            : L.T("treemap.hover", node.Name, Format.Bytes(node.Bytes));
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        VolumeIndex? index = Model?.Index;
        if (index is null || _currentFolder < 0) return;

        uint parent = index.Entries[_currentFolder].ParentIndex;
        if (parent >= index.Entries.Length) return;

        _currentFolder = (int)parent;
        Show(index, _currentFolder);
    }
}
