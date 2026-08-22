using Vacuon.App.Infra;
using Vacuon.Core.Cleanup;
using Vacuon.Core.Localization;

namespace Vacuon.App.ViewModels;

/// <summary>
/// One rule on the cleanup screen, with the tick that decides whether it runs.
/// <para>
/// A rule that cannot run — nothing matched, a program is holding its files, it needs
/// Administrator — is shown with the reason rather than hidden. "Why did it not clean that?"
/// is the question a cleanup tool has to answer without being asked.
/// </para>
/// </summary>
public sealed class CleanupRuleViewModel(RulePlan plan, Action changed) : Observable
{
    public RulePlan Plan { get; } = plan;
    public CleanupRule Rule => Plan.Rule;

    public string Name => Rule.Name;
    public string Description => Rule.Description;

    public string RiskLabel => L.T(Rule.Risk switch
    {
        CleanupRisk.Caution => "cleanup.riskCaution",
        CleanupRisk.Dangerous => "cleanup.riskDangerous",
        _ => "cleanup.riskSafe",
    });

    /// <summary>Theme brush key for the risk badge.</summary>
    public string RiskBrush => Rule.Risk switch
    {
        CleanupRisk.Caution => "Risk.Notable",
        CleanupRisk.Dangerous => "Risk.Danger",
        _ => "Risk.Safe",
    };

    public bool CanRun => Plan.WillDoSomething;

    /// <summary>What this rule would remove, or empty for a tool rule that cannot predict.</summary>
    public string SizeText
    {
        get
        {
            if (Rule.IsSystemTool) return string.Empty;
            return Plan.Matches.Count == 0
                ? string.Empty
                : $"{Format.Count(Plan.Matches.Count)} · {Format.Bytes(Plan.Bytes)}";
        }
    }

    /// <summary>
    /// For tool rules: says the gain is only known afterwards, instead of showing a number
    /// borrowed from someone else's machine.
    /// </summary>
    public string ToolText => Rule.IsSystemTool && Plan.Skipped == RuleSkipReason.None
        ? L.T("cleanup.toolWillRun", Rule.Tool ?? "?")
        : string.Empty;

    public string SkipText => Plan.Skipped switch
    {
        RuleSkipReason.ProcessRunning => L.T("cleanup.skipProcess", Plan.SkipDetail ?? "?"),
        RuleSkipReason.NeedsElevation => L.T("cleanup.skipElevation"),
        RuleSkipReason.RiskAboveProfile => L.T("cleanup.skipRisk"),
        RuleSkipReason.NothingMatched => L.T("cleanup.skipNothing"),
        _ => string.Empty,
    };

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // A rule that cannot run must not be tickable: ticking it would put it in a
            // total that it is never going to contribute to.
            if (!CanRun) value = false;
            if (!Set(ref _isChecked, value)) return;
            changed();
        }
    }
}

/// <summary>A category header with the rules under it.</summary>
public sealed class CleanupCategoryViewModel(string key, IReadOnlyList<CleanupRuleViewModel> rules)
{
    public string Name => L.T(key);
    public IReadOnlyList<CleanupRuleViewModel> Rules { get; } = rules;

    public string SummaryText
    {
        get
        {
            long bytes = 0;
            int files = 0;

            foreach (CleanupRuleViewModel rule in Rules)
            {
                bytes += rule.Plan.Bytes;
                files += rule.Plan.Matches.Count;
            }

            return files == 0 ? string.Empty : $"{Format.Count(files)} · {Format.Bytes(bytes)}";
        }
    }
}
