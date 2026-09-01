namespace Vacuon.Core.Analyzers;

/// <summary>
/// An acoustic fingerprint: what a recording sounds like, rather than what its bytes are
/// (PRD F4.7).
/// <para>
/// ⚠️ <b>This is not the exact-duplicate search wearing a hat.</b> Two files holding the same
/// bytes are already found by the full-file hash, in a fraction of the time and with no doubt
/// at all. This is for the other case: the same recording in FLAC and in a 320 kbps MP3, with
/// different tags, a different length of silence at the front, and not one byte in common.
/// </para>
/// <para>
/// ⚠️ <b>And that is exactly why a fingerprint match may never become a hard link.</b> A link
/// gives two names to <em>one</em> set of bytes; pointing the MP3's name at the FLAC's bytes
/// would not deduplicate anything, it would replace somebody's file with a different file.
/// Sounding the same is not being the same. The only honest actions here are the ones that
/// remove a copy the person chose.
/// </para>
/// <para>
/// How it works, and it is the same idea Chromaprint uses without being it: the audio is
/// folded down to twelve pitch classes per frame — every C is one band, every C sharp the
/// next — so transposing the encoding, the bitrate or the channel count changes almost
/// nothing. Each frame becomes one 32-bit word by comparing neighbouring bands against each
/// other rather than against an absolute level, which is what makes it survive a difference
/// in volume. The fingerprint is the sequence of those words.
/// </para>
/// </summary>
public static class AudioFingerprint
{
    /// <summary>
    /// Above this two recordings are called the same. Measured on real music, re-encoded:
    /// <code>
    /// mesma gravacao   WAV x AAC 96k                     91,5%
    ///                  WAV x MP3 192k a 25% do volume    87,9%
    ///                  WAV x MP3 64k mono 22 kHz         84,8%   <- o pior caso verdadeiro
    ///                  AAC x MP3 64k                     90,1%
    ///                  MP3 64k x MP3 192k                96,2%
    /// outra coisa      qualquer uma x ruido rosa         62,4% a 63,0%
    /// </code>
    /// <para>
    /// ⚠️ <b>O limiar era 0,85 e a musica real o derrubou.</b> Calibrei primeiro contra tons
    /// sintetizados, onde o falso positivo mais proximo — uma melodia de quatro notas com uma
    /// trocada — dava 82,5%, e 0,85 parecia folgado. Contra musica de verdade o pior acerto
    /// legitimo deu <b>84,8%</b>: um MP3 de 64k mono a 22 kHz, que joga fora tudo acima de
    /// 11 kHz e ainda soma os canais. O limiar sintetico teria perdido esse arquivo.
    /// </para>
    /// <para>
    /// ⚠️ <b>Audio sem relacao nenhuma da cerca de 60%, nao 0%.</b> Cada bit e uma comparacao
    /// de sim ou nao, entao duas impressoes que nada tem a ver concordam na metade deles por
    /// acaso. Quem ler isto como uma escala de zero a um acha que 62% quer dizer "um pouco
    /// parecido"; nao quer dizer nada. A faixa que interessa e o quinto de cima.
    /// </para>
    /// <para>
    /// ⚠️ <b>E o preco de descer para 0,80:</b> uma peca que compartilhe a maior parte do seu
    /// material pode cair num grupo — a melodia com uma nota trocada, aos 82,5%, agora entra.
    /// Foi escolha deliberada: num achador de duplicados que <b>nunca apaga por conta
    /// propria</b>, deixar passar uma copia real e pior que mostrar um par a mais para alguem
    /// olhar. A tela mostra os dois lados e a pessoa decide.
    /// </para>
    /// </summary>
    public const double MatchThreshold = 0.80;

    /// <summary>Everything is resampled to this before anything else happens.</summary>
    public const int SampleRate = 11025;

    /// <summary>Samples per analysis frame. 4096 at 11 kHz is about a third of a second.</summary>
    public const int FrameSize = 4096;

    /// <summary>How far the window moves each frame. Half overlap.</summary>
    public const int HopSize = 2048;

    /// <summary>Pitch classes: C, C#, D … B.</summary>
    public const int Bands = 12;

    /// <summary>
    /// Turns mono samples into the sequence of words that identifies the recording.
    /// </summary>
    /// <param name="samples">Mono, at <see cref="SampleRate"/>, in -1..1.</param>
    public static uint[] Compute(ReadOnlySpan<float> samples)
    {
        if (samples.Length < FrameSize) return [];

        int frames = ((samples.Length - FrameSize) / HopSize) + 1;
        var words = new uint[frames];

        var window = new float[FrameSize];
        for (int i = 0; i < FrameSize; i++)
        {
            // Hann. Without a window the edges of every frame ring across all the bands and
            // the fingerprint of silence stops being the fingerprint of silence.
            window[i] = 0.5f - (0.5f * MathF.Cos(2 * MathF.PI * i / (FrameSize - 1)));
        }

        var real = new double[FrameSize];
        var imaginary = new double[FrameSize];
        var chroma = new double[Bands];
        var previous = new double[Bands];

        for (int f = 0; f < frames; f++)
        {
            int start = f * HopSize;

            for (int i = 0; i < FrameSize; i++)
            {
                real[i] = samples[start + i] * window[i];
                imaginary[i] = 0;
            }

            Fft(real, imaginary);
            Chroma(real, imaginary, chroma);

            words[f] = Quantise(chroma, previous, f == 0);
            chroma.CopyTo(previous, 0);
        }

        return words;
    }

