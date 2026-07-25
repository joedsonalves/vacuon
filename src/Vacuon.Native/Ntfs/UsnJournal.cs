using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vacuon.Native.Interop;

namespace Vacuon.Native.Ntfs;

/// <summary>
/// Reads the NTFS change journal.
/// <para>
/// This is what makes reopening the app cheap: instead of walking the whole volume
/// again, ask NTFS what changed since a known point. On a machine that has been idle
/// the answer is a handful of records.
/// </para>
/// <para>
/// Needs the same elevated volume handle as the MFT read — the journal is not exposed
/// through any user-level API.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsnJournal(VolumeDevice device)
{
    /// <summary>Reads the journal's identity and valid range.</summary>
    /// <returns><c>null</c> when the volume has no active journal.</returns>
    public UsnJournalData? Query()
    {
        int size = Marshal.SizeOf<UsnJournalData>();
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            bool ok = Kernel32.DeviceIoControl(
                device.Handle, Kernel32.FSCTL_QUERY_USN_JOURNAL,
                0, 0, buffer, (uint)size, out uint returned, 0);

            // ERROR_JOURNAL_NOT_ACTIVE (0x49B) / ERROR_JOURNAL_DELETE_IN_PROGRESS: the
            // volume simply has no journal to read. Not an error worth throwing over.
            if (!ok || returned < size) return null;

            return Marshal.PtrToStructure<UsnJournalData>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads records from <paramref name="startUsn"/> forward, invoking
    /// <paramref name="onRecord"/> for each.
    /// </summary>
    /// <param name="journalId">
    /// Must match the journal currently on the volume; the FSCTL fails otherwise, which
    /// is exactly the protection we want against applying deltas from a recreated journal.
    /// </param>
    /// <param name="reasonMask">Which changes to report. See <see cref="UsnReason.IndexRelevant"/>.</param>
    /// <returns>The USN to continue from next time.</returns>
    public long Read(ulong journalId, long startUsn, UsnReason reasonMask,
                     RecordHandler onRecord, CancellationToken cancellationToken = default)
    {
        // 64 KB per call is the size the journal APIs are tuned for; larger buffers do
        // not reduce the number of round trips much because the driver caps a batch.
        const int BufferSize = 64 * 1024;

        byte[] buffer = new byte[BufferSize];
        long next = startUsn;

        var request = new ReadUsnJournalData
        {
            StartUsn = startUsn,
            ReasonMask = (uint)reasonMask,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,   // 0 = do not block waiting for new records
            UsnJournalID = journalId,
        };

        int requestSize = Marshal.SizeOf<ReadUsnJournalData>();
        nint requestBuffer = Marshal.AllocHGlobal(requestSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                request.StartUsn = next;
                Marshal.StructureToPtr(request, requestBuffer, false);

                uint returned;
                bool ok;

                unsafe
                {
                    fixed (byte* output = buffer)
                    {
                        ok = Kernel32.DeviceIoControl(
                            device.Handle, Kernel32.FSCTL_READ_USN_JOURNAL,
                            requestBuffer, (uint)requestSize,
                            (nint)output, BufferSize, out returned, 0);
                    }
                }

                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();

                    // ERROR_JOURNAL_ENTRY_DELETED: the records we asked for were purged
                    // because the journal wrapped. The caller must fall back to a scan.
                    if (error == ErrorJournalEntryDeleted)
                        throw new UsnJournalWrappedException(startUsn);

                    throw new VolumeAccessException(VolumeAccessFailure.CannotOpen,
                        $"FSCTL_READ_USN_JOURNAL failed: {new Win32Exception(error).Message}");
                }

                // Fewer bytes than the prefix means there is nothing left to read.
                if (returned < UsnLayout.NextUsnPrefixSize) break;

                long batchNext = BinaryPrimitives.ReadInt64LittleEndian(buffer);

                int offset = UsnLayout.NextUsnPrefixSize;
                int consumed = 0;

                while (offset + UsnLayout.MinimumRecordSize <= returned)
                {
                    int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
                    if (length < UsnLayout.MinimumRecordSize || offset + length > returned) break;

                    UsnRecord record = Parse(buffer.AsSpan(offset, length));
                    if (record.IsValid) onRecord(ref record);

                    offset += length;
                    consumed++;
                }

                // No progress and no records: the journal is caught up.
                if (batchNext == next && consumed == 0) break;

                next = batchNext;

                // The batch had only the prefix — nothing more to fetch right now.
                if (consumed == 0) break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(requestBuffer);
        }

        return next;
    }

    public delegate void RecordHandler(ref UsnRecord record);

    private const int ErrorJournalEntryDeleted = 1181;

    /// <summary>
    /// Parses one <c>USN_RECORD_V2</c>.
    /// <para>
    /// Version 3 and 4 records exist (128-bit file ids on ReFS, range tracking) and are
    /// skipped rather than misread — the record layout differs after the header, and
    /// guessing would corrupt the index silently.
    /// </para>
    /// </summary>
    public static UsnRecord Parse(ReadOnlySpan<byte> record)
    {
        var result = default(UsnRecord);
        if (record.Length < UsnLayout.MinimumRecordSize) return result;

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record[UsnLayout.MajorVersion..]);
        if (major != 2) return result;

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[UsnLayout.FileNameLength..]);
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[UsnLayout.FileNameOffset..]);

        if (nameOffset < UsnLayout.MinimumRecordSize - 2) return result;
        if (nameOffset + nameLength > record.Length) return result;
        if ((nameLength & 1) != 0) return result;   // UTF-16: byte count must be even

        result.IsValid = true;
        result.FileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(record[UsnLayout.FileReferenceNumber..]);
        result.ParentFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(record[UsnLayout.ParentFileReferenceNumber..]);
        result.Usn = BinaryPrimitives.ReadInt64LittleEndian(record[UsnLayout.Usn..]);
        result.Reason = (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(record[UsnLayout.Reason..]);
        result.Attributes = (NtfsFileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[UsnLayout.FileAttributes..]);
        result.FileName = MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength));

        return result;
    }
}

/// <summary>
/// The journal discarded the records we needed. The index cannot be brought up to date
/// from it, so the caller has to rescan.
/// </summary>
public sealed class UsnJournalWrappedException(long requestedUsn)
    : Exception($"USN {requestedUsn} is no longer in the journal — records were purged.")
{
    public long RequestedUsn { get; } = requestedUsn;
}
