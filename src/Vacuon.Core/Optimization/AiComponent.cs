using Microsoft.Win32;
using Vacuon.Core.Localization;

namespace Vacuon.Core.Optimization;

/// <summary>How a component can be switched off — and whether Vacuon will do it.</summary>
public enum ControlKind
{
    /// <summary>
    /// A documented Group Policy value. Vacuon writes these, and records what was there before.
    /// </summary>
    RegistryPolicy,

    /// <summary>
    /// A user preference rather than a policy — the same switch exists in Settings. Vacuon
    /// writes these too, but the distinction matters: a preference is the user's own setting,
    /// and something else may legitimately change it back.
    /// </summary>
    RegistryPreference,

    /// <summary>
    /// A shipped package. Reported, never removed — Windows Update puts several of these back,
    /// and a button that quietly loses is worse than no button.
    /// </summary>
    Package,
}

/// <summary>What the machine currently says about a component.</summary>
public enum ComponentState
{
    /// <summary>Not on this machine.</summary>
    Absent,

    /// <summary>Present and allowed to run.</summary>
    On,

    /// <summary>Present, and switched off.</summary>
    Off,

    /// <summary>Present, but the state could not be read — usually a key that needs elevation.</summary>
    Unknown,
}

/// <summary>
/// One Microsoft AI component, with the documented way to turn it off.
/// <para>
/// Curated on purpose. Every entry names a real, documented control and a page that documents
/// it; guessing a registry value and shipping a button for it would be the app claiming to do
/// something it cannot verify. Where no documented control exists, the entry is
/// <see cref="ControlKind.Package"/> or <see cref="ControlKind.OptionalFeature"/> and Vacuon
/// reports it without offering to act.
/// </para>
/// </summary>
public sealed record AiComponent
{
    /// <summary>Stable identifier. Used by the change journal, so it must never change.</summary>
    public required string Id { get; init; }

    public required string NameKey { get; init; }
    public required string DescriptionKey { get; init; }

    public string Name => L.T(NameKey);
    public string Description => L.T(DescriptionKey);

    public required ControlKind Control { get; init; }

    /// <summary>Where the documented control lives. Null for package/feature entries.</summary>
    public RegistryHive? Hive { get; init; }
    public string? SubKey { get; init; }
    public string? ValueName { get; init; }

    /// <summary>The value that means "off", and the one that means "on".</summary>
    public int OffValue { get; init; } = 1;
    public int OnValue { get; init; }

    /// <summary>Package name prefix, for the entries Vacuon only reports.</summary>
    public string? PackagePrefix { get; init; }

    /// <summary>
    /// Processes this component runs. Used to measure what it actually costs right now — never
    /// to estimate what it "would" cost.
    /// </summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];

    /// <summary>Microsoft's own page for this control. Shown so the claim can be checked.</summary>
    public required string DocumentationUrl { get; init; }

    /// <summary>Writing under HKLM needs Administrator; HKCU does not.</summary>
    public bool NeedsElevation => Hive == RegistryHive.LocalMachine;

    /// <summary>
    /// Windows servicing is known to restore this one. Said out loud rather than discovered
    /// by the user a month later.
    /// </summary>
    public bool ReturnsAfterUpdate { get; init; }

    /// <summary>True when Vacuon is willing to change this, as opposed to only reporting it.</summary>
    public bool IsActionable => Control is ControlKind.RegistryPolicy or ControlKind.RegistryPreference;

    public string DisplayPath => Hive switch
    {
        RegistryHive.LocalMachine => $@"HKLM\{SubKey}\{ValueName}",
        RegistryHive.CurrentUser => $@"HKCU\{SubKey}\{ValueName}",
        _ => PackagePrefix ?? string.Empty,
    };
}
