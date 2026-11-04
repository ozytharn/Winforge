using WinForge.Application.Configuration;
using WinForge.Application.Diagnostics;
using WinForge.Domain.Configuration;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.UnitTests.Configuration;

public sealed class ConfigurationDriftEngineTests
{
    [Fact]
    public void Analyze_detects_added_modified_and_not_observed_without_claiming_removed()
    {
        var previous = Snapshot(
            new[]
            {
                CreateComponent(InventoryComponentType.Service, "svc:active", "Active Service", "Win32_Service", new Dictionary<string, string?>
                {
                    ["status"] = "Stopped",
                    ["path"] = @"C:\\Apps\\WinForge\\service.exe"
                }),
                CreateComponent(InventoryComponentType.Service, "svc:legacy", "Legacy Service", "Win32_Service", new Dictionary<string, string?>
                {
                    ["status"] = "Stopped"
                })
            });

        var current = Snapshot(
            new[]
            {
                CreateComponent(InventoryComponentType.Service, "svc:active", "Active Service", "Win32_Service", new Dictionary<string, string?>
                {
                    ["status"] = "Running",
                    ["path"] = @"C:\\Apps\\WinForge\\service.exe"
                }),
                CreateComponent(InventoryComponentType.Service, "svc:new", "New Service", "Win32_Service", new Dictionary<string, string?>
                {
                    ["status"] = "Running"
                })
            });

        var assessment = new ConfigurationDriftEngine().Analyze(previous, current);

        Assert.Contains(assessment.Changes, change => change.Classification == ConfigurationDriftClassification.Modified && change.EntityIdentity == "svc:active");
        Assert.Contains(assessment.Changes, change => change.Classification == ConfigurationDriftClassification.Added && change.EntityIdentity == "svc:new");
        Assert.Contains(assessment.Changes, change => change.Classification == ConfigurationDriftClassification.NotObserved && change.EntityIdentity == "svc:legacy");
        Assert.DoesNotContain(assessment.Changes, change => change.Classification == ConfigurationDriftClassification.PotentiallyRemoved);
    }

    [Fact]
    public void Analyze_reports_potential_removal_only_after_follow_up_confirms_absence()
    {
        var previous = Snapshot(
            new[]
            {
                CreateComponent(InventoryComponentType.Service, "svc:legacy", "Legacy Service", "Win32_Service", new Dictionary<string, string?>
                {
                    ["status"] = "Stopped"
                })
            });

        var current = Snapshot([]);
        var confirmation = Snapshot([]);

        var assessment = new ConfigurationDriftEngine().Analyze(previous, current, confirmation);

        Assert.Contains(assessment.Changes, change => change.Classification == ConfigurationDriftClassification.PotentiallyRemoved && change.EntityIdentity == "svc:legacy");
    }

    [Fact]
    public void Investigate_generates_hypotheses_only_from_supporting_evidence()
    {
        var telemetry = new GuardianTelemetrySnapshot(
            CollectedAt: DateTimeOffset.UtcNow,
            CpuUsagePercent: new TelemetryReading<double>(92d, InventoryFieldAvailability.Available, "System"),
            AvailableMemoryBytes: new TelemetryReading<ulong>(10_240, InventoryFieldAvailability.Available, "System"),
            TotalMemoryBytes: new TelemetryReading<ulong>(32_768, InventoryFieldAvailability.Available, "System"),
            SystemUptime: new TelemetryReading<TimeSpan>(TimeSpan.FromHours(12), InventoryFieldAvailability.Available, "System"),
            StorageEnumeration: new TelemetryReading<int>(1, InventoryFieldAvailability.Available, "System"),
            StorageVolumes: []);

        var inventory = Snapshot(
            new[]
            {
                CreateComponent(InventoryComponentType.Device, "PCI\\VEN_1234&DEV_0001", "Example device", "SetupAPI", new Dictionary<string, string?>
                {
                    ["deviceProblemCode"] = "28",
                    ["driverStatus"] = "Missing driver or failed install"
                })
            });

        var assessment = new RootCauseInvestigationEngine().Investigate(telemetry, inventory);

        Assert.NotEmpty(assessment.Hypotheses);
        Assert.Contains(assessment.Hypotheses, hypothesis => hypothesis.Category == "Performance");
        Assert.All(assessment.Hypotheses, hypothesis => Assert.True(hypothesis.IsCorrelationOnly));
    }

    private static PassportSnapshot Snapshot(IEnumerable<InventoryComponent> components) =>
        new PassportSnapshot(
            new LatestScan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow,
                "test",
                InventoryScanStatus.Completed,
                1,
                1,
                []),
            components.ToArray());

    private static InventoryComponent CreateComponent(
        InventoryComponentType type,
        string identityKey,
        string displayName,
        string source,
        IReadOnlyDictionary<string, string?> details)
        => new(
            type,
            identityKey,
            displayName,
            source,
            1,
            DateTimeOffset.UtcNow,
            details);
}
