using System.Globalization;
using System.Text;

namespace Vacuon.Core.Analyzers;

/// <summary>What was computed for one folder, and the evidence that it is still that folder.</summary>
public sealed record CachedFolderSignature(string Stamp, string Signature)
{
    /// <summary>Whether this entry still describes the tree on disk.</summary>
    public bool Matches(string stamp) => string.Equals(Stamp, stamp, StringComparison.Ordinal);
}

/// <summary>
/// Remembers the content signature of folders already read, so a second run does not read
/// them again.
/// <para>
/// Measured on this author's C:, twice in a row in the same process:
/// <list type="bullet">
///   <item>first run, nothing remembered: <b>52,5 min</b>, 109,8 GiB read</item>
///   <item>second run: <b>13,7 s</b>, <b>zero bytes read</b>, 7.490 of 7.504 folders from here</item>
/// </list>
/// Both produced the same answer — 847 groups, 24,8 GiB recoverable — which is the point:
/// the cache is only worth having if it cannot change the verdict.
/// </para>
/// <para>
/// The first run pays a little more than it used to, because the stamp is a walk of the
/// tree's metadata on top of reading it. That is the trade being made, and it is the right
/// way round: the expensive run happens once and every run after it is seconds.
/// </para>
/// <para>
/// ⚠️ <b>An entry is trusted only while the stamp still matches, and the stamp comes from
/// the disk, not from the index.</b> That distinction is the whole safety of this file. The
/// two free stages of the search read the index and may only ever drop a candidate; stage 3
/// is what actually asserts "these folders are identical", and an assertion built on a
/// cached read has to be as good as the read it replaces. A stamp taken from the index would
/// have made a stale index able to produce a stale verdict, which is a thing the uncached
/// search cannot do.
/// </para>
/// <para>
/// The stamp is every relative path with the size and the last-write time behind it, so a
/// file added, removed, resized or rewritten inside the tree invalidates the folder. What
/// survives it is a file edited into exactly its own length without its timestamp moving —
/// the same caveat the picture cache carries, and the cost is one stale group in a list
/// where every removal is chosen by hand.
/// </para>
/// </summary>
public sealed class FolderSignatureCache
{
    /// <summary>
    /// Format and algorithm version. A file that does not start with this is discarded.
    /// <para>
    /// <b>Bump this whenever the signature or the stamp changes, not only when the layout
    /// does.</b> A cache is a promise that a stored value equals what would be computed now,
    /// and a change to either hash breaks that promise silently for every entry on disk.
    /// </para>
    /// </summary>
    private const string Header = "vacuon-folder-signatures\t1";

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacuon", "folder-signatures.tsv");

    /// <summary>
    /// Entries kept. Far below the picture cache's ceiling on purpose: a real volume has
    /// millions of files and tens of thousands of folders worth comparing.
    /// </summary>
    public const int Capacity = 50_000;

    private readonly string _path;
    private readonly Dictionary<string, CachedFolderSignature> _entries;
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private bool _dirty;

    public FolderSignatureCache(string? path = null)
    {
        _path = path ?? DefaultPath;
        _entries = Load(_path);
    }

    public int Count => _entries.Count;

