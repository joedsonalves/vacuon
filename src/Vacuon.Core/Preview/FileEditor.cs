using System.Runtime.Versioning;
using System.Text;
using Vacuon.Core.Safety;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Preview;

/// <summary>Why a file could or could not be opened for editing.</summary>
public enum EditLoadOutcome
{
    Loaded,
    /// <summary>Bigger than <see cref="FileEditor.MaxEditableBytes"/>.</summary>
    TooBig,
    /// <summary>Nothing decoded it as text. Hex editing is a different door.</summary>
    NotText,
    Unreadable,
    /// <summary>Under a path the app never writes to.</summary>
    Protected,
}

/// <summary>
/// A file loaded for editing, with everything needed to write it back as it was.
/// </summary>
/// <param name="UsesCrLf">
/// Which line ending the file had.
/// <para>
/// Kept because a WPF text box works in <c>\r\n</c> and hands it back that way. Saving that
/// into a file that used <c>\n</c> would rewrite every line of it — a diff the person did not
/// ask for, on a file they opened to change one word.
/// </para>
/// </param>
public sealed record EditableFile(
    EditLoadOutcome Outcome,
    string Text,
    string EncodingName,
    bool HasBom,
    bool UsesCrLf,
    long Bytes)
{
    public bool CanEdit => Outcome == EditLoadOutcome.Loaded;
}

public enum SaveOutcome
{
    Saved,
    /// <summary>Something has the file open and will not share it.</summary>
    InUse,
    Protected,
    Failed,
}

/// <summary>
/// The result of a save, and — when it failed for being in use — who is holding it.
/// </summary>
public sealed record SaveResult(SaveOutcome Outcome, string? Message, IReadOnlyList<FileHolder> Holders)
{
    public bool Succeeded => Outcome == SaveOutcome.Saved;

    public static SaveResult Ok() => new(SaveOutcome.Saved, null, []);
}

/// <summary>
/// Loading a file to change it, and writing it back.
/// <para>
/// ⚠️ <b>This reads the whole file, unlike <see cref="FilePreview"/>, and the difference is
/// the point.</b> The preview reads the first 64 KiB because it answers "what is this?".
/// Editing on top of a truncated read and then saving would write those 64 KiB over the
/// original and destroy everything after them — the worst bug this screen could have. So a
/// file that does not fit is <b>refused</b>, with the reason, rather than opened partly.
/// </para>
/// <para>
/// Encoding, byte-order mark and line ending are carried across a round trip. A person who
/// opened a file to change one word should get back a file that differs by one word.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileEditor
{
    /// <summary>
    /// The ceiling for editing. Generous for anything anybody edits by hand, and far below
    /// what would make the window stop responding while a text box lays it out.
    /// </summary>
    public const long MaxEditableBytes = 8 * 1024 * 1024;

    public static EditableFile Load(string path, long maxBytes = MaxEditableBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new EditableFile(EditLoadOutcome.Unreadable, string.Empty, string.Empty, false, true, 0);

        // Said before the file is opened rather than after the save fails: the person should
        // not spend an edit on something that was never going to be written.
        if (ProtectedPaths.IsProtected(path))
            return new EditableFile(EditLoadOutcome.Protected, string.Empty, string.Empty, false, true, 0);

        byte[] bytes;
        long length;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return new EditableFile(EditLoadOutcome.Unreadable, string.Empty, string.Empty, false, true, 0);

            length = info.Length;

            if (length > maxBytes)
                return new EditableFile(EditLoadOutcome.TooBig, string.Empty, string.Empty, false, true, length);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);

            bytes = new byte[(int)length];
            int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return new EditableFile(EditLoadOutcome.Unreadable, string.Empty, string.Empty, false, true, 0);
        }

        Encoding? encoding = FilePreview.DetectText(bytes);

        if (encoding is null)
            return new EditableFile(EditLoadOutcome.NotText, string.Empty, string.Empty, false, true, length);

        bool bom = HasBom(bytes, encoding);
        string text = FilePreview.Decode(bytes, encoding);

        // A file with no line ending at all is written back with the platform's, which is
        // what a new line typed into it would have been anyway.
        bool crlf = !text.Contains('\n') || text.Contains("\r\n", StringComparison.Ordinal);

        return new EditableFile(EditLoadOutcome.Loaded, Normalise(text), encoding.WebName, bom, crlf, length);
    }

    /// <summary>
    /// Writes the text back, in the encoding and line ending it came with.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Written to a temporary file beside the original and then moved over it.</b> A
    /// write straight into the file truncates it first, so a failure halfway — the disk
    /// filling, the process dying — leaves a file that is neither the old one nor the new
    /// one. The move is the step that makes the change all-or-nothing.
    /// </remarks>
    public static SaveResult Save(string path, string text, EditableFile original)
    {
        ArgumentNullException.ThrowIfNull(original);

        ProtectionVerdict verdict = ProtectedPaths.Check(path);
        if (verdict.IsProtected) return new SaveResult(SaveOutcome.Protected, verdict.Reason.ToString(), []);

        Encoding encoding = EncodingOf(original.EncodingName, original.HasBom);
        string body = original.UsesCrLf ? Normalise(text) : Normalise(text).Replace("\r\n", "\n");

        string temporary = path + ".vacuon-edit";

        try
        {
            File.WriteAllText(temporary, body, encoding);

            // Overwrites in one step, and keeps the original's attributes and stream by
            // replacing rather than deleting first.
            File.Move(temporary, path, overwrite: true);

            return SaveResult.Ok();
        }
        catch (IOException ex)
        {
            Clean(temporary);

            // The file being held is the common failure and the only one with a way out, so
            // it is reported with the name of whoever is holding it rather than as an error
            // code the person can do nothing with.
            IReadOnlyList<FileHolder> holders = WhoHolds(path);

            return holders.Count > 0
                ? new SaveResult(SaveOutcome.InUse, ex.Message, holders)
                : new SaveResult(SaveOutcome.Failed, ex.Message, []);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            Clean(temporary);

            IReadOnlyList<FileHolder> holders = WhoHolds(path);

            return holders.Count > 0
                ? new SaveResult(SaveOutcome.InUse, ex.Message, holders)
                : new SaveResult(SaveOutcome.Failed, ex.Message, []);
        }
    }

    private static IReadOnlyList<FileHolder> WhoHolds(string path)
    {
        try { return RestartManager.WhoHolds(path); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }

    private static void Clean(string temporary)
    {
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Every line ending becomes CRLF, which is what a text box works in.</summary>
    internal static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    private static bool HasBom(byte[] bytes, Encoding encoding)
    {
        ReadOnlySpan<byte> preamble = encoding.GetPreamble();

        return preamble.Length > 0
               && bytes.Length >= preamble.Length
               && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble);
    }

    /// <summary>
    /// The encoding to write with, rebuilt so the byte-order mark matches what was there.
    /// </summary>
    /// <remarks>
    /// <c>Encoding.GetEncoding</c> hands back UTF-8 <b>with</b> a preamble, so a file that had
    /// none would silently grow three bytes at the front — enough to break a shell script's
    /// shebang or a JSON parser that is stricter than most.
    /// </remarks>
    private static Encoding EncodingOf(string name, bool bom) => name switch
    {
        "utf-16" => new UnicodeEncoding(bigEndian: false, byteOrderMark: bom),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: bom),
        "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom),
    };
}
