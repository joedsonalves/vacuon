using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vacuon.Native.Interop;

/// <summary>
/// Handle bruto para um volume (<c>\\.\C:</c>). Exige privilégio de administrador.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;

    public char DriveLetter { get; }
    public NtfsVolumeData VolumeData { get; }
    public bool IncursSeekPenalty { get; }

    private VolumeDevice(char driveLetter, SafeFileHandle handle, NtfsVolumeData data, bool seekPenalty)
    {
        DriveLetter = driveLetter;
        _handle = handle;
        VolumeData = data;
        IncursSeekPenalty = seekPenalty;
        _stream = new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: false);
    }

    /// <summary>
    /// Abre o volume e consulta os dados do NTFS. Lança <see cref="VolumeAccessException"/>
    /// com um motivo classificado — o chamador decide se cai para outra estratégia.
    /// </summary>
    public static VolumeDevice Open(char driveLetter)
    {
        string path = $@"\\.\{char.ToUpperInvariant(driveLetter)}:";

        SafeFileHandle handle = Kernel32.CreateFile(
            path,
            Kernel32.GENERIC_READ,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            0,
            Kernel32.OPEN_EXISTING,
            Kernel32.FILE_FLAG_BACKUP_SEMANTICS | Kernel32.FILE_FLAG_SEQUENTIAL_SCAN,
            0);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new VolumeAccessException(
                err == Kernel32.ERROR_ACCESS_DENIED
                    ? VolumeAccessFailure.NeedsElevation
                    : VolumeAccessFailure.CannotOpen,
                $"Não foi possível abrir {path}: {new Win32Exception(err).Message}");
        }

        NtfsVolumeData data = QueryNtfsVolumeData(handle, path);
        bool seekPenalty = QuerySeekPenalty(driveLetter);

        return new VolumeDevice(driveLetter, handle, data, seekPenalty);
    }

    private static NtfsVolumeData QueryNtfsVolumeData(SafeFileHandle handle, string path)
    {
        int size = Marshal.SizeOf<NtfsVolumeData>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = Kernel32.DeviceIoControl(
                handle, Kernel32.FSCTL_GET_NTFS_VOLUME_DATA,
                0, 0, buffer, (uint)size, out _, 0);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                // Volume não-NTFS devolve ERROR_INVALID_FUNCTION / ERROR_NOT_SUPPORTED.
                throw new VolumeAccessException(
                    err is Kernel32.ERROR_INVALID_FUNCTION or Kernel32.ERROR_NOT_SUPPORTED
                        ? VolumeAccessFailure.NotNtfs
                        : VolumeAccessFailure.CannotOpen,
                    $"FSCTL_GET_NTFS_VOLUME_DATA falhou em {path}: {new Win32Exception(err).Message}");
            }

            return Marshal.PtrToStructure<NtfsVolumeData>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// HDD ou SSD? Define o paralelismo de I/O — paralelizar seek em disco mecânico piora.
    /// Falha silenciosa assume SSD (o padrão moderno), sem quebrar a varredura.
    /// </summary>
    private static bool QuerySeekPenalty(char driveLetter)
    {
        try
        {
            using SafeFileHandle h = Kernel32.CreateFile(
                $@"\\.\{char.ToUpperInvariant(driveLetter)}:",
                0, // sem GENERIC_READ: consulta de propriedade não precisa
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                0, Kernel32.OPEN_EXISTING, 0, 0);

            if (h.IsInvalid) return false;

            var query = new StoragePropertyQuery
            {
                PropertyId = 7,  // StorageDeviceSeekPenaltyProperty
                QueryType = 0,   // PropertyStandardQuery
            };

            int inSize = Marshal.SizeOf<StoragePropertyQuery>();
            int outSize = Marshal.SizeOf<DeviceSeekPenaltyDescriptor>();
            nint inBuf = Marshal.AllocHGlobal(inSize);
            nint outBuf = Marshal.AllocHGlobal(outSize);
            try
            {
                Marshal.StructureToPtr(query, inBuf, false);
                if (!Kernel32.DeviceIoControl(h, Kernel32.IOCTL_STORAGE_QUERY_PROPERTY,
                        inBuf, (uint)inSize, outBuf, (uint)outSize, out _, 0))
                {
                    return false;
                }

                return Marshal.PtrToStructure<DeviceSeekPenaltyDescriptor>(outBuf).IncursSeekPenalty;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lê bytes crus do volume a partir de um deslocamento absoluto.</summary>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        _stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length)
        {
            int read = _stream.Read(buffer[total..]);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}

public enum VolumeAccessFailure
{
    NeedsElevation,
    NotNtfs,
    CannotOpen,
    MftUnreadable,
}

public sealed class VolumeAccessException(VolumeAccessFailure failure, string message)
    : Exception(message)
{
    public VolumeAccessFailure Failure { get; } = failure;
}
