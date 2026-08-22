using System.Windows.Media.Imaging;
using Vacuon.App.Infra;
using Vacuon.App.Services;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Localization;
using Vacuon.Core.Preview;

namespace Vacuon.App.ViewModels;

/// <summary>
/// One version of a picture on the similar-images screen.
/// <para>
/// Carries its own thumbnail, and that is not decoration: these files are genuinely
/// different, so the only way anyone can agree that they are the same picture is to see
/// them next to each other. A list of paths would be asking for trust the app has not
/// earned about a judgement that is, by nature, fuzzy.
/// </para>
/// </summary>
public sealed class SimilarVersionViewModel : Observable
{
    private readonly Action _changed;

    public SimilarVersionViewModel(SimilarImage image, bool isKeeper, Action changed)
    {
        Image = image;
        IsKeeper = isKeeper;
        _changed = changed;
    }

    public SimilarImage Image { get; }
    public bool IsKeeper { get; }

    public string Path => Image.Path;
    public string Name => System.IO.Path.GetFileName(Image.Path);

    public string SizeText => Image.ResolutionLabel is null
        ? Format.Bytes(Image.Bytes)
        : $"{Image.ResolutionLabel} · {Format.Bytes(Image.Bytes)}";

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    public async Task LoadThumbnailAsync(ThumbnailService service)
    {
        Thumbnail = await service.GetAsync(Path, ThumbnailSize.Large, preferContent: true,
                                           CancellationToken.None);
    }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // The keeper has no tick in the view at all; this guard is the second lock, so
            // that no code path can put the version being kept into a removal list.
            if (IsKeeper) value = false;
            if (!Set(ref _isChecked, value)) return;
            _changed();
        }
    }
}

/// <summary>A set of pictures that look the same.</summary>
public sealed class SimilarGroupViewModel
{
    public SimilarGroupViewModel(SimilarGroup group, Action changed)
    {
        Group = group;

        var versions = new List<SimilarVersionViewModel>(group.Images.Count)
        {
            new(group.Keeper, isKeeper: true, changed),
        };

        foreach (SimilarImage other in group.Others)
            versions.Add(new SimilarVersionViewModel(other, isKeeper: false, changed));

        Versions = versions;
    }

    public SimilarGroup Group { get; }

    /// <summary>Keeper first, then the rest. All of them are shown, because seeing is the point.</summary>
    public IReadOnlyList<SimilarVersionViewModel> Versions { get; }

    /// <summary>
    /// The group's own confidence, stated rather than hidden.
    /// <para>
    /// At zero bits these are the same image re-encoded. At nine they merely look alike, and
    /// whether that is close enough to delete something over is the user's call, not the
    /// algorithm's.
    /// </para>
    /// </summary>
    public string HeaderText
    {
        get
        {
            int spread = Group.Spread;

            return spread == 0
                ? L.T("similar.identical", Format.Count(Group.Images.Count),
                      Format.Bytes(Group.RecoverableBytes))
                : L.T("similar.groupHeader", Format.Count(Group.Images.Count),
                      Format.Bytes(Group.RecoverableBytes), Format.Count(spread));
        }
    }

    public async Task LoadThumbnailsAsync(ThumbnailService service)
    {
        foreach (SimilarVersionViewModel version in Versions)
            await version.LoadThumbnailAsync(service);
    }
}
