using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Vacuon.Core.Index;

namespace Vacuon.Core.Analyzers;

/// <summary>What kind of space this is, because none of them is deleted the same way.</summary>
public enum SystemSpaceKind
{
    /// <summary>`pagefile.sys` — virtual memory. Resized through System properties, never deleted.</summary>
    PageFile,
    /// <summary>`swapfile.sys` — the Store apps' companion to the page file.</summary>
    SwapFile,
    /// <summary>`hiberfil.sys` — turned off with `powercfg /h off`, which is what removes it.</summary>
    Hibernation,
    /// <summary>Restore points and previous versions. `vssadmin` resizes or deletes them.</summary>
    ShadowCopies,
}

/// <summary>
/// One block of space that belongs to Windows rather than to a file somebody chose to keep.
/// </summary>
/// <param name="Bytes">What it occupies, or -1 when it exists and the size is not knowable.</param>
/// <param name="Detail">
/// A second reading where there is one — the page file's own figure from the system, beside
/// the one the index measured — or null.
/// </param>
public readonly record struct SystemSpaceItem(
    SystemSpaceKind Kind,
    string Path,
    long Bytes,
    string? Detail = null)
{
    public bool IsKnown => Bytes >= 0;
}

/// <summary>
/// The space that never shows up in a folder tree (PRD F1.9).
/// <para>
/// A volume can be missing twenty gigabytes that no file explains: the page file, the
/// hibernation file, and the shadow copies behind System Restore and "previous versions".
/// They are the answer to "the folders add up to 300 GB and Windows says 340", and without
/// them the app's own cross-check has to report a discrepancy it cannot explain.
/// </para>
/// <para>
/// ⚠️ <b>Read only.</b> Nothing here deletes or resizes anything: the page file is changed
/// through System properties, hibernation through <c>powercfg /h off</c>, and shadow copies
/// through <c>vssadmin</c> — the cleanup rules already call those, deliberately, instead of
/// this app removing files that Windows owns.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemSpace
{
    /// <summary>
    /// What the three big system files occupy on this volume, read from the index.
    /// <para>
    /// From the index rather than from the disk because the MFT already measured them, and
    /// because <c>pagefile.sys</c> cannot be opened at all — a size read through the file
    /// system comes back as an access error while the record in the MFT states it plainly.
    /// </para>
    /// </summary>
    public static List<SystemSpaceItem> InIndex(VolumeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var found = new List<SystemSpaceItem>(3);

        Add(index, found, "pagefile.sys", SystemSpaceKind.PageFile);
        Add(index, found, "swapfile.sys", SystemSpaceKind.SwapFile);
        Add(index, found, "hiberfil.sys", SystemSpaceKind.Hibernation);

        return found;
    }

    private static void Add(VolumeIndex index, List<SystemSpaceItem> into, string name, SystemSpaceKind kind)
    {
        string path = index.Volume.Root + name;
        int entry = index.FindEntry(path);
        if (entry < 0) return;

        ref FileEntry file = ref index.Entries[entry];

        // Allocated, not logical: these are the clusters the volume cannot use for anything
        // else, which is the question being asked.
        long bytes = file.AllocatedSize > 0 ? file.AllocatedSize : file.LogicalSize;
        into.Add(new SystemSpaceItem(kind, path, bytes));
    }

    /// <summary>
    /// What the shadow copies hold, asked of the system rather than guessed at.
    /// <para>
    /// ⚠️ Read through WMI, not by parsing <c>vssadmin list shadowstorage</c>. That command
    /// prints <c>Used Shadow Copy Storage space: 5,53 GB (1%)</c> on this machine — English
    /// labels with a comma for a decimal point, because the words and the numbers follow
    /// different settings. Parsing it means guessing at a separator and a unit; WMI hands
    /// back <c>5939675136</c>, which needs no guessing at all. Same number, checked.
    /// </para>
    /// <para>
    /// Returns an item with <see cref="SystemSpaceItem.Bytes"/> of -1 when the query could
    /// not run — reading "no shadow copies" out of a failed question would be inventing an
    /// answer, and this is a volume where twenty gigabytes can hide.
    /// </para>
    /// </summary>
    public static SystemSpaceItem ShadowCopies(string volumeRoot, TimeSpan? timeout = null)
    {
        string root = string.IsNullOrWhiteSpace(volumeRoot) ? "C:\\" : volumeRoot;

        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");

        // One line, three raw numbers, no formatting to undo on this side.
        info.ArgumentList.Add(
            "$s = Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue; " +
            "if ($null -eq $s) { 'none' } else { " +
            "$u = ($s | Measure-Object -Property UsedSpace -Sum).Sum; " +
            "$a = ($s | Measure-Object -Property AllocatedSpace -Sum).Sum; " +
            "$m = ($s | Measure-Object -Property MaxSpace -Sum).Sum; " +
            "\"$u $a $m\" }");

        try
        {
            using var process = Process.Start(info);
            if (process is null) return Unknown(root);

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return Unknown(root);
            }

            return Parse(root, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                        or FileNotFoundException)
        {
            return Unknown(root);
        }
    }

    /// <summary>Three integers, or nothing. Anything else is not an answer.</summary>
    public static SystemSpaceItem Parse(string volumeRoot, string output)
    {
        string trimmed = (output ?? string.Empty).Trim();

        if (trimmed.Length == 0) return Unknown(volumeRoot);
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new SystemSpaceItem(SystemSpaceKind.ShadowCopies, volumeRoot, 0);

        string[] fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3) return Unknown(volumeRoot);

        if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long used) ||
            !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long allocated) ||
            !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long max))
        {
            return Unknown(volumeRoot);
        }

        // Used is what the copies hold; allocated is what the service has taken and will not
        // give back on its own; max is the ceiling it was told to respect. All three, because
        // "5.5 GB used of 9.5 GB allowed" is a different sentence from "5.5 GB used".
        return new SystemSpaceItem(SystemSpaceKind.ShadowCopies, volumeRoot, used,
                                   $"{allocated}/{max}");
    }

    private static SystemSpaceItem Unknown(string volumeRoot) =>
        new(SystemSpaceKind.ShadowCopies, volumeRoot, -1);
}
