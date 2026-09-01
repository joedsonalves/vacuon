using Vacuon.App.Infra;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Localization;

namespace Vacuon.App.ViewModels;

/// <summary>One redundant copy of a recording, with the tick that decides whether it goes.</summary>
public sealed class AudioCopyViewModel(AudioTrack track, Action changed) : Observable
{
    public AudioTrack Track { get; } = track;

    public string Path => Track.Path;
    public string SizeText => Format.Bytes(Track.Bytes);

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

    public long RecoverableBytes => Track.Bytes;
}

/// <summary>One set of recordings that sound like the same track.</summary>
public sealed class AudioMatchGroupViewModel
{
    public AudioMatchGroupViewModel(AudioMatchGroup group, Action changed)
    {
        Group = group;

        var copies = new List<AudioCopyViewModel>(group.Redundant.Count);
        foreach (AudioTrack track in group.Redundant) copies.Add(new AudioCopyViewModel(track, changed));

        Copies = copies;
    }

    public AudioMatchGroup Group { get; }
    public IReadOnlyList<AudioCopyViewModel> Copies { get; }

    public string KeeperPath => Group.Keeper.Path;
    public string KeeperLabel => L.T("similar.keeping");

    /// <summary>
    /// The score, shown as it is.
    /// <para>
    /// ⚠️ Unrelated audio scores around 60% on this scale rather than 0%, so the number on
    /// screen is not a percentage of sameness in the way a reader would assume. It is shown
    /// because a group at 96% and a group at 81% deserve different amounts of suspicion, and
    /// the person is the one deciding.
    /// </para>
    /// </summary>
    public string HeaderText => L.T("similar.audioHeader",
                                    Format.Count(Group.CopyCount),
                                    Group.Similarity.ToString("P0", L.Culture),
                                    Format.Bytes(Group.RecoverableBytes));

    public void SelectAll()
    {
        foreach (AudioCopyViewModel copy in Copies) copy.IsChecked = true;
    }
}
