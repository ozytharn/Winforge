using WinForge.Domain.Inventory;
using WinForge.Infrastructure.Monitoring;

namespace WinForge.IntegrationTests;

public sealed class GuardianTelemetryCollectorTests
{
    [Fact]
    public async Task Windows_collector_reports_memory_uptime_and_cpu_after_baseline()
    {
        var collector = new WindowsGuardianTelemetryCollector();

        var baseline = await collector.CollectAsync(CancellationToken.None);
        Assert.Equal(InventoryFieldAvailability.Available, baseline.TotalMemoryBytes.Availability);
        Assert.True(baseline.TotalMemoryBytes.Value > 0);
        Assert.Equal(InventoryFieldAvailability.Available, baseline.SystemUptime.Availability);

        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var sample = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(InventoryFieldAvailability.Available, sample.CpuUsagePercent.Availability);
        Assert.InRange(sample.CpuUsagePercent.Value!.Value, 0, 100);
        Assert.True(sample.CpuUsagePercent.Interval >= TimeSpan.FromMilliseconds(100));
        Assert.Equal(InventoryFieldAvailability.Available, sample.StorageEnumeration.Availability);
    }

    [Fact]
    public async Task Windows_collector_honors_cancellation_before_accessing_telemetry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var collector = new WindowsGuardianTelemetryCollector();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(cancellation.Token));
    }

    [Fact]
    public async Task Windows_collector_reports_bounded_observations_and_only_interval_based_rates()
    {
        var collector = new WindowsGuardianTelemetryCollector();
        _ = await collector.CollectAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        var sample = await collector.CollectAsync(CancellationToken.None);

        Assert.InRange(sample.Processes.Count, 0, 20);
        Assert.InRange(sample.ProcessEnumeration.Value ?? 0, 0, 128);
        Assert.All(sample.Processes, process =>
        {
            Assert.True(process.ProcessId >= 0);
            if (process.StartedAt is { } startedAt)
            {
                Assert.Equal($"process:{process.ProcessId}:{startedAt.UtcTicks}", process.Identity);
            }
        });
        Assert.All(sample.NetworkInterfaces, network =>
        {
            Assert.StartsWith("network:", network.Identity, StringComparison.OrdinalIgnoreCase);
            foreach (var reading in new[] { network.SentBytesPerSecond, network.ReceivedBytesPerSecond })
            {
                if (reading.Availability == InventoryFieldAvailability.Available)
                {
                    Assert.True(reading.Value >= 0);
                    Assert.True(reading.Interval > TimeSpan.Zero);
                    Assert.Contains("delta", reading.Source, StringComparison.OrdinalIgnoreCase);
                }
            }
        });

        Assert.All(sample.GpuAdapters, adapter =>
        {
            Assert.NotEmpty(adapter.Source);
            Assert.Equal(InventoryFieldAvailability.Unsupported, adapter.UtilizationPercent.Availability);
            Assert.Equal(InventoryFieldAvailability.Unsupported, adapter.DedicatedMemoryBytes.Availability);
            Assert.Equal(InventoryFieldAvailability.Unsupported, adapter.TemperatureCelsius.Availability);
        });
    }
}
