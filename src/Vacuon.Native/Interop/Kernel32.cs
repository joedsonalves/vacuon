using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vacuon.Native.Interop;

/// <summary>
/// P/Invoke para kernel32. Só o que o motor de varredura realmente usa.
/// </summary>
public static partial class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;

    public const uint OPEN_EXISTING = 3;

    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_HANDLE_EOF = 38;

    // FSCTL_GET_NTFS_VOLUME_DATA = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 25, METHOD_BUFFERED, FILE_ANY_ACCESS)
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    // FSCTL_GET_RETRIEVAL_POINTERS = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 28, METHOD_NEITHER, FILE_ANY_ACCESS)
    public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;

    // FSCTL_QUERY_USN_JOURNAL = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 61, METHOD_BUFFERED, FILE_ANY_ACCESS)
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    // FSCTL_READ_USN_JOURNAL = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 46, METHOD_NEITHER, FILE_ANY_ACCESS).
    // METHOD_NEITHER is why the low byte is 0xBB and not 0xB8 — worth spelling out,
    // because a wrong control code fails with a generic "invalid function".
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

    // FSCTL_ENUM_USN_DATA = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 44, METHOD_NEITHER, FILE_ANY_ACCESS)
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;

    // IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS)
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    /// <summary>
    /// Estado da memória física da máquina. Os campos vêm em bytes, e o
    /// <c>dwLength</c> tem de ser preenchido antes da chamada.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>
    /// Esvazia o working set de um processo.
    /// <para>
    /// As páginas não são liberadas: vão para a lista de standby ou para o pagefile, e o
    /// processo as traz de volta — do disco — assim que precisar. É o que os "limpadores de
    /// RAM" chamam de liberar memória, e é por isso que a interface do Vacuon diz na tela o
    /// que realmente aconteceu.
    /// </para>
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(IntPtr process);

    /// <summary>
    /// Adds a second name to a file that already exists — the same bytes, another directory
    /// entry. Fails when the new name is taken, when the two are on different volumes, or on
    /// a file system that has no such thing.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(
        string lpRootPathName,
        [Out] char[] lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}

/// <summary>
/// BY_HANDLE_FILE_INFORMATION — o que <c>GetFileInformationByHandle</c> devolve.
/// <para>
/// The two halves of nFileIndex are the file id: on NTFS its low 48 bits are the MFT
/// record number and the top 16 are the sequence number. It is the only way to learn
/// the record number of a folder without reading the MFT — which needs elevation.
/// </para>
/// <para>
/// ⚠️ <b><c>Pack = 4</c> is load-bearing, and it was missing.</b> A FILETIME is two DWORDs
/// and sits on a four-byte boundary; a C# <c>long</c> wants eight. Without the packing the
/// runtime slid four bytes of padding in after <c>dwFileAttributes</c>, every field from the
/// first FILETIME on was read four bytes late, and <c>nFileIndexLow</c> was read from past
/// the end of the 52 bytes the API actually filled.
/// </para>
/// <para>
/// Measured, on a folder whose id <c>fsutil file queryfileid</c> reported as
/// <c>0x0014000000073 4AA</c> — record 471,722, sequence 20. This struct handed back
/// <c>57,904,749,084,672</c>, which is that record number shifted into the high half beside
/// a zero. Nothing threw: every caller guards with "record number outside the index, give
/// up", so a folder created after the scan was simply never adopted and the list quietly
/// stayed behind the disk. There is a test on the size now, because 52 against 56 is the
/// whole bug and it is one number.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ByHandleFileInformation
{
    public uint dwFileAttributes;
    public long ftCreationTime;
    public long ftLastAccessTime;
    public long ftLastWriteTime;
    public uint dwVolumeSerialNumber;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint nNumberOfLinks;
    public uint nFileIndexHigh;
    public uint nFileIndexLow;
}

