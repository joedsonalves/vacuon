using Vacuon.Core.Monitoring;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M9, F8.2 — the projection.
/// <para>
/// The app's governing rule is that it never states a number it did not measure, and a
/// projection is by definition not measured. What makes it allowable is that it is built from
/// measured readings and refused whenever they cannot carry it. So most of what follows tests
/// the refusals: a projection that appears when it should not is the bug here, not a missing
/// one.
/// </para>
/// </summary>
public class SpaceTrendTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static SpaceReading At(double hours, long free, long total = 500_000_000_000) =>
        new(Start.AddHours(hours), 'C', free, total);

    [Fact]
    public void ASteadyFallProjectsTheDayItRunsOut()
    {
        // 10 GB free, losing 1 GB a day, measured over four days: ten days left.
        var readings = new List<SpaceReading>();
        for (int day = 0; day <= 4; day++)
            readings.Add(At(day * 24, 14_000_000_000L - day * 1_000_000_000L));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.True(trend.HasProjection);
        Assert.Equal(10, trend.DaysUntilFull!.Value, precision: 1);
        Assert.Equal(-1, trend.Direction);
    }

    [Fact]
    public void TwoReadingsNeverProject()
    {
        // Any two points fit a line perfectly, whatever the disk actually did between them.
        VolumeTrend trend = SpaceTrend.Of('C',
        [
            At(0, 100_000_000_000),
            At(48, 50_000_000_000),
        ]);

        Assert.False(trend.HasProjection);
        Assert.Equal(ProjectionRefusal.TooFewReadings, trend.Refusal);
    }

    [Fact]
    public void AnHourOfHistoryDoesNotBecomeAForecastInDays()
    {
        // A build ran. Extrapolating that hour would announce the disk full by dinner.
        var readings = new List<SpaceReading>();
        for (int i = 0; i < 6; i++)
            readings.Add(At(i * 0.1, 20_000_000_000L - i * 1_000_000_000L));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.False(trend.HasProjection);
        Assert.Equal(ProjectionRefusal.SpanTooShort, trend.Refusal);
    }

    [Fact]
    public void ADiskThatIsGainingSpaceHasNothingToProjectTowards()
    {
        var readings = new List<SpaceReading>();
        for (int day = 0; day <= 4; day++)
            readings.Add(At(day * 24, 10_000_000_000L + day * 2_000_000_000L));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.False(trend.HasProjection);
        Assert.Equal(ProjectionRefusal.NotFilling, trend.Refusal);
        Assert.Equal(1, trend.Direction);
    }

    [Fact]
    public void AFlatDiskGetsNoArrowAndNoDate()
    {
        var readings = new List<SpaceReading>();
        for (int day = 0; day <= 4; day++)
            readings.Add(At(day * 24, 10_000_000_000L));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.False(trend.HasProjection);
        Assert.Equal(0, trend.Direction);
    }

    [Fact]
    public void SpaceThatWobblesIsNotATrend()
    {
        // Files arrive and leave; free space ends lower than it started, so a naive slope
        // points down. The readings sit nowhere near that line, and the sign of the slope is
        // decided by whichever sample happened to land last.
        long[] free =
        [
            30_000_000_000, 12_000_000_000, 28_000_000_000, 11_000_000_000,
            27_000_000_000, 10_000_000_000, 26_000_000_000, 9_000_000_000,
        ];

        var readings = new List<SpaceReading>();
        for (int i = 0; i < free.Length; i++) readings.Add(At(i * 24, free[i]));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.False(trend.HasProjection);
        Assert.Equal(ProjectionRefusal.FitTooPoor, trend.Refusal);
        Assert.True(trend.FitQuality < SpaceTrend.MinimumFit);
    }

    [Fact]
    public void AVolumeThatFillsInDecadesIsToldItIsNotSoon()
    {
        // 400 GB free, losing a megabyte a day. True, and useless as a date.
        var readings = new List<SpaceReading>();
        for (int day = 0; day <= 10; day++)
            readings.Add(At(day * 24, 400_000_000_000L - day * 1_000_000L));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.False(trend.HasProjection);
        Assert.Equal(ProjectionRefusal.BeyondHorizon, trend.Refusal);
    }

    [Fact]
    public void NoReadingsAtAllIsAnswerable()
    {
        VolumeTrend trend = SpaceTrend.Of('C', []);

        Assert.False(trend.HasProjection);
        Assert.Equal(ProjectionRefusal.TooFewReadings, trend.Refusal);
        Assert.Equal(0, trend.ReadingCount);
    }

    [Fact]
    public void ReadingsOutOfOrderGiveTheSameAnswer()
    {
        // The store keeps them sorted, but nothing in the maths should depend on that.
        var ordered = new List<SpaceReading>();
        for (int day = 0; day <= 4; day++)
            ordered.Add(At(day * 24, 14_000_000_000L - day * 1_000_000_000L));

        var shuffled = new List<SpaceReading> { ordered[3], ordered[0], ordered[4], ordered[2], ordered[1] };

        Assert.Equal(SpaceTrend.Of('C', ordered).DaysUntilFull,
                     SpaceTrend.Of('C', shuffled).DaysUntilFull);
    }

    [Fact]
    public void TheProjectionCountsFromTheNewestReadingNotTheOldest()
    {
        // Counting from the oldest would keep announcing a date that has already passed.
        var readings = new List<SpaceReading>();
        for (int day = 0; day <= 4; day++)
            readings.Add(At(day * 24, 14_000_000_000L - day * 1_000_000_000L));

        VolumeTrend trend = SpaceTrend.Of('C', readings);

        Assert.Equal(10_000_000_000L, trend.FreeBytes);
        Assert.Equal(10, trend.DaysUntilFull!.Value, precision: 1);
    }
}

