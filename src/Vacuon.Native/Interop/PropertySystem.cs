using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vacuon.Native.Interop;

/// <summary>
/// Identifies one property in the Windows Property System: a format GUID plus an id.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

/// <summary>
/// A PROPVARIANT, treated as an opaque blob.
/// <para>
/// Deliberately not unpacked field by field. PROPVARIANT is a union of some forty types
/// whose layout differs between architectures, and hand-decoding it is a well-known source
/// of memory corruption. The values are pulled out with the <c>PropVariantTo*</c> helpers in
/// <c>propsys.dll</c>, which do the coercion Windows itself uses — so a duration stored as
/// UI8 and one stored as a string both come back as a number.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PropVariant
{
    public ushort VarType;
    private readonly ushort _reserved1;
    private readonly ushort _reserved2;
    private readonly ushort _reserved3;
    private nint _value;
    private readonly nint _value2;

    public readonly bool IsEmpty => VarType == 0;   // VT_EMPTY

    /// <summary>
    /// A VT_I8 variant, which is how Media Foundation takes a seek position — 100-nanosecond
    /// units, the same as <see cref="TimeSpan.Ticks"/>.
    /// </summary>
    public static PropVariant FromLong(long value)
    {
        var variant = new PropVariant { VarType = 20 };   // VT_I8
        variant._value = (nint)value;
        return variant;
    }

    /// <summary>
    /// Releases anything the variant owns.
    /// <para>
    /// Harmless on the plain numeric ones this code creates, and kept anyway: the day someone
    /// builds a string variant here, the leak would be silent and the habit is what catches it.
    /// </para>
    /// </summary>
    public void Clear() => PropVariantClear(ref this);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    int GetCount(out uint count);
    int GetAt(uint index, out PropertyKey key);
    int GetValue(ref PropertyKey key, out PropVariant value);
    int SetValue(ref PropertyKey key, ref PropVariant value);
    int Commit();
}

/// <summary>
/// Reads file metadata through the Windows Property System.
/// <para>
/// This is how the shell itself knows a video is 3840×2160 — the same handlers Explorer's
/// details pane uses. It means duration, resolution, codec and bitrate come out of Windows
/// with <b>no media library linked into the app at all</b>, which matters for a 62 MB
/// portable that must survive being copied to another folder.
/// </para>
/// <para>
/// What it cannot do is decode: a format with no property handler installed answers nothing,
/// and answering nothing is reported as such rather than guessed from the extension.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PropertySystem
{
    private static readonly Guid IPropertyStoreIid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    /// <summary>GETPROPERTYSTOREFLAGS.GPS_READWRITE is 0x2; 0 is the read-only default.</summary>
    private const uint ReadOnly = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, nint bindContext, uint flags, in Guid riid, out IPropertyStore store);

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToUInt64(in PropVariant value, out ulong result);

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToUInt32(in PropVariant value, out uint result);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PropVariantToStringAlloc(in PropVariant value, out nint result);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    /// <summary>
    /// Opens the property store for a path, or null when the shell has no handler for it.
    /// </summary>
    public static IPropertyStore? Open(string path)
    {
        try
        {
            // A "parsing name" is what the shell parses, not what the CRT accepts: forward
            // slashes and relative paths both come back as "no handler", which is
            // indistinguishable from a format Windows genuinely knows nothing about. Found
            // by passing a path with forward slashes and being told a 4K video had no
            // metadata — GetFullPath normalises both problems away.
            string full = Path.GetFullPath(path);

            int hr = SHGetPropertyStoreFromParsingName(
                full, nint.Zero, ReadOnly, IPropertyStoreIid, out IPropertyStore store);

            return hr == 0 ? store : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException
                                      or FileNotFoundException or PathTooLongException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads one property as a number, or null when it is absent.</summary>
    public static ulong? ReadUInt64(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;

        try
        {
            if (store.GetValue(ref key, out value) != 0 || value.IsEmpty) return null;
            return PropVariantToUInt64(value, out ulong result) == 0 ? result : null;
        }
        catch (COMException) { return null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Reads one property as a 32-bit number, or null when it is absent.</summary>
    public static uint? ReadUInt32(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;

        try
        {
            if (store.GetValue(ref key, out value) != 0 || value.IsEmpty) return null;
            return PropVariantToUInt32(value, out uint result) == 0 ? result : null;
        }
        catch (COMException) { return null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Reads one property as text, or null when it is absent.</summary>
    public static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;
        nint buffer = nint.Zero;

        try
        {
            if (store.GetValue(ref key, out value) != 0 || value.IsEmpty) return null;
            if (PropVariantToStringAlloc(value, out buffer) != 0 || buffer == nint.Zero) return null;

            string text = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return text.Length == 0 ? null : text;
        }
        catch (COMException) { return null; }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeCoTaskMem(buffer);
            PropVariantClear(ref value);
        }
    }

    /// <summary>
    /// The keys Vacuon asks for, named as the shell names them.
    /// <para>
    /// The two media format GUIDs differ by one digit and are easy to swap by accident:
    /// <c>…0490</c> carries duration and audio, <c>…0491</c> carries video.
    /// </para>
    /// </summary>
    public static class Keys
    {
        private static readonly Guid Media = new("64440490-4c8b-11d1-8b70-080036b11a03");
        private static readonly Guid Video = new("64440491-4c8b-11d1-8b70-080036b11a03");
        private static readonly Guid Image = new("6444048f-4c8b-11d1-8b70-080036b11a03");
        private static readonly Guid Photo = new("14b81da1-0135-4d31-96d9-6cbfc9671a99");

        /// <summary>Duration in 100-nanosecond units.</summary>
        public static PropertyKey Duration => new(Media, 3);

        public static PropertyKey AudioEncodingBitrate => new(Media, 4);
        public static PropertyKey AudioSampleRate => new(Media, 5);
        public static PropertyKey AudioChannelCount => new(Media, 7);
        public static PropertyKey AudioCompression => new(Media, 10);

        public static PropertyKey FrameWidth => new(Video, 3);
        public static PropertyKey FrameHeight => new(Video, 4);
        public static PropertyKey FrameRate => new(Video, 6);
        public static PropertyKey VideoEncodingBitrate => new(Video, 8);
        public static PropertyKey VideoCompression => new(Video, 10);

        /// <summary>
        /// Pixel dimensions — <c>System.Image.HorizontalSize</c> and <c>VerticalSize</c>.
        /// <para>
        /// Ids 3 and 4, not 5 and 6. Those two are <c>HorizontalResolution</c> and
        /// <c>VerticalResolution</c>, which are <b>DPI</b>: a 1200×800 PNG reported itself as
        /// "96×96" and the number looked plausible enough to ship. Caught by generating an
        /// image of a known size and reading the label back.
        /// </para>
        /// </summary>
        public static PropertyKey ImageWidth => new(Image, 3);
        public static PropertyKey ImageHeight => new(Image, 4);

        public static PropertyKey HorizontalDpi => new(Image, 5);
        public static PropertyKey VerticalDpi => new(Image, 6);
        public static PropertyKey BitDepth => new(Image, 7);

        public static PropertyKey CameraModel => new(Photo, 272);
        public static PropertyKey DateTaken => new(Photo, 36867);
    }
}
