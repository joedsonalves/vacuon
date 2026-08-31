using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vacuon.Core.Optimization;

/// <summary>
/// One change Vacuon made, with enough of the previous state to put it back.
/// </summary>
public sealed record PolicyChange
{
    public required string ComponentId { get; init; }
    public required string Hive { get; init; }
    public required string SubKey { get; init; }
    public required string ValueName { get; init; }

    /// <summary>
    /// What was there before, or null when the value did not exist.
    /// <para>
    /// The difference matters more than it looks. Undoing a value that never existed by
    /// writing a zero leaves the machine in a third state that is neither before nor after —
    /// an explicit "allow" where Windows previously had its own default. Undo has to delete.
    /// </para>
    /// </summary>
    public int? PreviousValue { get; init; }

    /// <summary>True when Vacuon created the key itself, so undo can remove it again.</summary>
    public bool KeyCreated { get; init; }

    public required int WrittenValue { get; init; }
    public required DateTime AtUtc { get; init; }
}

/// <summary>
/// The record of every change, on disk.
/// <para>
/// The quarantine holds files; a registry value has nowhere to be moved to, so this file
/// is the only way back for these. It is written <b>before</b> the registry is touched: a
/// crash between the two must leave a note about a change that did not happen, never a
/// change with no note — the same ordering the quarantine manifest uses.
/// </para>
/// </summary>
public sealed class PolicyJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacuon", "ai-changes.json");

    private readonly string _path;

    public PolicyJournal(string? path = null) => _path = path ?? DefaultPath;

    public List<PolicyChange> Read()
    {
        try
        {
            if (!File.Exists(_path)) return [];

            List<PolicyChange>? loaded =
                JsonSerializer.Deserialize<List<PolicyChange>>(File.ReadAllText(_path), Options);

            return loaded ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A journal we cannot read is a journal we must not overwrite silently, but it
            // also cannot be allowed to block the app from opening.
            return [];
        }
    }

    /// <summary>Appends a change. Called before the registry write, never after.</summary>
    public void Append(PolicyChange change)
    {
        List<PolicyChange> all = Read();
        all.Add(change);
        Write(all);
    }

    /// <summary>Drops the newest entry for a component, once it has been undone.</summary>
    public void RemoveLast(string componentId)
    {
        List<PolicyChange> all = Read();

        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(all[i].ComponentId, componentId, StringComparison.Ordinal)) continue;
            all.RemoveAt(i);
            break;
        }

        Write(all);
    }

    /// <summary>The most recent change for a component, or null if Vacuon never touched it.</summary>
    public PolicyChange? LastFor(string componentId)
    {
        List<PolicyChange> all = Read();

        for (int i = all.Count - 1; i >= 0; i--)
            if (string.Equals(all[i].ComponentId, componentId, StringComparison.Ordinal)) return all[i];

        return null;
    }

    private void Write(List<PolicyChange> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
