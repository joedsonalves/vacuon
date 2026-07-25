using Vacuon.Core.Localization;

namespace Vacuon.Core.Index;

public enum ReconciliationVerdict
{
    /// <summary>The measured total sits where it should relative to the filesystem's own figure.</summary>
    Agrees,

    /// <summary>
    /// We measured MORE than the filesystem says is used. There is no innocent explanation:
    /// files cannot occupy space the volume does not report as occupied. Something is being
    /// counted twice.
    /// </summary>
    Overcounted,

    /// <summary>
    /// A large slice of the used space is not attributed to any file. Sometimes real —
    /// shadow copies and quotas occupy space no file points at, and an unprivileged
    /// traversal silently skips folders it cannot open — so this is a caveat, not a verdict.
    /// </summary>
    Undercounted,

    /// <summary>The volume reported no used space; there is nothing to compare against.</summary>
    Unknown,
}

/// <summary>
/// Compares the sum of what we measured against what the filesystem reports as used.
/// <para>
/// The two are never identical, and the reasons are structural rather than sloppiness:
/// directory indexes ($INDEX_ALLOCATION) occupy clusters but are not files, $LogFile and
/// $Bitmap are metadata, and volume shadow copies hold space that no directory entry
/// points at. So a measurement slightly UNDER the reported figure is the healthy case.
/// </para>
/// <para>
/// Measuring OVER it is not. That direction is arithmetically impossible and always means a
/// bug — which is exactly how <c>$BadClus:$Bad</c>, a sparse stream the size of the whole
/// volume, was caught being counted at its logical size.
/// </para>
/// </summary>
public readonly record struct Reconciliation(long MeasuredBytes, long ReportedUsedBytes, ScanStrategy Strategy)
{
    /// <summary>
    /// Directory indexes and filesystem metadata are unattributed by construction, and
    /// shadow copies can hold a lot. Below this share of the used space, say so.
    /// </summary>
    private const double UnderThreshold = 0.85;

    /// <summary>
    /// A little slack over 1.0 for the gap between a snapshot's figures and the moment the
    /// free space was read. Beyond it, the arithmetic is broken.
    /// </summary>
    private const double OverThreshold = 1.02;

    public double Ratio => ReportedUsedBytes <= 0 ? 0 : (double)MeasuredBytes / ReportedUsedBytes;

    /// <summary>Signed gap: positive means we claim more than the volume reports as used.</summary>
    public long DifferenceBytes => MeasuredBytes - ReportedUsedBytes;

    public ReconciliationVerdict Verdict => ReportedUsedBytes <= 0
        ? ReconciliationVerdict.Unknown
        : Ratio > OverThreshold ? ReconciliationVerdict.Overcounted
        : Ratio < UnderThreshold ? ReconciliationVerdict.Undercounted
        : ReconciliationVerdict.Agrees;

    /// <summary>
    /// True when the numbers cannot both be right. Callers should surface this loudly
    /// rather than print the total as though it were measured fact.
    /// </summary>
    public bool IsImpossible => Verdict == ReconciliationVerdict.Overcounted;

    /// <summary>One line for the user, in their language.</summary>
    public string Describe() => Verdict switch
    {
        ReconciliationVerdict.Agrees =>
            L.T("reconcile.agrees", $"{Ratio * 100:0.#}"),

        ReconciliationVerdict.Overcounted =>
            L.T("reconcile.overcounted", ByteSize.Format(DifferenceBytes)),

        // An unprivileged traversal cannot open every folder, so the gap is expected there
        // and worth explaining rather than alarming about.
        ReconciliationVerdict.Undercounted when Strategy == ScanStrategy.Win32Walk =>
            L.T("reconcile.undercountedWalk", $"{Ratio * 100:0.#}"),

        ReconciliationVerdict.Undercounted =>
            L.T("reconcile.undercounted", $"{Ratio * 100:0.#}"),

        _ => L.T("reconcile.unknown"),
    };
}
