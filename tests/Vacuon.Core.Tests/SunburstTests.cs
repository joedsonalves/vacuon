using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>The sunburst layout (PRD F2.8) — the same disk, told by angle instead of area.</summary>
public class SunburstTests
{
    [Fact]
    public void AngleIsProportionalToSize()
    {
        // The invariant the whole picture rests on: half the bytes is half the circle.
        long[] weights = [50, 25, 25];
        var wedges = new SunburstWedge[3];

        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        Assert.Equal(Sunburst.FullTurn / 2, wedges[0].SweepAngle, 6);
        Assert.Equal(Sunburst.FullTurn / 4, wedges[1].SweepAngle, 6);
        Assert.Equal(Sunburst.FullTurn / 4, wedges[2].SweepAngle, 6);
    }

    [Fact]
    public void TheRingClosesExactly()
    {
        // ⚠️ Summing fractions of a double leaves a sliver of a degree open, and a ring that
        // does not quite close says the parts do not add up to the whole — which is the one
        // thing this picture exists to show. The last wedge is stretched to the exact end.
        long[] weights = [7, 11, 13, 17, 19, 23];
        var wedges = new SunburstWedge[weights.Length];

        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        Assert.Equal(Sunburst.FullTurn, wedges[^1].EndAngle, 12);
        Assert.Equal(0, wedges[0].StartAngle, 12);
    }

    [Fact]
    public void WedgesDoNotOverlapAndLeaveNoGap()
    {
        long[] weights = [3, 1, 4, 1, 5, 9, 2, 6];
        var wedges = new SunburstWedge[weights.Length];

        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        for (int i = 1; i < wedges.Length; i++)
            Assert.Equal(wedges[i - 1].EndAngle, wedges[i].StartAngle, 12);
    }

    [Fact]
    public void AChildRingIsCarvedOutOfItsParentsWedge()
    {
        // ⚠️ The invariant that makes every ring the same disk seen deeper: a child's slice
        // comes out of its parent's, never out of the whole circle.
        long[] top = [60, 40];
        var outer = new SunburstWedge[2];
        Sunburst.Layout(top, 0, 0, Sunburst.FullTurn, outer);

        long[] children = [30, 30];
        var inner = new SunburstWedge[2];
        Sunburst.Layout(children, 1, outer[0].StartAngle, outer[0].SweepAngle, inner);

        Assert.Equal(outer[0].StartAngle, inner[0].StartAngle, 12);
        Assert.Equal(outer[0].EndAngle, inner[^1].EndAngle, 12);
        Assert.True(inner[^1].EndAngle <= outer[0].EndAngle + 1e-9);
    }

    [Fact]
    public void EverythingEmptyIsSplitEvenlyRatherThanNotDrawn()
    {
        // A ring of empty folders is still a ring of folders somebody wants to see.
        long[] weights = [0, 0, 0, 0];
        var wedges = new SunburstWedge[4];

        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        foreach (SunburstWedge wedge in wedges)
            Assert.Equal(Sunburst.FullTurn / 4, wedge.SweepAngle, 6);
    }

    [Fact]
    public void RingsGetThinnerFurtherOut()
    {
        // An outer ring holds many more items than an inner one; equal thickness spends most
        // of the picture on the level with the least to say.
        double inner = Sunburst.RingOuterRadius(0, 4, 200) - Sunburst.RingInnerRadius(0, 4, 200);
        double outer = Sunburst.RingOuterRadius(3, 4, 200) - Sunburst.RingInnerRadius(3, 4, 200);

        Assert.True(inner > outer, $"anel interno {inner:N1} deveria ser mais grosso que {outer:N1}");
    }

    [Fact]
    public void TheLastRingEndsExactlyAtTheEdge()
    {
        Assert.Equal(200, Sunburst.RingOuterRadius(3, 4, 200), 9);
        Assert.Equal(50, Sunburst.RingInnerRadius(0, 4, 200), 9);
    }

    [Fact]
    public void APointLandsInTheWedgeItLooksLikeItIsIn()
    {
        long[] weights = [25, 25, 25, 25];
        var wedges = new SunburstWedge[4];
        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        // The middle of each quarter, clockwise from twelve o'clock. Not the boundaries: a
        // point exactly on one belongs to a single wedge by the half-open rule, and testing
        // that is a different assertion from testing that the quarters are where they look.
        Assert.Equal(0, Sunburst.HitTest(wedges, 85, -85, 1, 200));     // 45 graus
        Assert.Equal(1, Sunburst.HitTest(wedges, 85, 85, 1, 200));      // 135
        Assert.Equal(2, Sunburst.HitTest(wedges, -85, 85, 1, 200));     // 225
        Assert.Equal(3, Sunburst.HitTest(wedges, -85, -85, 1, 200));    // 315

        // And a boundary lands in exactly one of them, never in both.
        Assert.Equal(1, Sunburst.HitTest(wedges, 120, 0, 1, 200));
    }

    [Fact]
    public void TheHoleAndTheOutsideAreNotAnyWedge()
    {
        long[] weights = [100];
        var wedges = new SunburstWedge[1];
        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        Assert.Equal(-1, Sunburst.HitTest(wedges, 0, 0, 1, 200));        // no centro
        Assert.Equal(-1, Sunburst.HitTest(wedges, 0, -300, 1, 200));     // fora
    }

    [Fact]
    public void ASliverIsNotWorthDrawing()
    {
        // A wedge thinner than an eyelash cannot be clicked, and a hundred thousand of them
        // is what makes a picture of a real volume take a second to paint.
        long[] weights = [1_000_000, 1];
        var wedges = new SunburstWedge[2];

        Sunburst.Layout(weights, 0, 0, Sunburst.FullTurn, wedges);

        Assert.True(wedges[0].IsVisible());
        Assert.False(wedges[1].IsVisible());
    }
}
