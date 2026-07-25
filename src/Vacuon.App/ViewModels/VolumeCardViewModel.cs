using Vacuon.App.Infra;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;

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
}
