using Microsoft.Extensions.Logging.Abstractions;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WinForge.Infrastructure.Collectors;
using WinForge.Infrastructure.Persistence;

namespace WinForge.IntegrationTests;

public sealed class SoftwareSystemInventoryCollectorTests
{
    [Fact]
    public async Task Process_inventory_is_read_only_and_marks_protected_metadata_as_unavailable()
    {
        var collector = new ProcessInventoryCollector(NullLogger<ProcessInventoryCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.NotEmpty(result.Components);
        Assert.All(result.Components, component =>
        {
            Assert.Equal(InventoryComponentType.Process, component.Type);
            Assert.Contains("processId", component.Details);
            Assert.Contains("workingSetBytes", component.Details);
            Assert.Contains("cpuTimeMilliseconds", component.Details);
            Assert.Null(component.Details["commandLine"]);
            Assert.Equal(
                InventoryFieldAvailability.Unsupported,
                component.FieldAvailability["commandLine"]);
        });
    }

    [Fact]
    public async Task Process_inventory_honors_cancellation_before_enumerating_processes()
    {
        var collector = new ProcessInventoryCollector(NullLogger<ProcessInventoryCollector>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(cancellation.Token));
    }

    [Fact]
    public async Task Registered_application_inventory_deduplicates_views_and_never_collects_uninstall_commands()
    {
        var collector = new InstalledApplicationInventoryCollector(
            NullLogger<InstalledApplicationInventoryCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.All(result.Components, component =>
        {
            Assert.Equal(InventoryComponentType.InstalledApplication, component.Type);
            Assert.Equal("Registered", component.Details["registrationKind"]);
            Assert.Contains(component.Details["installScope"], new[] { "System", "CurrentUser" });
            Assert.NotNull(component.Details["registryViews"]);
            Assert.Null(component.Details["uninstallCommand"]);
            Assert.Equal(
                InventoryFieldAvailability.Unsupported,
                component.FieldAvailability["uninstallCommand"]);
        });
        Assert.Equal(
            result.Components.Count,
            result.Components.Select(component => component.IdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Scheduled_task_inventory_does_not_collect_task_arguments()
    {
        var collector = new ScheduledTaskInventoryCollector(
            NullLogger<ScheduledTaskInventoryCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.All(result.Components, component =>
        {
            Assert.Equal(InventoryComponentType.ScheduledTask, component.Type);
            Assert.Contains("taskPath", component.Details);
            Assert.Contains("actions", component.Details);
            Assert.DoesNotContain("arguments", component.Details.Keys, StringComparer.OrdinalIgnoreCase);
        });
        Assert.True(result.Components.Count > 0 || result.Issues.Count > 0);
    }

    [Fact]
    public async Task System_and_service_inventory_report_partial_provider_failures_without_throwing()
    {
        var collectors = new WinForge.Application.Inventory.IInventoryCollector[]
        {
            new WindowsServicesInventoryCollector(NullLoggerFactory.Instance),
            new WindowsUpdatesInventoryCollector(NullLoggerFactory.Instance),
            new WindowsFeaturesInventoryCollector(NullLoggerFactory.Instance),
            new WindowsBootConfigurationInventoryCollector(NullLoggerFactory.Instance),
            new WindowsOperatingSystemDetailsInventoryCollector(NullLoggerFactory.Instance),
            new WindowsActivationInventoryCollector(NullLoggerFactory.Instance),
            new EnvironmentVariablesInventoryCollector(NullLogger<EnvironmentVariablesInventoryCollector>.Instance)
        };

        foreach (var collector in collectors)
        {
            var result = await collector.CollectAsync(CancellationToken.None);
            Assert.All(result.Components, component =>
                Assert.NotEmpty(component.FieldAvailability));
            Assert.True(result.Components.Count > 0 || result.Issues.Count > 0);
            if (collector is EnvironmentVariablesInventoryCollector)
            {
                Assert.All(result.Components, component =>
                    Assert.DoesNotContain(
                        component.Details["name"],
                        new[] { "PATH", "TEMP", "TMP", "USERPROFILE" },
                        StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task Software_and_system_scans_persist_independently_and_keep_each_others_history()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForgeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var softwareCollectors = new IInventoryCollector[]
            {
                new InstalledApplicationInventoryCollector(
                    NullLogger<InstalledApplicationInventoryCollector>.Instance),
                new StartupInventoryCollector(NullLogger<StartupInventoryCollector>.Instance),
                new WindowsServicesInventoryCollector(NullLoggerFactory.Instance),
                new ProcessInventoryCollector(NullLogger<ProcessInventoryCollector>.Instance),
                new ScheduledTaskInventoryCollector(NullLogger<ScheduledTaskInventoryCollector>.Instance)
            };
            var systemCollectors = new IInventoryCollector[]
            {
                new WindowsUpdatesInventoryCollector(NullLoggerFactory.Instance),
                new WindowsFeaturesInventoryCollector(NullLoggerFactory.Instance),
                new WindowsBootConfigurationInventoryCollector(NullLoggerFactory.Instance),
                new WindowsOperatingSystemDetailsInventoryCollector(NullLoggerFactory.Instance),
                new WindowsActivationInventoryCollector(NullLoggerFactory.Instance),
                new EnvironmentVariablesInventoryCollector(
                    NullLogger<EnvironmentVariablesInventoryCollector>.Instance)
            };
            var service = new PassportScanService(
                softwareCollectors.Concat(systemCollectors),
                new SqliteInventoryRepository(
                    Path.Combine(directory, "inventory.db"),
                    NullLogger<SqliteInventoryRepository>.Instance),
                NullLogger<PassportScanService>.Instance);
            await service.InitializeAsync(CancellationToken.None);

            var softwareScan = await service.RunScanAsync(
                InventoryScanGroup.Software,
                progress: null,
                CancellationToken.None);
            var systemScan = await service.RunScanAsync(
                InventoryScanGroup.System,
                progress: null,
                CancellationToken.None);
            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.NotEqual(InventoryScanStatus.Failed, softwareScan.Status);
            Assert.NotEqual(InventoryScanStatus.Failed, systemScan.Status);
            Assert.All(softwareScan.Components, component =>
                Assert.Contains(
                    component.Type,
                    new[]
                    {
                        InventoryComponentType.InstalledApplication,
                        InventoryComponentType.Service,
                        InventoryComponentType.Process,
                        InventoryComponentType.StartupEntry,
                        InventoryComponentType.ScheduledTask
                    }));
            Assert.All(systemScan.Components, component =>
                Assert.Contains(
                    component.Type,
                    new[]
                    {
                        InventoryComponentType.WindowsUpdate,
                        InventoryComponentType.WindowsFeature,
                        InventoryComponentType.EnvironmentVariable,
                        InventoryComponentType.WindowsSystemInfo
                    }));
            Assert.Contains(
                snapshot.Components,
                component => component.Type == InventoryComponentType.Process);
            Assert.Contains(
                snapshot.Components,
                component => component.Type == InventoryComponentType.EnvironmentVariable);
            Assert.Equal(systemScan.ScanId, snapshot.ComparisonScan?.ScanId);
            Assert.Equal(systemScan.Scope, snapshot.ComparisonScan?.Scope);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
