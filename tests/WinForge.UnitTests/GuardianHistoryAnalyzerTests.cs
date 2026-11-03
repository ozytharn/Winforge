using WinForge.Application.Monitoring;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.UnitTests;

public sealed class GuardianHistoryAnalyzerTests
{
    [Fact]
    public void Analyze_summarizes_available_values_and_compares_previous_period()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var recent = new[]
        {
            Sample(start, 20),
            Sample(start.AddMinutes(1), 40),
            Sample(start.AddMinutes(2), 60)
        };
        var previous = new[] { Sample(start.AddMinutes(-1), 10), Sample(start, 30) };

        var summary = Assert.Single(new GuardianHistoryAnalyzer().Analyze(recent, previous),
            item => item.Metric == "cpu.percent");

        Assert.Equal("cpu.percent", summary.Metric);
        Assert.Equal(3, summary.SampleCount);
        Assert.Equal(0, summary.UnavailableCount);
        Assert.Equal(40, summary.Average);
        Assert.Equal(20, summary.Minimum);
        Assert.Equal(60, summary.Maximum);
        Assert.Equal(60, summary.Latest);
        Assert.Equal(20, summary.PreviousPeriodAverage);
        Assert.Equal(start.AddMinutes(2), summary.LatestAt);
    }

    [Fact]
    public void Analyze_counts_unavailable_samples_and_detects_large_sampling_gaps()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var samples = new[]
        {
            Sample(start, 20),
            Sample(start.AddMinutes(1), null),
            Sample(start.AddMinutes(10), 80)
        };

        var summary = Assert.Single(new GuardianHistoryAnalyzer().Analyze(samples, []),
            item => item.Metric == "cpu.percent");

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(1, summary.SamplingGapCount);
        Assert.Equal(80, summary.Latest);
    }

    [Fact]
    public void AnalyzeThresholdCrossings_counts_warning_and_critical_observations_separately()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var warning = Sample(start, 90) with
        {
            AvailableMemoryBytes = new TelemetryReading<ulong>(10, InventoryFieldAvailability.Available, "test"),
            TotalMemoryBytes = new TelemetryReading<ulong>(100, InventoryFieldAvailability.Available, "test")
        };
        var critical = Sample(start.AddMinutes(1), 96) with
        {
            AvailableMemoryBytes = new TelemetryReading<ulong>(4, InventoryFieldAvailability.Available, "test"),
            TotalMemoryBytes = new TelemetryReading<ulong>(100, InventoryFieldAvailability.Available, "test")
        };

        var summaries = new GuardianHistoryAnalyzer().AnalyzeThresholdCrossings(
            [warning, critical],
            new GuardianThresholds(80, 80, 10, 95, 95, 5));

        Assert.Contains(summaries, item =>
            item.Category == "CPU" && item.WarningSampleCount == 1 && item.CriticalSampleCount == 1);
        Assert.Contains(summaries, item =>
            item.Category == "Memory" && item.WarningSampleCount == 1 && item.CriticalSampleCount == 1);
    }

    private static GuardianTelemetrySnapshot Sample(DateTimeOffset at, double? cpu)
    {
        var reading = cpu is { } value
            ? new TelemetryReading<double>(value, InventoryFieldAvailability.Available, "test")
            : new TelemetryReading<double>(null, InventoryFieldAvailability.Inaccessible, "test", "Access denied.");
        return new GuardianTelemetrySnapshot(
            at,
            reading,
            new TelemetryReading<ulong>(1024, InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<ulong>(2048, InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<TimeSpan>(TimeSpan.FromHours(1), InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<int>(0, InventoryFieldAvailability.Available, "test"),
            []);
    }
}
