using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M7. A treemap is a picture that claims "area is size". Two properties make
/// that claim true — areas exactly proportional to weights, and rectangles that tile the
/// bounds with no gap and no overlap — and both are asserted here rather than eyeballed,
/// because a treemap that is slightly wrong looks exactly like one that is right.
/// </summary>
public class TreemapTests
{
    private static readonly TreemapRect Canvas = new(0, 0, 800, 600);

    private static TreemapRect[] Layout(params long[] weights)
    {
        var output = new TreemapRect[weights.Length];
        Treemap.Layout(weights, Canvas, output);
        return output;
    }

    [Fact]
    public void AreasAreProportionalToWeights()
    {
        long[] weights = [500, 300, 150, 50];
        TreemapRect[] rects = Layout(weights);

        double total = Canvas.Area;
        long sum = 1000;

        for (int i = 0; i < weights.Length; i++)
        {
            double expected = total * weights[i] / sum;
            Assert.Equal(expected, rects[i].Area, 1.0);
        }
    }

    [Fact]
    public void TheRectanglesFillTheBoundsExactly()
    {
        TreemapRect[] rects = Layout(400, 250, 200, 100, 50);

        double covered = 0;
        foreach (TreemapRect r in rects) covered += r.Area;

        Assert.Equal(Canvas.Area, covered, 1.0);
    }

