using Vacuon.App.Infra;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Monitoring;

namespace Vacuon.App.ViewModels;

/// <summary>Um cartão de volume no painel.</summary>
public sealed class VolumeCardViewModel(VolumeInfo volume) : Observable
{
    public VolumeInfo Volume { get; } = volume;

    public char DriveLetter => Volume.DriveLetter;
    public string Header => $"{Volume.DriveLetter}:";
    public string Label => Volume.Label;
    public string FileSystem => Volume.FileSystem;

    public string TotalText => Format.Bytes(Volume.TotalBytes);
    public string FreeText => Format.Bytes(Volume.FreeBytes);
    public string UsedText => Format.Bytes(Volume.UsedBytes);

    public double UsedPercent =>
        Volume.TotalBytes <= 0 ? 0 : Volume.UsedBytes * 100.0 / Volume.TotalBytes;

    public string UsedPercentText => Format.Percent(UsedPercent);

    /// <summary>
    /// Acima de 90% a barra vira vermelha. É o único uso de cor de risco fora do
    /// módulo de segurança, e é literal: o disco está em risco de encher.
    /// </summary>
    public bool IsCritical => UsedPercent >= 90;

    /// <summary>Só volume NTFS tem MFT — sem isso a varredura rápida não existe.</summary>
    public bool SupportsFastScan =>
        Volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public string ScanHintText => SupportsFastScan
        ? L.T("volumes.ntfsReady")
        : L.T("volumes.noMft", Volume.FileSystem);

    // ---------------------------------------------------------------
    // Trend (F8.2)

    private VolumeTrend? _trend;

    /// <summary>
    /// What free space has been doing, from the readings on file. Null until the history has
    /// been read; a volume with no readings still gets a trend object, one that refuses.
    /// </summary>
    public VolumeTrend? Trend
    {
        get => _trend;
        set
        {
            _trend = value;

            Raise(nameof(Trend));
            Raise(nameof(TrendArrow));
            Raise(nameof(TrendText));
            Raise(nameof(HasProjection));
        }
    }

    /// <summary>
    /// The arrow needs far less evidence than a date does, so it shows whenever there are
    /// readings at all — a direction is a summary of what happened, not a claim about
    /// what will.
    /// </summary>
    public string TrendArrow => _trend?.Direction switch
    {
        -1 => "▼",
        1 => "▲",
        _ => string.Empty,
    };

    public bool HasProjection => _trend?.HasProjection == true;

    /// <summary>
    /// Either the projection, or why there is not one.
    /// <para>
    /// Never blank when there is a reason. A widget that simply shows nothing reads as broken,
    /// and the honest answer — "six hours of readings is not a forecast" — is more useful than
    /// an empty line and much more useful than a made-up date.
    /// </para>
    /// </summary>
    public string TrendText
    {
        get
        {
            if (_trend is null) return string.Empty;

            if (_trend.HasProjection)
            {
                double days = _trend.DaysUntilFull!.Value;

                return L.T("trend.fullIn",
                           days < 1 ? L.T("trend.today") : Format.Days(days),
                           Format.Bytes((long)Math.Abs(_trend.BytesPerDay)));
            }

            return _trend.Refusal switch
            {
                ProjectionRefusal.TooFewReadings => L.T("trend.needsMore", SpaceTrend.MinimumReadings),
                ProjectionRefusal.SpanTooShort => L.T("trend.needsLonger", (int)SpaceTrend.MinimumSpan.TotalHours),
                ProjectionRefusal.NotFilling => L.T("trend.notFilling"),
                ProjectionRefusal.FitTooPoor => L.T("trend.tooNoisy"),
                ProjectionRefusal.BeyondHorizon => L.T("trend.notSoon"),
                _ => string.Empty,
            };
        }
    }
}
