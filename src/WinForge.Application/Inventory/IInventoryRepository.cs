using WinForge.Domain.Inventory;

namespace WinForge.Application.Inventory;

public interface IInventoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task StartScanAsync(
        Guid scanId,
        DateTimeOffset startedAt,
        string scope,
        int totalCollectors,
        CancellationToken cancellationToken);

    Task CompleteScanAsync(ScanRecord scan, CancellationToken cancellationToken);

    Task<PassportSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<string> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken);
}
