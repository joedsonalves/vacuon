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
