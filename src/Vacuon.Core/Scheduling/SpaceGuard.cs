using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;

namespace Vacuon.Core.Scheduling;

/// <summary>What a volume looks like against its threshold.</summary>
public sealed record GuardReading(
    char DriveLetter,
    long FreeBytes,
    long TotalBytes,
    long ThresholdBytes)
{
    public bool BelowThreshold => FreeBytes < ThresholdBytes;

    public double FreePercent => TotalBytes == 0 ? 0 : FreeBytes * 100.0 / TotalBytes;

    /// <summary>How much would have to come back to clear the threshold.</summary>
    public long Shortfall => BelowThreshold ? ThresholdBytes - FreeBytes : 0;
}

public sealed record GuardReport(IReadOnlyList<GuardReading> Volumes)
{
    public IReadOnlyList<GuardReading> Breached
    {
        get
        {
            var below = new List<GuardReading>();
            foreach (GuardReading volume in Volumes)
                if (volume.BelowThreshold) below.Add(volume);
            return below;
        }
    }

    public bool AnyBreached => Breached.Count > 0;
}

/// <summary>
/// Checks free space against a threshold.
/// <para>
/// Read-only and deliberately dumb: it measures and reports, and does not decide what to do
/// about it. That separation is the point — a guard that also cleaned would be a scheduled
/// task that deletes when a number moves, which is a great deal of trust to place in a
/// threshold somebody typed once.
/// </para>
/// <para>
/// What it is for is the exit code. Run it from a schedule and it answers, in the only
/// language a scheduler understands, whether the disk is in trouble — leaving the response
/// to a second, deliberate step.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SpaceGuard
{
    /// <summary>
    /// Reads every fixed volume, or only the one named.
    /// </summary>
    public static GuardReport Check(long thresholdBytes, char? driveLetter = null)
    {
        var readings = new List<GuardReading>();

        foreach (VolumeInfo volume in VolumeProbe.EnumerateFixedVolumes())
        {
            if (driveLetter is not null &&
                char.ToUpperInvariant(volume.DriveLetter) != char.ToUpperInvariant(driveLetter.Value))
            {
                continue;
            }

            long free = FreeSpaceOf(volume.DriveLetter);

            readings.Add(new GuardReading(volume.DriveLetter, free, volume.TotalBytes, thresholdBytes));
        }

        return new GuardReport(readings);
    }

    /// <summary>
    /// Free space read now, not taken from the scan.
    /// <para>
    /// A guard exists to notice a change, so reading a cached figure would defeat it — the
    /// snapshot could be hours old and the whole point is what the disk looks like at this
    /// moment.
    /// </para>
    /// </summary>
    private static long FreeSpaceOf(char driveLetter)
    {
        try { return new DriveInfo(driveLetter + ":\\").AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}
