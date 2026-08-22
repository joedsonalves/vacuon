namespace Vacuon.Core.Analyzers;

/// <summary>
/// A rectangle, in whatever unit the caller laid the treemap out in.
/// <para>
/// Deliberately not <c>System.Windows.Rect</c>. <c>Vacuon.Core</c> never references a UI
/// assembly, and the layout is arithmetic — it has no business knowing what will draw it.
/// </para>
/// </summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Longest side over shortest. 1.0 is a square; large is a sliver.</summary>
    public double AspectRatio
    {
        get
        {
            if (Width <= 0 || Height <= 0) return double.PositiveInfinity;
            return Width > Height ? Width / Height : Height / Width;
        }
    }
}

/// <summary>
/// Squarified treemap layout — Bruls, Huizing and van Wijk.
/// <para>
/// The naive alternative, slice-and-dice, splits the whole rectangle along one axis and
/// gives every item a full-height sliver. It is simpler and it is useless: a 40 GB folder
/// next to a 4 KB one becomes a line one pixel wide that nobody can hover, click or even
/// see. Squarifying keeps rectangles near square, which is what makes the picture readable
/// and, more to the point, clickable.
/// </para>
/// <para>
/// Areas are exactly proportional to weights, and the rectangles tile the bounds with no
/// gaps and no overlap. Those two properties are what make a treemap an honest picture of
/// a disk rather than a decoration, and both are covered by tests.
/// </para>
/// </summary>
public static class Treemap
{
    /// <summary>
    /// Lays weights out inside <paramref name="bounds"/>, writing one rectangle per weight
    /// into <paramref name="output"/> in the same order.
    /// <para>
    /// Weights should arrive sorted descending: the algorithm assumes it, and unsorted
    /// input produces a correct but visibly worse layout. Zero and negative weights get an
    /// empty rectangle rather than being dropped, so indices stay aligned with the caller's
    /// own list.
    /// </para>
    /// </summary>
    public static void Layout(ReadOnlySpan<long> weights, TreemapRect bounds, Span<TreemapRect> output)
    {
        if (output.Length < weights.Length)
            throw new ArgumentException("output is shorter than weights", nameof(output));

        for (int i = 0; i < weights.Length; i++) output[i] = default;

        if (weights.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        double total = 0;
        int usable = 0;

        foreach (long weight in weights)
        {
            if (weight <= 0) continue;
            total += weight;
            usable++;
        }

        if (usable == 0 || total <= 0) return;

        // Work in area units: one weight unit becomes this many square units.
        double scale = bounds.Area / total;

        TreemapRect free = bounds;
        int index = 0;

        // Items in the row being accumulated, as areas.
        var row = new double[weights.Length];
        var rowIndex = new int[weights.Length];
        int rowCount = 0;
        double rowArea = 0;

        while (index < weights.Length)
        {
            long weight = weights[index];

            if (weight <= 0)
            {
                // Nothing to draw, but the slot must still line up with the input.
                index++;
                continue;
            }

            double area = weight * scale;
            double shortest = Math.Min(free.Width, free.Height);

            // Adding this item is worth it while the worst aspect ratio in the row keeps
            // improving. When it stops, the row is as square as it will get.
            if (rowCount > 0 &&
                Worst(row, rowCount, rowArea + area, area, shortest) > Worst(row, rowCount, rowArea, 0, shortest))
            {
                free = PlaceRow(row, rowIndex, rowCount, rowArea, free, output);
                rowCount = 0;
                rowArea = 0;
                continue;   // retry this item against the new free rectangle
            }

            row[rowCount] = area;
            rowIndex[rowCount] = index;
            rowCount++;
            rowArea += area;
            index++;
        }

        if (rowCount > 0) PlaceRow(row, rowIndex, rowCount, rowArea, free, output);
    }

    /// <summary>
    /// Worst aspect ratio of a row, optionally including one more item.
    /// </summary>
    private static double Worst(double[] row, int count, double rowArea, double extra, double shortest)
    {
        if (rowArea <= 0 || shortest <= 0) return double.PositiveInfinity;

        double max = extra;
        double min = extra > 0 ? extra : double.MaxValue;

        for (int i = 0; i < count; i++)
        {
            if (row[i] > max) max = row[i];
            if (row[i] < min) min = row[i];
        }

        if (min == double.MaxValue || min <= 0) return double.PositiveInfinity;

        double side2 = shortest * shortest;
        double area2 = rowArea * rowArea;

        return Math.Max(side2 * max / area2, area2 / (side2 * min));
    }

    /// <summary>
    /// Places one accumulated row along the short side and returns what is left over.
    /// </summary>
    private static TreemapRect PlaceRow(double[] row, int[] rowIndex, int count, double rowArea,
                                        TreemapRect free, Span<TreemapRect> output)
    {
        bool horizontal = free.Width <= free.Height;

        if (horizontal)
        {
            // Row runs left to right across the top, and is this tall.
            double height = rowArea / free.Width;
            double x = free.X;

            for (int i = 0; i < count; i++)
            {
                double width = row[i] / height;

                // The last one takes the remainder, so rounding never leaves a seam.
                if (i == count - 1) width = free.Right - x;

                output[rowIndex[i]] = new TreemapRect(x, free.Y, width, height);
                x += width;
            }

            return new TreemapRect(free.X, free.Y + height, free.Width, free.Height - height);
        }

        double columnWidth = rowArea / free.Height;
        double y = free.Y;

        for (int i = 0; i < count; i++)
        {
            double h = row[i] / columnWidth;

            if (i == count - 1) h = free.Bottom - y;

            output[rowIndex[i]] = new TreemapRect(free.X, y, columnWidth, h);
            y += h;
        }

        return new TreemapRect(free.X + columnWidth, free.Y, free.Width - columnWidth, free.Height);
    }
}
