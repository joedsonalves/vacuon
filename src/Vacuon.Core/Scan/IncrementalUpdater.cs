using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Native.Interop;
using Vacuon.Native.Ntfs;

namespace Vacuon.Core.Scan;

/// <summary>Why an incremental update could not be used.</summary>
public enum IncrementalRefusal
{
    None,
    /// <summary>No snapshot on disk for this volume.</summary>
    NoSnapshot,
    /// <summary>The snapshot predates the current schema, or is corrupt.</summary>
    UnusableSnapshot,
    /// <summary>The volume has no active change journal.</summary>
    NoJournal,
    /// <summary>The journal was deleted and recreated — its ids no longer line up.</summary>
    JournalReplaced,
    /// <summary>The journal wrapped and discarded the records we needed.</summary>
    JournalWrapped,
    /// <summary>Reading the journal needs an elevated handle on the volume.</summary>
    NeedsElevation,
}

public sealed record IncrementalResult(
    VolumeIndex? Index,
    IncrementalRefusal Refusal,
    int ChangesApplied,
    JournalMark Journal,
    DateTime SnapshotTakenAtUtc)
{
    public bool Succeeded => Index is not null && Refusal == IncrementalRefusal.None;
}

/// <summary>
/// Brings a saved index up to date from the NTFS change journal instead of rescanning.
/// <para>
/// The journal reports <b>what</b> changed but never <b>how big</b> anything became, so
/// created and modified files still need one size lookup each. That is a fine trade: on
/// an idle machine the delta is a handful of records, against a traversal of millions.
/// </para>
/// <para>
/// Every refusal path here is deliberate. An index that is quietly wrong is worse than
/// no index at all, so anything that casts doubt on the delta — a replaced journal, a
/// wrapped journal, a schema change — falls back to a full scan and says so.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IncrementalUpdater(string? snapshotDirectory = null)
{
    private readonly string? _directory = snapshotDirectory;

    /// <summary>Attempts to produce a current index without scanning.</summary>
    public IncrementalResult TryUpdate(char driveLetter, CancellationToken cancellationToken = default)
    {
        VolumeDevice device;

        try
        {
            device = VolumeDevice.Open(driveLetter);
        }
        catch (VolumeAccessException ex)
        {
            return Refuse(ex.Failure == VolumeAccessFailure.NeedsElevation
                ? IncrementalRefusal.NeedsElevation
                : IncrementalRefusal.NoJournal);
        }

        using (device)
        {
            string path = IndexSnapshot.PathFor(device.SerialNumber, _directory);

            LoadedSnapshot? snapshot = IndexSnapshot.Load(path, device.SerialNumber);
            if (snapshot is null)
                return Refuse(File.Exists(path)
                    ? IncrementalRefusal.UnusableSnapshot
                    : IncrementalRefusal.NoSnapshot);

            var journal = new UsnJournal(device);
            UsnJournalData? data = journal.Query();

            if (data is null) return Refuse(IncrementalRefusal.NoJournal, snapshot);

            UsnJournalData current = data.Value;

            // A different journal id means the journal was deleted and recreated; USNs
            // from the old one mean nothing in the new numbering.
            if (current.UsnJournalID != snapshot.Journal.JournalId)
                return Refuse(IncrementalRefusal.JournalReplaced, snapshot);

            // Our mark fell off the back of the journal: the records between then and now
            // are gone, so there is no way to know what changed.
            if (snapshot.Journal.LastUsn < current.FirstUsn)
                return Refuse(IncrementalRefusal.JournalWrapped, snapshot);

            VolumeIndex index = snapshot.Index;
            var applier = new DeltaApplier(index);

            long next;
            try
            {
                next = journal.Read(current.UsnJournalID, snapshot.Journal.LastUsn,
                                    UsnReason.IndexRelevant, applier.Apply, cancellationToken);
            }
            catch (UsnJournalWrappedException)
            {
                return Refuse(IncrementalRefusal.JournalWrapped, snapshot);
            }

            applier.Finish();

            // Free space moves without any journal record, so re-read it rather than
            // reporting the number from whenever the snapshot was taken.
            VolumeInfo refreshed = VolumeProbe.Describe(driveLetter, device);
            VolumeIndex updated = index.WithVolume(refreshed);

            var mark = new JournalMark(current.UsnJournalID, next);
            IndexSnapshot.Save(updated, mark, path);

            return new IncrementalResult(updated, IncrementalRefusal.None,
                                         applier.ChangeCount, mark, snapshot.TakenAtUtc);
        }
    }

    /// <summary>Saves the index and the journal position it corresponds to.</summary>
    public void SaveSnapshot(VolumeIndex index, char driveLetter)
    {
        try
        {
            using VolumeDevice device = VolumeDevice.Open(driveLetter);

            UsnJournalData? data = new UsnJournal(device).Query();

            // NextUsn is the position a future read must start from. Storing anything
            // else would either replay records or skip them.
            var mark = data is null
                ? JournalMark.None
                : new JournalMark(data.Value.UsnJournalID, data.Value.NextUsn);

            IndexSnapshot.Save(index, mark, IndexSnapshot.PathFor(device.SerialNumber, _directory));
        }
        catch (VolumeAccessException)
        {
            // Without elevation there is no journal position to record, and a snapshot
            // with no mark could never be updated incrementally — so skip it entirely
            // rather than leave a file that only ever forces a rescan.
        }
    }

    private static IncrementalResult Refuse(IncrementalRefusal reason, LoadedSnapshot? snapshot = null) =>
        new(null, reason, 0, snapshot?.Journal ?? JournalMark.None,
            snapshot?.TakenAtUtc ?? DateTime.MinValue);
}