/// <summary>
/// Reads the MFT record number of a path that exists.
/// <para>
/// Opening a handle with no access rights at all is enough to ask for the id, so this
/// works in an ordinary session — no elevation, and no share violation against whoever
/// else has the file open.
/// </para>
/// </summary>
public static class FileIdentity
{
    /// <summary>The NTFS record number, or -1 when it could not be read.</summary>
    public static long RecordNumberOf(string path)
    {
        try
        {
            using SafeFileHandle handle = Kernel32.CreateFile(
                path,
                0,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
                0,
                Kernel32.OPEN_EXISTING,
                // Without BACKUP_SEMANTICS, CreateFile refuses to open a directory.
                Kernel32.FILE_FLAG_BACKUP_SEMANTICS,
                0);

            if (handle.IsInvalid) return -1;

            if (!Kernel32.GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                return -1;

            long id = ((long)info.nFileIndexHigh << 32) | info.nFileIndexLow;

            // Drop the sequence number: the index is addressed by record number alone.
            return id & 0x0000_FFFF_FFFF_FFFFL;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>FILE_ID_DESCRIPTOR, in its FileId form.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public uint Type;        // 0 = FileIdType
        public long FileId;
        private readonly long _padding;   // the union is 16 bytes wide (GUID form)
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHandle, in FileIdDescriptor fileId, uint desiredAccess,
        uint shareMode, nint securityAttributes, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle, [Out] char[] path, uint size, uint flags);

    /// <summary>
    /// The path a file reference points at, or null when it no longer resolves.
    /// <para>
    /// The reverse of <see cref="RecordNumberOf"/>, and the piece the change-journal monitor
    /// needs: a USN record names a parent by <b>id</b>, never by path. Resolving is a real
    /// open, so it fails for anything already deleted — which on a busy volume is a great
    /// deal of what the journal just reported, and is why the caller must handle null rather
    /// than treat it as an error.
    /// </para>
    /// </summary>
    public static string? PathFromFileId(string volumeRoot, ulong fileReference)
    {
        try
        {
            // The volume must be opened as a directory to serve as OpenFileById's anchor.
            using SafeFileHandle volume = Kernel32.CreateFile(
                volumeRoot,
                Kernel32.GENERIC_READ,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
                0,
                Kernel32.OPEN_EXISTING,
                Kernel32.FILE_FLAG_BACKUP_SEMANTICS,
                0);

            if (volume.IsInvalid) return null;

            var descriptor = new FileIdDescriptor
            {
                Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
                Type = 0,
                FileId = unchecked((long)fileReference),
            };

            using SafeFileHandle handle = OpenFileById(
                volume, descriptor, 0,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
                0, Kernel32.FILE_FLAG_BACKUP_SEMANTICS);

            if (handle.IsInvalid) return null;

            var buffer = new char[1024];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);

            if (length == 0 || length >= buffer.Length) return null;

            string path = new(buffer, 0, (int)length);

            // GetFinalPathNameByHandle returns the \?\ form; callers want the plain one.
            // The \\?\ prefix, four characters. Getting this literal wrong is silent:
            // the path still works for the file APIs, but it reaches the screen as
            // \\?\C:\Users\... and every consumer that inspects it for a drive letter or a
            // question mark starts misbehaving. That is exactly what happened — the
            // monitor stopped measuring folder sizes because its own guard saw the "?".
            const string ExtendedPrefix = @"\\?\";

            return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
                ? path[ExtendedPrefix.Length..]
                : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException
                                        or EntryPointNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>
/// NTFS_VOLUME_DATA_BUFFER — resposta do FSCTL_GET_NTFS_VOLUME_DATA.
/// Evita ter que parsear o boot sector na mão e, mais importante, entrega
/// MftValidDataLength (quantos bytes da MFT são reais, não apenas alocados).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NtfsVolumeData
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

[StructLayout(LayoutKind.Sequential)]
public struct StoragePropertyQuery
{
    public uint PropertyId;
    public uint QueryType;
    public byte AdditionalParameters;
}

[StructLayout(LayoutKind.Sequential)]
public struct DeviceSeekPenaltyDescriptor
{
    public uint Version;
    public uint Size;
    [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
}
