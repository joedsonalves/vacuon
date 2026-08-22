using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Core.Actions;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Cleanup;

/// <summary>One file a rule matched.</summary>
public sealed record CleanupMatch(string Path, long Bytes, DateTime LastWriteUtc);

/// <summary>Why a rule produced nothing.</summary>
public enum RuleSkipReason
{
    None,
    /// <summary>Nothing on this machine matched the patterns.</summary>
    NothingMatched,
    /// <summary>A program the rule names is running, and its files are in use.</summary>
    ProcessRunning,
    /// <summary>The rule needs Administrator and this process does not have it.</summary>
    NeedsElevation,
    /// <summary>Excluded by the chosen profile because of its risk grade.</summary>
    RiskAboveProfile,
}

/// <summary>What one rule would do.</summary>
public sealed record RulePlan(
    CleanupRule Rule,
    IReadOnlyList<CleanupMatch> Matches,
    RuleSkipReason Skipped,
    string? SkipDetail = null)
{
    public long Bytes
    {
        get
        {
            long total = 0;
            foreach (CleanupMatch match in Matches) total += match.Bytes;
            return total;
        }
    }

    public bool WillDoSomething => Skipped == RuleSkipReason.None
                                && (Matches.Count > 0 || Rule.IsSystemTool);
}

public sealed record CleanupPlan(IReadOnlyList<RulePlan> Rules, CleanupProfile Profile)
{
    /// <summary>
    /// Bytes the file rules matched.
    /// <para>
    /// System-tool rules contribute nothing here, and that is deliberate: DISM and vssadmin
    /// report what they freed after the fact and nobody can honestly predict it beforehand.
    /// Quoting the catalog's "typical gain" as if it were this machine's number would be an
    /// estimate dressed as a measurement.
    /// </para>
    /// </summary>
    public long MatchedBytes
    {
        get
        {
            long total = 0;
            foreach (RulePlan plan in Rules) total += plan.Bytes;
            return total;
        }
    }

    public int FileCount
    {
        get
        {
            int total = 0;
            foreach (RulePlan plan in Rules) total += plan.Matches.Count;
            return total;
        }
    }

    /// <summary>Rules that would run a Windows tool, which report their gain only afterwards.</summary>
    public IReadOnlyList<RulePlan> SystemTools
    {
        get
        {
            var tools = new List<RulePlan>();
            foreach (RulePlan plan in Rules)
                if (plan.Rule.IsSystemTool && plan.Skipped == RuleSkipReason.None) tools.Add(plan);
            return tools;
        }
    }
}

