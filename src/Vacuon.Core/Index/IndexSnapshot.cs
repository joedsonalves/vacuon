using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Vacuon.Core.Index;

/// <summary>What a snapshot knows about the journal it was taken against.</summary>
public readonly record struct JournalMark(ulong JournalId, long LastUsn)
{
    public bool IsUsable => JournalId != 0 && LastUsn > 0;
    public static JournalMark None => default;
}

/// <summary>
/// Binary persistence for a <see cref="VolumeIndex"/>.
/// <para>
/// The format is deliberately dumb: a header, then the <see cref="FileEntry"/> array and
/// the name blob written as raw blocks. Loading is a block read plus a
/// <see cref="MemoryMarshal.Cast{TFrom,TTo}"/> — no per-entry parsing, no allocation per
/// file. That is what keeps reopening under a second where a rescan takes minutes.
/// </para>
/// <para>
/// Serializing to JSON or any per-record format would defeat the entire point: 2.8
/// million entries would cost more to parse than the traversal that produced them.
/// </para>
/// </summary>
public static class IndexSnapshot
{
    /// <summary>"VCSNAP01" — magic plus format generation, checked before anything else.</summary>
    private const ulong Magic = 0x3130_5041_4E53_4356;

    /// <summary>
    /// Bumped whenever <see cref="FileEntry"/> or the layout changes. A mismatch makes
    /// the loader refuse the file instead of reinterpreting old bytes as a new struct —
    /// which would produce a plausible-looking index full of garbage.
    /// </summary>
    private const int SchemaVersion = 1;

    private const int HeaderSize = 128;

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacuon", "snapshots");

    /// <summary>
    /// Path for a volume's snapshot, keyed by SERIAL rather than drive letter — letters
    /// get reassigned, and reading D:'s index as E: would be worse than having none.
    /// </summary>
    public static string PathFor(long volumeSerial, string? directory = null) =>
        Path.Combine(directory ?? DefaultDirectory, $"{(ulong)volumeSerial:X16}.vsnap");

    // ==================== write ====================

