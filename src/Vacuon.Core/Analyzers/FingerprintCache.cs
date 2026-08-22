using System.Globalization;
using System.Text;

namespace Vacuon.Core.Analyzers;

/// <summary>What was computed for one file, and the evidence that it is still that file.</summary>
public sealed record CachedFingerprint(
    long Size,
    long WriteTimeTicks,
    ulong[] Hashes,
    long DurationTicks)
{
    /// <summary>Whether this entry still describes the file on disk.</summary>
    public bool Matches(long size, long writeTimeTicks) =>
        Size == size && WriteTimeTicks == writeTimeTicks;
}

/// <summary>
/// Remembers the fingerprints already computed, so a second run does not pay for them again.
/// <para>
/// Reading fifty thousand pictures off this author's disk takes about nine minutes, every
/// single time, and almost none of them changed between one run and the next. The expensive
/// part is decoding — the shell opening a JPEG, Media Foundation decoding five frames — and
/// the result is sixty-four bits. Keeping those bits is the difference between a feature
/// someone uses once and one they use.
/// </para>
/// <para>
/// <b>An entry is trusted only while the file's size and last-write time both still match.</b>
/// Not a hash of the contents: hashing to avoid decoding would trade one full read for
/// another and save nothing. Size and timestamp are what the file system already knows, cost
/// nothing to check, and are wrong only for a file edited into exactly its own length without
/// its timestamp moving — at which point the cost is one stale group in a list where every
/// removal is chosen by hand.
/// </para>
/// </summary>
public sealed class FingerprintCache
{
    /// <summary>
    /// Format and algorithm version. A file that does not start with this is discarded.
    /// <para>
    /// <b>Bump this whenever the fingerprint changes, not only when the file layout does.</b>
    /// A cache is a promise that a stored value equals what would be computed now, and a
    /// change to the hashing quietly breaks that promise for every entry already on disk.
    /// It happened here: a stride bug in the video frame copy was fixed, the corrected run
    /// read its own wrong answers back out of the cache, and the fix looked like it had not
    /// worked.
    /// </para>
    /// </summary>
    private const string Header = "vacuon-fingerprints\t3";

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacuon", "fingerprints.tsv");

    /// <summary>
    /// Entries kept. Beyond this the least recently used are dropped — a cache that grows
    /// without bound stops being a convenience and becomes something to clean up.
    /// </summary>
    public const int Capacity = 200_000;

    private readonly string _path;
    private readonly Dictionary<string, CachedFingerprint> _entries;
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    private bool _dirty;

    public FingerprintCache(string? path = null)
    {
        _path = path ?? DefaultPath;
        _entries = Load(_path);
    }

    public int Count => _entries.Count;

    /// <summary>
    /// The stored fingerprint for a file, if it still describes that file.
    /// </summary>
    public ulong[]? Get(string path, long size, DateTime writeTime, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (!_entries.TryGetValue(path, out CachedFingerprint? entry)) return null;
        if (!entry.Matches(size, writeTime.Ticks)) return null;

        _touched.Add(path);
        duration = TimeSpan.FromTicks(entry.DurationTicks);

        return entry.Hashes;
    }

    public void Put(string path, long size, DateTime writeTime, ulong[] hashes, TimeSpan duration = default)
    {
        _entries[path] = new CachedFingerprint(size, writeTime.Ticks, hashes, duration.Ticks);
        _touched.Add(path);
        _dirty = true;
    }

    /// <summary>
    /// Writes the cache back.
    /// <para>
    /// Failure is swallowed on purpose. A cache that cannot be written costs the next run
    /// some time; a scan that fails because of one costs the run itself.
    /// </para>
    /// </summary>
    public void Save()
    {
        if (!_dirty) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var builder = new StringBuilder();
            builder.Append(Header).Append('\n');

            foreach ((string path, CachedFingerprint entry) in Evict())
            {
                builder.Append(path).Append('\t')
                       .Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                       .Append(entry.WriteTimeTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                       .Append(entry.DurationTicks.ToString(CultureInfo.InvariantCulture)).Append('\t');

                for (int i = 0; i < entry.Hashes.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(entry.Hashes[i].ToString("X16", CultureInfo.InvariantCulture));
                }

                builder.Append('\n');
            }

            File.WriteAllText(_path, builder.ToString());
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The entries to keep, favouring the ones this run looked at.
    /// <para>
    /// When the cache is over capacity, what was touched during this run survives: those are
    /// the files that still exist and are still being scanned. What was not touched is either
    /// gone or outside the scope people actually use.
    /// </para>
    /// </summary>
    private IEnumerable<KeyValuePair<string, CachedFingerprint>> Evict()
    {
        if (_entries.Count <= Capacity) return _entries;

        var kept = new List<KeyValuePair<string, CachedFingerprint>>(Capacity);

        foreach (KeyValuePair<string, CachedFingerprint> pair in _entries)
            if (_touched.Contains(pair.Key)) kept.Add(pair);

        foreach (KeyValuePair<string, CachedFingerprint> pair in _entries)
        {
            if (kept.Count >= Capacity) break;
            if (!_touched.Contains(pair.Key)) kept.Add(pair);
        }

        return kept;
    }

    private static Dictionary<string, CachedFingerprint> Load(string path)
    {
        var entries = new Dictionary<string, CachedFingerprint>(StringComparer.OrdinalIgnoreCase);

        string[] lines;

        try
        {
            if (!File.Exists(path)) return entries;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        // A file whose format changed is thrown away rather than parsed hopefully. The cost
        // is one slow run; the cost of misreading it is fingerprints attributed to the wrong
        // files, which shows up as groups that make no sense and no way to tell why.
        if (lines.Length == 0 || lines[0] != Header) return entries;

        for (int i = 1; i < lines.Length; i++)
        {
            string[] fields = lines[i].Split('\t');
            if (fields.Length != 5) continue;

            if (!long.TryParse(fields[1], CultureInfo.InvariantCulture, out long size)) continue;
            if (!long.TryParse(fields[2], CultureInfo.InvariantCulture, out long ticks)) continue;
            if (!long.TryParse(fields[3], CultureInfo.InvariantCulture, out long duration)) continue;

            string[] parts = fields[4].Split(',');
            var hashes = new ulong[parts.Length];
            bool ok = true;

            for (int h = 0; h < parts.Length; h++)
            {
                if (!ulong.TryParse(parts[h], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hashes[h]))
                {
                    ok = false;
                    break;
                }
            }

            if (ok) entries[fields[0]] = new CachedFingerprint(size, ticks, hashes, duration);
        }

        return entries;
    }
}
