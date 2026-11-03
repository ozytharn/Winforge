using WinForge.Domain.Recovery;

namespace WinForge.Application.Recovery;

public interface IBackupInspector
{
    Task<BackupInspectionResult> InspectAsync(
        BackupCandidate candidate,
        Guid inspectionId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);
}