/// <summary>
/// Evaluates cleanup rules and, separately, carries them out.
/// <para>
/// <b>Planning never touches the disk</b> and is not optional — <see cref="Execute"/> takes a
/// plan, it cannot be handed a profile and told to go. That is requirement F3.3, and it is
/// the difference between a tool that shows you 40,000 files before removing them and one
/// that tells you afterwards.
/// </para>
/// <para>
/// The default disposal is the quarantine, not deletion. Cleanup rules are the part of the
/// app most likely to be run without reading, on a schedule or by someone in a hurry, so
/// they get the reversible route by default and say plainly that the space does not come
/// back until the batch is purged.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RuleEngine
{
    private readonly TimeProvider _time;

    public RuleEngine(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>Builds a plan. Reads the file system; changes nothing.</summary>
    public CleanupPlan Plan(IReadOnlyList<CleanupRule> rules, CleanupProfile profile,
                            bool isElevated, CancellationToken cancellationToken = default)
    {
        var plans = new List<RulePlan>(rules.Count);

        foreach (CleanupRule rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(PlanOne(rule, profile, isElevated, cancellationToken));
        }

        // Biggest first, then the tool rules, then the empty ones.
        plans.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        return new CleanupPlan(plans, profile);
    }

    private RulePlan PlanOne(CleanupRule rule, CleanupProfile profile, bool isElevated,
                             CancellationToken cancellationToken)
    {
        if (!Allows(profile, rule.Risk))
            return new RulePlan(rule, [], RuleSkipReason.RiskAboveProfile);

        if (rule.NeedsElevation && !isElevated)
            return new RulePlan(rule, [], RuleSkipReason.NeedsElevation);

        string? running = FirstRunning(rule.BlockedByProcesses);
        if (running is not null)
            return new RulePlan(rule, [], RuleSkipReason.ProcessRunning, running);

        if (rule.IsSystemTool) return new RulePlan(rule, [], RuleSkipReason.None);

        List<CleanupMatch> matches = Match(rule, cancellationToken);

        return matches.Count == 0
            ? new RulePlan(rule, [], RuleSkipReason.NothingMatched)
            : new RulePlan(rule, matches, RuleSkipReason.None);
    }

    /// <summary>Which risk grades a profile is willing to run.</summary>
    public static bool Allows(CleanupProfile profile, CleanupRisk risk) => profile switch
    {
        CleanupProfile.Quick => risk == CleanupRisk.Safe,
        CleanupProfile.Deep => risk is CleanupRisk.Safe or CleanupRisk.Caution,
        _ => true,
    };

    private List<CleanupMatch> Match(CleanupRule rule, CancellationToken cancellationToken)
    {
        var matches = new List<CleanupMatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DateTime cutoff = rule.MinimumAgeDays > 0
            ? _time.GetUtcNow().UtcDateTime.AddDays(-rule.MinimumAgeDays)
            : DateTime.MaxValue;

        foreach (string pattern in rule.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string expanded = RuleCatalog.Expand(pattern);
            if (expanded.Length == 0) continue;

            foreach (string path in Enumerate(expanded, cancellationToken))
            {
                if (!seen.Add(path)) continue;

                // Every single match is checked, not just the pattern's root: a glob can
                // reach further than whoever wrote it expected, and this is the last gate
                // before the file is handed to a delete.
                if (ProtectedPaths.IsProtected(path)) continue;

                FileInfo info;
                try { info = new FileInfo(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (!info.Exists) continue;

                if (rule.MinimumAgeDays > 0 && info.LastWriteTimeUtc > cutoff) continue;

                matches.Add(new CleanupMatch(path, info.Length, info.LastWriteTimeUtc));
            }
        }

        return matches;
    }

    /// <summary>
    /// Expands one glob. Supports a trailing <c>**</c> for "everything below".
    /// </summary>
    private static IEnumerable<string> Enumerate(string pattern, CancellationToken cancellationToken)
    {
        bool recursive = pattern.EndsWith("**", StringComparison.Ordinal);
        string trimmed = recursive ? pattern[..^2].TrimEnd('\\') : pattern;

        string? directory = recursive ? trimmed : Path.GetDirectoryName(trimmed);
        string leaf = recursive ? "*" : Path.GetFileName(trimmed);

        if (string.IsNullOrEmpty(directory)) yield break;

        // A wildcard in the directory part means several directories to walk.
        IEnumerable<string> roots = directory.IndexOfAny(['*', '?']) >= 0
            ? ExpandDirectories(directory)
            : [directory];

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root)) continue;

            IEnumerator<string> walker;

            try
            {
                walker = Directory.EnumerateFiles(
                    root, leaf,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = recursive,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (walker)
            {
                while (true)
                {
                    string current;

                    try
                    {
                        if (!walker.MoveNext()) break;
                        current = walker.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }

                    yield return current;
                }
            }
        }
    }

    /// <summary>Resolves a directory path containing wildcards into the real directories.</summary>
    private static IEnumerable<string> ExpandDirectories(string pattern)
    {
        string[] parts = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return [];

        // Rebuild the drive-qualified head, which Split loses the separator from.
        var current = new List<string> { parts[0].EndsWith(':') ? parts[0] + "\\" : parts[0] };

        for (int i = 1; i < parts.Length; i++)
        {
            var next = new List<string>();

            foreach (string root in current)
            {
                if (parts[i].IndexOfAny(['*', '?']) < 0)
                {
                    next.Add(Path.Combine(root, parts[i]));
                    continue;
                }

                try
                {
                    next.AddRange(Directory.EnumerateDirectories(root, parts[i]));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            current = next;
            if (current.Count == 0) break;
        }

        return current;
    }

    private static string? FirstRunning(IReadOnlyList<string> names)
    {
        foreach (string name in names)
        {
            Process[] found;

            try { found = Process.GetProcessesByName(name); }
            catch (InvalidOperationException) { continue; }

            try
            {
                if (found.Length > 0) return name;
            }
            finally
            {
                foreach (Process process in found) process.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Carries out a plan. Takes the plan, never a profile: what runs is exactly what was
    /// shown, and nothing can be matched between the showing and the doing.
    /// </summary>
    public CleanupReport Execute(CleanupPlan plan, CleanupDisposal disposal,
                                 CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();

        foreach (RulePlan rule in plan.Rules)
        {
            if (rule.Skipped != RuleSkipReason.None || rule.Rule.IsSystemTool) continue;

            foreach (CleanupMatch match in rule.Matches) paths.Add(match.Path);
        }

        if (paths.Count == 0)
            return new CleanupReport(0, 0, 0, disposal, null, []);

        if (disposal == CleanupDisposal.Quarantine)
        {
            QuarantineReport report = new QuarantineService(_time)
                .Execute(paths, "cleanup", cancellationToken);

            return new CleanupReport(
                report.QuarantinedCount, report.FailedCount, report.BytesHeld,
                disposal, report.BatchId, [.. report.Failures.Select(f => f.Path)]);
        }

        DeleteReport deleted = new DeleteService().Execute(
            paths,
            disposal == CleanupDisposal.RecycleBin ? DeleteMode.RecycleBin : DeleteMode.Permanent,
            cancellationToken);

        return new CleanupReport(
            deleted.DeletedCount, deleted.FailedCount, deleted.BytesFreed,
            disposal, null, [.. deleted.Failures.Select(f => f.Path)]);
    }
}

/// <summary>What happens to the files a rule matched.</summary>
public enum CleanupDisposal
{
    /// <summary>The default. Reversible, and frees nothing until the batch is purged.</summary>
    Quarantine,
    RecycleBin,
    Permanent,
}

public sealed record CleanupReport(
    int Handled,
    int Failed,
    long Bytes,
    CleanupDisposal Disposal,
    string? QuarantineBatchId,
    IReadOnlyList<string> Failures)
{
    /// <summary>
    /// True when <see cref="Bytes"/> is space the volume actually got back.
    /// <para>
    /// False for the quarantine, where the files only changed folder, and false for the
    /// Recycle Bin, which holds them until it is emptied. Only the permanent route frees
    /// anything at the moment it runs.
    /// </para>
    /// </summary>
    public bool BytesWereFreed => Disposal == CleanupDisposal.Permanent;
}
