using System.Diagnostics;

namespace Vacuon.Core.Scan;

/// <summary>
/// A scan that can be held and let go again (PRD F1.4).
/// <para>
/// Cancelling a scan of two and a half million records throws away eleven seconds of disk;
/// pausing keeps them. The case it exists for is the ordinary one — something else needs the
/// drive right now, or the machine has to be left alone for a minute — and the alternative
/// today is to cancel and start over.
/// </para>
/// <para>
/// ⚠️ <b>The clock stops with it.</b> The scanner reports records per second and megabytes
/// per second, and those are measured against elapsed time. A scan paused for five minutes
/// and resumed would report a rate an order of magnitude below what the disk actually did —
/// a number nobody measured, arrived at by dividing real work by imaginary time. Paused time
/// is subtracted, so the rate stays a rate.
/// </para>
/// </summary>
public sealed class ScanPause
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly Stopwatch _held = new();
    private readonly Lock _lock = new();

    /// <summary>True while the scan is being held.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>How long the scan has spent held, in total, across every pause.</summary>
    public TimeSpan HeldFor
    {
        get { lock (_lock) return _held.Elapsed; }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused) return;

            IsPaused = true;
            _held.Start();
            _gate.Reset();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!IsPaused) return;

            IsPaused = false;
            _held.Stop();
            _gate.Set();
        }
    }

    /// <summary>Paused becomes running and running becomes paused.</summary>
    public void Toggle()
    {
        if (IsPaused) Resume();
        else Pause();
    }

    /// <summary>
    /// Blocks while the scan is held. Returns at once when it is not, which is the case
    /// every time but a handful, so this has to cost nothing — and an already-set event
    /// costs nothing.
    /// </summary>
    /// <remarks>
    /// Cancelling while paused works: the wait is cancellable, so Stop does not have to
    /// wait for somebody to press Resume first.
    /// </remarks>
    public void WaitIfPaused(CancellationToken cancellationToken) => _gate.Wait(cancellationToken);
}
