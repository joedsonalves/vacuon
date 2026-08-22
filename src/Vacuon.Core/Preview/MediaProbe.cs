using System.Runtime.Versioning;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Preview;

/// <summary>
/// What Windows knows about one media file.
/// <para>
/// Every field is nullable, and that is the point: a property the shell did not answer is
/// <c>null</c>, never zero and never a guess from the extension. "Unknown" and "zero" lead
/// to different decisions — one means look closer, the other means there is nothing there.
/// </para>
/// </summary>
public sealed record MediaInfo
{
    public TimeSpan? Duration { get; init; }

    public uint? Width { get; init; }
    public uint? Height { get; init; }
    public double? FrameRate { get; init; }

    /// <summary>Video codec as a FourCC, when the handler reports one.</summary>
    public string? VideoCodec { get; init; }
    public ulong? VideoBitrate { get; init; }

    public string? AudioCodec { get; init; }
    public ulong? AudioBitrate { get; init; }
    public uint? SampleRate { get; init; }
    public uint? Channels { get; init; }

    public string? CameraModel { get; init; }
    public DateTime? DateTaken { get; init; }

    /// <summary>True when the shell answered nothing at all about this file.</summary>
    public bool IsEmpty =>
        Duration is null && Width is null && Height is null
        && VideoCodec is null && AudioCodec is null
        && VideoBitrate is null && AudioBitrate is null
        && CameraModel is null && DateTaken is null;

    /// <summary>
    /// The vertical resolution rounded to how people say it — 2160, 1080, 720.
    /// <para>
    /// This is the number that answers "which of these five renders do I keep": comparing
    /// two files of the same video is comparing 2160p against 720p, not two byte counts.
    /// </para>
    /// </summary>
    public string? ResolutionLabel => Height is null or 0 ? null : $"{Height}p";

    public string? Dimensions => Width is null || Height is null ? null : $"{Width}×{Height}";
}

/// <summary>
/// Reads media metadata through the Windows Property System.
/// <para>
/// No media library is linked in. Windows already knows a video's resolution — it is what
/// Explorer's details pane shows — and asking it costs nothing at run time and, more to the
/// point, nothing in the binary. A player would add tens of megabytes of native DLLs to a
/// portable executable whose whole promise is that copying one file is enough.
/// </para>
/// <para>
/// The limit is honest and worth stating: this reads what a property handler installed on
/// the machine chose to expose. A format nobody has a handler for answers nothing, and
/// <see cref="MediaInfo.IsEmpty"/> says so rather than the app inventing plausible numbers.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class MediaProbe
{
    /// <summary>100-nanosecond ticks, which is what the shell stores duration in.</summary>
    private const long TicksPerSecond = 10_000_000;

    public static MediaInfo Read(string path)
    {
        IPropertyStore? store = PropertySystem.Open(path);
        if (store is null) return new MediaInfo();

        try
        {
            ulong? duration = PropertySystem.ReadUInt64(store, PropertySystem.Keys.Duration);

            // Frame rate arrives multiplied by 1000: 29.97 fps is stored as 29970.
            uint? rawFrameRate = PropertySystem.ReadUInt32(store, PropertySystem.Keys.FrameRate);

            return new MediaInfo
            {
                Duration = duration is > 0 ? TimeSpan.FromTicks((long)duration.Value) : null,

                Width = PropertySystem.ReadUInt32(store, PropertySystem.Keys.FrameWidth)
                     ?? PropertySystem.ReadUInt32(store, PropertySystem.Keys.ImageWidth),
                Height = PropertySystem.ReadUInt32(store, PropertySystem.Keys.FrameHeight)
                      ?? PropertySystem.ReadUInt32(store, PropertySystem.Keys.ImageHeight),

                FrameRate = rawFrameRate is > 0 ? rawFrameRate.Value / 1000.0 : null,

                // Three shapes, in order of how readable the answer is: a packed FourCC,
                // a media subtype GUID wrapping one, or plain text.
                VideoCodec = Codec(store, PropertySystem.Keys.VideoCompression),
                VideoBitrate = PropertySystem.ReadUInt64(store, PropertySystem.Keys.VideoEncodingBitrate),

                AudioCodec = Codec(store, PropertySystem.Keys.AudioCompression),
                AudioBitrate = PropertySystem.ReadUInt64(store, PropertySystem.Keys.AudioEncodingBitrate),
                SampleRate = PropertySystem.ReadUInt32(store, PropertySystem.Keys.AudioSampleRate),
                Channels = PropertySystem.ReadUInt32(store, PropertySystem.Keys.AudioChannelCount),

                CameraModel = PropertySystem.ReadString(store, PropertySystem.Keys.CameraModel),
                DateTaken = ParseDate(PropertySystem.ReadString(store, PropertySystem.Keys.DateTaken)),
            };
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>Reads a compression tag in whichever of its three shapes it arrives in.</summary>
    private static string? Codec(IPropertyStore store, PropertyKey key)
    {
        string? packed = FourCc(PropertySystem.ReadUInt32(store, key));
        if (packed is not null) return packed;

        string? text = PropertySystem.ReadString(store, key);
        return FourCcFromSubtype(text) ?? text;
    }

    /// <summary>
    /// The tail every Windows media subtype GUID shares. What varies is the first field.
    /// </summary>
    private const string MediaSubtypeSuffix = "-0000-0010-8000-00aa00389b71";

    /// <summary>
    /// Turns a compression tag into the four characters people recognise, when it is one.
    /// <para>
    /// Measured, not assumed: the shell answers video compression as a <b>GUID</b>, not as a
    /// number — H.264 arrives as <c>{34363248-0000-0010-8000-00AA00389B71}</c>. That is a
    /// media subtype GUID, and its first field is the FourCC: 0x34363248 is the bytes
    /// <c>H</c> <c>2</c> <c>6</c> <c>4</c>. Printing the GUID would be technically true and
    /// useless; "H264" is what somebody choosing between two files reads.
    /// </para>
    /// </summary>
    internal static string? FourCc(uint? packed)
    {
        if (packed is null or 0) return null;
        return FromPacked(packed.Value);
    }

    /// <summary>Extracts the FourCC from a media subtype GUID, when the text is one.</summary>
    internal static string? FourCcFromSubtype(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim().Trim('{', '}');
        if (!trimmed.EndsWith(MediaSubtypeSuffix, StringComparison.OrdinalIgnoreCase)) return null;

        string head = trimmed[..trimmed.IndexOf('-')];

        return uint.TryParse(head, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out uint packed)
            ? FromPacked(packed)
            : null;
    }

    private static string? FromPacked(uint value)
    {
        Span<char> chars = stackalloc char[4];

        for (int i = 0; i < 4; i++)
        {
            char c = (char)((value >> (i * 8)) & 0xFF);

            // Anything that is not printable ASCII means this was never a FourCC.
            if (c < 0x20 || c > 0x7E) return null;

            chars[i] = c;
        }

        string text = new string(chars).Trim();
        return text.Length == 0 ? null : text;
    }

    private static DateTime? ParseDate(string? text) =>
        DateTime.TryParse(text, out DateTime date) ? date : null;
}
