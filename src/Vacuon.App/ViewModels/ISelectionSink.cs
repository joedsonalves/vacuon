namespace Vacuon.App.ViewModels;

/// <summary>
/// Where a ticked row or folder registers itself.
/// <para>
/// The selection is keyed by entry index and kept by <see cref="MainViewModel"/>, never by
/// the rows. Rows and tree nodes are rebuilt constantly — a different folder, a different
/// sort, another search — and anything stored in them dies with them. Keying on the entry
/// index also means the basket needs no paths: the index can still answer for the name, the
/// size and the full path of everything in it.
/// </para>
/// </summary>
public interface ISelectionSink
{
    /// <summary>Adds or removes an entry from the selection.</summary>
    void SetChecked(int entryIndex, bool isChecked);

    /// <summary>Whether this entry is currently in the selection.</summary>
    bool IsChecked(int entryIndex);
}
