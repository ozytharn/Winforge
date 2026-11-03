using Microsoft.Extensions.Logging.Abstractions;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.UnitTests;

public sealed class PassportScanServiceTests
{
    [Fact]
    public async Task Scan_with_partial_collector_result_reports_partial_and_persists_components()
    {
        var repository = new InMemoryInventoryRepository();
        var component = CreateComponent("os:current", "Windows 11");
        var collector = new FixedCollector(new CollectorResult(
            "operating-system",
            [component],
            [new CollectorIssue("operating-system", "limited", "One optional field was unavailable.")]));
        var service = CreateService(repository, collector);

        var scan = await service.RunScanAsync(progress: null, CancellationToken.None);
        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(InventoryScanStatus.Partial, scan.Status);
        Assert.Single(snapshot.Components);
        Assert.Equal(scan.ScanId, snapshot.LatestScan?.ScanId);
        Assert.Single(scan.Issues);
    }

    [Fact]
    public async Task Failed_scan_does_not_remove_components_from_previous_observations()
    {
        var repository = new InMemoryInventoryRepository();
        var previousComponent = CreateComponent("device:pnp-1", "Previously observed device");
        var successfulService = CreateService(
            repository,
            new FixedCollector(new CollectorResult("devices", [previousComponent], [])));
        await successfulService.RunScanAsync(progress: null, CancellationToken.None);

        var failedService = CreateService(
            repository,
            new FixedCollector(new CollectorResult(
                "devices",
                [],
                [new CollectorIssue("devices", "access_denied", "Device inventory was inaccessible.") ])));
        var failedScan = await failedService.RunScanAsync(progress: null, CancellationToken.None);
        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(InventoryScanStatus.Failed, failedScan.Status);
        Assert.Single(snapshot.Components);
        Assert.Equal(previousComponent.IdentityKey, snapshot.Components[0].IdentityKey);
        Assert.Equal(InventoryScanStatus.Failed, snapshot.LatestScan?.Status);
    }

    [Fact]
    public async Task Collector_exception_is_logged_and_reported_as_a_failed_scan()
    {
        var repository = new InMemoryInventoryRepository();
        var service = CreateService(repository, new ThrowingCollector());

        var scan = await service.RunScanAsync(progress: null, CancellationToken.None);

        Assert.Equal(InventoryScanStatus.Failed, scan.Status);
        Assert.Contains(scan.Issues, issue => issue.Code == "collector_failed");
    }

    [Fact]
    public async Task Collector_exception_does_not_prevent_later_collectors_from_running()
    {
        var repository = new InMemoryInventoryRepository();
        var component = CreateComponent("os:current", "Windows 11");
        var service = CreateService(
            repository,
            new ThrowingCollector(),
            new FixedCollector(new CollectorResult("operating-system", [component], [])));

        var scan = await service.RunScanAsync(progress: null, CancellationToken.None);

        Assert.Equal(InventoryScanStatus.Partial, scan.Status);
        Assert.Contains(scan.Issues, issue => issue.Code == "collector_failed");
        Assert.Contains(scan.Components, item => item.IdentityKey == component.IdentityKey);
        Assert.Equal(2, scan.AttemptedCollectors);
    }

    [Fact]
    public async Task Scoped_scan_runs_only_collectors_in_the_requested_inventory_group()
    {
        var repository = new InMemoryInventoryRepository();
        var hardware = CreateComponent("cpu:one", "Processor");
        var application = new InventoryComponent(
            InventoryComponentType.InstalledApplication,
            "app:one",
            "Example app",
            "Registry",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>());
        var hardwareCollector = new FixedCollector(new CollectorResult("hardware", [hardware], []));
        var softwareCollector = new GroupCollector(
            InventoryScanGroup.Software,
            new CollectorResult("software", [application], []));
        var service = CreateService(repository, hardwareCollector, softwareCollector);

        var scan = await service.RunScanAsync(
            InventoryScanGroup.Software,
            progress: null,
            CancellationToken.None);

        Assert.Equal("Software, services, processes, startup, and scheduled-task inventory", scan.Scope);
        Assert.Equal(1, scan.AttemptedCollectors);
        Assert.Equal(1, scan.TotalCollectors);
        Assert.Equal(InventoryComponentType.InstalledApplication, Assert.Single(scan.Components).Type);
    }

