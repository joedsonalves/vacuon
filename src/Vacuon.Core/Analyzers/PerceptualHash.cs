using System.Numerics;
using Vacuon.Core.Preview;

namespace Vacuon.Core.Analyzers;

/// <summary>
/// A 64-bit perceptual fingerprint of an image, and the distance between two of them.
/// <para>
/// This is dHash — the image is reduced to a 9×8 grid of brightness and each pixel is
/// compared with the one to its right, giving 8×8 = 64 bits of "was this brighter than its
/// neighbour". What survives is the shape of the picture, which is exactly what a resize, a
/// re-encode or a quality change do not alter. Two copies of the same photo at 4000 px and
/// 800 px land within a bit or two of each other.
/// </para>
/// <para>
/// Not a cryptographic hash and not trying to be. <see cref="DuplicateFinder"/> answers
/// "are these the same bytes"; this answers "are these the same picture", which is a
/// different question with a fuzzier answer, and the fuzziness is the feature.
/// </para>
/// </summary>
public static class PerceptualHash
{
    /// <summary>Grid the image is reduced to. One extra column, because each row compares pairs.</summary>
    private const int Columns = 9;
    private const int Rows = 8;

    /// <summary>
    /// Fingerprints a thumbnail, or null when the bitmap cannot say anything about content.
    /// <para>
    /// <b>An icon is refused.</b> Every <c>.docx</c> on a machine shares one icon, so hashing
    /// icons would report thousands of identical "pictures" that have nothing to do with each
    /// other — the single loudest false positive this feature could produce. The thumbnail
    /// provider already knows which it handed back, and that flag is checked here rather
    /// than trusted to callers.
    /// </para>
    /// </summary>
    public static ulong? Compute(ThumbnailBitmap? bitmap)
    {
        if (bitmap is null || !bitmap.IsContentThumbnail) return null;
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return null;
        if (bitmap.Bgra32.Length < bitmap.Width * bitmap.Height * 4) return null;

        Span<double> cells = stackalloc double[Columns * Rows];
        Reduce(bitmap, cells);

        ulong hash = 0;
        int bit = 0;

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns - 1; column++)
            {
                double left = cells[row * Columns + column];
                double right = cells[row * Columns + column + 1];

                if (left > right) hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    /// <summary>
    /// Averages the source into a 9×8 grid of brightness.
    /// <para>
    /// Box-averaging rather than nearest-neighbour sampling, because sampling makes the
    /// fingerprint depend on which pixels happen to land on the grid — and then the same
    /// photo at two sizes gives two different answers, which is the one thing this must not
    /// do.
    /// </para>
    /// </summary>
    private static void Reduce(ThumbnailBitmap bitmap, Span<double> cells)
    {
        Span<int> counts = stackalloc int[Columns * Rows];
        cells.Clear();
        counts.Clear();

        byte[] pixels = bitmap.Bgra32;

        for (int y = 0; y < bitmap.Height; y++)
        {
            int cellY = y * Rows / bitmap.Height;
            int rowStart = y * bitmap.Stride;

            for (int x = 0; x < bitmap.Width; x++)
            {
                int cellX = x * Columns / bitmap.Width;
                int offset = rowStart + x * 4;

                // BGRA order, and the usual luma weights: the eye is far more sensitive to
                // green than to blue, so a plain average would let a blue-heavy re-encode
                // shift the fingerprint more than it should.
                double luma = 0.114 * pixels[offset]        // B
                            + 0.587 * pixels[offset + 1]    // G
                            + 0.299 * pixels[offset + 2];   // R

                int cell = cellY * Columns + cellX;
                cells[cell] += luma;
                counts[cell]++;
            }
        }

        for (int i = 0; i < cells.Length; i++)
            if (counts[i] > 0) cells[i] /= counts[i];
    }

    /// <summary>
    /// How many of the 64 bits differ. Zero means the same picture as far as this can tell.
    /// </summary>
    public static int Distance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    /// <summary>
    /// Distance at or below which two images are called the same picture.
    /// <para>
    /// Ten of sixty-four bits. Below about five, a re-encode at a different quality is
    /// already enough to separate two copies of one photo; above about fifteen, unrelated
    /// pictures with similar composition start being grouped — and a false "these are the
    /// same" is what gets somebody's photo deleted.
    /// </para>
    /// </summary>
    public const int DefaultThreshold = 10;
}
