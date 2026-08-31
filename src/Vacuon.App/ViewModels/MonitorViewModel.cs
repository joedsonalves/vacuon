using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Monitoring;

namespace Vacuon.App.ViewModels;

/// <summary>
/// The live view of what a volume is doing right now.
/// <para>
/// It answers one question the rest of the app cannot: "my disk loses a gigabyte an hour and
/// I have no idea where it goes". Every scan in Vacuon is a photograph; this is the only
/// screen that watches.
/// </para>
/// <para>
/// <b>It cannot name the program responsible, and does not guess.</b> A USN record carries
/// the file, its parent, the reason and the attributes — there is no process id in it,
/// because the journal is a file-system log and not an audit trail. The footer says so on
/// every run rather than leaving people to assume the blank column is a bug.
/// </para>
/// </summary>
public sealed class MonitorViewModel : Observable, IDisposable
{
    private readonly DispatcherTimer _timer;
    private DiskMonitor? _monitor;
    private bool _disposed;

    public MonitorViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };

        _timer.Tick += (_, _) => Tick();
    }

    public ObservableCollection<FolderActivityViewModel> Folders { get; } = [];

    private bool _isWatching;
    public bool IsWatching
    {
        get => _isWatching;
        private set { Set(ref _isWatching, value); Raise(nameof(IsStopped)); }
    }

    public bool IsStopped => !_isWatching;

    private string _status = string.Empty;
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _headline = string.Empty;
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    private bool _hasGap;

    /// <summary>
    /// Whether the last interval lost records.
    /// <para>
    /// Kept apart from <see cref="Status"/> so the screen can colour it as a warning. An
    /// empty list from a gap looks exactly like an empty list from a quiet minute, and only
    /// one of the two is something the app actually observed.
    /// </para>
    /// </summary>
    public bool HasGap { get => _hasGap; private set => Set(ref _hasGap, value); }

    /// <summary>Starts watching a volume. Returns whether it could.</summary>
    public bool Start(char driveLetter)
    {
        Stop();

        _monitor = DiskMonitor.Start(driveLetter);

        if (_monitor is null)
        {
            Status = ElevationService.IsElevated
                ? L.T("watch.noJournal")
                : L.T("watch.needsElevation");

            return false;
        }

        Folders.Clear();
        HasGap = false;
        Headline = string.Empty;
        Status = L.T("watch.watching", driveLetter + ":");

        // The first poll returns everything since the monitor opened, which is nothing —
        // the journal position starts at the end on purpose. The question is what happens
        // from now on, not what the volume did before anyone asked.
        IsWatching = true;
        _timer.Start();

        return true;
    }

    public void Stop()
    {
        _timer.Stop();

        _monitor?.Dispose();
        _monitor = null;

        if (IsWatching) Status = L.T("watch.stopping");
        IsWatching = false;
    }

    private void Tick()
    {
        if (_monitor is null || _disposed) return;

        ActivitySnapshot snapshot;

        try
        {
            snapshot = _monitor.Poll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            Stop();
            return;
        }

        HasGap = snapshot.JournalGap;

        if (snapshot.JournalGap)
        {
            // Deliberately leaves the previous list on screen rather than clearing it: an
            // empty table under a warning reads as "nothing happened", which is the one
            // thing the warning exists to deny.
            Headline = L.T("watch.gap");
            return;
        }

        Folders.Clear();

        foreach (FolderActivity folder in snapshot.Folders)
            Folders.Add(new FolderActivityViewModel(folder));

        if (snapshot.RecordsRead == 0)
        {
            Headline = L.T("watch.quiet");
            return;
        }

        // Signed on purpose: a volume that gained space is as interesting as one losing it.
        string delta = snapshot.FreeBytesDelta == 0
            ? "±0"
            : (snapshot.FreeBytesDelta > 0 ? "+" : "−") + Format.Bytes(Math.Abs(snapshot.FreeBytesDelta));

        Headline = L.T("watch.header",
                       Format.Count(snapshot.RecordsRead),
                       Format.Count(snapshot.Folders.Count),
                       Format.Bytes(snapshot.FreeBytes),
                       delta);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}
