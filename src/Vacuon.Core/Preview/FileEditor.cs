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
    /// <summary>
    /// Its bytes would not come back the same: neither UTF-8 nor this system's ANSI code page
    /// reads them and writes them back unchanged. See <see cref="FileEditor.Load"/>.
    /// </summary>
    WouldChange,
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
    /// <summary>A character typed has no place in the file's encoding. Nothing was written.</summary>
    CannotEncode,
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
    // The ANSI code pages - 1252 and its neighbours - are not in .NET until asked for.
    static FileEditor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

        Encoding? detected = FilePreview.DetectText(bytes);

        if (detected is null)
            return new EditableFile(EditLoadOutcome.NotText, string.Empty, string.Empty, false, true, length);

        // ⚠️ Opened for editing only in an encoding the file's bytes survive unchanged. The
        // detection says UTF-8 for anything without a BOM or NULs - including the Windows-1252
        // that Notepad saved as "ANSI" until 2019 - and bytes that are not UTF-8 decode to the
        // replacement character. Measured: changing "valor=1" to "valor=2" in a 1252 file with
        // "Configuração: ação" in it wrote EF BF BD over every accent, four characters
        // destroyed by an edit that touched none of them.
        Encoding? encoding = Lossless(bytes, detected);

        if (encoding is null)
            return new EditableFile(EditLoadOutcome.WouldChange, string.Empty, string.Empty, false, true, length);

        bool bom = HasBom(bytes, encoding);
        int skip = bom ? encoding.GetPreamble().Length : 0;
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);

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

        // Encoded in memory, all of it, before the disk is touched: a character the file's
        // encoding has no place for stops the save here, rather than going in as a "?".
        if (!TryEncode(text, original, out byte[] content))
            return new SaveResult(SaveOutcome.CannotEncode, original.EncodingName, []);

        string temporary = path + ".vacuon-edit";

        try
        {
            File.WriteAllBytes(temporary, content);
            Swap(temporary, path);

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

    /// <summary>
    /// The ceiling for editing bytes, far below the text one.
    /// <para>
    /// A hex dump is about four and a half characters per byte, so a megabyte of file is
    /// four and a half million characters in a text box. The limit is what the window can
    /// lay out, not what the disk can read.
    /// </para>
    /// </summary>
    public const long MaxEditableBytesAsHex = 1024 * 1024;

    /// <summary>A file opened for editing byte by byte.</summary>
    public sealed record EditableBytes(EditLoadOutcome Outcome, byte[] Bytes, string Dump, long FileBytes)
    {
        public bool CanEdit => Outcome == EditLoadOutcome.Loaded;
    }

    /// <summary>
    /// Reads the whole file and renders every byte of it as a dump.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Every byte, not the preview's sample.</b> The preview reads 64 KiB and stops the
    /// dump at 512 lines, which is eight kilobytes on screen. Editing that and writing it
    /// back would replace the file with its first eight kilobytes.
    /// </remarks>
    public static EditableBytes LoadBytes(string path, long maxBytes = MaxEditableBytesAsHex)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new EditableBytes(EditLoadOutcome.Unreadable, [], string.Empty, 0);

        if (ProtectedPaths.IsProtected(path))
            return new EditableBytes(EditLoadOutcome.Protected, [], string.Empty, 0);

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new EditableBytes(EditLoadOutcome.Unreadable, [], string.Empty, 0);

            if (info.Length > maxBytes)
                return new EditableBytes(EditLoadOutcome.TooBig, [], string.Empty, info.Length);

            byte[] bytes = File.ReadAllBytes(path);

            return new EditableBytes(EditLoadOutcome.Loaded, bytes,
                                     FilePreview.Hex(bytes, int.MaxValue), bytes.LongLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return new EditableBytes(EditLoadOutcome.Unreadable, [], string.Empty, 0);
        }
    }

    /// <summary>
    /// Writes bytes back, the same all-or-nothing way text is written.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>There is no undo for this and the app does not pretend otherwise.</b> Changing a
    /// byte of a program is not the kind of edit a quarantine helps with — the file keeps its
    /// name, its size and its place, and only stops working. The screen says so before the
    /// first keystroke, in the same tier as "Delete forever".
    /// </remarks>
    public static SaveResult SaveBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        ProtectionVerdict verdict = ProtectedPaths.Check(path);
        if (verdict.IsProtected) return new SaveResult(SaveOutcome.Protected, verdict.Reason.ToString(), []);

        string temporary = path + ".vacuon-edit";

        try
        {
            File.WriteAllBytes(temporary, bytes);
            Swap(temporary, path);

            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            Clean(temporary);

            IReadOnlyList<FileHolder> holders = WhoHolds(path);

            return holders.Count > 0
                ? new SaveResult(SaveOutcome.InUse, ex.Message, holders)
                : new SaveResult(SaveOutcome.Failed, ex.Message, []);
        }
    }

    /// <summary>
    /// The exact bytes <see cref="Save"/> would write for this text.
    /// </summary>
    /// <remarks>
    /// ⚠️ Exists so a refused save can be held on to as <b>bytes</b> and written later without
    /// the encoding decision being made twice. Encoding the editor's text as UTF-8 at the
    /// point of queueing would quietly rewrite a UTF-16 file into UTF-8, and would put CRLF
    /// into a file that used LF — the two round-trip rules this class exists to keep.
    /// </remarks>
    public static byte[] BytesFor(string text, EditableFile original)
    {
        ArgumentNullException.ThrowIfNull(original);

        // Empty when the text cannot be encoded - and then the save it would have queued has
        // already failed for that reason, so there is nothing to queue.
        return TryEncode(text, original, out byte[] content) ? content : [];
    }

    /// <summary>
    /// The exact bytes the text becomes in the file's own encoding, line endings and BOM, or
    /// false when a character in it has no place in that encoding.
    /// </summary>
    private static bool TryEncode(string text, EditableFile original, out byte[] content)
    {
        Encoding encoding = EncodingOf(original.EncodingName, original.HasBom);
        string body = original.UsesCrLf ? Normalise(text) : Normalise(text).Replace("\r\n", "\n");

        try
        {
            content = [.. encoding.GetPreamble(), .. encoding.GetBytes(body)];
            return true;
        }
        catch (EncoderFallbackException)
        {
            content = [];
            return false;
        }
    }

    /// <summary>
    /// The encoding <paramref name="bytes"/> survive a round trip through, or null when none
    /// of the candidates hands them back unchanged.
    /// <para>
    /// UTF-8 is what the detection guesses when it has nothing else to go on. When the bytes
    /// are not UTF-8, the ANSI code page this Windows uses is the next candidate, because that
    /// is what every program that wrote "ANSI" meant by it. Each one is checked the same way:
    /// decode strictly, encode strictly, and compare with the original byte for byte.
    /// </para>
    /// </summary>
    private static Encoding? Lossless(byte[] bytes, Encoding detected)
    {
        if (RoundTrips(bytes, detected)) return detected;

        if (detected.CodePage != Encoding.UTF8.CodePage) return null;

        // Code page 0 is this machine's ANSI code page, once the provider is registered.
        Encoding ansi = Encoding.GetEncoding(0);

        return ansi.CodePage != detected.CodePage && RoundTrips(bytes, ansi) ? ansi : null;
    }

    private static bool RoundTrips(byte[] bytes, Encoding encoding)
    {
        Encoding strict = Encoding.GetEncoding(encoding.CodePage,
                                               EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        int skip = HasBom(bytes, encoding) ? encoding.GetPreamble().Length : 0;

        try
        {
            string text = strict.GetString(bytes, skip, bytes.Length - skip);
            return strict.GetBytes(text).AsSpan().SequenceEqual(bytes.AsSpan(skip));
        }
        catch (Exception ex) when (ex is DecoderFallbackException or EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the freshly written file in the original's place, keeping what the original was
    /// besides its contents.
    /// <para>
    /// ⚠️ <c>File.Replace</c>, not <c>File.Move</c> with overwrite. This used to say the move
    /// "keeps the original's attributes and stream by replacing rather than deleting first",
    /// and it did not: a move over a file throws the old one away, and the new one is the
    /// scratch file with nothing of the original's. Measured: a hidden file created in 2020
    /// came out of one save visible and created today. <c>ReplaceFile</c> is the call that
    /// carries over the replaced file's attributes, creation time, permissions and alternate
    /// streams — the Zone.Identifier that says where a download came from among them.
    /// </para>
    /// </summary>
    private static void Swap(string replacement, string original) =>
        File.Replace(replacement, original, destinationBackupFileName: null);

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
    /// <remarks>
    /// Every one of them throws on a character it cannot write rather than writing a "?" in
    /// its place: <see cref="TryEncode"/> turns that into a save that says why it did not
    /// happen.
    /// </remarks>
    private static Encoding EncodingOf(string name, bool bom) => name switch
    {
        "utf-16" => new UnicodeEncoding(bigEndian: false, byteOrderMark: bom, throwOnInvalidBytes: true),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: bom, throwOnInvalidBytes: true),
        "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom, throwOnInvalidBytes: true),
        _ => Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
    };
}
