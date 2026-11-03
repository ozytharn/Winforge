using WinForge.Application.Monitoring;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.UnitTests;

public sealed class GuardianTelemetryServiceTests
{
    [Fact]
    public async Task CollectAndPersist_surfaces_write_failure_without_losing_live_sample()
    {
        var sample = CreateSample();
        var service = new GuardianTelemetryService(
            new FixedCollector(sample),
            new FailingRepository(),
            new GuardianHistoryAnalyzer());

        var result = await service.CollectAndPersistAsync(
            GuardianRetentionPolicy.Default,
            CancellationToken.None);

        Assert.Same(sample, result.Snapshot);
        Assert.False(result.Persisted);
        Assert.Contains("database is unavailable", result.PersistenceFailure, StringComparison.OrdinalIgnoreCase);
    }

    private static GuardianTelemetrySnapshot CreateSample() =>
        new(
            DateTimeOffset.UtcNow,
            new TelemetryReading<double>(null, InventoryFieldAvailability.Unavailable, "test"),
            new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unavailable, "test"),
            new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unavailable, "test"),
            new TelemetryReading<TimeSpan>(null, InventoryFieldAvailability.Unavailable, "test"),
            new TelemetryReading<int>(null, InventoryFieldAvailability.Unavailable, "test"),
            []);

    private sealed class FixedCollector(GuardianTelemetrySnapshot sample) : IGuardianTelemetryCollector
    {
        public Task<GuardianTelemetrySnapshot> CollectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(sample);
    }

    private sealed class FailingRepository : IGuardianTelemetryRepository
    {
        public Task SaveSampleAsync(
            GuardianTelemetrySnapshot snapshot,
            GuardianRetentionPolicy retention,
            CancellationToken cancellationToken) =>
            throw new IOException("The database is unavailable.");

        public Task<IReadOnlyList<GuardianTelemetrySnapshot>> GetSamplesAsync(
            GuardianTelemetryRange range,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GuardianTelemetrySnapshot>>([]);

        public Task<GuardianTelemetrySnapshot?> GetLatestSampleAsync(CancellationToken cancellationToken) =>
            Task.FromResult<GuardianTelemetrySnapshot?>(null);
    }
}
