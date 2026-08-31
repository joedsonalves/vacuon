using System.Runtime.Versioning;
using Vacuon.Native.Interop;
using Vacuon.Native.Ntfs;

namespace Vacuon.Core.Monitoring;

/// <summary>What happened to one folder in a slice of time.</summary>
public sealed record FolderActivity(
    string Folder,
    int Created,
    int Deleted,
    int Modified,
    long BytesAdded)
{
    public int Total => Created + Deleted + Modified;
}

/// <summary>One slice of what the volume did.</summary>
/// <param name="JournalGap">
/// The journal discarded records before they could be read — the volume changed faster than
/// the log holds. When this is set the folder breakdown is <b>incomplete</b>, and an empty
/// one means unknown rather than quiet. Callers must say so: those two are identical on
/// screen and only one of them is something the app measured.
/// </param>
public sealed record ActivitySnapshot(
    IReadOnlyList<FolderActivity> Folders,
    int RecordsRead,
    long FreeBytes,
    long FreeBytesDelta,
    TimeSpan Elapsed,
    bool JournalGap = false)
{
    public long BytesAdded
    {
        get
        {
            long total = 0;
            foreach (FolderActivity folder in Folders) total += folder.BytesAdded;
            return total;
        }
    }
}

/// <summary>
/// Watches the NTFS change journal and reports what is being written, as it happens.
/// <para>
/// This exists for one question: "my disk loses a gigabyte an hour and I have no idea
/// where it goes". Polling the journal answers it directly — every create, delete and
/// write on the volume passes through, so the folder that is growing shows itself within
/// seconds instead of after a full rescan.
/// </para>
/// <para>
/// <b>It cannot tell you which program is responsible, and the PRD's promise that it would
/// was wrong.</b> A USN record carries the file, its parent, the reason and the attributes —
/// there is no process id in it, because the journal is a file-system log, not an audit
/// trail. Naming a culprit would mean guessing from the path, and "this folder belongs to
/// Chrome, so Chrome did it" is exactly the kind of plausible invention this app does not
/// make. Getting the process really requires ETW's file-I/O provider, which is a different
/// piece of work.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskMonitor : IDisposable
{
    private readonly VolumeDevice _device;
    private readonly UsnJournal _journal;
    private readonly ulong _journalId;
    private readonly string _root;

    private long _lastUsn;
    private long _lastFree;

    private DiskMonitor(char driveLetter, VolumeDevice device, UsnJournal journal,
                        ulong journalId, long startUsn)
    {
        _device = device;
        _journal = journal;
        _journalId = journalId;
        _lastUsn = startUsn;
        _root = driveLetter + ":\\";
        _lastFree = FreeSpace();
    }

    /// <summary>
    /// Starts watching, from now forward. Null when the volume has no journal, or when the
    /// process is not elevated enough to open the volume.
    /// <para>
    /// Deliberately starts at the journal's current end rather than its beginning: the
    /// question is what is happening <em>now</em>, and replaying hours of history first
    /// would answer a different one.
    /// </para>
    /// </summary>
    public static DiskMonitor? Start(char driveLetter)
    {
        VolumeDevice? device = null;

        try
        {
            device = VolumeDevice.Open(driveLetter);
            var journal = new UsnJournal(device);

            UsnJournalData? data = journal.Query();
            if (data is null)
            {
                device.Dispose();
                return null;
            }

            return new DiskMonitor(driveLetter, device, journal,
                                   data.Value.UsnJournalID, data.Value.NextUsn);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or VolumeAccessException or ArgumentException)
        {
            // VolumeAccessException is what Open throws for a drive letter nothing is
            // mounted on, and it was missing here — so asking to watch Q: came back as a
            // stack trace instead of "cannot watch this". The caller's next line is a
            // message to a person; it must not be an exception.
            device?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Reads everything the volume did since the last call and groups it by folder.
    /// </summary>
    public ActivitySnapshot Poll(CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Parent reference number to what happened under it. The journal gives a parent id,
        // not a path, so paths are resolved once per folder at the end rather than per
        // record — a busy volume produces thousands of records for a handful of folders.
        var byParent = new Dictionary<ulong, (int Created, int Deleted, int Modified, List<string> Names)>();
        int records = 0;

        long next;

        try
        {
            // The same mask the index uses. Security and object-id changes never move a
            // byte, and asking for them would multiply the records to walk for nothing.
            next = _journal.Read(_journalId, _lastUsn, UsnReason.IndexRelevant, Handle, cancellationToken);
        }
        catch (UsnJournalWrappedException)
        {
            // The journal purged past our position — the volume was busier than the log is
            // large. Skipping ahead loses records, and saying so beats pretending the gap
            // was quiet. Which is what the flag is for: without it this return is an empty
            // folder list, byte for byte the same thing a quiet minute produces.
            UsnJournalData? data = _journal.Query();
            _lastUsn = data?.NextUsn ?? _lastUsn;

            // The free-space delta survives the gap — it is two reads subtracted, and owes
            // the journal nothing. What is lost is which folders account for it.
            long freeNow = FreeSpace();
            long deltaNow = freeNow - _lastFree;
            _lastFree = freeNow;

            return new ActivitySnapshot([], 0, freeNow, deltaNow, watch.Elapsed, JournalGap: true);
        }

        _lastUsn = next;

        void Handle(ref UsnRecord record)
        {
            if (!record.IsValid) return;
            if ((record.Attributes & NtfsFileAttributes.Directory) != 0) return;

            records++;

            byParent.TryGetValue(record.ParentFileReferenceNumber, out var counts);
            counts.Names ??= [];

            if ((record.Reason & UsnReason.FileCreate) != 0) counts.Created++;
            else if ((record.Reason & UsnReason.FileDelete) != 0) counts.Deleted++;
            else counts.Modified++;

            if (counts.Names.Count < 64) counts.Names.Add(record.FileName.ToString());

            byParent[record.ParentFileReferenceNumber] = counts;
        }

        long free = FreeSpace();
        long delta = free - _lastFree;
        _lastFree = free;

        var folders = new List<FolderActivity>(byParent.Count);

        foreach ((ulong parent, var counts) in byParent)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string folder = ResolveFolder(parent);
            long added = MeasureAdded(folder, counts.Names);

            folders.Add(new FolderActivity(folder, counts.Created, counts.Deleted,
                                           counts.Modified, added));
        }

        folders.Sort(static (a, b) =>
        {
            int bytes = b.BytesAdded.CompareTo(a.BytesAdded);
            return bytes != 0 ? bytes : b.Total.CompareTo(a.Total);
        });

        return new ActivitySnapshot(folders, records, free, delta, watch.Elapsed);
    }

    /// <summary>
    /// Turns a parent file reference into a path, using the same handle-by-id call the
    /// quarantine uses in reverse. Falls back to the reference number when the folder has
    /// already been deleted, which happens constantly on a busy volume.
    /// </summary>
    private string ResolveFolder(ulong reference)
    {
        string? path = FileIdentity.PathFromFileId(_root, reference);
        return path ?? $"{_root}?{reference:X}";
    }

    /// <summary>
    /// Adds up what the named files now occupy.
    /// <para>
    /// The journal says a file changed, never by how much — so the only honest figure is
    /// the size the file has right now. A file created and deleted within one interval
    /// measures zero, which is correct: it is not what filled the disk.
    /// </para>
    /// </summary>
    private static long MeasureAdded(string folder, List<string> names)
    {
        // Unresolved folders are marked with a '?' by ResolveFolder; a real path has none.
        if (folder.Length == 0 || folder.Contains('?')) return 0;

        long total = 0;

        foreach (string name in names)
        {
            try
            {
                var info = new FileInfo(Path.Combine(folder, name));
                if (info.Exists) total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or PathTooLongException)
            {
            }
        }

        return total;
    }

    private long FreeSpace()
    {
        try { return new DriveInfo(_root).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    public void Dispose() => _device.Dispose();
}
