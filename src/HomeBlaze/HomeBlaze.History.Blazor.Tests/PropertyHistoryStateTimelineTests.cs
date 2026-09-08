using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

/// <summary>
/// Tests the state timeline: the discrete-property view that renders history as constant-value runs
/// instead of a line through interpolated values.
/// </summary>
public class PropertyHistoryStateTimelineTests
{
    private static readonly DateTimeOffset From = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(1);

    private static ImmutableArray<HistoryCoverage> FullCoverage => [new HistoryCoverage(From, To)];

    private static HistoryPoint Bool(DateTimeOffset timestamp, bool value) =>
        new(timestamp, value ? 1d : 0d, null);

    private static IReadOnlyList<PropertyHistoryChartModel.StateSegment> Build(
        IReadOnlyList<HistoryPoint> points,
        HistoryPoint? valueAtStart = null,
        IReadOnlyList<HistoryCoverage>? coverage = null) =>
        PropertyHistoryChartModel.BuildStateSegments(
            points,
            valueAtStart,
            From,
            To,
            coverage ?? FullCoverage,
            point => PropertyHistoryChartModel.FormatDiscreteValue(point, typeof(bool?)));

    [Fact]
    public void WhenValueTogglesOnce_ThenTwoRunsSplitAtTheTransition()
    {
        // Arrange
        var toggledAt = From.AddMinutes(20);

        // Act
        var segments = Build([Bool(toggledAt, true)], valueAtStart: Bool(From.AddHours(-1), false));

        // Assert
        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal("No", first.Label);
                Assert.Equal(From, first.Start);
                Assert.Equal(toggledAt, first.End);
            },
            second =>
            {
                Assert.Equal("Yes", second.Label);
                Assert.Equal(toggledAt, second.Start);
                Assert.Equal(To, second.End);
            });
    }

    [Fact]
    public void WhenNoSampleIsInsideTheWindow_ThenTheHeldValueSpansIt()
    {
        // Arrange & Act
        // The common case for a rarely changing property: the last change predates the window, so a raw
        // query returns nothing and only the entering value can fill it.
        var segments = Build([], valueAtStart: Bool(From.AddDays(-3), true));

        // Assert
        var single = Assert.Single(segments);
        Assert.Equal("Yes", single.Label);
        Assert.False(single.IsUnknown);
        Assert.Equal(From, single.Start);
        Assert.Equal(To, single.End);
    }

    [Fact]
    public void WhenCoveredButNoValueWasEverObserved_ThenTheRunIsCoveredWithNoLabel()
    {
        // Arrange & Act
        var segments = Build([], valueAtStart: null);

        // Assert: the store vouches for the window, so this is not a coverage gap. It is a covered
        // span whose value is absent, and conflating the two makes a healthy null read as lost history.
        var single = Assert.Single(segments);
        Assert.False(single.IsUnknown);
        Assert.Null(single.Label);
    }

    [Fact]
    public void WhenValueIsExplicitlyNull_ThenItIsNotReportedAsACoverageGap()
    {
        // Arrange: a nullable property recorded as null, inside fully covered time.
        var clearedAt = From.AddMinutes(20);

        // Act
        var segments = Build(
            [new HistoryPoint(clearedAt, null, null)],
            valueAtStart: Bool(From.AddMinutes(-5), true));

        // Assert
        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal("Yes", first.Label);
                Assert.False(first.IsUnknown);
            },
            second =>
            {
                Assert.Null(second.Label);
                Assert.False(second.IsUnknown);
                Assert.Equal(clearedAt, second.Start);
            });
    }

    [Fact]
    public void WhenAGapAdjoinsAnAbsentValue_ThenTheyRemainSeparateRuns()
    {
        // Arrange: coverage stops at 20m; the value was already absent before that.
        ImmutableArray<HistoryCoverage> coverage = [new HistoryCoverage(From, From.AddMinutes(20))];

        // Act
        var segments = Build([], valueAtStart: null, coverage: coverage);

        // Assert: merging these would hide where recording actually stopped.
        Assert.Collection(
            segments,
            covered =>
            {
                Assert.False(covered.IsUnknown);
                Assert.Equal(From.AddMinutes(20), covered.End);
            },
            gap => Assert.True(gap.IsUnknown));
    }

    [Fact]
    public void WhenCoverageHasAGap_ThenTheValueIsNotCarriedAcrossIt()
    {
        // Arrange
        var gapStart = From.AddMinutes(20);
        var gapEnd = From.AddMinutes(40);
        ImmutableArray<HistoryCoverage> coverage =
            [new HistoryCoverage(From, gapStart), new HistoryCoverage(gapEnd, To)];

        // Act
        var segments = Build([], valueAtStart: Bool(From.AddMinutes(-5), true), coverage: coverage);

        // Assert
        Assert.Collection(
            segments,
            first => Assert.Equal((From, gapStart, "Yes", false), (first.Start, first.End, first.Label, first.IsUnknown)),
            gap => Assert.Equal((gapStart, gapEnd, (string?)null, true), (gap.Start, gap.End, gap.Label, gap.IsUnknown)),
            last => Assert.Equal((gapEnd, To, "Yes", false), (last.Start, last.End, last.Label, last.IsUnknown)));
    }

    [Fact]
    public void WhenConsecutiveSamplesRepeatTheValue_ThenTheRunsAreMerged()
    {
        // Arrange & Act
        var segments = Build(
            [Bool(From.AddMinutes(10), true), Bool(From.AddMinutes(20), true), Bool(From.AddMinutes(30), true)],
            valueAtStart: Bool(From.AddMinutes(-1), true));

        // Assert
        var single = Assert.Single(segments);
        Assert.Equal("Yes", single.Label);
        Assert.Equal(TimeSpan.FromHours(1), single.Duration);
    }

    [Fact]
    public void WhenPropertyIsBooleanOrEnumOrString_ThenItIsDiscreteWithoutTheAttribute()
    {
        // Act & Assert
        Assert.True(PropertyHistoryChartModel.IsDiscrete(typeof(bool?), isDiscreteState: false));
        Assert.True(PropertyHistoryChartModel.IsDiscrete(typeof(string), isDiscreteState: false));
        Assert.True(PropertyHistoryChartModel.IsDiscrete(typeof(DayOfWeek), isDiscreteState: false));
        Assert.False(PropertyHistoryChartModel.IsDiscrete(typeof(double), isDiscreteState: false));
        Assert.True(PropertyHistoryChartModel.IsDiscrete(typeof(double), isDiscreteState: true));
    }

    [Fact]
    public void WhenPropertyIsDiscrete_ThenAveragingAggregationsAreNotOffered()
    {
        // Arrange
        var union = new HashSet<string>(InMemoryAggregations, StringComparer.Ordinal);

        // Act
        var result = PropertyHistoryChartModel.GateAggregations(
            ValueColumn.Long, isCumulative: false, union, isDiscrete: true);

        // Assert
        Assert.Equal(
            [HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count],
            result);
    }

    private static readonly string[] InMemoryAggregations =
    [
        HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.SampleAverage,
        HistoryAggregations.TimeWeightedAverage, HistoryAggregations.Minimum, HistoryAggregations.Maximum,
        HistoryAggregations.Sum, HistoryAggregations.Count, HistoryAggregations.StandardDeviation
    ];
}