/// <summary>
/// Applies journal records to an index in place.
/// <para>
/// Records arrive in USN order, so the last word on any given file wins naturally —
/// create-then-delete leaves the entry gone, rename-then-extend leaves the new name and
/// the new size.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DeltaApplier(VolumeIndex index)
{
    private readonly HashSet<int> _needsSize = [];
    private readonly Dictionary<int, long> _ads = new(index.AdsBytes);

    public int ChangeCount { get; private set; }

    public void Apply(ref UsnRecord record)
    {
        int entry = (int)record.RecordNumber;
        if (entry < 0 || entry >= index.Entries.Length) return;

        ChangeCount++;

        ref FileEntry target = ref index.Entries[entry];

        // ---- deletion ----
        if ((record.Reason & UsnReason.FileDelete) != 0)
        {
            // NameLength 0 is what marks a slot free everywhere else in the index, so
            // clearing the whole entry is both the deletion and the free mark.
            target = default;
            _ads.Remove(entry);
            _needsSize.Remove(entry);
            return;
        }

        // ---- creation and rename ----
        // RenameOldName is skipped: the matching RenameNewName record carries the name
        // we want, and acting on both would write the old name and then overwrite it.
        bool namesThis = (record.Reason & (UsnReason.FileCreate | UsnReason.RenameNewName)) != 0;

        if (namesThis && !record.FileName.IsEmpty)
        {
            target.RecordNumber = record.RecordNumber;
            target.ParentIndex = record.ParentRecordNumber;
            target.NameOffset = index.Names.Append(record.FileName);
            target.NameLength = (ushort)record.FileName.Length;
            target.Flags = Translate(record.Attributes);
            target.HardLinkCount = 1;

            _needsSize.Add(entry);
        }

        // An entry we have never seen and that no record names cannot be placed in the
        // tree, so leave it out rather than inventing a parent.
        if (target.NameLength == 0) return;

        // ---- attribute changes ----
        if ((record.Reason & (UsnReason.CompressionChange | UsnReason.ReparsePointChange
                            | UsnReason.HardLinkChange)) != 0)
        {
            target.Flags = Translate(record.Attributes) | (target.Flags & EntryFlags.Suspicious);
        }

        // ---- size changes ----
        if ((record.Reason & (UsnReason.DataExtend | UsnReason.DataOverwrite
                            | UsnReason.DataTruncation
                            | UsnReason.NamedDataExtend | UsnReason.NamedDataOverwrite
                            | UsnReason.NamedDataTruncation)) != 0)
        {
            _needsSize.Add(entry);
        }
    }

    /// <summary>
    /// Resolves the sizes the journal did not carry.
    /// <para>
    /// One stat per changed file. Deferring to the end means a file written a hundred
    /// times in the delta is measured once, at its final size.
    /// </para>
    /// </summary>
    public void Finish()
    {
        foreach (int entry in _needsSize)
        {
            if (entry >= index.Entries.Length) continue;

            ref FileEntry target = ref index.Entries[entry];
            if (target.NameLength == 0 || target.IsDirectory) continue;

            string path = index.GetFullPath(entry);
            if (path.Length == 0) continue;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    // Gone between the journal read and now. Dropping it keeps the index
                    // closer to the truth than leaving a stale entry behind.
                    target = default;
                    _ads.Remove(entry);
                    continue;
                }

                target.LogicalSize = info.Length;

                // Without the MFT there is no AllocatedSize, so round to the cluster —
                // and that estimate is exactly why the UI labels the column "not
                // measured" outside an MFT scan.
                uint cluster = index.Volume.BytesPerCluster;
                target.AllocatedSize = cluster == 0
                    ? info.Length
                    : (info.Length + cluster - 1) / cluster * cluster;

                target.LastWriteUtc = SafeFileTime(info.LastWriteTimeUtc);
                target.CreatedUtc = SafeFileTime(info.CreationTimeUtc);
                target.LastAccessUtc = SafeFileTime(info.LastAccessTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
            {
                // Unreadable now: keep whatever the snapshot had rather than zeroing a
                // size and making the totals lie.
            }
        }

        index.ReplaceAdsTable(_ads);
        index.InvalidateAggregates();
    }

    private static long SafeFileTime(DateTime value)
    {
        try { return value.ToFileTimeUtc(); }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    private static EntryFlags Translate(NtfsFileAttributes a)
    {
        EntryFlags f = EntryFlags.None;
        if ((a & NtfsFileAttributes.Directory) != 0) f |= EntryFlags.Directory;
        if ((a & NtfsFileAttributes.Hidden) != 0) f |= EntryFlags.Hidden;
        if ((a & NtfsFileAttributes.System) != 0) f |= EntryFlags.System;
        if ((a & NtfsFileAttributes.Compressed) != 0) f |= EntryFlags.Compressed;
        if ((a & NtfsFileAttributes.SparseFile) != 0) f |= EntryFlags.Sparse;
        if ((a & NtfsFileAttributes.Encrypted) != 0) f |= EntryFlags.Encrypted;
        if ((a & NtfsFileAttributes.ReparsePoint) != 0) f |= EntryFlags.ReparsePoint;
        if ((a & (NtfsFileAttributes.RecallOnDataAccess | NtfsFileAttributes.RecallOnOpen)) != 0)
            f |= EntryFlags.CloudPlaceholder;
        return f;
    }
}
