using Vacuon.App.Infra;
using Vacuon.Core.Actions;
using Vacuon.Core.Localization;

namespace Vacuon.App.ViewModels;

/// <summary>
/// One batch as the quarantine screen shows it.
/// <para>
/// Every figure here comes from <see cref="QuarantineService.Held"/>, never from the
/// manifest total. The manifest says what the batch set out to hold; after a partial
/// restore those are different numbers, and only one of them is still true.
/// </para>
/// </summary>
public sealed class QuarantineBatchViewModel(QuarantineBatch batch, long heldBytes, int heldCount)
{
    public QuarantineBatch Batch { get; } = batch;

    public string BatchId => Batch.BatchId;
    public string Volume => Batch.Volume;

    public long HeldBytes { get; } = heldBytes;
    public int HeldCount { get; } = heldCount;

    public string CountText => HeldCount == 1
        ? L.T("quarantine.itemCountOne")
        : L.T("quarantine.itemCount", Format.Count(HeldCount));

    public string HeldText => L.T("quarantine.held", Format.Bytes(HeldBytes));

    public string AgeText
    {
        get
        {
            int days = (int)(DateTime.UtcNow - Batch.CreatedUtc).TotalDays;
            return days <= 0 ? L.T("quarantine.ageToday") : L.T("quarantine.ageDays", Format.Count(days));
        }
    }

    /// <summary>First few original paths, so a batch is recognisable without opening it.</summary>
    public string PreviewText
    {
        get
        {
            var names = new List<string>(3);

            foreach (QuarantineItem item in Batch.Items)
            {
                if (names.Count == 3) break;
                names.Add(System.IO.Path.GetFileName(item.OriginalPath.TrimEnd('\\')));
            }

            string joined = string.Join(" · ", names);
            return Batch.Items.Count > names.Count ? joined + " …" : joined;
        }
    }
}
