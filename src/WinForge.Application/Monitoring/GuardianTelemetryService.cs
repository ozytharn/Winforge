using System.Data.Common;
using WinForge.Domain.Monitoring;

namespace WinForge.Application.Monitoring;

public sealed record GuardianCollectionResult(
    GuardianTelemetrySnapshot Snapshot,
    bool Persisted,
    string? PersistenceFailure);

public sealed class GuardianTelemetryService(
    IGuardianTelemetryCollector collector,
    IGuardianTelemetryRepository repository,
    GuardianHistoryAnalyzer analyzer)
{
    public async Task<GuardianCollectionResult> CollectAndPersistAsync(
        GuardianRetentionPolicy retention,
        CancellationToken cancellationToken)
    {
        retention.Validate();
        var snapshot = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await repository.SaveSampleAsync(snapshot, retention, cancellationToken).ConfigureAwait(false);
            return new GuardianCollectionResult(snapshot, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DbException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new GuardianCollectionResult(snapshot, false, exception.Message);
        }
    }

    public Task<GuardianTelemetrySnapshot?> GetLatestAsync(CancellationToken cancellationToken) =>
        repository.GetLatestSampleAsync(cancellationToken);

    public async Task<IReadOnlyList<GuardianTelemetrySnapshot>> GetHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int maximumSamples,
        CancellationToken cancellationToken) =>
        await repository.GetSamplesAsync(
                new GuardianTelemetryRange(from, to, maximumSamples),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<GuardianMetricSummary>> AnalyzeAsync(
        DateTimeOffset to,
        TimeSpan range,
        CancellationToken cancellationToken)
    {
        if (range <= TimeSpan.Zero || range > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Guardian analysis range must be positive and no longer than 30 days.");
        }

        var midpoint = to - range;
        var samples = await repository.GetSamplesAsync(
                new GuardianTelemetryRange(midpoint, to, 10_000),
                cancellationToken)
            .ConfigureAwait(false);
        var previous = await repository.GetSamplesAsync(
                new GuardianTelemetryRange(midpoint - range, midpoint.AddTicks(-1), 10_000),
                cancellationToken)
            .ConfigureAwait(false);
        return analyzer.Analyze(samples, previous);
    }
}
