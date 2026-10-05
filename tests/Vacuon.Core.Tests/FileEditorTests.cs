using System.Text;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Loading a file to change it, and writing it back as it was.
/// </summary>
public class FileEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-editor-tests-" + Guid.NewGuid().ToString("N"));

    public FileEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ARoundTripWithNoChangeLeavesTheBytesAlone()
    {
        // The whole promise in one test: open a file, save it untouched, get the same file.
        byte[] original = Encoding.UTF8.GetBytes("linha um\r\nlinha dois\r\n");
        string path = Write("plano.txt", original);

        EditableFile file = FileEditor.Load(path);
        Assert.True(file.CanEdit);

        Assert.True(FileEditor.Save(path, file.Text, file).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // "Configuração: ação\r\nvalor=1\r\n" as Notepad saved "ANSI" until 2019: Windows-1252,
    // one byte per accented letter, and not valid UTF-8.
    private static readonly byte[] Ansi =
    [
        0x43, 0x6F, 0x6E, 0x66, 0x69, 0x67, 0x75, 0x72, 0x61, 0xE7, 0xE3, 0x6F, 0x3A, 0x20,
        0x61, 0xE7, 0xE3, 0x6F, 0x0D, 0x0A, 0x76, 0x61, 0x6C, 0x6F, 0x72, 0x3D, 0x31, 0x0D, 0x0A,
    ];

    [Fact]
    public void AnAnsiFile_ChangesOnlyWhereItWasEdited()
    {
        // Measured before the fix: changing "valor=1" to "valor=2" wrote EF BF BD - the
        // replacement character - over all four accents.
        string path = Write("ansi.txt", Ansi);

        EditableFile file = FileEditor.Load(path);
        Assert.True(file.CanEdit);
        Assert.NotEqual("utf-8", file.EncodingName);

        Assert.True(FileEditor.Save(path, file.Text.Replace("valor=1", "valor=2"), file).Succeeded);

        byte[] expected = [.. Ansi];
        expected[^3] = 0x32;
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void ACharacterTheFilesEncodingHasNoPlaceFor_StopsTheSave_InsteadOfBecomingAQuestionMark()
    {
        string path = Write("ansi.txt", Ansi);
        EditableFile file = FileEditor.Load(path);

        SaveResult result = FileEditor.Save(path, file.Text + "\U0001F600", file);

        Assert.Equal(SaveOutcome.CannotEncode, result.Outcome);
        Assert.Equal(Ansi, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));       // and no scratch file left beside it
        Assert.Empty(FileEditor.BytesFor(file.Text + "\U0001F600", file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingKeepsWhatTheFileWasBesidesItsContents(bool asBytes)
    {
        // Measured before the fix: a hidden file created in 2020 came out of one save visible
        // and created today. A move over the original throws the old file away; only the
        // replace carries its attributes, its creation time and its other streams across.
        string path = Write("dados.txt", Encoding.UTF8.GetBytes("um\r\ndois\r\n"));

        // Where a download came from lives in a stream beside the content.
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);

        try
        {
            SaveResult result = asBytes
                ? FileEditor.SaveBytes(path, Encoding.UTF8.GetBytes("um\r\ntres\r\n"))
                : FileEditor.Save(path, "um\r\ntres", FileEditor.Load(path));

            Assert.True(result.Succeeded, result.Message);
            Assert.StartsWith("um\r\ntres", File.ReadAllText(path));

            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
            Assert.Equal(created, File.GetCreationTimeUtc(path));
            Assert.Contains("ZoneId=3", File.ReadAllText(path + ":Zone.Identifier"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void BytesThatDoNotSurviveTheRoundTrip_AreNotOpenedForEditing()
    {
        // Looks like UTF-16 without a BOM - every other byte NUL - with half a surrogate pair
        // in it. Decoded and encoded back, those bytes cannot come out the same.
        byte[] bytes = [.. Encoding.Unicode.GetBytes("ABCDEFGHIJ"), 0x00, 0xD8, .. Encoding.Unicode.GetBytes("KLMNOPQRST")];
        string path = Write("broken.txt", bytes);

        Assert.Equal(EditLoadOutcome.WouldChange, FileEditor.Load(path).Outcome);
    }

    [Fact]
    public void AFileWithUnixEndingsKeepsThem()
    {
        // ⚠️ A text box hands text back as CRLF. Writing that into a file that used LF would
        // rewrite every line of it — a diff nobody asked for, from changing one word.
        byte[] original = Encoding.UTF8.GetBytes("um\ndois\ntres\n");
        string path = Write("unix.sh", original);

        EditableFile file = FileEditor.Load(path);
        Assert.False(file.UsesCrLf);

        FileEditor.Save(path, file.Text.Replace("dois", "DOIS"), file);

        string saved = File.ReadAllText(path);
        Assert.DoesNotContain('\r', saved);
        Assert.Equal("um\nDOIS\ntres\n", saved);
    }

    [Fact]
    public void AFileWithoutABomDoesNotGrowOne()
    {
        // Encoding.GetEncoding("utf-8") emits a preamble. Three bytes at the front break a
        // shebang and some JSON parsers, on a file that never had them.
        byte[] original = Encoding.UTF8.GetBytes("#!/bin/sh\necho oi\n");
        string path = Write("script.sh", original);

        EditableFile file = FileEditor.Load(path);
        Assert.False(file.HasBom);

        FileEditor.Save(path, file.Text, file);

        byte[] saved = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, saved[0]);
        Assert.Equal((byte)'#', saved[0]);
    }

    [Fact]
    public void AFileWithABomKeepsIt()
    {
        // GetBytes never emits the preamble — only a writer does — so it goes on by hand.
        // My first version of this test wrote a file with no BOM and then asserted there
        // was one, which made the code look wrong when the test was.
        string path = Write("com-bom.txt", [.. Encoding.UTF8.GetPreamble(),
                                            .. Encoding.UTF8.GetBytes("conteúdo\r\n")]);

        EditableFile file = FileEditor.Load(path);
        Assert.True(file.HasBom);

        FileEditor.Save(path, file.Text, file);

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(0xEF, saved[0]);
        Assert.Equal(0xBB, saved[1]);
        Assert.Equal(0xBF, saved[2]);
    }

    [Fact]
    public void Utf16SurvivesTheRoundTrip()
    {
        string path = Write("wide.txt", Encoding.Unicode.GetPreamble()
                                                .Concat(Encoding.Unicode.GetBytes("acentuação\r\n"))
                                                .ToArray());

        EditableFile file = FileEditor.Load(path);
        Assert.True(file.CanEdit);
        Assert.Contains("acentuação", file.Text);

        FileEditor.Save(path, file.Text, file);

        Assert.Contains("acentuação", File.ReadAllText(path, Encoding.Unicode));
    }

    [Fact]
    public void AFileTooBigIsRefusedRatherThanOpenedPartly()
    {
        // ⚠️ The bug this prevents is the worst one this screen could have: editing a
        // truncated read and saving it would write the first slice over the whole file.
        string path = Write("grande.bin", new byte[4096]);

        EditableFile file = FileEditor.Load(path, maxBytes: 1024);

        Assert.Equal(EditLoadOutcome.TooBig, file.Outcome);
        Assert.False(file.CanEdit);
        Assert.Empty(file.Text);
        Assert.Equal(4096, file.Bytes);
    }

    [Fact]
    public void BinaryIsNotOfferedAsText()
    {
        string path = Write("bicho.bin", [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x7F, 0x03]);

        Assert.Equal(EditLoadOutcome.NotText, FileEditor.Load(path).Outcome);
    }

    [Fact]
    public void AMissingFileIsUnreadable()
    {
        Assert.Equal(EditLoadOutcome.Unreadable,
                     FileEditor.Load(Path.Combine(_root, "nao-existe.txt")).Outcome);
    }

    [Fact]
    public void AProtectedPathIsRefusedBeforeAnythingIsRead()
    {
        // Said at the door, not after the person spent an edit on it.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        EditableFile file = FileEditor.Load(Path.Combine(windows, "explorer.exe"));

        Assert.Equal(EditLoadOutcome.Protected, file.Outcome);
    }

    [Fact]
    public void SavingIntoAProtectedPathIsRefused()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pretend = new EditableFile(EditLoadOutcome.Loaded, "x", "utf-8", false, true, 1);

        SaveResult result = FileEditor.Save(Path.Combine(windows, "vacuon-nunca.txt"), "x", pretend);

        Assert.Equal(SaveOutcome.Protected, result.Outcome);
    }

    [Fact]
    public void NoLeftoverTemporaryFileIsAbandoned()
    {
        string path = Write("limpo.txt", Encoding.UTF8.GetBytes("um\r\n"));

        EditableFile file = FileEditor.Load(path);
        FileEditor.Save(path, "dois\r\n", file);

        Assert.Empty(Directory.GetFiles(_root, "*.vacuon-edit"));
        Assert.Equal("dois\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void TheBytesHeldForALaterSaveAreTheOnesSaveWouldWrite()
    {
        // ⚠️ A refused save is kept as bytes and written later. Encoding the editor's text as
        // UTF-8 at that point would rewrite a UTF-16 file into UTF-8 and put CRLF into a file
        // that used LF — the two round-trip rules this class exists to keep, undone by the
        // deferral rather than by the save.
        string path = Write("adiado.txt", Encoding.UTF8.GetBytes("um' + B + 'ndois' + B + 'n"));

        EditableFile file = FileEditor.Load(path);
        byte[] held = FileEditor.BytesFor(file.Text, file);

        FileEditor.Save(path, file.Text, file);

        Assert.Equal(File.ReadAllBytes(path), held);
    }

    [Fact]
    public void TheHeldBytesKeepUtf16AndItsMark()
    {
        string path = Write("adiado-wide.txt", [.. Encoding.Unicode.GetPreamble(),
                                                .. Encoding.Unicode.GetBytes("largo' + B + 'r' + B + 'n")]);

        EditableFile file = FileEditor.Load(path);
        byte[] held = FileEditor.BytesFor(file.Text, file);

        Assert.Equal(0xFF, held[0]);
        Assert.Equal(0xFE, held[1]);
        Assert.Contains("largo", Encoding.Unicode.GetString(held), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFlavourOfLineEndingComesBackAsCrLfInTheEditor()
    {
        // A text box cannot show a lone CR as a line break, so the editor works in one
        // flavour and the file's own is restored on the way out.
        string path = Write("misturado.txt", Encoding.UTF8.GetBytes("um\rdois\ntres\r\nquatro"));

        EditableFile file = FileEditor.Load(path);

        Assert.Equal("um\r\ndois\r\ntres\r\nquatro", file.Text);
    }
}
