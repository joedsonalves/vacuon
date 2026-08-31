using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vacuon.Native.Interop;

/// <summary>
/// Directory junctions — the reparse point that makes one folder answer for another.
/// <para>
/// This is what lets 240 GB of media move to another drive without a single path breaking:
/// the folder is gone from where it was, and a junction stands in its place pointing at
/// where it went. Anything opening the old path lands on the new one, in the file system,
/// below the level where a program could tell.
/// </para>
/// <para>
/// ⚠️ <b>A junction, not a symbolic link, and the difference is who is allowed to make one.</b>
/// A directory symlink needs Developer Mode or an elevated process; a junction needs neither,
/// so this works for somebody running Vacuon normally. The trade is that a junction is local
/// and directory-only — which is exactly this case, since the whole point is a folder that
/// moved to another drive in the same machine.
/// </para>
/// </summary>
public static partial class Junction
{
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code,
                                                byte[] input, int inputSize,
                                                nint output, int outputSize,
                                                out uint returned, nint overlapped);

    /// <summary>
    /// Creates a junction at <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>.
    /// </summary>
    /// <remarks>
    /// The directory has to exist and be empty — the reparse point is set <b>on</b> a
    /// directory, it does not create one. It is created here and removed again if the
    /// control call fails, so a failure leaves nothing behind.
    /// </remarks>
    public static bool Create(string linkPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath)) return false;

        string link = Path.GetFullPath(linkPath);
        string target = Path.GetFullPath(targetPath).TrimEnd('\\');

        if (!Directory.Exists(target)) return false;

        bool created = false;

        if (!Directory.Exists(link))
        {
            Directory.CreateDirectory(link);
            created = true;
        }
        else if (Directory.EnumerateFileSystemEntries(link).Any())
        {
            return false;
        }

        try
        {
            using SafeFileHandle handle = Kernel32.CreateFile(
                link,
                Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
                0,
                0,
                Kernel32.OPEN_EXISTING,
                Kernel32.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                0);

            if (handle.IsInvalid) return Undo(link, created);

            byte[] buffer = Build(target);

            if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length,
                                 0, 0, out _, 0))
            {
                handle.Dispose();
                return Undo(link, created);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Undo(link, created);
        }
    }

    private static bool Undo(string link, bool created)
    {
        if (!created) return false;

        try { Directory.Delete(link); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return false;
    }

    /// <summary>
    /// The REPARSE_DATA_BUFFER a mount point wants, laid out by hand.
    /// <para>
    /// Two names live in it and both are needed: the <b>substitute</b> name is what the file
    /// system follows and carries the <c>\??\</c> device prefix, and the <b>print</b> name is
    /// the plain path that tools show. Writing only the first one produces a junction that
    /// works and that <c>dir</c> displays with an empty target.
    /// </para>
    /// </summary>
    private static byte[] Build(string target)
    {
        string substitute = @"\??\" + target;

        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(target);

        // Both names are NUL-terminated inside the path buffer, and the offsets are byte
        // offsets into it — not indexes, and not character counts.
        int pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;

        // 8 bytes of header (tag, length, reserved) plus 8 of mount-point fields.
        var buffer = new byte[8 + 8 + pathBufferLength];
        int at = 0;

        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, at); at += 4;
        BitConverter.GetBytes((ushort)(8 + pathBufferLength)).CopyTo(buffer, at); at += 2;
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, at); at += 2;   // reserved

        BitConverter.GetBytes((ushort)0).CopyTo(buffer, at); at += 2;                        // substitute offset
        BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, at); at += 2;   // substitute length
        BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, at); at += 2;  // print offset
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, at); at += 2;        // print length

        substituteBytes.CopyTo(buffer, at); at += substituteBytes.Length + 2;
        printBytes.CopyTo(buffer, at);

        return buffer;
    }

    /// <summary>Whether this path is a junction — a directory that stands for another.</summary>
    public static bool Exists(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Where a junction points, or null when it is not one.</summary>
    public static string? TargetOf(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
