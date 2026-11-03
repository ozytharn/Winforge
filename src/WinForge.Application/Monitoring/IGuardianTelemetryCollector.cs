using WinForge.Domain.Monitoring;

namespace WinForge.Application.Monitoring;

public interface IGuardianTelemetryCollector
{
    Task<GuardianTelemetrySnapshot> CollectAsync(CancellationToken cancellationToken);
}
