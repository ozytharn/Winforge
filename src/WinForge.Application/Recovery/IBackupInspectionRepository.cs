using WinForge.Domain.Recovery;

namespace WinForge.Application.Recovery;

public interface IBackupInspectionRepository
{
    Task SaveInspectionAsync(BackupInspectionResult result, CancellationToken cancellationToken);

    Task<BackupInspectionResult?> GetInspectionAsync(Guid inspectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupInspectionSummary>> GetInspectionHistoryAsync(
        string candidateIdentity,
        int limit,
        CancellationToken cancellationToken);
}