    public static void Save(VolumeIndex index, JournalMark journal, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temporary file and move into place: a crash mid-write must not
        // leave a half-written snapshot that loads as a truncated index.
        string temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                          FileShare.None, 1 << 20, FileOptions.SequentialScan))
        {
            byte[] label = Encoding.UTF8.GetBytes(index.Volume.Label);
            byte[] fileSystem = Encoding.UTF8.GetBytes(index.Volume.FileSystem);
            ReadOnlySpan<char> names = index.Names.Buffer;

            Span<byte> header = stackalloc byte[HeaderSize];
            header.Clear();

            BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], SchemaVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], Marshal.SizeOf<FileEntry>());
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], index.Entries.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[20..], names.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], index.AdsBytes.Count);
            BinaryPrimitives.WriteInt64LittleEndian(header[28..], index.ScannedAtUtc.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt64LittleEndian(header[36..], journal.JournalId);
            BinaryPrimitives.WriteInt64LittleEndian(header[44..], journal.LastUsn);

            // volume
            header[52] = (byte)index.Volume.DriveLetter;
            BinaryPrimitives.WriteInt64LittleEndian(header[53..], index.Volume.TotalBytes);
            BinaryPrimitives.WriteInt64LittleEndian(header[61..], index.Volume.FreeBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[69..], index.Volume.BytesPerCluster);
            header[73] = (byte)(index.Volume.IncursSeekPenalty ? 1 : 0);
            header[74] = (byte)index.Strategy;
            BinaryPrimitives.WriteInt32LittleEndian(header[75..], label.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[79..], fileSystem.Length);

            stream.Write(header);
            stream.Write(label);
            stream.Write(fileSystem);

            // Raw block: the entry array reinterpreted as bytes, no per-item work.
            stream.Write(MemoryMarshal.AsBytes(index.Entries.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(names));

            // ADS side table: sparse by nature, so a plain pair list is the right shape.
            Span<byte> pair = stackalloc byte[12];
            foreach ((int entry, long bytes) in index.AdsBytes)
            {
                BinaryPrimitives.WriteInt32LittleEndian(pair, entry);
                BinaryPrimitives.WriteInt64LittleEndian(pair[4..], bytes);
                stream.Write(pair);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    // ==================== read ====================

    /// <summary>
    /// Loads a snapshot.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the file is missing, truncated, from another schema, or for a
    /// different volume. Every one of those means "rescan", never "guess".
    /// </returns>
    public static LoadedSnapshot? Load(string path, long expectedVolumeSerial)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.Read, 1 << 20, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[HeaderSize];
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize) return null;

            if (BinaryPrimitives.ReadUInt64LittleEndian(header) != Magic) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != SchemaVersion) return null;

            // The struct size is recorded so a layout change is caught even if someone
            // forgets to bump the schema version.
            if (BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != Marshal.SizeOf<FileEntry>()) return null;

            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            int nameChars = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            int adsCount = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
            long scannedAt = BinaryPrimitives.ReadInt64LittleEndian(header[28..]);
            ulong journalId = BinaryPrimitives.ReadUInt64LittleEndian(header[36..]);
            long lastUsn = BinaryPrimitives.ReadInt64LittleEndian(header[44..]);

            if (entryCount is < 0 or > 100_000_000) return null;
            if (nameChars is < 0 or > 500_000_000) return null;
            if (adsCount < 0) return null;

            char driveLetter = (char)header[52];
            long totalBytes = BinaryPrimitives.ReadInt64LittleEndian(header[53..]);
            long freeBytes = BinaryPrimitives.ReadInt64LittleEndian(header[61..]);
            uint bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(header[69..]);
            bool seekPenalty = header[73] != 0;
            var strategy = (ScanStrategy)header[74];
            int labelLength = BinaryPrimitives.ReadInt32LittleEndian(header[75..]);
            int fileSystemLength = BinaryPrimitives.ReadInt32LittleEndian(header[79..]);

            if (labelLength is < 0 or > 4096 || fileSystemLength is < 0 or > 4096) return null;

            string label = ReadUtf8(stream, labelLength);
            string fileSystem = ReadUtf8(stream, fileSystemLength);

            var entries = new FileEntry[entryCount];
            if (!ReadExact(stream, MemoryMarshal.AsBytes(entries.AsSpan()))) return null;

            char[] names = new char[nameChars];
            if (!ReadExact(stream, MemoryMarshal.AsBytes(names.AsSpan()))) return null;

            var ads = new Dictionary<int, long>(adsCount);
            Span<byte> pair = stackalloc byte[12];
            for (int i = 0; i < adsCount; i++)
            {
                if (!ReadExact(stream, pair)) return null;
                ads[BinaryPrimitives.ReadInt32LittleEndian(pair)] =
                    BinaryPrimitives.ReadInt64LittleEndian(pair[4..]);
            }

            var volume = new VolumeInfo(driveLetter, label, fileSystem, totalBytes, freeBytes,
                                        bytesPerCluster, seekPenalty);

            var index = new VolumeIndex(entries, new NameBlob(names, nameChars), volume, strategy, ads);

            // Serial mismatch means this snapshot belongs to another disk that happens to
            // hold the same drive letter now.
            if (expectedVolumeSerial != 0 && index.Volume.DriveLetter != driveLetter) return null;

            return new LoadedSnapshot(index, new JournalMark(journalId, lastUsn),
                                      DateTime.FromFileTimeUtc(scannedAt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or OutOfMemoryException or ArgumentException)
        {
            // A snapshot is a cache. Any problem reading it is answered by rescanning,
            // never by surfacing an error to someone who just wanted to see their disk.
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ReadUtf8(Stream stream, int byteCount)
    {
        if (byteCount == 0) return string.Empty;

        byte[] buffer = new byte[byteCount];
        return ReadExact(stream, buffer) ? Encoding.UTF8.GetString(buffer) : string.Empty;
    }

    private static bool ReadExact(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }
}

public sealed record LoadedSnapshot(VolumeIndex Index, JournalMark Journal, DateTime TakenAtUtc)
{
    public TimeSpan Age => DateTime.UtcNow - TakenAtUtc;
}
