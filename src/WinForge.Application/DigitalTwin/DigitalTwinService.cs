using WinForge.Domain.DigitalTwin;
using WinForge.Domain.Inventory;

namespace WinForge.Application.DigitalTwin;

public sealed class DigitalTwinService(
    IDigitalTwinRepository repository,
    DigitalTwinBuilder builder,
    DigitalTwinGraph graph,
    DigitalTwinChangeDetector changeDetector)
{
    public async Task<DigitalTwinSnapshot> CreateSnapshotAsync(
        PassportSnapshot inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Components.Count == 0)
        {
            throw new InvalidOperationException("A Digital Twin snapshot requires collected inventory observations.");
        }

        var snapshot = builder.Build(inventory);
        cancellationToken.ThrowIfCancellationRequested();
        await repository.SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public Task<DigitalTwinSnapshot?> GetSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken) =>
        repository.GetSnapshotAsync(snapshotId, cancellationToken);

    public Task<IReadOnlyList<DigitalTwinSnapshotSummary>> GetHistoryAsync(
        int limit,
        CancellationToken cancellationToken) =>
        repository.GetHistoryAsync(limit, cancellationToken);

    public Task<DigitalTwinEntityPage> GetEntityPageAsync(
        Guid snapshotId,
        int offset,
        int pageSize,
        DigitalTwinEntityType? category,
        string? searchText,
        CancellationToken cancellationToken) =>
        repository.GetEntityPageAsync(snapshotId, offset, pageSize, category, searchText, cancellationToken);

    public async Task<IReadOnlyList<DigitalTwinChange>> CompareAsync(
        Guid previousSnapshotId,
        Guid currentSnapshotId,
        CancellationToken cancellationToken) =>
        (await CompareDetailedAsync(previousSnapshotId, currentSnapshotId, cancellationToken)
            .ConfigureAwait(false)).EntityChanges;

    public async Task<DigitalTwinComparison> CompareDetailedAsync(
        Guid previousSnapshotId,
        Guid currentSnapshotId,
        CancellationToken cancellationToken)
    {
        var previous = await repository.GetSnapshotAsync(previousSnapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Digital Twin snapshot '{previousSnapshotId}' was not found.");
        var current = await repository.GetSnapshotAsync(currentSnapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Digital Twin snapshot '{currentSnapshotId}' was not found.");
        var history = await repository.GetHistoryAsync(100, cancellationToken).ConfigureAwait(false);
        var currentIndex = history.ToList().FindIndex(summary => summary.SnapshotId == currentSnapshotId);
        DigitalTwinSnapshot? confirmation = null;
        if (currentIndex > 0)
        {
            confirmation = await repository.GetSnapshotAsync(
                    history[currentIndex - 1].SnapshotId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return changeDetector.CompareDetailed(previous, current, confirmation);
    }

    public DigitalTwinTraversalResult FindDependencies(
        DigitalTwinSnapshot snapshot,
        string entityId,
        int maximumDepth = 4,
        int maximumNodes = 250) =>
        graph.FindDependencies(snapshot, entityId, maximumDepth, maximumNodes);

    public DigitalTwinCycleResult FindCycles(
        DigitalTwinSnapshot snapshot,
        int maximumNodes = DigitalTwinGraph.MaximumTraversalNodes) =>
        graph.FindCycles(snapshot, maximumNodes);
}
