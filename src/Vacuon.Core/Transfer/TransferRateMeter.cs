namespace Vacuon.Core.Transfer;

/// <summary>
/// Turns a stream of "this much, by now" readings into a rate and an estimate.
/// <para>
/// A sliding window, not an average since the start. Ten thousand small files and then one
/// large one are different speeds, and an average over the whole run keeps quoting the first
/// one long after it stopped being true — which is why the Windows dialog's own estimate has
/// the reputation it has.
/// </para>
/// <para>
/// Everything that could invent a figure returns null instead. One reading is not a rate,
/// and a rate of zero cannot be divided into a remaining time.
/// </para>
/// </summary>
public sealed class TransferRateMeter
{
    private readonly record struct Sample(TimeSpan At, long Bytes, int Files);

    private readonly TimeSpan _window;
    private readonly Queue<Sample> _samples = new();
    private Sample _last;

    public TransferRateMeter(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    public TransferRateMeter() : this(TimeSpan.FromSeconds(5)) { }

    /// <summary>Records cumulative totals as of <paramref name="at"/>.</summary>
    public void Record(TimeSpan at, long bytes, int files)
    {
        _last = new Sample(at, bytes, files);
        _samples.Enqueue(_last);

        // Two readings are the minimum a difference can be taken between, so the window is
        // allowed to overrun rather than empty itself.
        while (_samples.Count > 2 && at - _samples.Peek().At > _window) _samples.Dequeue();
    }

    private bool TryWindow(out TimeSpan span, out long bytes, out int files)
    {
        span = default; bytes = 0; files = 0;
        if (_samples.Count < 2) return false;

        Sample first = _samples.Peek();
        span = _last.At - first.At;
        if (span <= TimeSpan.Zero) return false;

        bytes = _last.Bytes - first.Bytes;
        files = _last.Files - first.Files;
        return true;
    }

    /// <summary>Bytes per second across the window; 0 while there is nothing to divide.</summary>
    public double BytesPerSecond =>
        TryWindow(out TimeSpan span, out long bytes, out _) ? Math.Max(0, bytes / span.TotalSeconds) : 0;

    public double FilesPerSecond =>
        TryWindow(out TimeSpan span, out _, out int files) ? Math.Max(0, files / span.TotalSeconds) : 0;

    /// <summary>
    /// How long the remaining bytes should take at the current rate, or null when that
    /// cannot be worked out: no window yet, or a rate that has fallen to zero.
    /// </summary>
    public TimeSpan? Estimate(long bytesRemaining)
    {
        if (bytesRemaining <= 0) return TimeSpan.Zero;

        double rate = BytesPerSecond;
        if (rate <= 0) return null;

        double seconds = bytesRemaining / rate;

        // A day of "remaining" is not an estimate, it is a rate that has collapsed. Saying
        // nothing beats printing a number nobody would act on.
        return seconds > 86_400 ? null : TimeSpan.FromSeconds(seconds);
    }
}
