using System.Runtime.InteropServices;

namespace Vacuon.Native.Interop;

/// <summary>Why no samples came back, when none did.</summary>
public enum AudioReadFailure
{
    None,
    /// <summary>Media Foundation would not start, or the file has no audio it can open.</summary>
    NoDecoder,
    /// <summary>It opened and produced nothing worth fingerprinting.</summary>
    NoSamples,
}

/// <summary>Mono samples at a fixed rate, and what happened.</summary>
public sealed record AudioReadResult(float[] Samples, AudioReadFailure Failure)
{
    public bool Succeeded => Samples.Length > 0;
}

/// <summary>
/// Decodes audio to plain mono samples, using the Media Foundation that ships with Windows.
/// <para>
/// ⚠️ <b>No audio library is linked, and that is a decision this project already paid for
/// once.</b> The video player was measured and turned down at 101,8 MB across 525 files, 325
/// of them plugins the library looks for on disk at run time — which a single-file executable
/// does not have. Media Foundation is already on every Windows this app runs on, already used
/// here to pull frames out of video, and adds nothing to the binary.
/// </para>
/// <para>
/// What it decodes is therefore whatever Windows can already open, and it says so rather than
/// pretending: a FLAC on a machine with no FLAC codec comes back as <see cref="AudioReadFailure.NoDecoder"/>,
/// not as a file that is somehow different from every other file.
/// </para>
/// </summary>
public static class AudioSamples
{
    private const uint FirstAudioStream = 0xFFFFFFFD;

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioMajor = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    /// <summary>
    /// Reads up to <paramref name="seconds"/> of audio, mono, at <paramref name="rate"/>.
    /// </summary>
    /// <remarks>
    /// Media Foundation does the mixing down and the resampling: asking the reader for the
    /// format wanted is one call, and doing either by hand here would be a second
    /// implementation of something the operating system already has.
    /// </remarks>
    public static AudioReadResult Read(string path, int rate, double seconds)
    {
        if (!MediaFoundationRuntime.Start()) return new AudioReadResult([], AudioReadFailure.NoDecoder);

        IMFSourceReader? reader = null;

        try
        {
            if (MediaFoundationRuntime.CreateReader(path, out reader) != 0 || reader is null)
                return new AudioReadResult([], AudioReadFailure.NoDecoder);

            reader.SetStreamSelection(0xFFFFFFFE, false);   // ALL_STREAMS off
            reader.SetStreamSelection(FirstAudioStream, true);

            if (!Configure(reader, rate)) return new AudioReadResult([], AudioReadFailure.NoDecoder);

            var samples = new List<float>((int)(rate * seconds));
            int wanted = (int)(rate * seconds);

            while (samples.Count < wanted)
            {
                int hr = reader.ReadSample(FirstAudioStream, 0, out _, out uint flags, out _,
                                           out IMFSample? sample);

                if (hr != 0) break;

                // 0x2 is END_OF_STREAM. A null sample without it is a gap, not the end.
                if ((flags & 0x2) != 0) break;
                if (sample is null) continue;

                Append(sample, samples);
                Marshal.ReleaseComObject(sample);
            }

            if (samples.Count > wanted) samples.RemoveRange(wanted, samples.Count - wanted);

            return samples.Count > 0
                ? new AudioReadResult([.. samples], AudioReadFailure.None)
                : new AudioReadResult([], AudioReadFailure.NoSamples);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return new AudioReadResult([], AudioReadFailure.NoDecoder);
        }
        finally
        {
            if (reader is not null) Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>Asks the reader for 32-bit float, mono, at the rate wanted.</summary>
    private static bool Configure(IMFSourceReader reader, int rate)
    {
        if (MediaFoundationRuntime.CreateMediaType(out IMFMediaType? type) != 0 || type is null) return false;

        try
        {
            Guid major = MajorType, sub = SubType;
            Guid audio = AudioMajor, format = AudioFloat;
            Guid channels = Channels, persec = SamplesPerSecond, bits = BitsPerSample;
            Guid align = BlockAlignment, bytes = BytesPerSecond;

            type.SetGUID(ref major, ref audio);
            type.SetGUID(ref sub, ref format);
            type.SetUINT32(ref channels, 1);
            type.SetUINT32(ref persec, (uint)rate);
            type.SetUINT32(ref bits, 32);
            type.SetUINT32(ref align, 4);
            type.SetUINT32(ref bytes, (uint)(rate * 4));

            return reader.SetCurrentMediaType(FirstAudioStream, 0, type) == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static void Append(IMFSample sample, List<float> into)
    {
        if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer? buffer) != 0 || buffer is null) return;

        try
        {
            if (buffer.Lock(out nint data, out _, out uint length) != 0) return;

            try
            {
                int count = (int)(length / 4);
                var block = new float[count];
                Marshal.Copy(data, block, 0, count);
                into.AddRange(block);
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
}
