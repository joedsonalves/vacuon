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
}
