using System.Globalization;
using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;

namespace Vacuon.Core.Monitoring;

/// <summary>
/// The measured free-space readings a trend is built from.
/// <para>
/// The projection in the dashboard is the only forward-looking number the app shows, and this
/// is where it earns the right to exist: it comes from readings taken on this machine and
/// written down, not from a rate invented at the moment of asking. On a fresh install there
/// is no history, so there is no projection, and the widget says so rather than filling the
/// space with something plausible.
/// </para>
/// <para>
/// Stored as one line per reading — appending is a single write with nothing to corrupt, and
/// a file a person can open in Notepad and check the app against. A malformed line is skipped
/// rather than taken as a reason to throw the history away.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SpaceHistory
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacuon", "space-history.tsv");

    /// <summary>
    /// Readings closer together than this are not recorded.
    /// <para>
    /// A trend measured in days gains nothing from a sample every second, and the app can be
    /// open for hours. Without a floor the file grows without bound and the fit ends up
    /// dominated by whichever hour the window happened to be open.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinimumSpacing = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Kept readings, per volume. At the spacing above this is several months of history,
    /// which is well past the horizon any projection is allowed to reach.
    /// </summary>
    public const int MaximumPerVolume = 4000;

    private readonly string _path;

    public SpaceHistory(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Every reading on file, oldest first, malformed lines skipped.</summary>
    public IReadOnlyList<SpaceReading> Read()
    {
        var readings = new List<SpaceReading>();

        string[] lines;

        try
        {
            if (!File.Exists(_path)) return readings;
            lines = File.ReadAllLines(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return readings;
        }

        foreach (string line in lines)
        {
            if (TryParse(line, out SpaceReading? reading)) readings.Add(reading!);
        }

        readings.Sort(static (a, b) => a.TakenAt.CompareTo(b.TakenAt));
        return readings;
    }

    /// <summary>Readings for one volume, oldest first.</summary>
    public IReadOnlyList<SpaceReading> Read(char driveLetter)
    {
        var mine = new List<SpaceReading>();

        foreach (SpaceReading reading in Read())
        {
            if (char.ToUpperInvariant(reading.DriveLetter) == char.ToUpperInvariant(driveLetter))
                mine.Add(reading);
        }

        return mine;
    }

    /// <summary>
    /// Reads every fixed volume now and appends what it finds, honouring the spacing floor.
    /// </summary>
    /// <returns>What was written; empty when the last readings are still too recent.</returns>
    public IReadOnlyList<SpaceReading> Record(DateTimeOffset? now = null)
    {
        DateTimeOffset moment = now ?? DateTimeOffset.Now;
        IReadOnlyList<SpaceReading> existing = Read();

        var fresh = new List<SpaceReading>();

        foreach (VolumeInfo volume in VolumeProbe.EnumerateFixedVolumes())
        {
            if (!TooSoon(existing, volume.DriveLetter, moment))
            {
                long free = FreeSpaceOf(volume.DriveLetter);
                if (free > 0)
                    fresh.Add(new SpaceReading(moment, volume.DriveLetter, free, volume.TotalBytes));
            }
        }

        if (fresh.Count > 0) Append(fresh, existing);

        return fresh;
    }

    /// <summary>Appends readings taken elsewhere. Exposed so tests can build a history.</summary>
    public void Append(IEnumerable<SpaceReading> readings) => Append(readings, Read());

    private void Append(IEnumerable<SpaceReading> readings, IReadOnlyList<SpaceReading> existing)
    {
        var all = new List<SpaceReading>(existing);
        all.AddRange(readings);

        List<SpaceReading> kept = Prune(all);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var lines = new List<string>(kept.Count);
            foreach (SpaceReading reading in kept) lines.Add(Format(reading));

            File.WriteAllLines(_path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience, not a result. Losing a reading costs a later
            // projection; failing the caller's operation over it would cost more.
        }
    }

    /// <summary>Keeps the newest <see cref="MaximumPerVolume"/> readings of each volume.</summary>
    private static List<SpaceReading> Prune(List<SpaceReading> all)
    {
        all.Sort(static (a, b) => a.TakenAt.CompareTo(b.TakenAt));

        var counts = new Dictionary<char, int>();
        foreach (SpaceReading reading in all)
        {
            char key = char.ToUpperInvariant(reading.DriveLetter);
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var kept = new List<SpaceReading>(all.Count);
        var dropped = new Dictionary<char, int>();

        foreach (SpaceReading reading in all)
        {
            char key = char.ToUpperInvariant(reading.DriveLetter);
            int over = counts[key] - MaximumPerVolume;

            if (over > 0)
            {
                int already = dropped.TryGetValue(key, out int d) ? d : 0;
                if (already < over)
                {
                    dropped[key] = already + 1;
                    continue;
                }
            }

            kept.Add(reading);
        }

        return kept;
    }

    private static bool TooSoon(IReadOnlyList<SpaceReading> existing, char driveLetter, DateTimeOffset now)
    {
        foreach (SpaceReading reading in existing)
        {
            if (char.ToUpperInvariant(reading.DriveLetter) != char.ToUpperInvariant(driveLetter))
                continue;

            if (now - reading.TakenAt < MinimumSpacing) return true;
        }

        return false;
    }

    private static long FreeSpaceOf(char driveLetter)
    {
        try { return new DriveInfo(driveLetter + ":\\").AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static string Format(SpaceReading reading) =>
        string.Join('\t',
            reading.TakenAt.ToString("O", CultureInfo.InvariantCulture),
            reading.DriveLetter,
            reading.FreeBytes.ToString(CultureInfo.InvariantCulture),
            reading.TotalBytes.ToString(CultureInfo.InvariantCulture));

    internal static bool TryParse(string line, out SpaceReading? reading)
    {
        reading = null;

        string[] fields = line.Split('\t');
        if (fields.Length != 4) return false;

        if (!DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture,
                                     DateTimeStyles.RoundtripKind, out DateTimeOffset takenAt))
            return false;

        if (fields[1].Length != 1) return false;

        if (!long.TryParse(fields[2], CultureInfo.InvariantCulture, out long free)) return false;
        if (!long.TryParse(fields[3], CultureInfo.InvariantCulture, out long total)) return false;

        reading = new SpaceReading(takenAt, fields[1][0], free, total);
        return true;
    }
}
