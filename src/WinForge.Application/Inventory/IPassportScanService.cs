using WinForge.Domain.Inventory;

namespace WinForge.Application.Inventory;

public interface IPassportScanService
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<PassportSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<ScanRecord> RunScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);

    Task<ScanRecord> RunScanAsync(
        InventoryScanGroup group,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
