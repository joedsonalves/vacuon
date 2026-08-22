using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Cleanup;

/// <summary>
/// Loads the cleanup rules: the built-in catalog, plus whatever the user added.
/// <para>
/// The shipped catalog is an embedded resource and is never written to. A user catalog at
/// <c>%AppData%\Vacuon\rules.json</c> is merged over it, matching by id, which is what makes
/// F3.1 real — rules can be corrected or added without rebuilding the app.
/// </para>
/// </summary>
public static class RuleCatalog
{
    private const string ResourceName = "Vacuon.Core.Cleanup.catalog.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record CatalogFile(int Version, List<CleanupRule> Rules);

    public static string UserCatalogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Vacuon", "rules.json");

    /// <summary>
    /// The rules, and everything that went wrong on the way to loading them.
    /// <para>
    /// <see cref="Problems"/> is not decoration. A user catalog with one bad escape is
    /// unreadable JSON, and the honest thing is to keep running on the built-in rules
    /// <b>and say so</b> — a file someone edited that silently does nothing is worse than
    /// one that refuses loudly, because they will edit it again and conclude the feature
    /// is broken.
    /// </para>
    /// </summary>
    public sealed record CatalogLoad(IReadOnlyList<CleanupRule> Rules, IReadOnlyList<string> Problems);

    /// <summary>Every rule, built-in first and user overrides applied on top.</summary>
    public static IReadOnlyList<CleanupRule> Load(string? userCatalogPath = null) =>
        LoadWithProblems(userCatalogPath).Rules;

    /// <summary>
    /// Same as <see cref="Load"/>, but also reports the rules that were dropped and why.
    /// </summary>
    public static CatalogLoad LoadWithProblems(string? userCatalogPath = null)
    {
        var byId = new Dictionary<string, CleanupRule>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (CleanupRule rule in ReadBuiltIn()) byId[rule.Id] = rule;

        foreach (CleanupRule rule in ReadUser(userCatalogPath ?? UserCatalogPath, problems))
            byId[rule.Id] = rule;

        var kept = new List<CleanupRule>(byId.Count);

        foreach (CleanupRule rule in byId.Values)
        {
            string? rejection = Rejected(rule);

            if (rejection is null) kept.Add(rule);
            else problems.Add($"{rule.Id}: {rejection}");
        }

        kept.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new CatalogLoad(kept, problems);
    }

    /// <summary>
    /// Why a rule cannot be used, or null when it is fine.
    /// <para>
    /// The check that matters is the protected-path one. <c>%WINDIR%\Temp</c> looks like an
    /// obvious cleanup target and every other tool sweeps it; here it is refused, because
    /// <see cref="ProtectedPaths"/> has no override and a rule pointing there would be a
    /// promise the engine cannot keep. The Windows folder gets cleaned by Microsoft's own
    /// tools, through <see cref="CleanupMethod.SystemTool"/>, or not at all.
    /// </para>
    /// </summary>
    public static string? Rejected(CleanupRule rule)
    {
        if (rule.IsSystemTool)
        {
            return string.IsNullOrWhiteSpace(rule.Tool)
                ? "system-tool rule without a tool id"
                : null;
        }

        if (rule.Paths.Count == 0) return "file rule with no paths";

        foreach (string pattern in rule.Paths)
        {
            string expanded = Expand(pattern);
            if (expanded.Length == 0) continue;

            // Check the fixed part of the pattern — everything before the first wildcard —
            // because that is what decides which subtree the matches can come from.
            string root = FixedRootOf(expanded);
            if (root.Length == 0) continue;

            ProtectionVerdict verdict = ProtectedPaths.Check(root);

            if (verdict.IsProtected && verdict.Reason != ProtectionReason.UserProfileFolder)
                return $"path lands in a protected area ({verdict.Reason}): {root}";
        }

        return null;
    }

    /// <summary>Expands <c>%VAR%</c> and normalises separators.</summary>
    public static string Expand(string pattern)
    {
        string expanded = Environment.ExpandEnvironmentVariables(pattern);

        // An unset variable comes back with the percent signs intact; a rule referring to a
        // variable this machine does not have simply has nothing to match.
        return expanded.Contains('%') ? string.Empty : expanded.Replace('/', '\\');
    }

    /// <summary>The longest leading directory of a glob that contains no wildcard.</summary>
    public static string FixedRootOf(string pattern)
    {
        int wildcard = pattern.IndexOfAny(['*', '?']);
        string head = wildcard < 0 ? pattern : pattern[..wildcard];

        int separator = head.LastIndexOf('\\');
        if (separator < 0) return string.Empty;

        string root = head[..separator];
        return root.Length <= 2 ? root + "\\" : root;
    }

    private static List<CleanupRule> ReadBuiltIn()
    {
        using Stream? stream = typeof(RuleCatalog).Assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            // Same failure mode as the language files: an embedded resource that MSBuild
            // renamed comes back null with no error anywhere. There is a test for this.
            throw new InvalidOperationException($"embedded catalog missing: {ResourceName}");
        }

        return JsonSerializer.Deserialize<CatalogFile>(stream, Options)?.Rules ?? [];
    }

    private static List<CleanupRule> ReadUser(string path, List<string> problems)
    {
        if (!File.Exists(path)) return [];

        try
        {
            return JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), Options)?.Rules ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken user file must not take the built-in catalog down with it — the
            // shipped rules keep working. But it must not vanish either: a single bad
            // backslash makes the whole file unreadable, and without this line the only
            // symptom is that editing rules.json changes nothing, forever.
            problems.Add($"{path}: {ex.Message}");
            return [];
        }
    }
}
