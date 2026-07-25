using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Scan;

public enum StrategyPreference
{
    /// <summary>Tenta MFT, cai para travessia por API se não der. É o padrão.</summary>
    Auto,
    /// <summary>Exige MFT; falha em vez de degradar silenciosamente.</summary>
    ForceMft,
    /// <summary>Força travessia por API (útil para comparar resultados e depurar).</summary>
    ForceWalk,
}

public sealed record ScanResult(
    VolumeIndex Index,
    ScanStrategy StrategyUsed,
    string? FallbackReason,
    /// <summary>Set when the index came from a snapshot plus a journal delta.</summary>
    IncrementalResult? Incremental = null)
{
    public bool CameFromSnapshot => Incremental?.Succeeded == true;
}

/// <summary>
/// Escolhe a estratégia de varredura por volume e cai em cascata quando a preferida
/// não está disponível (PRD §7.1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScanOrchestrator(MftScanOptions? options = null)
{
    private readonly MftScanOptions _options = options ?? new MftScanOptions();

    /// <summary>
    /// Brings the index up to date the cheapest way available: a saved snapshot plus the
    /// journal delta when that is trustworthy, a full scan when it is not.
    /// </summary>
    /// <param name="allowSnapshot">
    /// <c>false</c> forces a full scan — the <c>--fresh</c> escape hatch, and what to use
    /// when the numbers are ever in doubt.
    /// </param>
    public ScanResult Refresh(char driveLetter, StrategyPreference preference = StrategyPreference.Auto,
                              bool allowSnapshot = true, CancellationToken cancellationToken = default)
    {
        IncrementalResult? attempt = null;

        if (allowSnapshot && preference != StrategyPreference.ForceWalk)
        {
            attempt = new IncrementalUpdater().TryUpdate(driveLetter, cancellationToken);

            if (attempt.Succeeded)
                return new ScanResult(attempt.Index!, attempt.Index!.Strategy, null, attempt);
        }

        ScanResult result = ScanVolume(driveLetter, preference, cancellationToken);

        // Record the journal position that goes with this index, so the next open can
        // start from a delta. Needs elevation; silently skipped without it.
        new IncrementalUpdater().SaveSnapshot(result.Index, driveLetter);

        // The refusal travels with the result. Discarding it would hide the one case the
        // user can actually fix — "the journal needs Administrator" is actionable,
        // an unexplained full scan is not.
        return attempt is null ? result : result with { Incremental = attempt };
    }

    public ScanResult ScanVolume(char driveLetter, StrategyPreference preference = StrategyPreference.Auto,
                                 CancellationToken cancellationToken = default)
    {
        if (preference != StrategyPreference.ForceWalk)
        {
            try
            {
                VolumeIndex index = new MftScanner(_options).Scan(driveLetter, cancellationToken);
                return new ScanResult(index, ScanStrategy.Mft, null);
            }
            catch (VolumeAccessException ex)
            {
                if (preference == StrategyPreference.ForceMft) throw;

                string reason = ex.Failure switch
                {
                    VolumeAccessFailure.NeedsElevation =>
                        L.T("fallback.needsElevation"),
                    VolumeAccessFailure.NotNtfs =>
                        L.T("fallback.notNtfs"),
                    VolumeAccessFailure.MftUnreadable =>
                        L.T("fallback.mftUnreadable", ex.Message),
                    _ => ex.Message,
                };

                return WalkFallback(driveLetter, reason, cancellationToken);
            }
        }

        return WalkFallback(driveLetter, L.T("fallback.walkRequested"), cancellationToken);
    }

    /// <summary>Varredura de uma pasta específica — sempre por travessia, o escopo não é o volume.</summary>
    public ScanResult ScanFolder(string path, CancellationToken cancellationToken = default)
    {
        VolumeIndex index = new Win32Walker(_options).Scan(path, cancellationToken);
        return new ScanResult(index, ScanStrategy.Win32Walk, null);
    }

    private ScanResult WalkFallback(char driveLetter, string reason, CancellationToken ct)
    {
        VolumeIndex index = new Win32Walker(_options).Scan($"{driveLetter}:\\", ct);
        return new ScanResult(index, ScanStrategy.Win32Walk, reason);
    }
}
