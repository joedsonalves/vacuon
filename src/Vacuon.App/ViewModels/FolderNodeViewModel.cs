using System.Collections.ObjectModel;
using Vacuon.App.Infra;
using Vacuon.Core.Index;

namespace Vacuon.App.ViewModels;

/// <summary>
/// Um nó da árvore de pastas, com filhos carregados sob demanda.
/// <para>
/// A árvore só materializa o que o usuário abriu. Um nó fechado guarda um único
/// filho fantasma, que existe apenas para o WPF desenhar a seta de expansão — é o
/// padrão que permite navegar uma hierarquia de milhões de nós.
/// </para>
/// </summary>
public sealed class FolderNodeViewModel : Observable
{
    private static readonly FolderNodeViewModel Placeholder = new();

    private readonly VolumeIndex? _index;
    private readonly int _entryIndex;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isSelected;

    private FolderNodeViewModel()
    {
        Name = "…";
        Children = [];
    }

    public FolderNodeViewModel(VolumeIndex index, int entryIndex, string? displayName = null)
    {
        _index = index;
        _entryIndex = entryIndex;

        Name = displayName ?? index.GetName(entryIndex).ToString();
        if (string.IsNullOrEmpty(Name) || Name == ".") Name = index.Volume.Root;

        Children = [];

        if (index.HasChildDirectories(entryIndex))
            Children.Add(Placeholder);
    }

    public string Name { get; }
    public int EntryIndex => _entryIndex;
    public ObservableCollection<FolderNodeViewModel> Children { get; }

    public long SubtreeSize => _index?.GetSubtreeSize(_entryIndex) ?? 0;
    public string SizeText => _index is null ? string.Empty : Format.Bytes(SubtreeSize);

    /// <summary>Fatia do disco que esta subárvore representa, para a barra na árvore.</summary>
    public double ShareOfVolume
    {
        get
        {
            if (_index is null) return 0;
            long used = _index.Volume.UsedBytes;
            return used <= 0 ? 0 : Math.Min(1.0, (double)SubtreeSize / used);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            if (value) LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _isChecked;

    /// <summary>
    /// Ticked for a batch action.
    /// <para>
    /// A WPF TreeView has no multi-selection, so folders are gathered with a checkbox
    /// per node instead. It also makes the batch explicit — you can see what is marked
    /// without holding Ctrl and hoping.
    /// </para>
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    /// <summary>Full path of this folder, materialized on demand.</summary>
    public string FullPath => _index?.GetFullPath(_entryIndex) ?? string.Empty;

    /// <summary>Walks this node and its loaded descendants collecting the ticked ones.</summary>
    public void CollectChecked(List<string> into)
    {
        if (IsChecked && _index is not null)
        {
            string path = FullPath;
            if (!string.IsNullOrEmpty(path)) into.Add(path);
        }

        foreach (FolderNodeViewModel child in Children) child.CollectChecked(into);
    }

    /// <summary>Clears every tick in this subtree, after a delete or a rescan.</summary>
    public void ClearChecks()
    {
        IsChecked = false;
        foreach (FolderNodeViewModel child in Children) child.ClearChecks();
    }

    private void LoadChildren()
    {
        if (_loaded || _index is null) return;
        _loaded = true;

        Children.Clear();

        // Subpastas ordenadas por tamanho: quem abre a árvore quer achar o culpado,
        // não navegar em ordem alfabética.
        var subfolders = new List<(int Index, long Size)>();
        foreach (int child in _index.GetChildren(_entryIndex))
        {
            if (!_index.Entries[child].IsDirectory) continue;
            subfolders.Add((child, _index.GetSubtreeSize(child)));
        }

        subfolders.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        foreach ((int index, _) in subfolders)
            Children.Add(new FolderNodeViewModel(_index, index));
    }

    public void ExpandTo(int depth)
    {
        if (depth <= 0) return;
        IsExpanded = true;
        foreach (FolderNodeViewModel child in Children) child.ExpandTo(depth - 1);
    }
}
