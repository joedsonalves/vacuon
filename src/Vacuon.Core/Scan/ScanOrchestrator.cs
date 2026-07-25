using System.Runtime.Versioning;
using Vacuon.Core.Index;
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

public sealed record ScanResult(VolumeIndex Index, ScanStrategy StrategyUsed, string? FallbackReason);

/// <summary>
/// Escolhe a estratégia de varredura por volume e cai em cascata quando a preferida
/// não está disponível (PRD §7.1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScanOrchestrator(MftScanOptions? options = null)
{
    private readonly MftScanOptions _options = options ?? new MftScanOptions();

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
                        "leitura da MFT exige executar como Administrador",
                    VolumeAccessFailure.NotNtfs =>
                        "o volume não é NTFS (exFAT/FAT32/ReFS não têm MFT)",
                    VolumeAccessFailure.MftUnreadable =>
                        $"a MFT não pôde ser interpretada ({ex.Message})",
                    _ => ex.Message,
                };

                return WalkFallback(driveLetter, reason, cancellationToken);
            }
        }

        return WalkFallback(driveLetter, "estratégia de travessia solicitada explicitamente", cancellationToken);
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
