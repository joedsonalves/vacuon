using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Monitoring;

namespace Vacuon.App.ViewModels;

/// <summary>One folder's activity in the last interval.</summary>
public sealed class FolderActivityViewModel(FolderActivity activity) : Observable
{
    public FolderActivity Activity { get; } = activity;

    public string Folder => Activity.Folder;

    public string CountsText =>
        L.T("watch.counts", Format.Count(Activity.Created),
            Format.Count(Activity.Deleted), Format.Count(Activity.Modified));

    /// <summary>
    /// How much the folder grew, measured from the files themselves.
    /// <para>
    /// The journal says <i>that</i> a file changed, never by how much, so this is the size of
    /// what is there now. A file created and deleted inside the same interval measures zero —
    /// which is correct, and worth knowing when a folder shows heavy traffic and no growth.
    /// </para>
    /// </summary>
    public string BytesText => Activity.BytesAdded == 0
        ? "—"
        : Format.Bytes(Activity.BytesAdded);

    public int Total => Activity.Total;
}
