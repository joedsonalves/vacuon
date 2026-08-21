using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vacuon.App.ViewModels;
using Vacuon.Core.Actions;

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

    // ==================== selection ====================

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
