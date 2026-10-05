using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Actions;

/// <summary>
/// Finds where a destination folder sits in the index — and puts it there when the scan
/// predates it.
/// <para>
/// Without this the most natural way to use "Move to" would be the one the app handles
/// worst: pick the destination, click <em>New folder</em>, and move into something no scan
/// has ever seen. The files would land correctly on disk and the app would have nowhere to
/// show them.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class MoveTarget
{
    /// <summary>
    /// The entry for this folder, adopting it into the index if needed.
    /// </summary>
    /// <returns>
    /// The entry index, or -1 when the folder cannot be placed — another volume, a scan
    /// that walked the API instead of the MFT, or a record number the index already uses
    /// for something else. The caller then has a stale index and has to say so.
    /// </returns>
    public static int Locate(VolumeIndex index, string folderPath) =>
        Locate(index, folderPath, FileIdentity.RecordNumberOf);

    /// <param name="recordOf">Where record numbers come from: the file system, outside the tests.</param>
    internal static int Locate(VolumeIndex index, string folderPath, Func<string, long> recordOf)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return -1;

        string full;
        try { full = Path.GetFullPath(folderPath).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                        or PathTooLongException or IOException)
        {
            return -1;
        }

        int found = index.FindEntry(full);
        if (found >= 0) return index.Entries[found].IsDirectory ? found : -1;

        // Outside an MFT scan the entry numbers are positions in a walk, so a real record
        // number means nothing here and there is no free slot to put it in either.
        if (index.Strategy != ScanStrategy.Mft) return -1;

        if (!Directory.Exists(full)) return -1;

        string? parentPath = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parentPath)) return -1;

        // Recursion depth is the path depth — "D:\a\b\c" at worst. Each level either
        // finds an entry or adopts one, so a fresh chain of folders comes in whole.
        int parent = Locate(index, parentPath, recordOf);
        if (parent < 0) return -1;

        long record = recordOf(full);
        if (record <= 0 || record >= index.Entries.Length) return -1;

        // ⚠️ A folder made a moment ago very often lands on a record the index still gives to
        // a file deleted since the scan — NTFS recycles records fast. This gave up on that, so
        // the most natural way to use "Move to", into a folder just made, ended with the list
        // saying it could not place what had moved; and the quarantine, whose batch folder is
        // made on the spot every time, could end every run the same way. The graft already
        // frees such a record when the disk says its occupant is gone, and so does this now.
        if (!IndexGraft.Claim(index, (int)record, recordOf)) return -1;

        return index.AddDirectory((int)record, parent, Path.GetFileName(full).AsSpan());
    }
}
