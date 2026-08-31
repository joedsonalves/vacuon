using Vacuon.Core.Actions;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Overwriting a file before removing it (PRD F7.6) — and, mostly, being honest about when
/// that does not mean what people think it means.
/// </summary>
public class ShredTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-shred-tests-" + Guid.NewGuid().ToString("N"));

    public ShredTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, int bytes, byte fill = 0x41)
    {
        string path = Path.Combine(_root, name);
        var data = new byte[bytes];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void TheFileIsOverwrittenAndThenGone()
    {
        string path = Write("segredo.txt", 200_000);

        ShredResult result = ShredService.Shred(path, volumeIsSolidState: false);

        Assert.Equal(ShredOutcome.Shredded, result.Outcome);
        Assert.False(File.Exists(path));
        Assert.Equal(200_000, result.Bytes);
    }

    [Fact]
    public void OnASolidStateVolumeItSaysSoRatherThanClaimingTheBytesAreGone()
    {
        // ⚠️ The whole point of this feature's honesty. Overwriting works on a spinning
        // disk; on an SSD wear levelling puts the new bytes in different cells and the old
        // ones stay on the drive, unaddressable by the OS and readable by the controller.
        string path = Write("nvme.txt", 4096);

        ShredResult result = ShredService.Shred(path, volumeIsSolidState: true);

        Assert.Equal(ShredOutcome.Shredded, result.Outcome);
        Assert.True(result.IsUncertain);
        Assert.True(result.Doubt.HasFlag(ShredDoubt.SolidState));
    }

    [Fact]
    public void ASmallFileMayLiveInsideItsOwnRecord_AndThatIsSaid()
    {
        // Under about 900 bytes NTFS keeps the contents in the MFT record itself. A write
        // through the file API changes the record; it does not overwrite the old record
        // content cluster by cluster, because there are no clusters.
        string path = Write("bilhete.txt", 300);

        ShredResult result = ShredService.Shred(path, volumeIsSolidState: false);

        Assert.True(result.Doubt.HasFlag(ShredDoubt.MaybeResident));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ACompressedFileWritesElsewhereAndThatIsSaidToo()
    {
        string path = Write("comprimido.log", 400_000, 0x20);
        Assert.Equal(CompressOutcome.Compressed, CompressionService.Compress(path).Outcome);

        var info = new FileInfo(path);
        ShredDoubt doubt = ShredService.DoubtsAbout(info, volumeIsSolidState: false, hasShadowCopies: false);

        // Compressed and sparse files are not rewritten in place: NTFS allocates for the new
        // contents and lets the old clusters go, still holding what was there.
        Assert.True(doubt.HasFlag(ShredDoubt.MovesWhenWritten));
    }

    [Fact]
    public void ShadowCopiesAreADoubtOfTheirOwn()
    {
        string path = Write("com-sombra.txt", 5000);

        ShredResult result = ShredService.Shred(path, volumeIsSolidState: false, hasShadowCopies: true);

        Assert.True(result.Doubt.HasFlag(ShredDoubt.ShadowCopies));
    }

    [Fact]
    public void AVolumeWithNoneOfThoseProblemsGetsNoDoubts()
    {
        // A big, plain file on a spinning disk with no shadow copies: the one case where
        // overwriting does what it says.
        string path = Write("grande.bin", 300_000);
        var info = new FileInfo(path);

        Assert.Equal(ShredDoubt.None,
                     ShredService.DoubtsAbout(info, volumeIsSolidState: false, hasShadowCopies: false));
    }

    [Fact]
    public void AReadOnlyFileIsNotReportedAsShreddedWhileStillSittingThere()
    {
        // A write to a read-only file fails. Reporting success there would be the worst
        // possible outcome for this feature: the file whole, and somebody told it was gone.
        string path = Write("somente-leitura.txt", 9000);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        ShredResult result = ShredService.Shred(path, volumeIsSolidState: false);

        Assert.Equal(ShredOutcome.Shredded, result.Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AProtectedPathIsRefused()
    {
        Assert.Equal(ShredOutcome.Blocked,
                     ShredService.Shred(@"C:\Windows\explorer.exe", volumeIsSolidState: true).Outcome);
    }

    [Fact]
    public void AFolderIsNotAStreamOfBytes()
    {
        string folder = Path.Combine(_root, "pasta");
        Directory.CreateDirectory(folder);

        Assert.Equal(ShredOutcome.Blocked, ShredService.Shred(folder, volumeIsSolidState: false).Outcome);
        Assert.True(Directory.Exists(folder));
    }
}
