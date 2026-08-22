using Vacuon.Core.Monitoring;
using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M9. The monitor itself needs an elevated handle on a volume with a change
/// journal, so what is covered here is the arithmetic and the path handling around it —
/// including the prefix bug that made the whole thing measure zero.
/// </summary>
public class DiskMonitorTests
{
    [Fact]
    public void ActivityTotalsAddUp()
    {
        var folder = new FolderActivity(@"C:\x", Created: 3, Deleted: 2, Modified: 5, BytesAdded: 100);
        Assert.Equal(10, folder.Total);
    }

    [Fact]
    public void ASnapshotSumsWhatItsFoldersGained()
    {
        var snapshot = new ActivitySnapshot(
        [
            new FolderActivity(@"C:\a", 1, 0, 0, 1_000),
            new FolderActivity(@"C:\b", 2, 0, 0, 2_500),
        ], RecordsRead: 3, FreeBytes: 100, FreeBytesDelta: -3_500, Elapsed: TimeSpan.Zero);

        Assert.Equal(3_500, snapshot.BytesAdded);
    }

    [Fact]
    public void FreeSpaceDeltaKeepsItsSign()
    {
        // A volume that GAINED space is as interesting as one losing it — something large
        // was deleted. Storing the magnitude only would flatten the two into one number.
        var losing = new ActivitySnapshot([], 0, 100, -5_000, TimeSpan.Zero);
        var gaining = new ActivitySnapshot([], 0, 100, 5_000, TimeSpan.Zero);

        Assert.True(losing.FreeBytesDelta < 0);
        Assert.True(gaining.FreeBytesDelta > 0);
    }

    [Fact]
    public void AResolvedPathCarriesNoExtendedPrefix()
    {
        // The bug this test exists for. GetFinalPathNameByHandle returns the \\?\ form, and
        // the literal that strips it was written with the wrong number of backslashes — a
        // mistake that compiles, and whose only symptom was folder sizes reading as zero,
        // because the monitor's own guard rejects a path containing '?'.
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        long id = FileIdentity.RecordNumberOf(Environment.SystemDirectory);
        if (id < 0) return;   // no NTFS handle available on this machine

        string? path = FileIdentity.PathFromFileId(root, (ulong)id);
        if (path is null) return;   // the id did not resolve; nothing to assert

        Assert.DoesNotContain('?', path);
        Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileIdRoundTripsToTheSameFolder()
    {
        // Confirms the two halves agree: a path gives an id, and the id gives the path back.
        // Without this the monitor would silently attribute activity to the wrong folder.
        string directory = Path.Combine(Path.GetTempPath(), $"vacuon-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            long id = FileIdentity.RecordNumberOf(directory);
            if (id < 0) return;

            string root = Path.GetPathRoot(directory) ?? @"C:\";
            string? resolved = FileIdentity.PathFromFileId(root, (ulong)id);

            // The id carries no sequence number here, so a resolve can legitimately fail;
            // when it succeeds it must land on the folder we started from.
            if (resolved is null) return;

            Assert.Equal(directory.TrimEnd('\\'), resolved.TrimEnd('\\'), ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void StartingOnAVolumeWithoutAJournalReturnsNullRatherThanThrowing()
    {
        // A drive letter nothing is mounted on. The monitor has to answer "cannot watch
        // this" without an exception, because the caller's next line is a message to a user.
        using DiskMonitor? monitor = DiskMonitor.Start('Q');
        Assert.Null(monitor);
    }
}
