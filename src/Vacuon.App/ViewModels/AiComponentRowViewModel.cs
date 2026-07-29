using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Optimization;

namespace Vacuon.App.ViewModels;

/// <summary>One row in the AI components tab.</summary>
public sealed class AiComponentRowViewModel : Observable
{
    private AiComponentStatus _status;

    public AiComponentRowViewModel(AiComponentStatus status) => _status = status;

    public AiComponent Component => _status.Component;

    public AiComponentStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            RaiseAll();
        }
    }

    public string Name => Component.Name;
    public string Description => Component.Description;
    public string Path => Component.DisplayPath;
    public string DocumentationUrl => Component.DocumentationUrl;

    public string StateText => L.T(_status.State switch
    {
        ComponentState.On => "ai.stateOn",
        ComponentState.Off => "ai.stateOff",
        ComponentState.Absent => "ai.stateAbsent",
        _ => "ai.stateUnknown",
    });

    /// <summary>
    /// What this component is holding in memory right now.
    /// <para>
    /// Measured at the moment of the scan, never projected. When nothing is running the row
    /// says so instead of implying that switching it off would give anything back.
    /// </para>
    /// </summary>
    public string MeasuredText => _status.RunningProcesses > 0
        ? L.T("ai.measured", Format.Bytes(_status.MeasuredBytes), _status.RunningProcesses)
        : L.T("ai.measuredNone");

    public bool IsOn => _status.IsOn;

    /// <summary>Vacuon offers to switch this one, as opposed to only reporting it.</summary>
    public bool CanTurnOff => _status.CanAct && _status.IsOn;
    public bool CanUndo => _status.CanAct && !_status.IsOn && _status.Component.IsActionable;

    public bool IsReportedOnly => !Component.IsActionable;
    public string ReportedOnlyText => L.T("ai.reportedOnly");

    public bool ReturnsAfterUpdate => Component.ReturnsAfterUpdate;
    public string ReturnsAfterUpdateText => L.T("ai.returnsAfterUpdate");

    public bool NeedsElevation => Component.NeedsElevation;
    public string NeedsElevationText => L.T("ai.needsElevationHint");

    public string TurnOffText => L.T("ai.turnOff");
    public string UndoText => L.T("ai.undo");
    public string DocsText => L.T("ai.docs");

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
            nameof(Name), nameof(Description), nameof(StateText), nameof(MeasuredText),
            nameof(IsOn), nameof(CanTurnOff), nameof(CanUndo), nameof(IsReportedOnly),
            nameof(ReportedOnlyText), nameof(ReturnsAfterUpdateText), nameof(NeedsElevationText),
            nameof(TurnOffText), nameof(UndoText), nameof(DocsText),
        })
        {
            Raise(name);
        }
    }
}
