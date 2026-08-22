using System.Text.Json;
using System.Text.Json.Serialization;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Actions;

/// <summary>
/// One item held in quarantine, and where it came from.
/// </summary>
public sealed record QuarantineItem
{
    /// <summary>
    /// File name inside the batch folder — <c>00001.bin</c>, <c>00002.bin</c>…
    /// <para>
    /// Numbered rather than kept under the original name for two reasons: two files
    /// selected from different folders often share a name, and the original path can be
    /// long enough that re-rooting it under the batch folder crosses <c>MAX_PATH</c>.
    /// </para>
    /// </summary>
    public required string StoredName { get; init; }

    /// <summary>Absolute path this came from, and the only place restore will put it back.</summary>
    public required string OriginalPath { get; init; }

    public required long Bytes { get; init; }
    public required bool IsDirectory { get; init; }

    /// <summary>Last write time of the original, kept so the history can show it.</summary>
    public DateTime? ModifiedUtc { get; init; }

    /// <summary>Why it was quarantined — a rule id, or free text from the caller.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// One quarantine batch on one volume: the manifest file, in memory.
/// <para>
/// A batch that spans volumes is written as one manifest per volume sharing a
/// <see cref="BatchId"/>. Quarantine is a rename inside a volume, and a rename cannot
/// cross one — moving across would be a copy, which is neither instant nor free, and the
/// point of the quarantine is that it costs nothing to change your mind.
/// </para>
/// </summary>
public sealed record QuarantineBatch
{
    public required string BatchId { get; init; }
    public required DateTime CreatedUtc { get; init; }

    /// <summary>Volume root this batch lives on, e.g. <c>C:\</c>.</summary>
    public required string Volume { get; init; }

    public required IReadOnlyList<QuarantineItem> Items { get; init; }

    /// <summary>Folder holding this batch, set when the manifest is read. Not serialized.</summary>
    [JsonIgnore]
    public string BatchFolder { get; init; } = string.Empty;

    /// <summary>
    /// What the batch set out to hold, summed from <see cref="Items"/>.
    /// <para>
    /// Not serialized. Writing it into the file would put a second copy of a number that is
    /// already there item by item, and a stored total nobody recomputes is a total that
    /// quietly stops matching the list it claims to describe.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (QuarantineItem item in Items) total += item.Bytes;
            return total;
        }
    }
}

/// <summary>
/// Reads and writes <c>manifest.json</c>, and knows where quarantine folders live.
/// </summary>
public static class QuarantineManifest
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The quarantine root for a volume, e.g. <c>C:\$Vacuon.Quarantine</c>.</summary>
    public static string RootFor(string volumeRoot) =>
        Path.Combine(volumeRoot, ProtectedPaths.QuarantineFolderName);

    /// <summary>
    /// Batch ids sort chronologically as text and are safe as folder names, so listing
    /// the quarantine is a directory listing and nothing has to be parsed to order it.
    /// </summary>
    public static string NewBatchId(DateTime utcNow) =>
        utcNow.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'") + "-" + Guid.NewGuid().ToString("N")[..4];

    public static void Write(string batchFolder, QuarantineBatch batch)
    {
        Directory.CreateDirectory(batchFolder);
        string path = Path.Combine(batchFolder, FileName);

        // Write to a temporary file and swap it in. A half-written manifest is worse than
        // no manifest: it names files that are already gone from their original place.
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(batch, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads one batch, or null when the folder holds no readable manifest.</summary>
    public static QuarantineBatch? Read(string batchFolder)
    {
        string path = Path.Combine(batchFolder, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            QuarantineBatch? batch =
                JsonSerializer.Deserialize<QuarantineBatch>(File.ReadAllText(path), Options);

            return batch is null ? null : batch with { BatchFolder = batchFolder };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable manifest must not take the whole listing down with it: the
            // other batches on the volume are still restorable.
            return null;
        }
    }
}
