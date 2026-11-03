using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WinForge.Domain.Recovery;

namespace WinForge.Application.Recovery;

public sealed class BackupInspectionService(
    IPassportScanService scanService,
    IBackupInspector inspector,
    IBackupInspectionRepository repository)
{
    private const int MaximumHistoryEntries = 100;

    public async Task<IReadOnlyList<BackupCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        var snapshot = await scanService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Components
            .Where(component =>
                component.Type == InventoryComponentType.BackupCatalogItem &&
                component.IdentityKey.StartsWith("backup-folder:", StringComparison.OrdinalIgnoreCase))
            .Select(ToCandidate)
            .OrderBy(candidate => candidate.Provider)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<BackupInspectionResult> InspectAsync(
        BackupCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = DateTimeOffset.UtcNow;
        var inspectionId = Guid.NewGuid();
        BackupInspectionResult result;
        try
        {
            result = await inspector.InspectAsync(
                    candidate,
                    inspectionId,
                    startedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new BackupInspectionResult(
                inspectionId,
                candidate.Identity,
                candidate.Provider,
                candidate.Location,
                startedAt,
                DateTimeOffset.UtcNow,
                BackupInspectionState.Cancelled,
                "Inspection was cancelled; results may be incomplete.",
                false,
                null,
                null,
                null,
                [new BackupInspectionCheck(
                    "Inspection cancellation",
                    BackupCheckState.Skipped,
                    "Cancellation was requested before all bounded metadata checks completed.",
                    "WinForge",
                    DateTimeOffset.UtcNow)],
                ["No backup data was modified. A cancelled inspection is not an integrity result."]);
            await repository.SaveInspectionAsync(result, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await repository.SaveInspectionAsync(result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<BackupInspectionSummary>> GetHistoryAsync(
        string candidateIdentity,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateIdentity);
        if (limit is < 1 or > MaximumHistoryEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"History limit must be from 1 through {MaximumHistoryEntries}.");
        }

        return await repository.GetInspectionHistoryAsync(candidateIdentity, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BackupInspectionResult?> GetInspectionAsync(
        Guid inspectionId,
        CancellationToken cancellationToken) =>
        repository.GetInspectionAsync(inspectionId, cancellationToken);

    public async Task<BackupInspectionComparison?> CompareLatestAsync(
        string candidateIdentity,
        CancellationToken cancellationToken)
    {
        var history = await GetHistoryAsync(candidateIdentity, 2, cancellationToken).ConfigureAwait(false);
        if (history.Count < 2)
        {
            return null;
        }

        var current = history[0];
        var previous = history[1];
        return new BackupInspectionComparison(
            current,
            previous,
            current.State != previous.State,
            !string.Equals(current.Summary, previous.Summary, StringComparison.Ordinal));
    }

    private static BackupCandidate ToCandidate(InventoryComponent component)
    {
        var provider = component.IdentityKey.EndsWith(":WINDOWSIMAGEBACKUP", StringComparison.OrdinalIgnoreCase)
            ? BackupProvider.WindowsImageBackup
            : component.IdentityKey.EndsWith(":FILEHISTORY", StringComparison.OrdinalIgnoreCase)
                ? BackupProvider.FileHistory
                : BackupProvider.Unsupported;
        var path = component.Details.GetValueOrDefault("additionalInformation");
        var volume = component.Details.GetValueOrDefault("driveName");
        if (string.IsNullOrWhiteSpace(volume) && !string.IsNullOrWhiteSpace(path))
        {
            volume = Path.GetPathRoot(path);
        }

        return new BackupCandidate(
            component.IdentityKey,
            component.DisplayName,
            provider,
            string.IsNullOrWhiteSpace(path) ? null : path,
            string.IsNullOrWhiteSpace(volume) ? null : volume,
            component.ObservedAt,
            component.Details.GetValueOrDefault("availability") ?? "Unknown",
            component.Details.GetValueOrDefault("dataSource") ?? component.Source,
            component.Details.GetValueOrDefault("limitations"));
    }
}
