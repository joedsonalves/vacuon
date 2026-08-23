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

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToDouble(in PropVariant value, out double result);

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToDoubleVectorAlloc(in PropVariant value, out nint values, out uint count);

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

    /// <summary>Reads one property as a floating-point number, or null when it is absent.</summary>
    public static double? ReadDouble(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;

        try
        {
            if (store.GetValue(ref key, out value) != 0 || value.IsEmpty) return null;
            return PropVariantToDouble(value, out double result) == 0 ? result : null;
        }
        catch (COMException) { return null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>
    /// Reads one property as a list of numbers, or null when it is absent.
    /// <para>
    /// EXIF stores a coordinate as three of them — degrees, minutes, seconds — rather than as
    /// one decimal, so a scalar read of the same key returns nothing at all.
    /// </para>
    /// </summary>
    public static double[]? ReadDoubleVector(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;
        nint buffer = nint.Zero;

        try
        {
            if (store.GetValue(ref key, out value) != 0 || value.IsEmpty) return null;
            if (PropVariantToDoubleVectorAlloc(value, out buffer, out uint count) != 0) return null;
            if (buffer == nint.Zero || count == 0) return null;

            var numbers = new double[count];
            Marshal.Copy(buffer, numbers, 0, (int)count);

            return numbers;
        }
        catch (COMException) { return null; }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeCoTaskMem(buffer);
            PropVariantClear(ref value);
        }
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

        // Every GUID below came from PSGetPropertyKeyFromName on this machine, not from
        // memory. Two of them were written from memory first and both were wrong — the
        // decimal pair in particular — and a wrong key does not fail: it returns nothing,
        // which is indistinguishable from a photograph that was never geotagged.
        private static readonly Guid GpsLatitudeId = new("8727cfff-4868-4ec6-ad5b-81b98521d1ab");
        private static readonly Guid GpsLatitudeRefId = new("029c0252-5b86-46c7-aca0-2769ffc8e3d4");
        private static readonly Guid GpsLongitudeId = new("c4c4dbb2-b593-466b-bbda-d03d27d5e43a");
        private static readonly Guid GpsLongitudeRefId = new("33dcf22b-28d5-464c-8035-1ee9efd25278");
        private static readonly Guid GpsLatitudeDecimalId = new("0f55cde2-4f49-450d-92c1-dcd16301b1b7");
        private static readonly Guid GpsLongitudeDecimalId = new("4679c1b5-844d-4590-baf5-f322231f1b81");
        private static readonly Guid GpsAltitudeId = new("827edb4f-5b73-44a7-891d-fdffabea35ca");
        private static readonly Guid GpsAltitudeRefId = new("46ac629d-75ea-4515-867f-6dc4321c5844");

        public static PropertyKey GpsLatitude => new(GpsLatitudeId, 100);
        public static PropertyKey GpsLatitudeRef => new(GpsLatitudeRefId, 100);
        public static PropertyKey GpsLongitude => new(GpsLongitudeId, 100);
        public static PropertyKey GpsLongitudeRef => new(GpsLongitudeRefId, 100);
        public static PropertyKey GpsLatitudeDecimal => new(GpsLatitudeDecimalId, 100);
        public static PropertyKey GpsLongitudeDecimal => new(GpsLongitudeDecimalId, 100);
        public static PropertyKey GpsAltitude => new(GpsAltitudeId, 100);

        /// <summary>0 means above sea level, 1 means below it. EXIF keeps the sign here.</summary>
        public static PropertyKey GpsAltitudeRef => new(GpsAltitudeRefId, 100);
    }
}
