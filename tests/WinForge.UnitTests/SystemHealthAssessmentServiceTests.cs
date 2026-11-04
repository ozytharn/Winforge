using WinForge.Application.Diagnostics;
using WinForge.Domain.Inventory;

namespace WinForge.UnitTests;

public sealed class SystemHealthAssessmentServiceTests
{
    [Fact]
    public void Analyze_identifies_storage_degradation_and_unsigned_drivers()
    {
        var assessment = new SystemHealthAssessmentService().Analyze(
        [
            CreateStorage("NVMe 1", "Healthy"),
            CreateStorage("NVMe 2", "Warning"),
            CreateDriver("driver-1", "false", "Untrusted"),
            CreateDriver("driver-2", "true", "Microsoft Windows")
        ]);

        Assert.Equal(2, assessment.StorageDeviceCount);
        Assert.Equal(1, assessment.HealthyStorageCount);
        Assert.Equal(1, assessment.DegradedStorageCount);
        Assert.Equal(0, assessment.UnknownStorageCount);
        Assert.Equal(2, assessment.DriverPackageCount);
        Assert.Equal(1, assessment.DriverWarningCount);
        Assert.Contains("driver-1", assessment.Warnings.Single());
    }

    [Fact]
    public void Analyze_marks_missing_storage_metrics_as_unknown()
    {
        var assessment = new SystemHealthAssessmentService().Analyze(
        [
            new InventoryComponent(
                InventoryComponentType.StorageHealth,
                "disk:unknown",
                "Generic disk",
                "WMI",
                2,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["deviceId"] = "\\\\.\\PHYSICALDRIVE1"
                })
            {
                Category = "Storage health"
            },
            CreateDriver("driver-3", "true", "Microsoft")
        ]);

        Assert.Equal(1, assessment.StorageDeviceCount);
        Assert.Equal(1, assessment.UnknownStorageCount);
        Assert.Equal(1, assessment.DriverPackageCount);
        Assert.Equal(0, assessment.DriverWarningCount);
    }

    [Fact]
    public void BuildSummary_returns_helpful_message_when_no_storage_or_driver_data_exists()
    {
        var service = new SystemHealthAssessmentService();
        var summary = service.BuildSummary(new StorageDriverAssessment(0, 0, 0, 0, 0, 0, []));

        Assert.Contains("No storage or driver inventory data", summary, StringComparison.OrdinalIgnoreCase);
    }

    private static InventoryComponent CreateStorage(string identity, string healthStatus) =>
        new(
            InventoryComponentType.StorageHealth,
            $"disk:{identity}",
            identity,
            "WMI",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["healthStatus"] = healthStatus,
                ["capacityBytes"] = healthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? "100" : "60"
            });

    private static InventoryComponent CreateDriver(string identity, string isSigned, string signer) =>
        new(
            InventoryComponentType.DriverPackage,
            $"driver:{identity}",
            identity,
            "WMI",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["isSigned"] = isSigned,
                ["signer"] = signer
            })
        {
            Category = "Drivers"
        };
}
