using WinForge.Domain.Inventory;

namespace WinForge.Application.Inventory;

public interface IInventoryCollector
{
    string CollectorId { get; }

    InventoryScanGroup ScanGroup => InventoryScanGroup.Hardware;

    Task<CollectorResult> CollectAsync(CancellationToken cancellationToken);
}
