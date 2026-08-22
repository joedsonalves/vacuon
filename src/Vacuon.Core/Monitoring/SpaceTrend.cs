namespace Vacuon.Core.Monitoring;

/// <summary>One free-space measurement of one volume, at one moment.</summary>
public sealed record SpaceReading(DateTimeOffset TakenAt, char DriveLetter, long FreeBytes, long TotalBytes);

/// <summary>Why a projection was not offered. Never guessed around — always said.</summary>
public enum ProjectionRefusal
{
    /// <summary>None: a projection is being offered.</summary>
    None,

    /// <summary>Two points define a line through any pair of numbers. Three is the floor.</summary>
    TooFewReadings,

    /// <summary>The readings all come from one short stretch; days cannot come out of minutes.</summary>
    SpanTooShort,

    /// <summary>Free space is steady or growing. There is no "full" to project towards.</summary>
    NotFilling,

    /// <summary>The readings do not sit near any line, so the slope through them means nothing.</summary>
    FitTooPoor,

    /// <summary>The volume does fill, but so slowly that a date would be theatre.</summary>
    BeyondHorizon,
}

/// <summary>
/// What the free space on a volume has been doing, and — only when the readings support it —
/// when it runs out.
/// </summary>
public sealed record VolumeTrend(
    char DriveLetter,
    long FreeBytes,
    long TotalBytes,
    int ReadingCount,
    TimeSpan Span,
    double BytesPerDay,
    double FitQuality,
    double? DaysUntilFull,
    ProjectionRefusal Refusal)
{
    public bool HasProjection => Refusal == ProjectionRefusal.None && DaysUntilFull is not null;

    /// <summary>
    /// Below this, a slope is called flat. A hundred megabytes a day on a modern disk is
    /// noise, and an arrow that twitches at noise tells the reader nothing.
    /// </summary>
    public const double FlatBandBytesPerDay = 100L * 1024 * 1024;

    /// <summary>Falling, rising or flat — the arrow, which needs far less evidence than a date.</summary>
    public int Direction => BytesPerDay switch
    {
        < -FlatBandBytesPerDay => -1,
        > FlatBandBytesPerDay => 1,
        _ => 0,
    };
}

/// <summary>
/// Fits a line through measured free-space readings and, when the readings earn it, says how
/// long until the volume is full.
/// <para>
/// <b>This is the one place in the app that states a number it did not measure</b>, and the
/// whole design is about paying for that honestly. A projection is a prediction, so it is
/// built only from readings actually taken on this machine, it is labelled a projection
/// wherever it appears, and — the part that matters — it is <b>refused outright</b> whenever
/// the data cannot carry it. Every refusal names its reason, so the interface can explain the
/// blank instead of quietly showing nothing.
/// </para>
/// <para>
/// The refusals are not defensive padding. Two readings fit a line perfectly no matter what
/// the disk did; a disk that gained and lost a gigabyte over one lunch break extrapolates to a
/// confident, absurd date; and free space that wobbles around a flat average has a slope whose
/// sign is decided by whichever reading happened to land last.
/// </para>
/// </summary>
public static class SpaceTrend
{
    /// <summary>Three points: the first count at which readings can disagree with a line.</summary>
    public const int MinimumReadings = 3;

    /// <summary>
    /// Below six hours of history, no projection is offered.
    /// <para>
    /// Not a round number for its own sake: a disk's day has shape — a build runs, a browser
    /// cache fills, a backup drops a file and takes it away again — and a window shorter than
    /// that samples one phase of the shape and calls the phase a trend.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromHours(6);

    /// <summary>
    /// How closely the readings must sit to the fitted line, as a coefficient of determination.
    /// <para>
    /// Free space that scatters around its own trend gives a slope whose sign the last reading
    /// decides. 0.5 is not a claim of statistical rigour; it is the point below which the line
    /// stops describing the readings at all.
    /// </para>
    /// </summary>
    public const double MinimumFit = 0.5;

    /// <summary>Past a year out, the honest answer is "not soon", not a date.</summary>
    public const double HorizonDays = 365;

    /// <summary>
    /// Reads the trend of one volume. The readings may arrive in any order.
    /// </summary>
    public static VolumeTrend Of(char driveLetter, IReadOnlyList<SpaceReading> readings)
    {
        if (readings.Count == 0)
        {
            return new VolumeTrend(driveLetter, 0, 0, 0, TimeSpan.Zero, 0, 0, null,
                                   ProjectionRefusal.TooFewReadings);
        }

        SpaceReading newest = readings[0];
        SpaceReading oldest = readings[0];

        foreach (SpaceReading reading in readings)
        {
            if (reading.TakenAt > newest.TakenAt) newest = reading;
            if (reading.TakenAt < oldest.TakenAt) oldest = reading;
        }

        TimeSpan span = newest.TakenAt - oldest.TakenAt;

        VolumeTrend Refuse(ProjectionRefusal why, double perDay = 0, double fit = 0) =>
            new(driveLetter, newest.FreeBytes, newest.TotalBytes, readings.Count, span,
                perDay, fit, null, why);

        if (readings.Count < MinimumReadings) return Refuse(ProjectionRefusal.TooFewReadings);
        if (span < MinimumSpan) return Refuse(ProjectionRefusal.SpanTooShort);

        (double slopePerDay, double fitQuality) = Fit(readings, oldest.TakenAt);

        // A slope shallower than a byte a day is the disk sitting still.
        if (slopePerDay >= -1)
            return Refuse(ProjectionRefusal.NotFilling, slopePerDay, fitQuality);

        if (fitQuality < MinimumFit)
            return Refuse(ProjectionRefusal.FitTooPoor, slopePerDay, fitQuality);

        double days = newest.FreeBytes / -slopePerDay;

        if (days > HorizonDays)
            return Refuse(ProjectionRefusal.BeyondHorizon, slopePerDay, fitQuality);

        return new VolumeTrend(driveLetter, newest.FreeBytes, newest.TotalBytes, readings.Count,
                               span, slopePerDay, fitQuality, days, ProjectionRefusal.None);
    }

    /// <summary>
    /// Least squares through the readings, in bytes per day, with the fraction of the
    /// variation the line accounts for.
    /// </summary>
    private static (double SlopePerDay, double FitQuality) Fit(IReadOnlyList<SpaceReading> readings,
                                                               DateTimeOffset origin)
    {
        double sumX = 0, sumY = 0;

        foreach (SpaceReading reading in readings)
        {
            sumX += (reading.TakenAt - origin).TotalDays;
            sumY += reading.FreeBytes;
        }

        double meanX = sumX / readings.Count;
        double meanY = sumY / readings.Count;

        double covariance = 0, varianceX = 0;

        foreach (SpaceReading reading in readings)
        {
            double dx = (reading.TakenAt - origin).TotalDays - meanX;
            double dy = reading.FreeBytes - meanY;

            covariance += dx * dy;
            varianceX += dx * dx;
        }

        if (varianceX == 0) return (0, 0);

        double slope = covariance / varianceX;
        double intercept = meanY - slope * meanX;

        double residual = 0, total = 0;

        foreach (SpaceReading reading in readings)
        {
            double predicted = slope * (reading.TakenAt - origin).TotalDays + intercept;
            double actual = reading.FreeBytes;

            residual += (actual - predicted) * (actual - predicted);
            total += (actual - meanY) * (actual - meanY);
        }

        // Readings that never move have no variation to explain. Calling that a perfect fit
        // would let a motionless disk earn a confident projection.
        double fit = total == 0 ? 0 : 1 - residual / total;

        return (slope, Math.Clamp(fit, 0, 1));
    }
}
