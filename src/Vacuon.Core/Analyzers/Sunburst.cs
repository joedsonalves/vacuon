namespace Vacuon.Core.Analyzers;

/// <summary>
/// One wedge of a sunburst: a ring, and the slice of it this item owns.
/// <para>
/// Angles are in radians, clockwise from twelve o'clock, which is where a reader's eye
/// starts. No <c>System.Windows</c> anything — the core draws nothing and knows nothing
/// about how this is painted.
/// </para>
/// </summary>
/// <param name="Ring">Depth: 0 is the innermost ring, one per level below the root.</param>
public readonly record struct SunburstWedge(int Ring, double StartAngle, double SweepAngle, int Item)
{
    public double EndAngle => StartAngle + SweepAngle;

    /// <summary>A wedge thinner than an eyelash cannot be clicked and should not be drawn.</summary>
    public bool IsVisible(double minimumSweep = 0.004) => SweepAngle >= minimumSweep;
}

/// <summary>
/// The sunburst layout (PRD F2.8) — the treemap's answer, told a different way.
/// <para>
/// A treemap spends its pixels on area and says "which of these is biggest". A sunburst
/// spends them on angle and depth, and says "how does this break down, level by level" —
/// the same disk, and the question people actually ask about a folder they do not
/// recognise. They earn their place beside each other rather than one replacing the other.
/// </para>
/// <para>
/// ⚠️ <b>A child's slice is carved out of its parent's, never out of the whole circle.</b>
/// That is the invariant: the ring below always adds up to exactly the wedge above it, so
/// every ring is the same disk seen at a different depth. Get that wrong and the picture
/// stops summing — the same failure as two overlapping treemap boxes drawing one byte twice.
/// </para>
/// </summary>
public static class Sunburst
{
    /// <summary>A whole turn. Everything here is a fraction of this.</summary>
    public const double FullTurn = Math.PI * 2;

    /// <summary>
    /// Lays one ring out inside the angular span of its parent.
    /// </summary>
    /// <param name="weights">Sizes of the items on this ring, in order.</param>
    /// <param name="startAngle">Where the parent's wedge begins.</param>
    /// <param name="sweepAngle">How wide the parent's wedge is.</param>
    /// <param name="output">One wedge per weight, filled in.</param>
    public static void Layout(ReadOnlySpan<long> weights, int ring, double startAngle, double sweepAngle,
                              Span<SunburstWedge> output)
    {
        if (output.Length < weights.Length)
            throw new ArgumentException("output menor que a entrada", nameof(output));

        long total = 0;
        foreach (long weight in weights) total += weight > 0 ? weight : 0;

        if (total <= 0)
        {
            // Nothing has a size: split the span evenly rather than drawing nothing. A ring
            // of empty folders is still a ring of folders somebody wants to see.
            double each = weights.Length > 0 ? sweepAngle / weights.Length : 0;

            for (int i = 0; i < weights.Length; i++)
                output[i] = new SunburstWedge(ring, startAngle + (each * i), each, i);

            return;
        }

        double at = startAngle;

        for (int i = 0; i < weights.Length; i++)
        {
            long weight = weights[i] > 0 ? weights[i] : 0;
            double sweep = sweepAngle * ((double)weight / total);

            output[i] = new SunburstWedge(ring, at, sweep, i);
            at += sweep;
        }

        // ⚠️ The last wedge is stretched to the parent's exact end rather than left where the
        // divisions landed. Summing fractions of a double leaves a sliver of a degree open,
        // and a ring that does not quite close is a ring that says the parts do not add up
        // to the whole — which is the one thing this picture exists to show.
        if (weights.Length > 0)
        {
            SunburstWedge last = output[weights.Length - 1];
            output[weights.Length - 1] = last with { SweepAngle = startAngle + sweepAngle - last.StartAngle };
        }
    }

    /// <summary>
    /// The outer radius of a ring, given how many rings are being drawn.
    /// <para>
    /// Rings get thinner as they go out: an outer ring holds many more items than an inner
    /// one, and giving them all the same thickness spends most of the picture on the level
    /// with the least to say.
    /// </para>
    /// </summary>
    public static double RingOuterRadius(int ring, int ringCount, double radius, double holeFraction = 0.25)
    {
        if (ringCount <= 0 || radius <= 0) return 0;

        double hole = radius * holeFraction;
        double usable = radius - hole;

        // Each ring gets a share that shrinks by a fixed ratio, normalised so the last one
        // ends exactly at the edge.
        double sum = 0;
        for (int i = 0; i < ringCount; i++) sum += Math.Pow(0.82, i);

        double used = 0;
        for (int i = 0; i <= ring && i < ringCount; i++) used += Math.Pow(0.82, i);

        return hole + (usable * used / sum);
    }

    /// <summary>The inner radius of a ring: where the one before it stopped.</summary>
    public static double RingInnerRadius(int ring, int ringCount, double radius, double holeFraction = 0.25) =>
        ring <= 0 ? radius * holeFraction : RingOuterRadius(ring - 1, ringCount, radius, holeFraction);

    /// <summary>
    /// Which wedge a point falls in, or -1. The hit test the picture is useless without.
    /// </summary>
    public static int HitTest(IReadOnlyList<SunburstWedge> wedges, double dx, double dy,
                              int ringCount, double radius, double holeFraction = 0.25)
    {
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance > radius || distance < radius * holeFraction) return -1;

        // Clockwise from twelve o'clock, to match the layout.
        double angle = Math.Atan2(dx, -dy);
        if (angle < 0) angle += FullTurn;

        for (int i = 0; i < wedges.Count; i++)
        {
            SunburstWedge wedge = wedges[i];

            double inner = RingInnerRadius(wedge.Ring, ringCount, radius, holeFraction);
            double outer = RingOuterRadius(wedge.Ring, ringCount, radius, holeFraction);

            if (distance < inner || distance > outer) continue;
            // Half-open, [start, end): a point exactly on a boundary belongs to one wedge
            // and not to both, and which one it is does not depend on the loop's order.
            if (angle < wedge.StartAngle || angle >= wedge.EndAngle) continue;

            return i;
        }

        return -1;
    }
}
