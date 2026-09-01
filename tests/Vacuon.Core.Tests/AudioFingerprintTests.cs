using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The acoustic fingerprint (PRD F4.7): what a recording sounds like, not what its bytes are.
/// <para>
/// Every signal here is generated, so the right answer is known rather than assumed — and
/// nothing depends on what audio happens to be on the machine running the tests.
/// </para>
/// </summary>
public class AudioFingerprintTests
{
    private const int Rate = AudioFingerprint.SampleRate;

    /// <summary>A little tune: a sequence of notes, each held for a fifth of a second.</summary>
    private static float[] Tune(double[] frequencies, double seconds = 6, double gain = 0.5, double startSeconds = 0)
    {
        int total = (int)(Rate * seconds);
        var samples = new float[total];
        int noteLength = Rate / 5;
        int offset = (int)(Rate * startSeconds);

        for (int i = 0; i < total; i++)
        {
            int at = i - offset;
            if (at < 0) continue;

            double frequency = frequencies[(at / noteLength) % frequencies.Length];

            // Two harmonics, so a band has a neighbour to be compared against.
            samples[i] = (float)(gain * ((0.7 * Math.Sin(2 * Math.PI * frequency * at / Rate))
                                       + (0.3 * Math.Sin(4 * Math.PI * frequency * at / Rate))));
        }

        return samples;
    }


    [Fact]
    public void TheSameTuneMatchesItself()
    {
        double[] notes = [261.63, 329.63, 392.00, 523.25];

        uint[] a = AudioFingerprint.Compute(Tune(notes));
        uint[] b = AudioFingerprint.Compute(Tune(notes));

        Assert.NotEmpty(a);
        Assert.Equal(1.0, AudioFingerprint.Similarity(a, b), 6);
    }

    [Fact]
    public void HalfTheVolumeIsTheSameRecording()
    {
        // ⚠️ The reason every bit compares two things against each other rather than against
        // a fixed level. An absolute threshold would make this a different song.
        double[] notes = [261.63, 329.63, 392.00, 523.25];

        uint[] loud = AudioFingerprint.Compute(Tune(notes, gain: 0.8));
        uint[] quiet = AudioFingerprint.Compute(Tune(notes, gain: 0.1));

        Assert.True(AudioFingerprint.Similarity(loud, quiet) > 0.95,
                    $"a mesma musica mais baixa deu {AudioFingerprint.Similarity(loud, quiet):P1}");
    }

    [Fact]
    public void ADifferentTuneIsADifferentRecording()
    {
        uint[] a = AudioFingerprint.Compute(Tune([261.63, 329.63, 392.00, 523.25]));
        uint[] b = AudioFingerprint.Compute(Tune([233.08, 277.18, 349.23, 466.16]));

        double similarity = AudioFingerprint.Similarity(a, b);

        Assert.True(similarity < AudioFingerprint.MatchThreshold,
                    $"musicas diferentes deram {similarity:P1}");
    }

    [Fact]
    public void ASecondOfSilenceAtTheFrontDoesNotMakeItANewSong()
    {
        // The same recording ripped twice can start a moment apart. Comparing frame zero to
        // frame zero would call them strangers, so the alignment is searched for.
        double[] notes = [261.63, 329.63, 392.00, 523.25];

        uint[] straight = AudioFingerprint.Compute(Tune(notes, seconds: 8));
        uint[] delayed = AudioFingerprint.Compute(Tune(notes, seconds: 8, startSeconds: 1));

        Assert.True(AudioFingerprint.Similarity(straight, delayed) > AudioFingerprint.MatchThreshold,
                    $"com um segundo de atraso deu {AudioFingerprint.Similarity(straight, delayed):P1}");
    }


    [Fact]
    public void UnrelatedAudioScoresAboutHalf_NotZero()
    {
        // ⚠️ Every bit is a yes-or-no comparison, so two fingerprints with nothing in common
        // agree on half of them by chance. Somebody reading this as a zero-to-one scale would
        // take 55% for "somewhat alike"; it means nothing at all. Measured: noise 54,7%,
        // silence 52,3%, a different tune 55,8%.
        uint[] tune = AudioFingerprint.Compute(Tune([261.63, 329.63, 392.00, 523.25], seconds: 8));
        uint[] silence = AudioFingerprint.Compute(new float[Rate * 8]);

        double score = AudioFingerprint.Similarity(tune, silence);

        Assert.True(score is > 0.35 and < AudioFingerprint.MatchThreshold,
                    $"silencio contra musica deu {score:P1}");
    }

    [Fact]
    public void TheThresholdIsBelowTheWorstTrueMatchMeasured()
    {
        // ⚠️ Calibrated against real music re-encoded, not against these tones. The worst
        // true match was a 64k mono 22 kHz MP3 against the WAV it came from, at 84,8%, and
        // unrelated audio sits around 62%. The line is at 0,80: under the true match, far
        // above the noise.
        Assert.True(AudioFingerprint.MatchThreshold < 0.848,
                    "o limiar tem de ficar abaixo do pior acerto verdadeiro medido");
        Assert.True(AudioFingerprint.MatchThreshold > 0.70,
                    "e bem acima dos ~62% que audio sem relacao nenhuma da");

        double[] notes = [261.63, 329.63, 392.00, 523.25];

        uint[] a = AudioFingerprint.Compute(Tune(notes, seconds: 8));
        uint[] delayed = AudioFingerprint.Compute(Tune(notes, seconds: 8, startSeconds: 0.1));

        Assert.True(AudioFingerprint.Similarity(a, delayed) > AudioFingerprint.MatchThreshold);
    }

    [Fact]
    public void SomethingShorterThanOneFrameHasNoFingerprint()
    {
        // A fingerprint built on almost nothing should not exist — the same rule the picture
        // fingerprints follow for a near-uniform image.
        Assert.Empty(AudioFingerprint.Compute(new float[100]));
        Assert.Equal(0, AudioFingerprint.Similarity([], [1, 2, 3]));
    }

    [Fact]
    public void TwoFramesThatAgreeByChanceAreNotAMatch()
    {
        // A handful of frames can line up for nothing. Under a few seconds of overlap there
        // is no evidence either way, and the answer is zero rather than a confident number.
        uint[] tiny = [0xFFFFFF, 0xFFFFFF, 0xFFFFFF];

        Assert.Equal(0, AudioFingerprint.Similarity(tiny, tiny));
    }

    [Fact]
    public void TheTransformIsATransform()
    {
        // A single sine at bin 8 should put all its energy in bin 8 and its mirror.
        const int n = 64;
        var real = new double[n];
        var imaginary = new double[n];

        for (int i = 0; i < n; i++) real[i] = Math.Sin(2 * Math.PI * 8 * i / n);

        AudioFingerprint.Fft(real, imaginary);

        double at8 = Math.Sqrt((real[8] * real[8]) + (imaginary[8] * imaginary[8]));
        double at9 = Math.Sqrt((real[9] * real[9]) + (imaginary[9] * imaginary[9]));

        Assert.True(at8 > 20, $"o pico deu {at8:N2}");
        Assert.True(at9 < 1, $"o vizinho deu {at9:N2}");
    }

    [Fact]
    public void TheTransformRefusesASizeItCannotDo()
    {
        Assert.Throws<ArgumentException>(() => AudioFingerprint.Fft(new double[100], new double[100]));
    }
}