    /// <summary>
    /// The stored signature for a folder, if it still describes that folder.
    /// </summary>
    /// <remarks>
    /// Locked because stage 3 reads eight folders at once and a dictionary torn by two
    /// threads is a crash in the middle of a half-hour run.
    /// </remarks>
    public string? Get(string folder, string stamp)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(folder, out CachedFolderSignature? entry)) return null;
            if (!entry.Matches(stamp)) return null;

            _touched.Add(folder);
            return entry.Signature;
        }
    }

    /// <summary>
    /// Whether there is an entry for this folder at all, valid or not.
    /// <para>
    /// ⚠️ For the estimate shown before a run, and nothing else. It deliberately does not
    /// check the stamp, because checking means walking the tree's metadata and the estimate
    /// is supposed to be free. Anything built on this has to say "already read once", never
    /// "will not be read".
    /// </para>
    /// </summary>
    public bool Knows(string folder)
    {
        lock (_gate) return _entries.ContainsKey(folder.TrimEnd('\\'));
    }

    public void Put(string folder, string stamp, string signature)
    {
        lock (_gate)
        {
            _entries[folder] = new CachedFolderSignature(stamp, signature);
            _touched.Add(folder);
            _dirty = true;
        }
    }

    /// <summary>
    /// Writes the cache back.
    /// <para>
    /// Failure is swallowed on purpose. A cache that cannot be written costs the next run
    /// some time; a search that fails because of one costs the run itself.
    /// </para>
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                var builder = new StringBuilder();
                builder.Append(Header).Append('\n');

                foreach ((string folder, CachedFolderSignature entry) in Evict())
                {
                    // A path with a tab in it would split into the wrong fields on the way
                    // back in, and the entry it corrupted would be somebody else's.
                    if (folder.Contains('\t')) continue;

                    builder.Append(folder).Append('\t')
                           .Append(entry.Stamp).Append('\t')
                           .Append(entry.Signature).Append('\n');
                }

                File.WriteAllText(_path, builder.ToString());
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The entries to keep, favouring the ones this run looked at.
    /// <para>
    /// When the cache is over capacity, what was touched during this run survives: those are
    /// the folders that still exist and are still being compared.
    /// </para>
    /// </summary>
    private IEnumerable<KeyValuePair<string, CachedFolderSignature>> Evict()
    {
        if (_entries.Count <= Capacity) return _entries;

        var kept = new List<KeyValuePair<string, CachedFolderSignature>>(Capacity);

        foreach (KeyValuePair<string, CachedFolderSignature> pair in _entries)
            if (_touched.Contains(pair.Key)) kept.Add(pair);

        foreach (KeyValuePair<string, CachedFolderSignature> pair in _entries)
        {
            if (kept.Count >= Capacity) break;
            if (!_touched.Contains(pair.Key)) kept.Add(pair);
        }

        return kept;
    }

    private static Dictionary<string, CachedFolderSignature> Load(string path)
    {
        var entries = new Dictionary<string, CachedFolderSignature>(StringComparer.OrdinalIgnoreCase);

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
        // is one slow run; the cost of misreading it is signatures attributed to the wrong
        // folders, which shows up as groups that make no sense and no way to tell why.
        if (lines.Length == 0 || lines[0] != Header) return entries;

        for (int i = 1; i < lines.Length; i++)
        {
            string[] fields = lines[i].Split('\t');
            if (fields.Length != 3) continue;
            if (fields[0].Length == 0 || fields[1].Length == 0 || fields[2].Length == 0) continue;

            entries[fields[0]] = new CachedFolderSignature(fields[1], fields[2]);
        }

        return entries;
    }

    /// <summary>
    /// The stamp of a tree, from the file system's own metadata.
    /// <para>
    /// Null when the folder could not be walked, which means the same thing here as
    /// everywhere else in this search: no cached answer may be used, and nothing may be
    /// stored either.
    /// </para>
    /// </summary>
    public static string? StampOf(string folder, out List<string>? files)
    {
        files = null;

        if (folder.Length == 0 || !Directory.Exists(folder)) return null;

        string root = folder.TrimEnd('\\');

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
        };

        var lines = new List<string>();
        var found = new List<string>();

        try
        {
            var directory = new DirectoryInfo(root);

            // FileInfo from the enumeration carries the size and the times that the
            // directory scan already returned, so this costs one walk and no extra opens.
            foreach (FileInfo file in directory.EnumerateFiles("*", options))
            {
                found.Add(file.FullName);

                lines.Add(file.FullName[root.Length..].TrimStart('\\').ToLowerInvariant()
                          + "|" + file.Length.ToString(CultureInfo.InvariantCulture)
                          + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            foreach (DirectoryInfo sub in directory.EnumerateDirectories("*", options))
                lines.Add(sub.FullName[root.Length..].TrimStart('\\').ToLowerInvariant() + "|dir");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lines.Sort(StringComparer.Ordinal);
        files = found;

        return Hashing.OfLines(lines);
    }
}

/// <summary>Hashing a sorted list of lines, which two of these stages both need.</summary>
internal static class Hashing
{
    /// <summary>
    /// The SHA-256 of the lines, fed in one at a time.
    /// <para>
    /// Incremental rather than joined: the biggest candidates hold thousands of files, and
    /// keeping one joined string per folder alive as a dictionary key is megabytes of
    /// nothing.
    /// </para>
    /// </summary>
    public static string OfLines(List<string> lines)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();

        foreach (string line in lines)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
