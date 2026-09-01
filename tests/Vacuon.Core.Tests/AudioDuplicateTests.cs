using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Grouping recordings that sound alike (PRD F4.7).
/// <para>
/// The fingerprints here are made rather than decoded, so the grouping is tested without
/// depending on which codecs the machine happens to have.
/// </para>
/// </summary>
public class AudioDuplicateTests
{
    private const int Rate = AudioFingerprint.SampleRate;

    private static float[] Tune(double[] freqs, double seconds = 8, double gain = 0.5)
    {
        int total = (int)(Rate * seconds);
        var samples = new float[total];
        int noteLength = Rate / 5;

        for (int i = 0; i < total; i++)
        {
            double f = freqs[(i / noteLength) % freqs.Length];
            samples[i] = (float)(gain * ((0.7 * Math.Sin(2 * Math.PI * f * i / Rate))
                                       + (0.3 * Math.Sin(4 * Math.PI * f * i / Rate))));
        }

        return samples;
    }

    private static AudioTrack Track(string name, long bytes, double[] notes, double gain = 0.5) =>
        new(0, @"C:\musica\" + name, bytes, AudioFingerprint.Compute(Tune(notes, gain: gain)));

    [Fact]
    public void TheSameRecordingAtTwoQualitiesIsOneGroup()
    {
        double[] song = [261.63, 329.63, 392.00, 523.25];

        var tracks = new List<AudioTrack>
        {
            Track("faixa.flac", 40_000_000, song),
            Track("faixa.mp3", 8_000_000, song, gain: 0.1),
        };

        AudioMatchGroup group = Assert.Single(AudioDuplicateFinder.Group(tracks, AudioFingerprint.MatchThreshold));

        Assert.Equal(2, group.CopyCount);
        Assert.True(group.Similarity >= AudioFingerprint.MatchThreshold);
    }

    [Fact]
    public void TheBiggestFileIsTheOneThatStays()
    {
        // Between a FLAC and an MP3 of the same recording, the one worth keeping is the one
        // that still has everything in it.
        double[] song = [261.63, 329.63, 392.00, 523.25];

        var tracks = new List<AudioTrack>
        {
            Track("pequeno.mp3", 3_000_000, song),
            Track("grande.flac", 30_000_000, song, gain: 0.2),
        };

        AudioMatchGroup group = Assert.Single(AudioDuplicateFinder.Group(tracks, AudioFingerprint.MatchThreshold));

        Assert.EndsWith("grande.flac", group.Keeper.Path);
        Assert.Equal(3_000_000, group.RecoverableBytes);
        Assert.DoesNotContain(group.Keeper, group.Redundant);
    }

    [Fact]
    public void DifferentRecordingsAreNotAGroup()
    {
        var tracks = new List<AudioTrack>
        {
            Track("uma.mp3", 5_000_000, [261.63, 329.63, 392.00, 523.25]),
            Track("outra.mp3", 5_000_000, [233.08, 277.18, 349.23, 466.16]),
        };

        Assert.Empty(AudioDuplicateFinder.Group(tracks, AudioFingerprint.MatchThreshold));
    }

    [Fact]
    public void AFileWithNoFingerprintIsNeverGrouped()
    {
        // Windows having no decoder for a file is not a reason to guess about it.
        var tracks = new List<AudioTrack>
        {
            Track("boa.mp3", 5_000_000, [261.63, 329.63, 392.00, 523.25]),
            new(0, @"C:\musica\ilegivel.ogg", 5_000_000, []),
        };

        Assert.Empty(AudioDuplicateFinder.Group(tracks, AudioFingerprint.MatchThreshold));
    }

    [Fact]
    public void ThreeCopiesLeaveTwoRedundant()
    {
        double[] song = [261.63, 329.63, 392.00, 523.25];

        var tracks = new List<AudioTrack>
        {
            Track("a.flac", 30_000_000, song),
            Track("b.mp3", 6_000_000, song, gain: 0.3),
            Track("c.m4a", 4_000_000, song, gain: 0.15),
        };

        AudioMatchGroup group = Assert.Single(AudioDuplicateFinder.Group(tracks, AudioFingerprint.MatchThreshold));

        Assert.Equal(3, group.CopyCount);
        Assert.Equal(2, group.Redundant.Count);
        Assert.Equal(10_000_000, group.RecoverableBytes);
    }

    [Fact]
    public void ASingleFileIsNotAGroupOfOne()
    {
        var tracks = new List<AudioTrack> { Track("sozinha.mp3", 5_000_000, [440, 494, 523]) };

        Assert.Empty(AudioDuplicateFinder.Group(tracks, AudioFingerprint.MatchThreshold));
    }
}
