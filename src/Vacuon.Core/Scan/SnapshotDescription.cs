using Vacuon.Core.Localization;

namespace Vacuon.Core.Scan;

/// <summary>
/// Turns an incremental result into text a person can act on.
/// <para>
/// Lives in the core so the CLI and the interface say the same thing. A refusal is
/// always spelled out: "no snapshot yet" and "the journal was recreated" send the user
/// to very different conclusions, and collapsing both into "scanning…" hides the one
/// case they could actually fix (running elevated).
/// </para>
/// </summary>
public static class SnapshotDescription
{
    public static string Describe(IncrementalResult result) => result.Succeeded
        ? result.ChangesApplied == 0
            ? L.T("snapshot.usedNoChanges", Age(result.SnapshotTakenAtUtc))
            : L.T("snapshot.used", result.ChangesApplied, Age(result.SnapshotTakenAtUtc))
        : Refusal(result.Refusal);

    public static string Refusal(IncrementalRefusal refusal) => L.T(refusal switch
    {
        IncrementalRefusal.NoSnapshot => "snapshot.refusedNoSnapshot",
        IncrementalRefusal.UnusableSnapshot => "snapshot.refusedUnusable",
        IncrementalRefusal.NoJournal => "snapshot.refusedNoJournal",
        IncrementalRefusal.JournalReplaced => "snapshot.refusedJournalReplaced",
        IncrementalRefusal.JournalWrapped => "snapshot.refusedJournalWrapped",
        IncrementalRefusal.NeedsElevation => "snapshot.refusedNeedsElevation",
        _ => "snapshot.refusedNoSnapshot",
    });

    /// <summary>
    /// Coarse age of a snapshot. The exact second is noise; what matters is whether the
    /// index is minutes or weeks old.
    /// </summary>
    public static string Age(DateTime takenAtUtc)
    {
        if (takenAtUtc == DateTime.MinValue) return L.T("snapshot.age.seconds", 0);

        TimeSpan age = DateTime.UtcNow - takenAtUtc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        return age.TotalSeconds < 90 ? L.T("snapshot.age.seconds", (int)age.TotalSeconds)
             : age.TotalMinutes < 90 ? L.T("snapshot.age.minutes", (int)age.TotalMinutes)
             : age.TotalHours < 36 ? L.T("snapshot.age.hours", (int)age.TotalHours)
             : L.T("snapshot.age.days", (int)age.TotalDays);
    }
}
