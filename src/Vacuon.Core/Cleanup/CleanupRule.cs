using System.Text.Json.Serialization;

namespace Vacuon.Core.Cleanup;

/// <summary>
/// How much a rule can hurt if it is wrong.
/// <para>
/// The grade is on the rule, not on the file it happens to match, because it is a statement
/// about the consequence: a browser cache comes back on its own, and a restore point does
/// not come back at all.
/// </para>
/// </summary>
public enum CleanupRisk
{
    /// <summary>Regenerates by itself. Losing it costs a slower next launch at worst.</summary>
    Safe,
    /// <summary>Loses something real but recoverable — history, an offline installer, a log.</summary>
    Caution,
    /// <summary>Removes a recovery route or changes how Windows is configured.</summary>
    Dangerous,
}

/// <summary>What the rule does about what it matches.</summary>
public enum CleanupMethod
{
    /// <summary>Vacuon moves the files itself, into the quarantine or to deletion.</summary>
    Files,
    /// <summary>
    /// Vacuon runs the tool Windows ships for the job and reports what it said.
    /// <para>
    /// This is not a fallback. Inside <c>%WINDIR%</c> Vacuon does not delete files at all —
    /// see <see cref="CleanupRule.Paths"/> — and for things like the component store,
    /// deleting by hand is how people break servicing and only find out at the next update.
    /// </para>
    /// </summary>
    SystemTool,
}

/// <summary>
/// One entry of the cleanup catalog.
/// <para>
/// Rules are data, not code: the catalog ships as JSON and a user can add to it without
/// rebuilding anything, which is requirement F3.1. Everything that decides what gets
/// touched lives here so that a rule can be read and argued with before it runs.
/// </para>
/// </summary>
public sealed record CleanupRule
{
    /// <summary>Stable id, used in reports and in the quarantine's reason field.</summary>
    public required string Id { get; init; }

    /// <summary>Category id this rule belongs to, for grouping in the UI.</summary>
    public required string Category { get; init; }

    /// <summary>One line, in English, describing what is removed.</summary>
    public required string Name { get; init; }

    /// <summary>Why it is safe to remove, or what is lost. Shown before anything runs.</summary>
    public required string Description { get; init; }

    public required CleanupRisk Risk { get; init; }

    public CleanupMethod Method { get; init; } = CleanupMethod.Files;

    /// <summary>
    /// Glob patterns, with <c>%VAR%</c> expanded at load.
    /// <para>
    /// Nothing here may resolve inside <c>%WINDIR%</c>. <see cref="Safety.ProtectedPaths"/>
    /// would refuse those anyway — there is no override and there will not be one — so a
    /// rule that pointed there would be a rule that silently does nothing. Where the catalog
    /// wants the Windows folder cleaned, the rule is a <see cref="CleanupMethod.SystemTool"/>
    /// one and Microsoft's own tool does it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Only match files last written more than this many days ago. Zero disables it.</summary>
    public int MinimumAgeDays { get; init; }

    /// <summary>Skip the rule entirely while any of these processes is running.</summary>
    public IReadOnlyList<string> BlockedByProcesses { get; init; } = [];

    /// <summary>For <see cref="CleanupMethod.SystemTool"/>: which tool, by id.</summary>
    public string? Tool { get; init; }

    /// <summary>True when Windows puts it back on the next update, so the gain is temporary.</summary>
    public bool ReturnsAfterUpdate { get; init; }

    /// <summary>True when the rule needs an elevated process to do anything.</summary>
    public bool NeedsElevation { get; init; }

    /// <summary>Docs link for "learn more". Optional.</summary>
    public string? LearnMore { get; init; }

    [JsonIgnore]
    public bool IsSystemTool => Method == CleanupMethod.SystemTool;
}

/// <summary>Which rules a profile runs, by risk.</summary>
public enum CleanupProfile
{
    /// <summary>Only <see cref="CleanupRisk.Safe"/>. The default, and what a schedule may run.</summary>
    Quick,
    /// <summary>Safe plus <see cref="CleanupRisk.Caution"/>, each still confirmed by hand.</summary>
    Deep,
    /// <summary>Whatever the caller ticked, including dangerous ones.</summary>
    Custom,
}
