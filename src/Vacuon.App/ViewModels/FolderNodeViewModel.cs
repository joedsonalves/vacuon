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
    private readonly ISelectionSink? _selection;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isSelected;

    private FolderNodeViewModel()
    {
        Name = "…";
        Children = [];
    }

    public FolderNodeViewModel(VolumeIndex index, int entryIndex, ISelectionSink? selection = null,
                               string? displayName = null)
    {
        _index = index;
        _entryIndex = entryIndex;
        _selection = selection;
        _isChecked = selection?.IsChecked(entryIndex) ?? false;

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
    /// <para>
    /// The tick itself is kept by the view model, in the same basket the file list uses,
    /// so folders and files ticked in either pane are one selection rather than two.
    /// </para>
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value)) return;
            _selection?.SetChecked(_entryIndex, value);
        }
    }

    /// <summary>
    /// Adopts the basket's answer for one entry, without telling it back.
    /// <para>
    /// A subfolder shows twice — as a node here and as a row in the list — and ticking
    /// either one has to be visible on the other.
    /// </para>
    /// </summary>
    internal void SyncChecked(int entryIndex, bool value)
    {
        if (_entryIndex == entryIndex && _index is not null && _isChecked != value)
        {
            _isChecked = value;
            Raise(nameof(IsChecked));
        }

        foreach (FolderNodeViewModel child in Children) child.SyncChecked(entryIndex, value);
    }

    /// <summary>Re-reads every loaded node's tick from the basket, after a batch change.</summary>
    internal void SyncAllChecks()
    {
        if (_selection is not null)
        {
            bool value = _selection.IsChecked(_entryIndex);
            if (_isChecked != value)
            {
                _isChecked = value;
                Raise(nameof(IsChecked));
            }
        }

        foreach (FolderNodeViewModel child in Children) child.SyncAllChecks();
    }

    /// <summary>Full path of this folder, materialized on demand.</summary>
    public string FullPath => _index?.GetFullPath(_entryIndex) ?? string.Empty;

    /// <summary>Clears every tick in this subtree, after a delete or a rescan.</summary>
    public void ClearChecks()
    {
        IsChecked = false;
        foreach (FolderNodeViewModel child in Children) child.ClearChecks();
    }

    /// <summary>
    /// Drops the children whose entries are gone and refreshes the sizes that are left.
    /// <para>
    /// Called after a delete. Without it a deleted folder keeps its node in the tree, and
    /// reopening a collapsed ancestor rebuilds it straight from the index.
    /// </para>
    /// </summary>
    public void PruneDeleted()
    {
        if (_index is null) return;

        for (int i = Children.Count - 1; i >= 0; i--)
        {
            FolderNodeViewModel child = Children[i];
            if (child._index is null) continue;   // the expansion placeholder

            if (!_index.Entries[child._entryIndex].IsInUse) Children.RemoveAt(i);
            else child.PruneDeleted();
        }

        // A folder that lost everything below it should stop offering an arrow to open.
        if (Children.Count == 0 && _loaded && !_index.HasChildDirectories(_entryIndex))
            _isExpanded = false;

        Raise(nameof(SubtreeSize));
        Raise(nameof(SizeText));
        Raise(nameof(ShareOfVolume));
    }

    /// <summary>
    /// Re-reads this subtree from the index, after folders changed place.
    /// <para>
    /// A move is the one operation that both takes a node away from a parent and gives it
    /// to another, so pruning what is gone — which is all a delete needs — would leave the
    /// destination looking empty until the next scan.
    /// </para>
    /// </summary>
    public void Resync()
    {
        if (_index is null) return;

        if (_loaded)
        {
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                FolderNodeViewModel child = Children[i];
                if (child._index is null) continue;   // the expansion placeholder

                ref FileEntry entry = ref _index.Entries[child._entryIndex];

                // Gone, or now hanging off somebody else.
                if (!entry.IsInUse || entry.ParentIndex != (uint)_entryIndex) Children.RemoveAt(i);
                else child.Resync();
            }

            foreach (int candidate in _index.GetChildren(_entryIndex))
            {
                if (!_index.Entries[candidate].IsDirectory) continue;
                if (Holds(candidate)) continue;

                Children.Add(new FolderNodeViewModel(_index, candidate, _selection));
            }
        }
        else if (Children.Count == 0 && _index.HasChildDirectories(_entryIndex))
        {
            // Never opened, and something just moved in: it needs its arrow back.
            Children.Add(Placeholder);
        }

        Raise(nameof(SubtreeSize));
        Raise(nameof(SizeText));
        Raise(nameof(ShareOfVolume));
    }

    private bool Holds(int entryIndex)
    {
        foreach (FolderNodeViewModel child in Children)
            if (child._index is not null && child._entryIndex == entryIndex) return true;

        return false;
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
            Children.Add(new FolderNodeViewModel(_index, index, _selection));
    }

    public void ExpandTo(int depth)
    {
        if (depth <= 0) return;
        IsExpanded = true;
        foreach (FolderNodeViewModel child in Children) child.ExpandTo(depth - 1);
    }

    /// <summary>
    /// Opens the tree along <paramref name="chain"/> and selects the node at its end.
    /// <para>
    /// Setting <see cref="IsExpanded"/> is what materialises a node's children, so walking
    /// down and expanding as it goes is also what makes the next step's children exist. That
    /// is the whole reason this cannot be a search over nodes already built: below the first
    /// closed folder there are none.
    /// </para>
    /// </summary>
    /// <param name="chain">
    /// Entry indices from just below this node down to the target, this node's own child
    /// first.
    /// </param>
    /// <returns>
    /// The node that ended up selected, or null when the walk could not start. A chain that
    /// runs out early — a step the tree does not list, such as a file — selects the deepest
    /// folder it did reach, which is still the right place to be looking.
    /// </returns>
    public FolderNodeViewModel? Reveal(IReadOnlyList<int> chain, int from = 0)
    {
        if (_index is null) return null;

        if (from >= chain.Count)
        {
            IsExpanded = true;
            IsSelected = true;
            return this;
        }

        IsExpanded = true;

        foreach (FolderNodeViewModel child in Children)
        {
            if (child.EntryIndex != chain[from]) continue;
            return child.Reveal(chain, from + 1);
        }

        IsSelected = true;
        return this;
    }
}
