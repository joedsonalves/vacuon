using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Optimization;

namespace Vacuon.App.ViewModels;

/// <summary>One program in the memory list.</summary>
public sealed class MemoryRowViewModel(ProcessMemory process) : Observable
{
    public string Name => process.Name;

    /// <summary>
    /// Private first, working set second and clearly labelled.
    /// <para>
    /// Showing working set alone would be the familiar lie: it counts shared pages once per
    /// process, so a browser's children add up to more memory than the machine owns.
    /// </para>
    /// </summary>
    public string SizeText => L.T("memory.privateAndWorkingSet",
        Format.Bytes(process.PrivateBytes), Format.Bytes(process.WorkingSetBytes));

    /// <summary>Fraction of the machine's memory, for the bar.</summary>
    public double Share { get; init; }

    public bool IsFromStartup => process.IsFromStartup;

    public string StartupText => process.StartupEntryName is null
        ? string.Empty
        : L.T("memory.fromStartup", process.StartupEntryName);

    // ================= fechar =================

    /// <summary>Windows needs this one. No override, matching the protection list for paths.</summary>
    public bool IsProtected { get; } = ProtectedProcesses.IsProtected(process.Name);

    public bool CanClose => !IsProtected && !_isArmed && !_isClosing;
    public string ProtectedText => L.T("memory.closeProtected");

    /// <summary>
    /// Set by the first click, cleared by the second or by Cancel.
    /// <para>
    /// Closing a process throws away whatever was unsaved in it, so the button asks twice.
    /// The second button says what is being agreed to rather than just "OK".
    /// </para>
    /// </summary>
    private bool _isArmed;
    public bool IsArmed
    {
        get => _isArmed;
        set
        {
            if (!Set(ref _isArmed, value)) return;
            Raise(nameof(CanClose));
        }
    }

    private bool _isClosing;
    public bool IsClosing
    {
        get => _isClosing;
        set
        {
            if (!Set(ref _isClosing, value)) return;
            Raise(nameof(CanClose));
        }
    }

    public string CloseText => L.T("memory.close");
    public string ConfirmText => L.T("memory.closeConfirm");
    public string CancelText => L.T("memory.closeCancel");

    private string _outcome = string.Empty;
    public string Outcome
    {
        get => _outcome;
        set { Set(ref _outcome, value); Raise(nameof(HasOutcome)); }
    }

    public bool HasOutcome => _outcome.Length > 0;
}