    [Fact]
    public void NoTwoRectanglesOverlap()
    {
        // The one defect that turns a treemap into a lie: overlapping boxes mean some bytes
        // are drawn twice and the picture no longer adds up to the disk.
        TreemapRect[] rects = Layout(300, 220, 180, 130, 90, 45, 25, 10);

        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                Assert.False(Overlaps(rects[i], rects[j]),
                             $"rect {i} and rect {j} overlap");
            }
        }
    }

    [Fact]
    public void EverythingStaysInsideTheBounds()
    {
        TreemapRect[] rects = Layout(700, 200, 60, 30, 10);

        foreach (TreemapRect r in rects)
        {
            Assert.True(r.X >= Canvas.X - 0.001, "left edge escaped");
            Assert.True(r.Y >= Canvas.Y - 0.001, "top edge escaped");
            Assert.True(r.Right <= Canvas.Right + 0.001, "right edge escaped");
            Assert.True(r.Bottom <= Canvas.Bottom + 0.001, "bottom edge escaped");
        }
    }

    [Fact]
    public void SquarifyingBeatsSliceAndDiceOnAspectRatio()
    {
        // The reason this algorithm exists. Slice-and-dice would give every item the full
        // height of the canvas, so a small item becomes an unhoverable sliver.
        long[] weights = [400, 300, 150, 80, 40, 20, 10];
        TreemapRect[] rects = Layout(weights);

        double worstSquarified = 0;
        foreach (TreemapRect r in rects)
            if (r.AspectRatio > worstSquarified) worstSquarified = r.AspectRatio;

        // Slice-and-dice: full height, width proportional to weight.
        double worstSlice = 0;
        long sum = 1000;
        foreach (long w in weights)
        {
            double width = Canvas.Width * w / sum;
            double ratio = Canvas.Height / width;
            if (ratio > worstSlice) worstSlice = ratio;
        }

        Assert.True(worstSquarified < worstSlice,
                    $"squarified worst {worstSquarified:N1} should beat slice {worstSlice:N1}");

        // And in absolute terms it stays in a range a person can actually click.
        Assert.True(worstSquarified < 12, $"worst aspect ratio {worstSquarified:N1} is a sliver");
    }

    [Fact]
    public void ASingleItemTakesTheWholeCanvas()
    {
        TreemapRect only = Assert.Single(Layout(42));

        Assert.Equal(Canvas.X, only.X, 0.001);
        Assert.Equal(Canvas.Y, only.Y, 0.001);
        Assert.Equal(Canvas.Area, only.Area, 0.001);
    }

    [Fact]
    public void EqualWeightsGetEqualAreas()
    {
        TreemapRect[] rects = Layout(10, 10, 10, 10);

        foreach (TreemapRect r in rects)
            Assert.Equal(Canvas.Area / 4, r.Area, 1.0);
    }

    [Fact]
    public void ZeroWeightGetsAnEmptyRectangleAndKeepsItsSlot()
    {
        // Dropping them would shift every later index, and the caller's list of folders is
        // indexed in step with this array.
        TreemapRect[] rects = Layout(100, 0, 100);

        Assert.Equal(0, rects[1].Area);
        Assert.Equal(Canvas.Area / 2, rects[0].Area, 1.0);
        Assert.Equal(Canvas.Area / 2, rects[2].Area, 1.0);
    }

    [Fact]
    public void NegativeWeightIsTreatedAsNothing()
    {
        TreemapRect[] rects = Layout(100, -50, 100);

        Assert.Equal(0, rects[1].Area);
        Assert.Equal(Canvas.Area, rects[0].Area + rects[2].Area, 1.0);
    }

    [Fact]
    public void AllZeroWeightsDrawNothingInsteadOfDividingByZero()
    {
        TreemapRect[] rects = Layout(0, 0, 0);

        foreach (TreemapRect r in rects) Assert.Equal(0, r.Area);
    }

    [Fact]
    public void EmptyInputIsNotAnError()
    {
        var output = Array.Empty<TreemapRect>();
        Treemap.Layout([], Canvas, output);
        Assert.Empty(output);
    }

    [Fact]
    public void ADegenerateCanvasProducesNothingRatherThanNaN()
    {
        var output = new TreemapRect[3];
        Treemap.Layout([10, 20, 30], new TreemapRect(0, 0, 0, 500), output);

        foreach (TreemapRect r in output)
        {
            Assert.False(double.IsNaN(r.Width), "NaN width");
            Assert.Equal(0, r.Area);
        }
    }

    [Fact]
    public void OutputShorterThanInputIsRejected()
    {
        var output = new TreemapRect[2];
        Assert.Throws<ArgumentException>(() => Treemap.Layout([1, 2, 3], Canvas, output));
    }

    [Fact]
    public void AWildRangeOfSizesStillTilesExactly()
    {
        // What a real disk looks like: one enormous folder and a long tail of small ones.
        long[] weights = [900_000, 50_000, 20_000, 9_000, 4_000, 1_000, 500, 200, 100, 50, 20, 10];

        TreemapRect[] rects = Layout(weights);

        double covered = 0;
        foreach (TreemapRect r in rects) covered += r.Area;

        Assert.Equal(Canvas.Area, covered, 1.0);

        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                Assert.False(Overlaps(rects[i], rects[j]), $"{i} overlaps {j}");
    }

    [Fact]
    public void OneHundredThousandRectanglesLayOutQuickly()
    {
        // The PRD asks for 100 k rectangles at 60 fps. Layout is not the render, but it is
        // the part that would rule the target out on its own if it were slow.
        var weights = new long[100_000];
        for (int i = 0; i < weights.Length; i++) weights[i] = weights.Length - i;

        var output = new TreemapRect[weights.Length];

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Treemap.Layout(weights, Canvas, output);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 500,
                    $"layout of 100k took {watch.ElapsedMilliseconds} ms");

        double covered = 0;
        foreach (TreemapRect r in output) covered += r.Area;
        Assert.Equal(Canvas.Area, covered, 5.0);
    }

    private static bool Overlaps(TreemapRect a, TreemapRect b)
    {
        const double epsilon = 0.001;

        if (a.Area <= 0 || b.Area <= 0) return false;

        return a.X < b.Right - epsilon
            && b.X < a.Right - epsilon
            && a.Y < b.Bottom - epsilon
            && b.Y < a.Bottom - epsilon;
    }
}
