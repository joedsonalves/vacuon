using System.Text;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M3: looking at a file before deleting it. The judgement being tested is "is
/// this text?", because getting it wrong either dumps binary garbage into a text pane or
/// hides a readable file behind a hex dump.
/// </summary>
public class FilePreviewTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vacuon-preview-tests", Guid.NewGuid().ToString("N"));

    public FilePreviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void PlainTextIsReadAsText()
    {
        string path = Write("notes.txt", Encoding.UTF8.GetBytes("hello\nworld\n"));

        PreviewContent preview = FilePreview.Read(path);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Contains("hello", preview.Text, StringComparison.Ordinal);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void TextWithAccentsSurvivesTheRoundTrip()
    {
        // This project has already corrupted two whole files by guessing an encoding wrong.
        string path = Write("pt.txt", Encoding.UTF8.GetBytes("configuração não é ação"));

        PreviewContent preview = FilePreview.Read(path);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Contains("configuração", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABomIsNotShownAsAStrayCharacter()
    {
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes("clean"));

        PreviewContent preview = FilePreview.Read(Write("bom.txt", [.. bytes]));

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.StartsWith("clean", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf16WithABomIsText()
    {
        string path = Write("utf16.txt", Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("wide text")).ToArray());

        PreviewContent preview = FilePreview.Read(path);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Contains("wide text", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf16WithoutABomIsStillText()
    {
        // No BOM, and half the bytes are NUL — the naive "any NUL means binary" test calls
        // this a binary file and hides a perfectly readable one behind a hex dump.
        byte[] bytes = Encoding.Unicode.GetBytes("plain wide text with no marker at all");

        PreviewContent preview = FilePreview.Read(Write("nobom.txt", bytes));

        Assert.Equal(PreviewKind.Text, preview.Kind);
    }

    [Fact]
    public void ABinaryFileIsShownAsHex()
    {
        var bytes = new byte[512];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 256);

        PreviewContent preview = FilePreview.Read(Write("blob.bin", bytes));

        Assert.Equal(PreviewKind.Binary, preview.Kind);
        Assert.Contains("00000000", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHexDumpShowsPrintableCharactersOnTheRight()
    {
        // The column that makes a hex dump worth showing: a file with the wrong extension
        // gives itself away because its magic bytes are legible.
        var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };   // "PK.." — a zip
        bytes.AddRange(new byte[60]);

        PreviewContent preview = FilePreview.Read(Write("actually.zip", [.. bytes]));

        Assert.Equal(PreviewKind.Binary, preview.Kind);
        Assert.Contains("50 4B", preview.Text, StringComparison.Ordinal);
        Assert.Contains("PK", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstSliceIsRead()
    {
        // A 4 GB log answers "what is this?" in its first kilobyte. Reading all of it would
        // stall the UI on the one screen where responsiveness is the product.
        var bytes = new byte[200_000];
        Encoding.UTF8.GetBytes("start of a very long log").CopyTo(bytes, 0);
        for (int i = 24; i < bytes.Length; i++) bytes[i] = (byte)'x';

        PreviewContent preview = FilePreview.Read(Write("huge.log", bytes), maxBytes: 4096);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal(4096, preview.BytesRead);
        Assert.Equal(200_000, preview.FileBytes);
        Assert.True(preview.Truncated);
    }

    [Fact]
    public void AnEmptyFileHasNoPreview()
    {
        PreviewContent preview = FilePreview.Read(Write("empty.txt", []));

        Assert.Equal(PreviewKind.None, preview.Kind);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void AMissingFileIsNotAnException()
    {
        PreviewContent preview = FilePreview.Read(Path.Combine(_root, "nope.txt"));
        Assert.Equal(PreviewKind.None, preview.Kind);
    }

    [Fact]
    public void AFileBeingWrittenToCanStillBePreviewed()
    {
        // A log that is open for writing is exactly the kind of file someone wants to look
        // at before deleting. Taking an exclusive lock would refuse the useful case.
        string path = Path.Combine(_root, "live.log");

        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.UTF8.GetBytes("line one\n"));
        writer.Flush();

        PreviewContent preview = FilePreview.Read(path);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Contains("line one", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedMultiByteCharacterDoesNotLeaveADiamond()
    {
        // Cutting UTF-8 mid-sequence decodes to the replacement character; ending every
        // truncated preview with a stray diamond looks like corruption.
        byte[] bytes = Encoding.UTF8.GetBytes("ação ação ação ação");

        PreviewContent preview = FilePreview.Read(Write("cut.txt", bytes), maxBytes: 6);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.DoesNotContain('�', preview.Text);
    }
}
