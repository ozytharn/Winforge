using WinForge.Domain.Monitoring;

namespace WinForge.Application.Monitoring;

public interface IGuardianTelemetryRepository
{
    Task SaveSampleAsync(
        GuardianTelemetrySnapshot snapshot,
        GuardianRetentionPolicy retention,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GuardianTelemetrySnapshot>> GetSamplesAsync(
        GuardianTelemetryRange range,
        CancellationToken cancellationToken);

    Task<GuardianTelemetrySnapshot?> GetLatestSampleAsync(CancellationToken cancellationToken);
}
