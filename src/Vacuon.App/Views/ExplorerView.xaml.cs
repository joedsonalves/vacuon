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
        // multi-selection has to be pushed to the view model from here.
        Model?.SetListSelection(Files.SelectedItems.OfType<FileRowViewModel>());
    }

    private void OnTreeCheckChanged(object sender, RoutedEventArgs e) => PushTreeSelection();

    private void PushTreeSelection()
    {
        MainViewModel? model = Model;
        if (model is null) return;

        var paths = new List<string>();
        foreach (FolderNodeViewModel root in model.RootNodes) root.CollectChecked(paths);

        model.SetTreeSelection(paths);
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
        if (e.Key != Key.Delete) return;

        Delete(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? DeleteMode.Permanent
            : DeleteMode.RecycleBin);

        e.Handled = true;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e) => OnListKeyDown(sender, e);

    private void OnDeleteToRecycleBin(object sender, RoutedEventArgs e) => Delete(DeleteMode.RecycleBin);

    private void OnDeletePermanently(object sender, RoutedEventArgs e) => Delete(DeleteMode.Permanent);

    private void Delete(DeleteMode mode)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        // Refresh the tree ticks first: a checkbox toggled and then a delete triggered
        // by keyboard would otherwise act on a stale batch.
        PushTreeSelection();

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.DeleteSelection(mode, owner);

        foreach (FolderNodeViewModel root in model.RootNodes) root.ClearChecks();
        PushTreeSelection();
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