/// <summary>
/// The stored readings a projection is built from. Without these the trend has nothing to
/// fit, so the failure modes here are the ones that quietly turn a projection into fiction.
/// </summary>
public class SpaceHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                 $"vacuon-history-{Guid.NewGuid():N}.tsv");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static SpaceReading Reading(int hoursAgo, char drive = 'C', long free = 1_000) =>
        new(DateTimeOffset.Now.AddHours(-hoursAgo), drive, free, 10_000);

    [Fact]
    public void WhatWasWrittenComesBack()
    {
        var history = new SpaceHistory(_path);
        history.Append([Reading(3), Reading(2), Reading(1)]);

        Assert.Equal(3, history.Read().Count);
    }

    [Fact]
    public void ReadingsComeBackOldestFirst()
    {
        var history = new SpaceHistory(_path);
        history.Append([Reading(1), Reading(5), Reading(3)]);

        IReadOnlyList<SpaceReading> readings = history.Read();

        for (int i = 1; i < readings.Count; i++)
            Assert.True(readings[i - 1].TakenAt <= readings[i].TakenAt);
    }

    [Fact]
    public void OneVolumeIsReadWithoutTheOthers()
    {
        var history = new SpaceHistory(_path);
        history.Append([Reading(3, 'C'), Reading(2, 'D'), Reading(1, 'C')]);

        Assert.Equal(2, history.Read('C').Count);
        Assert.Single(history.Read('D'));
    }

    [Fact]
    public void AMangledLineCostsThatLineAndNothingElse()
    {
        // A history that throws itself away over one bad line loses months of readings and
        // silently stops projecting, which looks exactly like a feature that does not work.
        var history = new SpaceHistory(_path);
        history.Append([Reading(3), Reading(2)]);

        File.AppendAllText(_path, "this is not a reading\n");
        history.Append([Reading(1)]);

        Assert.Equal(3, history.Read().Count);
    }

    [Fact]
    public void ReadingATimestampBackGivesTheSameMoment()
    {
        var written = new SpaceReading(new DateTimeOffset(2026, 8, 22, 13, 45, 12, TimeSpan.FromHours(-3)),
                                       'C', 123_456_789, 500_000_000_000);

        var history = new SpaceHistory(_path);
        history.Append([written]);

        SpaceReading read = Assert.Single(history.Read());

        Assert.Equal(written.TakenAt, read.TakenAt);
        Assert.Equal(written.FreeBytes, read.FreeBytes);
        Assert.Equal(written.TotalBytes, read.TotalBytes);
        Assert.Equal(written.DriveLetter, read.DriveLetter);
    }

    [Fact]
    public void RecordingTwiceInAMinuteOnlyKeepsTheFirst()
    {
        // The window can be open for hours. Without the spacing floor the file fills with
        // samples from one afternoon and the fit describes that afternoon, not the disk.
        var history = new SpaceHistory(_path);

        IReadOnlyList<SpaceReading> first = history.Record();
        IReadOnlyList<SpaceReading> second = history.Record();

        Assert.NotEmpty(first);
        Assert.Empty(second);
    }

    [Fact]
    public void RecordingReadsTheRealMachine()
    {
        var history = new SpaceHistory(_path);

        foreach (SpaceReading reading in history.Record())
        {
            Assert.True(reading.FreeBytes > 0, $"{reading.DriveLetter}: no free space reported");
            Assert.True(reading.TotalBytes > 0, $"{reading.DriveLetter}: no size reported");
        }
    }

    [Fact]
    public void TheOldestReadingsAreDroppedFirstWhenTheFileIsFull()
    {
        var history = new SpaceHistory(_path);

        var many = new List<SpaceReading>();
        for (int i = 0; i < SpaceHistory.MaximumPerVolume + 50; i++)
            many.Add(new SpaceReading(DateTimeOffset.Now.AddMinutes(i), 'C', 1_000 + i, 10_000));

        history.Append(many);

        IReadOnlyList<SpaceReading> kept = history.Read();

        const long Newest = 1_000 + SpaceHistory.MaximumPerVolume + 50 - 1;

        Assert.Equal(SpaceHistory.MaximumPerVolume, kept.Count);
        Assert.Equal(Newest, kept[^1].FreeBytes);                              // the newest survived
        Assert.Equal(Newest - SpaceHistory.MaximumPerVolume + 1, kept[0].FreeBytes);   // the oldest 50 went
    }

    [Fact]
    public void PruningOneVolumeDoesNotEvictAnother()
    {
        var history = new SpaceHistory(_path);

        var many = new List<SpaceReading>();
        for (int i = 0; i < SpaceHistory.MaximumPerVolume + 50; i++)
            many.Add(new SpaceReading(DateTimeOffset.Now.AddMinutes(i), 'C', 1_000 + i, 10_000));

        many.Add(new SpaceReading(DateTimeOffset.Now, 'D', 42, 10_000));

        history.Append(many);

        Assert.Single(history.Read('D'));
    }

    [Fact]
    public void AMissingFileIsAnEmptyHistoryNotAFailure()
    {
        Assert.Empty(new SpaceHistory(_path).Read());
    }
}