    [Fact]
    public async Task Recovery_scan_runs_only_recovery_collectors_and_uses_its_own_scope()
    {
        var repository = new InMemoryInventoryRepository();
        var recovery = new InventoryComponent(
            InventoryComponentType.RecoveryEnvironment,
            "winre:test",
            "Windows Recovery Environment",
            "Test recovery source",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>());
        var recoveryCollector = new GroupCollector(
            InventoryScanGroup.Recovery,
            new CollectorResult("recovery", [recovery], []));
        var hardwareCollector = new GroupCollector(
            InventoryScanGroup.Hardware,
            new CollectorResult("hardware", [CreateComponent("cpu:test", "Processor")], []));
        var service = CreateService(repository, recoveryCollector, hardwareCollector);

        var scan = await service.RunScanAsync(
            InventoryScanGroup.Recovery,
            progress: null,
            CancellationToken.None);

        Assert.Equal("Recovery discovery", scan.Scope);
        Assert.Equal(1, scan.AttemptedCollectors);
        Assert.Equal(1, scan.TotalCollectors);
        Assert.Equal(recovery.IdentityKey, Assert.Single(scan.Components).IdentityKey);
    }

    [Fact]
    public async Task Process_is_associated_only_when_its_executable_is_inside_a_registered_install_location()
    {
        var repository = new InMemoryInventoryRepository();
        var application = new InventoryComponent(
            InventoryComponentType.InstalledApplication,
            "registered-app:example",
            "Example app",
            "Windows uninstall registry",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["installLocation"] = @"C:\Apps\Example" });
        var process = new InventoryComponent(
            InventoryComponentType.Process,
            "process:123:1000",
            "example",
            "Windows Process API",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["executablePath"] = @"C:\Apps\Example\example.exe",
                ["associatedApplicationIdentity"] = null
            })
        {
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["associatedApplicationIdentity"] = InventoryFieldAvailability.Unknown
            }
        };
        var service = CreateService(
            repository,
            new GroupCollector(
                InventoryScanGroup.Software,
                new CollectorResult("installed-applications", [application], [])),
            new GroupCollector(
                InventoryScanGroup.Software,
                new CollectorResult("processes", [process], [])));

        var scan = await service.RunScanAsync(
            InventoryScanGroup.Software,
            progress: null,
            CancellationToken.None);

        var observedProcess = Assert.Single(
            scan.Components,
            component => component.Type == InventoryComponentType.Process);
        Assert.Equal("registered-app:example", observedProcess.Details["associatedApplicationIdentity"]);
        Assert.Equal("Example app", observedProcess.Details["associatedApplicationName"]);
        Assert.Equal(
            InventoryFieldAvailability.Available,
            observedProcess.FieldAvailability["associatedApplicationIdentity"]);
    }

    [Fact]
    public async Task Duplicate_device_results_merge_setupapi_authority_with_wmi_supplemental_fields()
    {
        var repository = new InMemoryInventoryRepository();
        var wmiDevice = new InventoryComponent(
            InventoryComponentType.Device,
            @"pci\ven_1234&dev_abcd",
            "WMI display name",
            "WMI",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["manufacturer"] = "WMI manufacturer", ["status"] = "OK" });
        var setupApiDevice = new InventoryComponent(
            InventoryComponentType.Device,
            @"PCI\VEN_1234&DEV_ABCD",
            "SetupAPI display name",
            "SetupAPI",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["friendlyName"] = "SetupAPI display name", ["manufacturer"] = null })
        {
            Category = "Display",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["friendlyName"] = InventoryFieldAvailability.Available,
                ["manufacturer"] = InventoryFieldAvailability.Missing
            }
        };
        var service = CreateService(
            repository,
            new FixedCollector(new CollectorResult("pnp-devices", [wmiDevice], [])),
            new FixedCollector(new CollectorResult("pnp-setupapi", [setupApiDevice], [])));

        var scan = await service.RunScanAsync(progress: null, CancellationToken.None);

        var device = Assert.Single(scan.Components);
        Assert.Equal("SetupAPI display name", device.DisplayName);
        Assert.Equal("SetupAPI, WMI", device.Source);
        Assert.Equal("WMI manufacturer", device.Details["manufacturer"]);
        Assert.Equal("OK", device.Details["status"]);
        Assert.Equal(InventoryFieldAvailability.Available, device.FieldAvailability["manufacturer"]);
        Assert.Equal("Display", device.Category);
    }

    [Fact]
    public async Task Shared_driver_package_reconciliation_preserves_all_device_associations_and_wmi_signature_metadata()
    {
        var repository = new InMemoryInventoryRepository();
        const string packageIdentity = "driver-inf:OEM42.INF";
        var setupApiPackages = new[]
        {
            CreateDriverPackage(packageIdentity, @"PCI\VEN_1234&DEV_0001"),
            CreateDriverPackage(packageIdentity, @"PCI\VEN_1234&DEV_0002")
        };
        var wmiPackage = new InventoryComponent(
            InventoryComponentType.DriverPackage,
            packageIdentity.ToLowerInvariant(),
            "Example display driver",
            "WMI",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["infName"] = "oem42.inf",
                ["associatedDeviceIds"] = @"PCI\VEN_1234&DEV_0003",
                ["signer"] = "Example signer",
                ["isSigned"] = "True"
            })
        {
            Category = "Drivers",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["signer"] = InventoryFieldAvailability.Available,
                ["isSigned"] = InventoryFieldAvailability.Available
            }
        };
        var service = CreateService(
            repository,
            new FixedCollector(new CollectorResult("setupapi", setupApiPackages, [])),
            new FixedCollector(new CollectorResult("wmi", [wmiPackage], [])));

        var scan = await service.RunScanAsync(progress: null, CancellationToken.None);

        var package = Assert.Single(scan.Components);
        Assert.Equal(InventoryComponentType.DriverPackage, package.Type);
        Assert.Equal(
            new[]
            {
                @"PCI\VEN_1234&DEV_0001",
                @"PCI\VEN_1234&DEV_0002",
                @"PCI\VEN_1234&DEV_0003"
            },
            package.Details["associatedDeviceIds"]!.Split(Environment.NewLine));
        Assert.Equal("Example signer", package.Details["signer"]);
        Assert.Equal("True", package.Details["isSigned"]);
        Assert.Equal(InventoryFieldAvailability.Available, package.FieldAvailability["signer"]);
    }

    [Fact]
    public async Task Cancellation_is_recorded_and_completed_collector_results_are_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new InMemoryInventoryRepository();
        var component = CreateComponent("os:current", "Windows 11");
        var service = new PassportScanService(
            [
                new FixedCollector(new CollectorResult("operating-system", [component], [])),
                new CancellingCollector(cancellation)
            ],
            repository,
            NullLogger<PassportScanService>.Instance);

        var scan = await service.RunScanAsync(progress: null, cancellation.Token);
        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(InventoryScanStatus.Cancelled, scan.Status);
        Assert.Contains(scan.Issues, issue => issue.Code == "cancelled");
        Assert.Single(snapshot.Components);
    }

    private static PassportScanService CreateService(
        IInventoryRepository repository,
        params IInventoryCollector[] collectors) =>
        new(collectors, repository, NullLogger<PassportScanService>.Instance);

    private static InventoryComponent CreateComponent(string identityKey, string name) =>
        new(
            InventoryComponentType.OperatingSystem,
            identityKey,
            name,
            "Test fixture",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["build"] = "test" });

    private static InventoryComponent CreateDriverPackage(string identityKey, string deviceInstanceId) =>
        new(
            InventoryComponentType.DriverPackage,
            identityKey,
            "Example driver package",
            "SetupAPI",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["infName"] = "oem42.inf",
                ["associatedDeviceIds"] = deviceInstanceId,
                ["signer"] = null,
                ["isSigned"] = null
            })
        {
            Category = "Drivers",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["signer"] = InventoryFieldAvailability.Unsupported,
                ["isSigned"] = InventoryFieldAvailability.Unsupported
            }
        };

    private sealed class FixedCollector(CollectorResult result) : IInventoryCollector
    {
        public string CollectorId => result.CollectorId;

        public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class GroupCollector(
        InventoryScanGroup scanGroup,
        CollectorResult result) : IInventoryCollector
    {
        public string CollectorId => result.CollectorId;

        public InventoryScanGroup ScanGroup => scanGroup;

        public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CancellingCollector(CancellationTokenSource cancellation) : IInventoryCollector
    {
        public string CollectorId => "z-cancelling-collector";

        public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<CollectorResult>(cancellation.Token);
        }
    }

    private sealed class ThrowingCollector : IInventoryCollector
    {
        public string CollectorId => "a-throwing-collector";

        public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic collector failure.");
    }

    private sealed class InMemoryInventoryRepository : IInventoryRepository
    {
        private readonly Dictionary<(InventoryComponentType Type, string Key), InventoryComponent> _components = [];
        private LatestScan? _latestScan;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartScanAsync(
            Guid scanId,
            DateTimeOffset startedAt,
            string scope,
            int totalCollectors,
            CancellationToken cancellationToken)
        {
            _latestScan = new LatestScan(
                scanId,
                startedAt,
                null,
                scope,
                InventoryScanStatus.InProgress,
                0,
                totalCollectors,
                []);
            return Task.CompletedTask;
        }

        public Task CompleteScanAsync(ScanRecord scan, CancellationToken cancellationToken)
        {
            foreach (var component in scan.Components)
            {
                _components[(component.Type, component.IdentityKey)] = component;
            }

            _latestScan = new LatestScan(
                scan.ScanId,
                scan.StartedAt,
                scan.CompletedAt,
                scan.Scope,
                scan.Status,
                scan.AttemptedCollectors,
                scan.TotalCollectors,
                scan.Issues);
            return Task.CompletedTask;
        }

        public Task<PassportSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PassportSnapshot(_latestScan, _components.Values.ToArray()));

        public Task<string> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
