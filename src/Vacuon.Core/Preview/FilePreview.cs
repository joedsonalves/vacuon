using System.Text;

namespace Vacuon.Core.Preview;

/// <summary>What kind of preview a file can have.</summary>
public enum PreviewKind
{
    /// <summary>Nothing readable — empty, unreadable, or a directory.</summary>
    None,
    /// <summary>Decodable as text.</summary>
    Text,
    /// <summary>Not text. Shown as hex.</summary>
    Binary,
}

/// <summary>The first slice of a file, decoded or dumped.</summary>
public sealed record PreviewContent(
    PreviewKind Kind,
    string Text,
    long FileBytes,
    int BytesRead,
    string? EncodingName = null)
{
    /// <summary>True when the file is longer than what was read.</summary>
    public bool Truncated => BytesRead < FileBytes;
}

/// <summary>
/// Reads the beginning of a file so it can be looked at before being deleted.
/// <para>
/// Only the first slice is ever read. The point is to answer "what is this?" about a file
/// someone is deciding to remove, and a 4 GB log answers that in its first kilobyte — while
/// reading all of it would stall the UI on the one screen where responsiveness is the whole
/// product.
/// </para>
/// </summary>
public static class FilePreview
{
    /// <summary>How much is read. Enough to fill a preview pane several times over.</summary>
    public const int MaxBytes = 64 * 1024;

    public static PreviewContent Read(string path, int maxBytes = MaxBytes)
    {
        byte[] buffer;
        long length;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return new PreviewContent(PreviewKind.None, string.Empty, 0, 0);

            length = info.Length;

            // FileShare.ReadWrite: a log being written to right now is exactly the kind of
            // file someone wants to look at, and locking it would be rude and pointless.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);

            buffer = new byte[(int)Math.Min(maxBytes, length)];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            if (read < buffer.Length) Array.Resize(ref buffer, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return new PreviewContent(PreviewKind.None, string.Empty, 0, 0);
        }

        if (buffer.Length == 0) return new PreviewContent(PreviewKind.None, string.Empty, length, 0);

        Encoding? encoding = DetectText(buffer);

        return encoding is null
            ? new PreviewContent(PreviewKind.Binary, Hex(buffer), length, buffer.Length)
            : new PreviewContent(PreviewKind.Text, Decode(buffer, encoding), length, buffer.Length,
                                 encoding.WebName);
    }

    /// <summary>
    /// Decides whether a slice of bytes is text, and in what encoding.
    /// <para>
    /// A BOM settles it. Without one, the test is a NUL byte: UTF-8 and the single-byte
    /// code pages never contain one, and every common binary format does within its first
    /// few hundred bytes. It is a cheap heuristic, and it is wrong about UTF-16 without a
    /// BOM — which is why that case is checked separately before giving up.
    /// </para>
    /// </summary>
    internal static Encoding? DetectText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8;

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

        int nulls = 0;
        int control = 0;

        foreach (byte b in bytes)
        {
            if (b == 0) nulls++;
            else if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;
        }

        // UTF-16 without a BOM: ASCII text in it is every other byte NUL, so a slice that is
        // roughly a third to two thirds NUL and otherwise printable is very likely UTF-16.
        if (nulls > bytes.Length / 4 && nulls < bytes.Length * 3 / 4 && control < bytes.Length / 20)
            return Encoding.Unicode;

        if (nulls > 0) return null;

        // Control characters that are not tab, newline or carriage return do not belong in
        // text. A few could be a stray escape sequence; many mean this is not text at all.
        if (control > bytes.Length / 50) return null;

        return Encoding.UTF8;
    }

    internal static string Decode(byte[] bytes, Encoding encoding)
    {
        string text = encoding.GetString(bytes).TrimStart('﻿');

        // The last character of a truncated slice can be half a multi-byte sequence, which
        // decodes to the replacement character. Dropping a trailing one avoids ending every
        // truncated preview with a stray diamond.
        return text.Length > 0 && text[^1] == '�' ? text[..^1] : text;
    }

    /// <summary>
    /// Classic hex dump: offset, sixteen bytes, then the printable characters.
    /// <para>
    /// The right-hand column is what makes it useful — it is where the magic bytes of a
    /// format are legible, so a file with the wrong extension gives itself away by eye.
    /// </para>
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> bytes, int maxLines = 512)
    {
        var text = new StringBuilder(maxLines * 78);
        int lines = 0;

        for (int offset = 0; offset < bytes.Length && lines < maxLines; offset += 16, lines++)
        {
            int count = Math.Min(16, bytes.Length - offset);

            text.Append(offset.ToString("X8")).Append("  ");

            for (int i = 0; i < 16; i++)
            {
                text.Append(i < count ? bytes[offset + i].ToString("X2") : "  ").Append(' ');
                if (i == 7) text.Append(' ');
            }

            text.Append(' ');

            for (int i = 0; i < count; i++)
            {
                byte b = bytes[offset + i];
                text.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
