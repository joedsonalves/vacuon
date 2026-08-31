using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vacuon.App.ViewModels;
using Vacuon.Core.Actions;
using Vacuon.Native.Interop;

namespace Vacuon.App.Views;

public partial class ExplorerView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public ExplorerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // As miniaturas são pedidas quando a linha entra em tela, não quando a lista
        // é construída: pedir 5000 de uma vez travaria a rolagem esperando o Shell.
        if (Files.ItemContainerGenerator is not null)
            Files.ItemContainerGenerator.StatusChanged += OnContainersChanged;

        // The zoom readout beside the title. The control owns the number; the view model
        // only carries it to the screen.
        PreviewZoom.ZoomChanged += zoom => Model?.ReportZoom(zoom);

        // Pasting a full file path into the search box lands on the file, not merely in the
        // folder holding it — which means scrolling to the row, and only the ListView can.
        if (Model is not null)
        {
            Model.RowRevealRequested += row =>
            {
                ItemsControl list = Model.IsGallery ? Gallery : (ItemsControl)Files;
                list.Dispatcher.BeginInvoke(() =>
                {
                    if (list is ListBox box) box.ScrollIntoView(row);
                    else if (list is ListView view) view.ScrollIntoView(row);
                });
            };
        }

        // The icon column follows the chosen thumbnail size, and that changes without the
        // list ever resizing — so the elastic columns have to be told separately.
        if (Model is not null)
        {
            Model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IconPixels)) FitColumns();
            };
        }

        FitColumns();
    }

    private void OnContainersChanged(object? sender, EventArgs e)
    {
        if (Files.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            return;

        RequestVisibleThumbnails();
    }

    private void RequestVisibleThumbnails()
    {
        MainViewModel? model = Model;
        if (model is null) return;

        foreach (object item in Files.Items)
        {
            if (item is not FileRowViewModel row) continue;

            // Só o que tem contêiner realizado está (ou está entrando) em tela.
            if (Files.ItemContainerGenerator.ContainerFromItem(row) is null) continue;

            _ = model.RequestThumbnailAsync(row);
        }
    }

    // ==================== column widths ====================

    /// <summary>Narrowest the two elastic columns may get before the list starts scrolling.</summary>
    private const double MinNameWidth = 220;
    private const double MinPathWidth = 140;

    /// <summary>
    /// Shares whatever width is left between Name and Path.
    /// <para>
    /// The six columns used to add up to a fixed 1124 px while the pane they sit in is
    /// whatever the window and the two splitters leave — usually far less. With horizontal
    /// scrolling switched off, everything past the pane's edge was simply not drawn: Size,
    /// Modified and Path were gone from a list that gave no sign it was hiding three
    /// columns. Sizing them to the pane is the fix; the scrollbar below is the floor, so
    /// that a pane too narrow even for the minimums scrolls instead of swallowing them.
    /// </para>
    /// </summary>
    private void OnFileListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) FitColumns();
    }

    /// <summary>
    /// Breathing room a cell needs beyond its content, on top of the column width.
    /// <para>
    /// Measured, not guessed: with the icon column asked for 64 px the thumbnail was drawn
    /// clipped to about 52, so a cell keeps roughly twelve pixels for itself. Anything sized
    /// to exactly its content is therefore sized to less than its content.
    /// </para>
    /// </summary>
    private const double CellPadding = 12;

    private void FitColumns()
    {
        // The icon column is set here rather than bound in XAML — see the comment on the
        // column. It has to be right before the elastic share below is worked out.
        if (Model is not null) IconColumn.Width = Model.IconPixels + CellPadding;

        // Vertical scrollbar plus the row border. Guessed high rather than low: leaving a
        // few pixels spare costs nothing, overshooting brings back the clipping.
        double available = Files.ActualWidth - 24;

        double fixedColumns = CheckColumn.ActualWidth
                            + IconColumn.ActualWidth
                            + SizeColumn.ActualWidth
                            + ModifiedColumn.ActualWidth;

        double elastic = available - fixedColumns;
        if (double.IsNaN(elastic) || elastic <= 0) return;

        // Name gets the larger share: it is what the eye reads first, and the path below it
        // is already repeated in every row's tooltip.
        double name = Math.Max(MinNameWidth, elastic * 0.58);
        double path = Math.Max(MinPathWidth, elastic - name);

        NameColumn.Width = name;
        PathColumn.Width = path;
    }

    // ==================== selection ====================

    /// <summary>Back to fitted. The same thing a double-click on the picture does.</summary>
    private void OnPreviewFit(object sender, RoutedEventArgs e) => PreviewZoom.Reset();

    // ---------------- shell context menu (M3, F6.10) ----------------

    /// <summary>
    /// The real Explorer menu for the selected file, opened from inside Vacuon's own menu.
    /// <para>
    /// Inside it, not instead of it. Vacuon's menu carries quarantine and a delete that
    /// honours the protected-path list; replacing it with the shell's would take exactly the
    /// actions this application exists to offer out of somebody's hands. The shell menu is
    /// one entry down, where Open With and Properties are reachable without displacing
    /// anything.
    /// </para>
    /// <para>
    /// What Windows then does is Windows' business — that menu can delete, and the app's own
    /// guard does not reach into it, because nothing there is the app acting.
    /// </para>
    /// </summary>
    /// <summary>
    /// Hides the Windows entry when the shell has nothing to offer for this file.
    /// <para>
    /// An entry that does nothing when clicked is the quiet kind of lie this project treats
    /// as a bug. Asking the shell how many items it would build costs a few milliseconds and
    /// never shows a popup.
    /// </para>
    /// </summary>
    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (Model is null) return;

        IReadOnlyList<string> paths = Model.SelectedPaths();

        bool offered = paths.Count > 0 && ShellContextMenu.CountItems(paths[0]) > 0;

        ShellMenuItem.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowShellMenu(object sender, RoutedEventArgs e)
    {
        if (Model is null) return;

        IReadOnlyList<string> paths = Model.SelectedPaths();
        if (paths.Count == 0) return;

        // One file: the shell menu addresses a single item, and a multi-selection menu needs
        // every pidl bound to one parent folder — which a search result is not.
        string path = paths[0];

        // The WPF menu is still up and still holding the mouse capture at this point, and a
        // native TrackPopupMenuEx raised underneath it never becomes interactive — it opened
        // and vanished, leaving nothing on screen and no error anywhere. Close ours first and
        // raise the shell's once the message queue has drained.
        if (Files.ContextMenu is not null) Files.ContextMenu.IsOpen = false;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (!GetCursorPos(out POINT cursor)) return;

            nint owner = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)!).Handle;

            ShellContextMenu.Show(owner, path, cursor.X, cursor.Y);
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>
    /// The cursor in screen coordinates.
    /// <para>
    /// Not <c>Mouse.GetPosition</c>: by the time this runs the pointer is over a menu that is
    /// closing, and asking the list where the mouse is relative to itself answers about a
    /// position it no longer has.
    /// </para>
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    // ---------------- drag out (M3, F6.11) ----------------

    private Point _dragOrigin;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e) =>
        _dragOrigin = e.GetPosition(null);

    /// <summary>
    /// Drags the ticked files out to anywhere that takes a file drop.
    /// <para>
    /// A copy, never a move: this application does not quietly relocate somebody's files
    /// because a mouse travelled a few pixels. Moving them is what the Move button is for,
    /// which asks first and says where they are going.
    /// </para>
    /// </summary>
    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Model is null) return;

        Point now = e.GetPosition(null);

        // The system's own threshold. Below it, a click that wobbles would start a drag.
        if (Math.Abs(now.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (((DependencyObject?)e.OriginalSource) is not { } source) return;
        if (RowUnder(source) is not { } row) return;

        var paths = new System.Collections.Specialized.StringCollection();

        // The same selection the buttons act on, so a drag cannot quietly disagree with
        // them about what is selected.
        foreach (string path in Model.SelectedPaths()) paths.Add(path);

        if (paths.Count == 0 && row.FullPath.Length > 0) paths.Add(row.FullPath);
        if (paths.Count == 0) return;

        var data = new DataObject();
        data.SetFileDropList(paths);

        DragDrop.DoDragDrop(Files, data, DragDropEffects.Copy);
    }

    /// <summary>The row a mouse event landed on, walking up from whatever was hit.</summary>
    private static FileRowViewModel? RowUnder(DependencyObject source)
    {
        for (int depth = 0; depth < 32 && source is not null; depth++)
        {
            if (source is FrameworkElement { DataContext: FileRowViewModel row }) return row;

            source = System.Windows.Media.VisualTreeHelper.GetParent(source)!;
        }

        return null;
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedItems is not a bindable dependency property on ListView, so the
        // highlight has to be pushed to the view model from here. The ticked basket does
        // not come through this path — a checkbox reaches the view model on its own.
        Model?.SetListSelection(Files.SelectedItems.OfType<FileRowViewModel>());
    }

    // ==================== sorting ====================

    /// <summary>
    /// Clicking a column header sorts by it; clicking the active one reverses it.
    /// <para>
    /// Which column was asked for comes from the Tag on the header's content, not from its
    /// position — the GridView allows reordering, so the position means nothing.
    /// </para>
    /// </summary>
    /// <summary>
    /// The gallery's selection feeds the same place the list's does.
    /// <para>
    /// Two controls, one notion of "selected". Letting them disagree would mean the Delete
    /// button acting on something other than what is highlighted, which is the worst kind of
    /// disagreement this application could have.
    /// </para>
    /// </summary>
    private void OnGallerySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is null) return;

        var rows = new List<FileRowViewModel>(Gallery.SelectedItems.Count);

        foreach (object item in Gallery.SelectedItems)
            if (item is FileRowViewModel row) rows.Add(row);

        Model.SetListSelection(rows);
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Column?.Header is not FrameworkElement content) return;
        if (content.Tag is not string tag) return;
        if (!Enum.TryParse(tag, out RowSortKey key)) return;

        Model?.SortBy(key);
        RequestVisibleThumbnails();
    }

    // ==================== deletion ====================

    /// <summary>
    /// Del on the list sends to the Recycle Bin; Shift+Del deletes for good.
    /// <para>
    /// Handled here rather than as a window-level KeyBinding so the shortcut only fires
    /// while the list or the tree has focus — Del must not delete files while the user
    /// is editing the search box.
    /// </para>
    /// </summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        // Space ticks whatever is highlighted, without the mouse ever finding the little
        // checkbox. It is the gesture that makes reviewing a folder file by file work:
        // open, look, Esc, Space — and the tick stays through the next double click.
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            List<FileRowViewModel> rows = [.. Files.SelectedItems.OfType<FileRowViewModel>()];
            if (rows.Count == 0) return;

            // One decision for the whole highlight: if anything is unticked, tick it all.
            bool tick = rows.Exists(static r => !r.IsChecked);
            foreach (FileRowViewModel row in rows) row.IsChecked = tick;

            e.Handled = true;
            return;
        }

        if (e.Key != Key.Delete) return;

        Delete(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? DeleteMode.Permanent
            : DeleteMode.RecycleBin);

        e.Handled = true;
    }

    /// <summary>
    /// The tree gets Del and Shift+Del only. Space belongs to the list, where it ticks the
    /// highlighted rows — forwarding it from here would tick files the tree cannot show.
    /// </summary>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;

        Delete(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? DeleteMode.Permanent
            : DeleteMode.RecycleBin);

        e.Handled = true;
    }

    private void OnDeleteToRecycleBin(object sender, RoutedEventArgs e) => Delete(DeleteMode.RecycleBin);

    private void OnDeletePermanently(object sender, RoutedEventArgs e) => Delete(DeleteMode.Permanent);

    private void Delete(DeleteMode mode)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.DeleteSelection(mode, owner);

        // What actually went is already out of the basket. Anything left was refused or
        // failed, and clearing it would hide the fact that it is still there.
        RequestVisibleThumbnails();
    }

    // ==================== moving ====================

    /// <summary>
    /// Copies the ticked items somewhere else. The one batch action that takes nothing away
    /// from where it started.
    /// </summary>
    private void OnCopyTo(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.CopySelection(owner);
    }

    private void OnMoveTo(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.MoveSelection(owner);

        // The rows that stayed on screen point at new paths, so their thumbnails are
        // requested again for whatever is visible now.
        RequestVisibleThumbnails();
    }

    private void OnQuarantine(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.QuarantineSelection(owner);

        RequestVisibleThumbnails();
    }

    // ==================== navigation ====================

    private void OnBiggestFiles(object sender, RoutedEventArgs e)
    {
        Model?.ShowBiggestFiles();
        RequestVisibleThumbnails();
    }

    private void OnBiggestFolders(object sender, RoutedEventArgs e)
    {
        Model?.ShowBiggestFolders();
        RequestVisibleThumbnails();
    }

    private void OnSuspicious(object sender, RoutedEventArgs e)
    {
        Model?.ShowSuspicious();
        RequestVisibleThumbnails();
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node && Model is not null)
            Model.SelectedFolder = node;
    }

    /// <summary>
    /// Clicking the folder that is already selected lists it again.
    /// <para>
    /// A TreeView raises nothing when the selection does not change, so clicking the folder
    /// you are already on did nothing — which is exactly what somebody does after a search
    /// has replaced the list, to get back to where they were. The event is the click, not
    /// the selection, so it fires either way.
    /// </para>
    /// </summary>
    private void OnFolderClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is null) return;

        // The tick box and the expander chevron are their own controls with their own
        // meaning. A click that lands on one of them is not a request to list the folder.
        if (e.OriginalSource is DependencyObject source && IsInsideControl(source)) return;

        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FolderNodeViewModel node) return;

        if (!ReferenceEquals(node, Model.SelectedFolder)) return;

        Model.ShowFolder(node.EntryIndex);
    }

    private static bool IsInsideControl(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ToggleButton) return true;
            if (current is TreeViewItem) return false;
        }

        return false;
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }

        return null;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        MainViewModel? model = Model;
        if (model?.SelectedRow is null) return;

        // Pasta: navega para dentro. Arquivo: abre no aplicativo padrão.
        if (model.SelectedRow.IsDirectory) model.ShowFolder(model.SelectedRow.EntryIndex);
        else model.OpenCommand.Execute(null);

        RequestVisibleThumbnails();
    }

    private void OnMinSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is null) return;
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;

        Model.MinSizeBytes = long.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out long bytes)
            ? bytes : 0;
        RequestVisibleThumbnails();
    }

    private void OnMinAgeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is null) return;
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;

        Model.MinAgeDays = int.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out int days)
            ? days : 0;
        RequestVisibleThumbnails();
    }
}
