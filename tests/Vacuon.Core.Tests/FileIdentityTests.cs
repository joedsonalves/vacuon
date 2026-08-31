using System.Runtime.InteropServices;
using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The record number of a path that exists, which is what lets a folder created after the
/// scan be adopted into the live index instead of waiting for the next one.
/// </summary>
public class FileIdentityTests
{
    [Fact]
    public void TheStructIsTheFiftyTwoBytesTheApiFills()
    {
        // BY_HANDLE_FILE_INFORMATION is 52 bytes: a DWORD, three FILETIMEs of two DWORDs
        // each, then five DWORDs. Declaring the FILETIMEs as long without Pack = 4 makes it
        // 56 — four bytes of padding after the first field, every field after it read late,
        // and nFileIndexLow read from past the end of what the API wrote.
        Assert.Equal(52, Marshal.SizeOf<ByHandleFileInformation>());
    }

    [Fact]
    public void ARecordNumberIsARecordNumber_NotAFileIdWithTheSequenceLeftIn()
    {
        // The value has to be small enough to index the MFT. The bug this guards produced
        // 57,904,749,084,672 for a record that is 471,722 — a number no index will ever hold
        // a slot for, so every caller gave up and nothing said why.
        string path = Path.Combine(Path.GetTempPath(), "vacuon-id-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[16]);

        try
        {
            long record = FileIdentity.RecordNumberOf(path);

            Assert.True(record > 0, "record number should be readable for a file that exists");
            Assert.True(record < uint.MaxValue,
                        $"record number {record} is too large to be an MFT record index");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TwoFilesAreTwoRecords()
    {
        string first = Path.Combine(Path.GetTempPath(), "vacuon-id-a-" + Guid.NewGuid().ToString("N"));
        string second = Path.Combine(Path.GetTempPath(), "vacuon-id-b-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(first, new byte[16]);
        File.WriteAllBytes(second, new byte[16]);

        try
        {
            Assert.NotEqual(FileIdentity.RecordNumberOf(first), FileIdentity.RecordNumberOf(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void AFolderHasOneToo()
    {
        // Directories need FILE_FLAG_BACKUP_SEMANTICS to open at all, and adopting a folder
        // created after the scan is the whole reason this exists.
        string path = Path.Combine(Path.GetTempPath(), "vacuon-id-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        try
        {
            long record = FileIdentity.RecordNumberOf(path);

            Assert.True(record > 0);
            Assert.True(record < uint.MaxValue);
        }
        finally
        {
            Directory.Delete(path);
        }
    }
}
