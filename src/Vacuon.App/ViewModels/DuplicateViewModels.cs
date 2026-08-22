using Vacuon.App.Infra;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Localization;

namespace Vacuon.App.ViewModels;

/// <summary>
/// One redundant copy, with the tick that decides whether it gets set aside.
/// <para>
/// Only redundant copies get one of these. The keeper is shown by the group and has no
/// checkbox at all — not a disabled one, none — so "select everything on screen" cannot
/// reach it however hard anyone clicks.
/// </para>
/// </summary>
public sealed class DuplicateCopyViewModel(DuplicateFile file, Action changed) : Observable
{
    public DuplicateFile File { get; } = file;

    public string Path => File.Path;
    public bool IsHardLinked => File.IsHardLinked;

    /// <summary>Why this copy would free nothing, or empty when it would.</summary>
    public string HardLinkNote => File.IsHardLinked ? L.T("dup.hardlinked") : string.Empty;

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

    /// <summary>Bytes this copy would really return — zero when it is a hardlink.</summary>
    public long RecoverableBytes => File.IsHardLinked ? 0 : File.BytesOnDisk;
}

/// <summary>One group of identical files, as the screen shows it.</summary>
public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(DuplicateGroup group, Action changed)
    {
        Group = group;

        var copies = new List<DuplicateCopyViewModel>(group.Redundant.Count);
        foreach (DuplicateFile file in group.Redundant)
            copies.Add(new DuplicateCopyViewModel(file, changed));

        Copies = copies;
    }

    public DuplicateGroup Group { get; }

    /// <summary>The redundant copies only. The keeper is deliberately not in here.</summary>
    public IReadOnlyList<DuplicateCopyViewModel> Copies { get; }

    public string KeeperPath => Group.Keeper.Path;
    public string KeeperLabel => L.T("dup.keeperLabel");

    public string HeaderText => L.T("dup.groupHeader",
                                    Format.Count(Group.CopyCount),
                                    Format.Bytes(Group.Bytes),
                                    Format.Bytes(Group.RecoverableBytes));

    /// <summary>Shown when every copy in the group is a hardlink and nothing would be freed.</summary>
    public string NothingRecoverableNote =>
        Group.RecoverableBytes == 0 ? L.T("dup.nothingRecoverable") : string.Empty;
}
