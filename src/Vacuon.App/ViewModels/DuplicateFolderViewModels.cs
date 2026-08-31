using Vacuon.App.Infra;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Localization;

namespace Vacuon.App.ViewModels;

/// <summary>
/// One redundant copy of a folder, with the tick that decides whether it goes.
/// <para>
/// The keeper of a group has no view model of this kind at all, exactly as with files: the
/// screen cannot express removing every copy of something, rather than merely discouraging
/// it.
/// </para>
/// </summary>
public sealed class DuplicateFolderCopyViewModel(DuplicateFolder folder, Action changed) : Observable
{
    public DuplicateFolder Folder { get; } = folder;

    public string Path => Folder.Path;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value)) return;
            changed();
        }
    }

    public long RecoverableBytes => Folder.Bytes;
}

/// <summary>One set of identical folders, as the screen shows it.</summary>
public sealed class DuplicateFolderGroupViewModel
{
    public DuplicateFolderGroupViewModel(DuplicateFolderGroup group, Action changed)
    {
        Group = group;

        var copies = new List<DuplicateFolderCopyViewModel>(group.Redundant.Count);
        foreach (DuplicateFolder folder in group.Redundant)
            copies.Add(new DuplicateFolderCopyViewModel(folder, changed));

        Copies = copies;
    }

    public DuplicateFolderGroup Group { get; }

    public IReadOnlyList<DuplicateFolderCopyViewModel> Copies { get; }

    public string KeeperPath => Group.Keeper.Path;
    public string KeeperLabel => L.T("dup.keeperLabel");
    public string SelectGroupLabel => L.T("dup.selectGroup");

    public string HeaderText => L.T("dup.folderHeader",
                                    Format.Count(Group.CopyCount),
                                    Format.Bytes(Group.Bytes),
                                    Format.Count(Group.FileCount));

    public void SelectAll()
    {
        foreach (DuplicateFolderCopyViewModel copy in Copies) copy.IsChecked = true;
    }
}
