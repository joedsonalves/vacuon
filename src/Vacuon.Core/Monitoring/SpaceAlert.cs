namespace Vacuon.Core.Monitoring;

/// <summary>A crossing worth telling someone about.</summary>
public sealed record SpaceAlert(char DriveLetter, long FreeBytes, long TotalBytes, long ThresholdBytes)
{
    public long Shortfall => Math.Max(0, ThresholdBytes - FreeBytes);
}

/// <summary>
/// Decides when a volume crossing its threshold is worth interrupting someone for.
/// <para>
/// The hard part of a notification is not showing it — it is <b>not</b> showing it. A check
/// that fires whenever free space sits below the line produces one notification per poll,
/// which is how a warning becomes something people turn off. So this reports the
/// <b>crossing</b>, once, and stays quiet until the volume has recovered.
/// </para>
/// <para>
/// Recovery needs more than climbing back over the line by a byte. A disk hovering at the
/// threshold crosses it repeatedly as ordinary work creates and deletes files, and each
/// crossing would be a fresh interruption. It has to clear the threshold by a margin before
/// the alert re-arms, so the same disk cannot ring twice for the same trouble.
/// </para>
/// </summary>
public sealed class SpaceAlerter
{
    /// <summary>
    /// How far above the threshold a volume must climb before it can alert again, as a
    /// fraction of the threshold. Ten percent of ten gigabytes is a gigabyte — comfortably
    /// more than the churn that makes a disk cross its own line while nothing is wrong.
    /// </summary>
    public const double RearmMargin = 0.10;

    private readonly Dictionary<char, bool> _alerted = [];

    /// <summary>Volumes currently in the alerted state, for tests and for diagnostics.</summary>
    public IReadOnlyCollection<char> Alerted
    {
        get
        {
            var live = new List<char>();
            foreach ((char drive, bool alerted) in _alerted)
                if (alerted) live.Add(drive);
            return live;
        }
    }

    /// <summary>
    /// Feeds one reading in and gets back an alert only on the crossing.
    /// </summary>
    /// <returns>The alert to show, or null — which is the answer nearly every time.</returns>
    public SpaceAlert? Consider(SpaceReading reading, long thresholdBytes)
    {
        char drive = char.ToUpperInvariant(reading.DriveLetter);
        bool alerted = _alerted.TryGetValue(drive, out bool a) && a;

        if (reading.FreeBytes < thresholdBytes)
        {
            if (alerted) return null;   // already said so

            _alerted[drive] = true;
            return new SpaceAlert(drive, reading.FreeBytes, reading.TotalBytes, thresholdBytes);
        }

        // Back above the line, but only clear of it by the margin does the alert re-arm.
        if (alerted && reading.FreeBytes >= thresholdBytes * (1 + RearmMargin))
            _alerted[drive] = false;

        return null;
    }

    /// <summary>Forgets a volume's state, so the next reading below the line alerts again.</summary>
    public void Rearm(char driveLetter) => _alerted[char.ToUpperInvariant(driveLetter)] = false;
}
