using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Vacuon.Core.Preview;

/// <summary>An edit kept on disk so it outlives the app being closed.</summary>
public sealed record StoredSave(string Id, string Path, long Bytes, DateTime QueuedUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Keeps refused edits on disk, so closing Vacuon does not throw them away.
/// <para>
/// The case this exists for: somebody edits a file that is in use, accepts the wait, and then
/// closes the app — or the machine restarts — before the program holding it lets go. Without
/// this the edit is gone and nothing ever said so.
/// </para>
/// <para>
/// ⚠️ <b>The content is written before the index entry, which is the opposite order to the
/// quarantine, and deliberately so.</b> The quarantine writes its manifest first because the
/// thing at risk is the original file, which is about to be moved and would otherwise be left
/// somewhere with no record of where it came from. Here the original file is untouched and the
/// blob is the <em>only</em> copy of the edit: an index entry pointing at a blob that was
/// never written is a promise the app cannot keep, while a blob with no entry is rubbish that
/// gets cleaned up.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PendingSaveStore
{
    private const string Header = "vacuon-pending\t1";

    public static string DefaultFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacuon", "pending-saves");

    private readonly string _folder;

    public PendingSaveStore(string? folder = null) => _folder = folder ?? DefaultFolder;

    private string IndexPath => System.IO.Path.Combine(_folder, "index.tsv");

    private string BlobPath(string id) => System.IO.Path.Combine(_folder, id + ".bin");

    /// <summary>
    /// Writes the edit to disk and records it.
    /// </summary>
    /// <returns>The stored entry, or <c>null</c> when nothing could be written.</returns>
    public StoredSave? Keep(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        // One entry per target: editing the same file again replaces the older edit, which is
        // a version the person already moved past.
        Forget(path);

        string id = Guid.NewGuid().ToString("N");
        var entry = new StoredSave(id, path, content.LongLength, DateTime.UtcNow);

        try
        {
            Directory.CreateDirectory(_folder);

            File.WriteAllBytes(BlobPath(id), content);

            List<StoredSave> all = [.. Load(), entry];
            WriteIndex(all);

            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Everything waiting, dropping entries whose content is no longer on disk.</summary>
    public IReadOnlyList<StoredSave> Load()
    {
        string[] lines;

        try
        {
            if (!File.Exists(IndexPath)) return [];
            lines = File.ReadAllLines(IndexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (lines.Length == 0 || lines[0] != Header) return [];

        var entries = new List<StoredSave>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            string[] fields = lines[i].Split('\t');
            if (fields.Length != 4) continue;

            if (!long.TryParse(fields[2], CultureInfo.InvariantCulture, out long bytes)) continue;
            if (!DateTime.TryParse(fields[3], CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out DateTime queued)) continue;

            // An entry whose blob is gone is not reported as waiting: it would offer to write
            // something the app can no longer produce.
            if (!File.Exists(BlobPath(fields[0]))) continue;

            entries.Add(new StoredSave(fields[0], fields[1], bytes, queued));
        }

        return entries;
    }

    public byte[]? Content(StoredSave entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try { return File.ReadAllBytes(BlobPath(entry.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Forget(StoredSave entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Remove(e => e.Id == entry.Id);
    }

    public void Forget(string path) =>
        Remove(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        foreach (StoredSave entry in Load()) Delete(entry.Id);

        try { if (File.Exists(IndexPath)) File.Delete(IndexPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Remove(Func<StoredSave, bool> matches)
    {
        List<StoredSave> all = [.. Load()];
        var kept = new List<StoredSave>(all.Count);

        foreach (StoredSave entry in all)
        {
            if (matches(entry)) Delete(entry.Id);
            else kept.Add(entry);
        }

        if (kept.Count != all.Count) WriteIndex(kept);
    }

    private void Delete(string id)
    {
        try { if (File.Exists(BlobPath(id))) File.Delete(BlobPath(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void WriteIndex(IReadOnlyList<StoredSave> entries)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');

        foreach (StoredSave entry in entries)
        {
            // A path with a tab would split into the wrong fields coming back, and the entry
            // it corrupted would point at somebody else's file.
            if (entry.Path.Contains('\t')) continue;

            text.Append(entry.Id).Append('\t')
                .Append(entry.Path).Append('\t')
                .Append(entry.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(entry.QueuedUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }

        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(IndexPath, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
