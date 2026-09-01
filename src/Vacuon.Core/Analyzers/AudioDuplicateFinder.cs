using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Analyzers;

/// <summary>One audio file with a fingerprint.</summary>
public sealed record AudioTrack(int EntryIndex, string Path, long Bytes, uint[] Fingerprint)
{
    public bool HasFingerprint => Fingerprint.Length > 0;
}

/// <summary>Recordings that sound like each other, with the closest match scored.</summary>
public sealed class AudioMatchGroup
{
    public AudioMatchGroup(IReadOnlyList<AudioTrack> tracks, double similarity)
    {
        Tracks = tracks;
        Similarity = similarity;

        // The biggest file stays: between a FLAC and an MP3 of the same recording, the one
        // worth keeping is the one that still has everything in it.
        Keeper = tracks.MaxBy(t => t.Bytes)!;
        Redundant = [.. tracks.Where(t => !ReferenceEquals(t, Keeper))];

        long recoverable = 0;
        foreach (AudioTrack track in Redundant) recoverable += track.Bytes;
        RecoverableBytes = recoverable;
    }

    public IReadOnlyList<AudioTrack> Tracks { get; }
    public AudioTrack Keeper { get; }
    public IReadOnlyList<AudioTrack> Redundant { get; }

    /// <summary>How alike, 0 to 1. See <see cref="AudioFingerprint.MatchThreshold"/> for the scale.</summary>
    public double Similarity { get; }

    public long RecoverableBytes { get; }

    public int CopyCount => Tracks.Count;
}

public sealed record AudioMatchReport(
    IReadOnlyList<AudioMatchGroup> Groups,
    int FilesConsidered,
    int FilesFingerprinted,
    int Unreadable)
{
    public int GroupCount => Groups.Count;

    public long RecoverableBytes
    {
        get
        {
            long total = 0;
            foreach (AudioMatchGroup group in Groups) total += group.RecoverableBytes;
            return total;
        }
    }
}

/// <summary>
/// Recordings that are the same music in different files (PRD F4.7).
/// <para>
/// ⚠️ <b>This is the near-duplicate search, not the exact one.</b> Two files with the same
/// bytes are already found by the hash, faster and with no doubt. This is for the FLAC and
/// the MP3 of the same track, with different tags and not a byte in common.
/// </para>
/// <para>
/// ⚠️ <b>And therefore nothing here may become a hard link.</b> A link gives two names to one
/// set of bytes; pointing the MP3's path at the FLAC's bytes would not remove a duplicate, it
/// would replace somebody's file with a different file. The only honest actions on a match
/// are the ones that remove a copy the person picked.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioDuplicateFinder
{
    /// <summary>Under this it is a sound effect or a voice note, not a recording worth pairing.</summary>
    public const long MinimumBytes = 256 * 1024;

    /// <summary>How much of each file is listened to. Enough to identify, cheap enough to do at scale.</summary>
    public const double Seconds = 30;

    private static readonly string[] Extensions =
        [".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".ogg", ".opus", ".aiff", ".alac"];

    public static AudioMatchReport Find(VolumeIndex index,
                                        double threshold = AudioFingerprint.MatchThreshold,
                                        IProgress<DuplicateProgress>? progress = null,
                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        var candidates = new List<int>();

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry entry = ref index.Entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;
            if (entry.LogicalSize < MinimumBytes) continue;

            // Same rule as everywhere else that opens a file: reading a cloud placeholder
            // downloads it, and a fingerprint is not worth somebody's connection.
            if ((entry.Flags & EntryFlags.CloudPlaceholder) != 0) continue;

            ReadOnlySpan<char> name = index.GetName(i);
            if (!IsAudio(name)) continue;

            candidates.Add(i);
        }

        progress?.Report(new DuplicateProgress(0, candidates.Count, 0));

        var tracks = new List<AudioTrack>(candidates.Count);
        int unreadable = 0;

        foreach (int entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string path = index.GetFullPath(entry);
            AudioReadResult audio = AudioSamples.Read(path, AudioFingerprint.SampleRate, Seconds);

            if (!audio.Succeeded)
            {
                // A file Windows has no decoder for is not a file we may call a duplicate.
                unreadable++;
                continue;
            }

            uint[] print = AudioFingerprint.Compute(audio.Samples);
            if (print.Length == 0) { unreadable++; continue; }

            tracks.Add(new AudioTrack(entry, path, index.Entries[entry].LogicalSize, print));
            progress?.Report(new DuplicateProgress(tracks.Count, candidates.Count, 0));
        }

        return new AudioMatchReport(Group(tracks, threshold), candidates.Count, tracks.Count, unreadable);
    }

    /// <summary>
    /// Puts the tracks that sound alike together.
    /// <para>
    /// ⚠️ Each group is re-checked against its <b>keeper</b>, not against the seed it grew
    /// from. Chaining "a is like b, b is like c" quietly builds a group where a and c have
    /// nothing to do with each other — the same mistake the picture search made and had
    /// corrected.
    /// </para>
    /// </summary>
    public static List<AudioMatchGroup> Group(List<AudioTrack> tracks, double threshold)
    {
        var groups = new List<AudioMatchGroup>();
        var taken = new bool[tracks.Count];

        for (int i = 0; i < tracks.Count; i++)
        {
            if (taken[i]) continue;

            var members = new List<AudioTrack> { tracks[i] };
            double worst = 1;

            for (int j = i + 1; j < tracks.Count; j++)
            {
                if (taken[j]) continue;

                double score = AudioFingerprint.Similarity(tracks[i].Fingerprint, tracks[j].Fingerprint);
                if (score < threshold) continue;

                members.Add(tracks[j]);
                taken[j] = true;
                if (score < worst) worst = score;
            }

            if (members.Count < 2) continue;

            taken[i] = true;

            var group = new AudioMatchGroup(members, worst);

            // Now against the keeper. Anything that only matched the seed goes back in the
            // pool rather than into a group it does not belong to.
            var confirmed = new List<AudioTrack> { group.Keeper };
            double lowest = 1;

            foreach (AudioTrack track in group.Redundant)
            {
                double score = AudioFingerprint.Similarity(group.Keeper.Fingerprint, track.Fingerprint);

                if (score < threshold)
                {
                    taken[tracks.IndexOf(track)] = false;
                    continue;
                }

                confirmed.Add(track);
                if (score < lowest) lowest = score;
            }

            if (confirmed.Count >= 2) groups.Add(new AudioMatchGroup(confirmed, lowest));
        }

        groups.Sort((a, b) => b.RecoverableBytes.CompareTo(a.RecoverableBytes));
        return groups;
    }

    private static bool IsAudio(ReadOnlySpan<char> name)
    {
        foreach (string extension in Extensions)
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
