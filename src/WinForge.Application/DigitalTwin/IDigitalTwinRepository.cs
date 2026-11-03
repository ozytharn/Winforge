using WinForge.Domain.DigitalTwin;

namespace WinForge.Application.DigitalTwin;

public interface IDigitalTwinRepository
{
    Task SaveSnapshotAsync(DigitalTwinSnapshot snapshot, CancellationToken cancellationToken);

    Task<DigitalTwinSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DigitalTwinSnapshotSummary>> GetHistoryAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<DigitalTwinEntityPage> GetEntityPageAsync(
        Guid snapshotId,
        int offset,
        int pageSize,
        DigitalTwinEntityType? category,
        string? searchText,
        CancellationToken cancellationToken);
}
