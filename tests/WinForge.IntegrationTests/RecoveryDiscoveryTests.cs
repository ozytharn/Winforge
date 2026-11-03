using Microsoft.Extensions.Logging.Abstractions;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WinForge.Infrastructure.Collectors;
using WinForge.Infrastructure.Persistence;

namespace WinForge.IntegrationTests;

public sealed class RecoveryDiscoveryTests
{
    [Fact]
    public async Task Recovery_discovery_honors_cancellation()
    {
        var collector = new RecoveryDiscoveryCollector(
            NullLogger<RecoveryDiscoveryCollector>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(cancellation.Token));
    }

    [Fact]
    public async Task Windows_recovery_discovery_reports_source_and_availability_metadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var collector = new RecoveryDiscoveryCollector(
            NullLogger<RecoveryDiscoveryCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None);
        var recoveryTypes = new HashSet<InventoryComponentType>
        {
            InventoryComponentType.RecoveryEnvironment,
            InventoryComponentType.RestorePoint,
            InventoryComponentType.BackupCatalogItem,
            InventoryComponentType.RecoveryPartition,
            InventoryComponentType.RecoveryVolume,
            InventoryComponentType.BitLockerVolume,
            InventoryComponentType.RecoveryService,
            InventoryComponentType.RecoveryEvent
        };

        Assert.True(result.Components.Count > 0 || result.Issues.Count > 0);
        Assert.All(result.Components, component =>
        {
            Assert.Contains(component.Type, recoveryTypes);
            Assert.Contains("status", component.Details);
            Assert.Contains("availability", component.Details);
            Assert.Contains("dataSource", component.Details);
            Assert.Contains("collectionTimestampUtc", component.Details);
            Assert.Contains("confidence", component.Details);
            Assert.Contains("limitations", component.Details);
            Assert.NotEmpty(component.FieldAvailability);
            Assert.Contains(
                Enum.Parse<InventoryFieldAvailability>(component.Details["availability"]!),
                Enum.GetValues<InventoryFieldAvailability>());
        });
        foreach (var component in result.Components.Where(component =>
                     component.Type == InventoryComponentType.BitLockerVolume &&
                     component.Details.ContainsKey("protectionStatus")))
        {
            Assert.Equal(
                Enum.Parse<InventoryFieldAvailability>(component.Details["availability"]!),
                component.FieldAvailability["protectionStatus"]);
        }

        foreach (var component in result.Components.Where(component =>
                     component.Type == InventoryComponentType.RecoveryEvent &&
                     component.Details.ContainsKey("eventMessage")))
        {
            Assert.Equal(
                InventoryFieldAvailability.Unsupported,
                component.FieldAvailability["eventMessage"]);
        }
    }

    [Fact]
    public async Task Recovery_scans_persist_independently_and_retain_observations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForgeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var recoveryCollector = new StaticCollector(
                "recovery-test",
                InventoryScanGroup.Recovery,
                CreateComponent(InventoryComponentType.RecoveryEnvironment, "winre:test"));
            var hardwareCollector = new StaticCollector(
                "hardware-test",
                InventoryScanGroup.Hardware,
                CreateComponent(InventoryComponentType.Device, "device:test"));
            var service = new PassportScanService(
                [recoveryCollector, hardwareCollector],
                new SqliteInventoryRepository(
                    Path.Combine(directory, "inventory.db"),
                    NullLogger<SqliteInventoryRepository>.Instance),
                NullLogger<PassportScanService>.Instance);
            await service.InitializeAsync(CancellationToken.None);

            var recoveryScan = await service.RunScanAsync(
                InventoryScanGroup.Recovery,
                progress: null,
                CancellationToken.None);
            var hardwareScan = await service.RunScanAsync(
                InventoryScanGroup.Hardware,
                progress: null,
                CancellationToken.None);
            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal("Recovery discovery", recoveryScan.Scope);
            Assert.Equal("Hardware and driver inventory", hardwareScan.Scope);
            Assert.Contains(snapshot.Components, component => component.IdentityKey == "winre:test");
            Assert.Contains(snapshot.Components, component => component.IdentityKey == "device:test");
            Assert.Equal(InventoryScanStatus.Completed, snapshot.LatestScan?.Status);
            Assert.Equal(hardwareScan.ScanId, snapshot.LatestScan?.ScanId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Failed_recovery_scan_retains_the_last_known_recovery_observation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForgeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var recoveryCollector = new SequenceCollector(
                "recovery-test",
                InventoryScanGroup.Recovery,
                new CollectorResult(
                    "recovery-test",
                    [CreateComponent(InventoryComponentType.RecoveryEnvironment, "winre:known")],
                    []),
                new CollectorResult(
                    "recovery-test",
                    [],
                    [new CollectorIssue("recovery-test", "source_unavailable", "Recovery source was unavailable.")]));
            var service = new PassportScanService(
                [recoveryCollector],
                new SqliteInventoryRepository(
                    Path.Combine(directory, "inventory.db"),
                    NullLogger<SqliteInventoryRepository>.Instance),
                NullLogger<PassportScanService>.Instance);
            await service.InitializeAsync(CancellationToken.None);

            await service.RunScanAsync(
                InventoryScanGroup.Recovery,
                progress: null,
                CancellationToken.None);
            var failedScan = await service.RunScanAsync(
                InventoryScanGroup.Recovery,
                progress: null,
                CancellationToken.None);
            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(InventoryScanStatus.Failed, failedScan.Status);
            Assert.Contains(snapshot.Components, component => component.IdentityKey == "winre:known");
            Assert.Equal(InventoryScanStatus.Failed, snapshot.LatestScan?.Status);
            Assert.Equal("Recovery discovery", snapshot.LatestScan?.Scope);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static InventoryComponent CreateComponent(
        InventoryComponentType type,
        string identityKey) =>
        new(
            type,
            identityKey,
            identityKey,
            "Recovery test source",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["status"] = "Source-reported" })
        {
            Category = "Recovery configuration",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["status"] = InventoryFieldAvailability.Available
            }
        };

    private sealed class StaticCollector(
        string collectorId,
        InventoryScanGroup scanGroup,
        InventoryComponent component) : IInventoryCollector
    {
        public string CollectorId => collectorId;

        public InventoryScanGroup ScanGroup => scanGroup;

        public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CollectorResult(CollectorId, [component], []));
        }
    }

    private sealed class SequenceCollector(
        string collectorId,
        InventoryScanGroup scanGroup,
        params CollectorResult[] results) : IInventoryCollector
    {
        private readonly Queue<CollectorResult> _results = new(results);

        public string CollectorId => collectorId;

        public InventoryScanGroup ScanGroup => scanGroup;

        public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
        }
    }
}