    /// <summary>
    /// Folds the spectrum into twelve pitch classes.
    /// <para>
    /// Every octave of the same note lands in the same band, which is what makes this about
    /// the music rather than about the recording.
    /// </para>
    /// </summary>
    private static void Chroma(double[] real, double[] imaginary, double[] into)
    {
        Array.Clear(into);

        // Only the bottom half of the spectrum is real; the rest is its mirror.
        for (int bin = 1; bin < FrameSize / 2; bin++)
        {
            double frequency = (double)bin * SampleRate / FrameSize;

            // Below and above this there is nothing worth a pitch: rumble at one end and
            // cymbals smeared across every band at the other.
            if (frequency < 55 || frequency > 2000) continue;

            double magnitude = Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
            if (magnitude <= 0) continue;

            // A440 is pitch class 9. Twelve semitones to the octave, logarithmically.
            double semitone = 12 * Math.Log2(frequency / 440.0);
            int band = (int)Math.Round(semitone + 9) % Bands;
            if (band < 0) band += Bands;

            into[band] += magnitude;
        }
    }

    /// <summary>
    /// One frame, one word.
    /// <para>
    /// ⚠️ Every bit compares two things against <b>each other</b>, never against a fixed
    /// level: this band against its neighbour, and this frame against the one before it. An
    /// absolute threshold would make the same song at half the volume a different song, which
    /// is precisely the mistake this exists to avoid.
    /// </para>
    /// </summary>
    private static uint Quantise(double[] chroma, double[] previous, bool first)
    {
        uint word = 0;

        for (int band = 0; band < Bands; band++)
        {
            // Is this band louder than the next one round the circle?
            if (chroma[band] > chroma[(band + 1) % Bands]) word |= 1u << band;

            // And is it rising or falling since the last frame? That is what carries the
            // rhythm, and it is what tells two songs in the same key apart.
            if (!first && chroma[band] > previous[band]) word |= 1u << (band + Bands);
        }

        return word;
    }

    /// <summary>
    /// How alike two fingerprints are, 0 to 1, over the best alignment found.
    /// <para>
    /// The alignment matters: the same recording ripped twice can start a second apart, and
    /// comparing them frame zero to frame zero would call them strangers.
    /// </para>
    /// </summary>
    public static double Similarity(uint[] left, uint[] right, int maxOffsetFrames = 60)
    {
        if (left.Length == 0 || right.Length == 0) return 0;

        double best = 0;

        for (int offset = -maxOffsetFrames; offset <= maxOffsetFrames; offset++)
        {
            int leftStart = offset > 0 ? offset : 0;
            int rightStart = offset > 0 ? 0 : -offset;

            int overlap = Math.Min(left.Length - leftStart, right.Length - rightStart);

            // A handful of frames can agree by chance. Anything under a few seconds of
            // overlap is not evidence of anything.
            if (overlap < 20) continue;

            long matching = 0;

            for (int i = 0; i < overlap; i++)
            {
                uint difference = left[leftStart + i] ^ right[rightStart + i];
                matching += 24 - System.Numerics.BitOperations.PopCount(difference & 0xFFFFFF);
            }

            double score = (double)matching / (overlap * 24.0);
            if (score > best) best = score;
        }

        return best;
    }

    /// <summary>
    /// In-place radix-2 FFT.
    /// <para>
    /// Written here rather than taken from a package: this is the only maths the feature
    /// needs, and <see cref="Vacuon.Core"/> having no third-party dependency is a property
    /// worth more than the fifty lines it costs.
    /// </para>
    /// </summary>
    public static void Fft(double[] real, double[] imaginary)
    {
        int n = real.Length;
        if (n <= 1 || (n & (n - 1)) != 0) throw new ArgumentException("tamanho tem de ser potencia de dois", nameof(real));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;

            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double stepReal = Math.Cos(angle);
            double stepImaginary = Math.Sin(angle);

            for (int i = 0; i < n; i += length)
            {
                double wReal = 1;
                double wImaginary = 0;

                for (int j = 0; j < length / 2; j++)
                {
                    int a = i + j;
                    int b = a + (length / 2);

                    double tReal = (real[b] * wReal) - (imaginary[b] * wImaginary);
                    double tImaginary = (real[b] * wImaginary) + (imaginary[b] * wReal);

                    real[b] = real[a] - tReal;
                    imaginary[b] = imaginary[a] - tImaginary;
                    real[a] += tReal;
                    imaginary[a] += tImaginary;

                    double nextReal = (wReal * stepReal) - (wImaginary * stepImaginary);
                    wImaginary = (wReal * stepImaginary) + (wImaginary * stepReal);
                    wReal = nextReal;
                }
            }
        }
    }
}
