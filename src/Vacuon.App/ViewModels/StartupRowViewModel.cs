using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Optimization;

namespace Vacuon.App.ViewModels;

/// <summary>One program in the startup list.</summary>
public sealed class StartupRowViewModel(StartupEntry entry) : Observable
{
    private StartupEntry _entry = entry;

    public StartupEntry Entry
    {
        get => _entry;
        set { _entry = value; RaiseAll(); }
    }

    public string Name => _entry.Name;
    public string Command => _entry.Command;
    public string SourceLabel => _entry.SourceLabel;

    public string StateText => L.T(_entry.IsEnabled ? "startup.enabled" : "startup.disabled");

    /// <summary>Measured now, never projected from what it might use.</summary>
    public string MeasuredText => _entry.RunningProcesses > 0
        ? L.T("startup.measured", Format.Bytes(_entry.MeasuredBytes), _entry.RunningProcesses)
        : L.T("startup.measuredNone");

    public bool IsEnabled => _entry.IsEnabled;
    public bool CanDisable => _entry.IsEnabled;
    public bool CanEnable => !_entry.IsEnabled;

    /// <summary>An entry pointing at a file that is gone is dead weight, and worth saying.</summary>
    public bool TargetMissing => _entry.TargetPath is not null && !_entry.TargetExists;
    public string MissingText => L.T("startup.missingTarget");

    public bool NeedsElevation => _entry.NeedsElevation;
    public string NeedsElevationText => L.T("startup.needsElevationHint");

    public string DisableText => L.T("startup.disable");
    public string EnableText => L.T("startup.enable");

    private string _outcome = string.Empty;
    public string Outcome
    {
        get => _outcome;
        set { Set(ref _outcome, value); Raise(nameof(HasOutcome)); }
    }

    public bool HasOutcome => _outcome.Length > 0;

    public void RaiseAll()
    {
        foreach (string name in new[]
        {
            nameof(Name), nameof(Command), nameof(SourceLabel), nameof(StateText),
            nameof(MeasuredText), nameof(IsEnabled), nameof(CanDisable), nameof(CanEnable),
            nameof(TargetMissing), nameof(MissingText), nameof(NeedsElevationText),
            nameof(DisableText), nameof(EnableText),
        })
        {
            Raise(name);
        }
    }
}
