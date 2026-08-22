using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vacuon.Native.Interop;

/// <summary>One decoded frame: BGRA32 pixels and the moment it was taken from.</summary>
public sealed record VideoFrame(int Width, int Height, byte[] Bgra32, TimeSpan Position);

/// <summary>Where a video read stopped, when it produced nothing.</summary>
public enum VideoReadFailure
{
    None,
    NothingAsked,
    MediaFoundationUnavailable,
    CannotOpen,
    NoDecoder,
    NoFramesDecoded,
    Threw,
}

public sealed record VideoReadResult(
    IReadOnlyList<VideoFrame> Frames,
    VideoReadFailure Failure,
    int HResult)
{
    public bool Succeeded => Frames.Count > 0;
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    // Only SetGUID and GetUINT32 are ever called. The rest are slots: a COM vtable is
    // positional, so every method ahead of the one you want has to be declared, whatever
    // its real signature is.
    [PreserveSig] int GetItem(nint key, nint value);
    [PreserveSig] int GetItemType(nint key, out int type);
    [PreserveSig] int CompareItem(nint key, nint value, out bool result);
    [PreserveSig] int Compare(nint theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(nint key, out uint length);
    [PreserveSig] int GetString(nint key, nint value, uint size, nint length);
    [PreserveSig] int GetAllocatedString(nint key, nint value, nint length);
    [PreserveSig] int GetBlobSize(nint key, out uint size);
    [PreserveSig] int GetBlob(nint key, nint buffer, uint size, nint written);
    [PreserveSig] int GetAllocatedBlob(nint key, nint buffer, out uint size);
    [PreserveSig] int GetUnknown(nint key, [In] ref Guid riid, out nint value);
    [PreserveSig] int SetItem(nint key, nint value);
    [PreserveSig] int DeleteItem(nint key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString(nint key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(nint key, nint buffer, uint size);
    [PreserveSig] int SetUnknown(nint key, nint value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(nint destination);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // Flattened on purpose, NOT declared as `: IMFAttributes`. A ComImport interface that
    // inherits another already carries the base's thirty slots, so redeclaring them with
    // `new` builds a sixty-slot vtable over a thirty-four-slot object — every call lands in
    // the wrong place and the QueryInterface comes back E_NOINTERFACE.
    [PreserveSig] int GetItem(nint key, nint value);
    [PreserveSig] int GetItemType(nint key, out int type);
    [PreserveSig] int CompareItem(nint key, nint value, out bool result);
    [PreserveSig] int Compare(nint theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(nint key, out uint length);
    [PreserveSig] int GetString(nint key, nint value, uint size, nint length);
    [PreserveSig] int GetAllocatedString(nint key, nint value, nint length);
    [PreserveSig] int GetBlobSize(nint key, out uint size);
    [PreserveSig] int GetBlob(nint key, nint buffer, uint size, nint written);
    [PreserveSig] int GetAllocatedBlob(nint key, nint buffer, out uint size);
    [PreserveSig] int GetUnknown(nint key, [In] ref Guid riid, out nint value);
    [PreserveSig] int SetItem(nint key, nint value);
    [PreserveSig] int DeleteItem(nint key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString(nint key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(nint key, nint buffer, uint size);
    [PreserveSig] int SetUnknown(nint key, nint value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(nint destination);

    [PreserveSig] int GetMajorType(out Guid type);
    [PreserveSig] int IsCompressedFormat(out bool compressed);
    [PreserveSig] int IsEqual(nint other, out uint flags);
    [PreserveSig] int GetRepresentation(Guid representation, out nint blob);
}

[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out nint buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // IMFSample derives from IMFAttributes too; the same positional rule applies, and the
    // three methods this code needs sit after all thirty of them.
    [PreserveSig] int GetItem(nint key, nint value);
    [PreserveSig] int GetItemType(nint key, out int type);
    [PreserveSig] int CompareItem(nint key, nint value, out bool result);
    [PreserveSig] int Compare(nint theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(nint key, out uint length);
    [PreserveSig] int GetString(nint key, nint value, uint size, nint length);
    [PreserveSig] int GetAllocatedString(nint key, nint value, nint length);
    [PreserveSig] int GetBlobSize(nint key, out uint size);
    [PreserveSig] int GetBlob(nint key, nint buffer, uint size, nint written);
    [PreserveSig] int GetAllocatedBlob(nint key, nint buffer, out uint size);
    [PreserveSig] int GetUnknown(nint key, [In] ref Guid riid, out nint value);
    [PreserveSig] int SetItem(nint key, nint value);
    [PreserveSig] int DeleteItem(nint key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString(nint key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(nint key, nint buffer, uint size);
    [PreserveSig] int SetUnknown(nint key, nint value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(nint destination);

    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint index, out bool selected);
    [PreserveSig] int SetStreamSelection(uint index, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [PreserveSig] int GetNativeMediaType(uint index, uint typeIndex, out IMFMediaType type);
    [PreserveSig] int GetCurrentMediaType(uint index, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(uint index, nint reserved, IMFMediaType type);
    [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, [In] ref PropVariant position);
    [PreserveSig] int ReadSample(uint index, uint flags, out uint actualIndex,
                                 out uint streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint index);
    [PreserveSig] int GetServiceForStream(uint index, [In] ref Guid service, [In] ref Guid riid, out nint obj);
    [PreserveSig] int GetPresentationAttribute(uint index, [In] ref Guid attribute, out PropVariant value);
}

/// <summary>
/// Decodes still frames out of a video, using the Media Foundation that ships with Windows.
/// <para>
/// This exists because a video near-duplicate cannot be judged from one picture. The shell's
/// thumbnail gives a single representative frame, and two unrelated films that both open on a
/// dark title card produce the same fingerprint from it. Sampling across the running time is
/// what separates "the same video re-encoded" from "both start black".
/// </para>
/// <para>
/// <b>No dependency is added by this.</b> <c>mfplat.dll</c> and <c>mfreadwrite.dll</c> are
/// part of Windows, so the portable binary does not grow by a byte — which is the whole
/// reason this route was taken over a decoding library. What it inherits is the machine's
/// codecs: a container Windows cannot open here yields no frames, and that is reported rather
/// than guessed around.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoFrames
{
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint AllStreams = 0xFFFFFFFE;

    private const uint StreamFlagEndOfStream = 0x02;

    /// <summary>
    /// How many frames may be decoded while walking forward from a key frame to the moment
    /// asked for. Ten seconds of ordinary footage, which is far past any sane key-frame
    /// interval, and a hard stop on a file that would otherwise be decoded end to end.
    /// </summary>
    private const int MaximumFramesWalked = 300;

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_RGB32 — D3DFMT_X8R8G8B8, which is BGRA byte order in memory.</summary>
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");

    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid TimeFormatNull = Guid.Empty;

    private static bool _started;
    private static readonly Lock StartLock = new();

    /// <summary>
    /// Reads up to <paramref name="count"/> frames spread across the running time.
    /// <para>
    /// Returns what it managed to get, which may be fewer than asked for or none at all.
    /// A file the machine has no decoder for is not an error here — it is a video this
    /// feature cannot see, and the caller says so instead of leaving it out silently.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VideoFrame> Sample(string path, TimeSpan duration, int count) =>
        Read(path, duration, count).Frames;

    /// <summary>
    /// The same read, with where it stopped when it produced nothing.
    /// <para>
    /// Kept as a first-class result rather than a debugging aid: "this video yielded no
    /// frames" and "this machine has no decoder for it" are different facts, and a scan that
    /// silently skips half a library teaches people it found nothing there.
    /// </para>
    /// </summary>
    public static VideoReadResult Read(string path, TimeSpan duration, int count)
    {
        var frames = new List<VideoFrame>();

        if (count <= 0 || duration <= TimeSpan.Zero)
            return new VideoReadResult(frames, VideoReadFailure.NothingAsked, 0);

        if (!Startup()) return new VideoReadResult(frames, VideoReadFailure.MediaFoundationUnavailable, 0);

        IMFSourceReader? reader = null;

        try
        {
            int hr = MFCreateSourceReaderFromURL(path, CreateAttributes(), out reader);

            if (hr != 0 || reader is null)
                return new VideoReadResult(frames, VideoReadFailure.CannotOpen, hr);

            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstVideoStream, true);

            if (!TryUseRgb32(reader, out int width, out int height, out int typeHr))
                return new VideoReadResult(frames, VideoReadFailure.NoDecoder, typeHr);

            for (int i = 0; i < count; i++)
            {
                // Spread over the middle of the running time. The first and last moments of a
                // video are the least characteristic part of it — titles, fades, black — and
                // are exactly where unrelated files look alike.
                double fraction = (i + 1.0) / (count + 1.0);
                TimeSpan at = duration * fraction;

                VideoFrame? frame = ReadAt(reader, at, width, height);
                if (frame is not null) frames.Add(frame);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // A broken or unusual file must not take down a scan of ten thousand of them.
            return new VideoReadResult(frames, VideoReadFailure.Threw, ex.HResult);
        }
        finally
        {
            if (reader is not null) Marshal.ReleaseComObject(reader);
        }

        return new VideoReadResult(frames,
            frames.Count > 0 ? VideoReadFailure.None : VideoReadFailure.NoFramesDecoded, 0);
    }

    /// <summary>
    /// Asks the reader for uncompressed BGRA and reads back the size it settled on.
    /// </summary>
    private static bool TryUseRgb32(IMFSourceReader reader, out int width, out int height, out int hr)
    {
        width = height = 0;
        hr = 0;

        hr = MFCreateMediaType(out IMFMediaType? type);
        if (hr != 0 || type is null) return false;

        try
        {
            Guid major = VideoMajor, sub = Rgb32, majorKey = MajorType, subKey = SubType;

            hr = type.SetGUID(ref majorKey, ref major);
            if (hr != 0) return false;

            hr = type.SetGUID(ref subKey, ref sub);
            if (hr != 0) return false;

            hr = reader.SetCurrentMediaType(FirstVideoStream, 0, type);
            if (hr != 0) return false;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }

        hr = reader.GetCurrentMediaType(FirstVideoStream, out IMFMediaType? current);
        if (hr != 0 || current is null) return false;

        try
        {
            Guid sizeKey = FrameSize;

            hr = current.GetUINT64(ref sizeKey, out ulong packed);
            if (hr != 0) return false;

            // MF packs frame size as width in the high half and height in the low half.
            width = (int)(packed >> 32);
            height = (int)(packed & 0xFFFFFFFF);

            return width > 0 && height > 0;
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }
    }

    /// <summary>
    /// Seeks to a moment and returns the frame there.
    /// <para>
    /// <b>Seeking alone is not enough, and assuming it was produced five copies of frame
    /// zero.</b> A seek lands on the nearest preceding key frame, so on footage with a sparse
    /// GOP — a short clip encoded in one go often has exactly one key frame — every requested
    /// moment resolves to the opening frame. Two unrelated videos that share a title card then
    /// fingerprint identically, five times over, and the whole design collapses into the
    /// single-thumbnail problem it exists to avoid.
    /// </para>
    /// <para>
    /// So after seeking it reads forward, discarding samples until the timestamp reaches the
    /// target. The walk is bounded: a video whose key frames are minutes apart is not worth
    /// decoding a minute of, and the frame reached by then is returned rather than nothing.
    /// </para>
    /// </summary>
    private static VideoFrame? ReadAt(IMFSourceReader reader, TimeSpan at, int width, int height)
    {
        var position = PropVariant.FromLong(at.Ticks);   // both are 100-nanosecond units

        try
        {
            Guid format = TimeFormatNull;
            if (reader.SetCurrentPosition(ref format, ref position) != 0) return null;
        }
        finally
        {
            position.Clear();
        }

        IMFSample? best = null;
        long bestTimestamp = 0;

        try
        {
            for (int read = 0; read < MaximumFramesWalked; read++)
            {
                int hr = reader.ReadSample(FirstVideoStream, 0, out _, out uint streamFlags,
                                           out long timestamp, out IMFSample? sample);

                if (hr != 0) break;

                if (sample is null)
                {
                    // Null with no end-of-stream is the reader saying "nothing yet"; a seek
                    // can produce a few of those before the decoder catches up.
                    if ((streamFlags & StreamFlagEndOfStream) != 0) break;
                    continue;
                }

                if (best is not null) Marshal.ReleaseComObject(best);

                best = sample;
                bestTimestamp = timestamp;

                // Far enough forward: this is the frame that was asked for.
                if (timestamp >= at.Ticks) break;
            }

            return best is null ? null : Copy(best, width, height, TimeSpan.FromTicks(bestTimestamp));
        }
        finally
        {
            if (best is not null) Marshal.ReleaseComObject(best);
        }
    }

    private static VideoFrame? Copy(IMFSample sample, int width, int height, TimeSpan position)
    {
        if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer? buffer) != 0 || buffer is null)
            return null;

        try
        {
            if (buffer.Lock(out nint scan0, out _, out uint length) != 0) return null;

            try
            {
                int expected = width * height * 4;
                if (length < expected) return null;

                var pixels = new byte[expected];
                Marshal.Copy(scan0, pixels, 0, expected);

                // RGB32 out of Media Foundation is commonly bottom-up, and no attempt is made
                // to correct it: every video goes through the same path, so a flip applied to
                // all of them cancels out in a comparison between them. Frames are never
                // compared against a shell thumbnail, which is the only place it would matter.
                return new VideoFrame(width, height, pixels, position);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>
    /// The attribute store that lets the reader insert a converter when the decoder's output
    /// is not the format asked for — without it, anything that decodes to YUV simply fails.
    /// </summary>
    private static nint CreateAttributes()
    {
        if (MFCreateAttributes(out IMFAttributes? attributes, 1) != 0 || attributes is null)
            return 0;

        try
        {
            Guid key = EnableVideoProcessing;
            attributes.SetUINT32(ref key, 1);

            return Marshal.GetIUnknownForObject(attributes);
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }
    }

    private static bool Startup()
    {
        lock (StartLock)
        {
            if (_started) return true;

            // MF_VERSION for Windows 7 and later; MFSTARTUP_NOSOCKET, since nothing here
            // reads from the network.
            _started = MFStartup(0x00020070, 1) == 0;
            return _started;
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType? type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes? attributes, uint initialSize);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out IMFSourceReader? reader);
}
