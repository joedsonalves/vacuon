using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Scan;

/// <summary>Descoberta e descrição de volumes, sem depender de elevação.</summary>
[SupportedOSPlatform("windows")]
public static class VolumeProbe
{
    public static IReadOnlyList<VolumeInfo> EnumerateFixedVolumes()
    {
        var list = new List<VolumeInfo>();

        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

            try
            {
                list.Add(Describe(d));
            }
            catch (IOException)
            {
                // Unidade sumiu entre o enumerar e o descrever. Ignorar é o certo.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    public static VolumeInfo Describe(DriveInfo d)
    {
        uint clusterBytes = TryGetClusterSize(d.Name);

        return new VolumeInfo(
            DriveLetter: char.ToUpperInvariant(d.Name[0]),
            Label: string.IsNullOrWhiteSpace(d.VolumeLabel) ? "(sem rótulo)" : d.VolumeLabel,
            FileSystem: d.DriveFormat,
            TotalBytes: d.TotalSize,
            FreeBytes: d.TotalFreeSpace,
            BytesPerCluster: clusterBytes,
            IncursSeekPenalty: false);
    }

    internal static VolumeInfo Describe(char driveLetter, VolumeDevice device)
    {
        var d = new DriveInfo($"{driveLetter}:\\");
        NtfsVolumeData vd = device.VolumeData;

        string label;
        string fs;
        long total;
        long free;

        try
        {
            label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "(sem rótulo)" : d.VolumeLabel;
            fs = d.DriveFormat;
            total = d.TotalSize;
            free = d.TotalFreeSpace;
        }
        catch (IOException)
        {
            label = "(indisponível)";
            fs = "NTFS";
            total = vd.TotalClusters * vd.BytesPerCluster;
            free = vd.FreeClusters * vd.BytesPerCluster;
        }

        return new VolumeInfo(
            DriveLetter: char.ToUpperInvariant(driveLetter),
            Label: label,
            FileSystem: fs,
            TotalBytes: total,
            FreeBytes: free,
            BytesPerCluster: vd.BytesPerCluster,
            IncursSeekPenalty: device.IncursSeekPenalty);
    }

    private static uint TryGetClusterSize(string root)
    {
        try
        {
            using VolumeDevice device = VolumeDevice.Open(root[0]);
            return device.VolumeData.BytesPerCluster;
        }
        catch (VolumeAccessException)
        {
            return 4096; // padrão do NTFS moderno; só afeta estimativa de slack
        }
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
