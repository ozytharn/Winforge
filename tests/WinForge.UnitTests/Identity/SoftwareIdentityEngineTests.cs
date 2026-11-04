using WinForge.Application.Identity;
using WinForge.Domain.Identity;
using WinForge.Domain.Inventory;

namespace WinForge.UnitTests.Identity;

public sealed class SoftwareIdentityEngineTests
{
    [Fact]
    public void Installed_application_process_and_startup_entry_are_grouped_into_one_product()
    {
        var app = CreateInstalledApplication(
            "app:winforge",
            "WinForge",
            "WinForge Ltd",
            "C:\\Program Files\\WinForge",
            "WinForge");
        var process = CreateProcess(
            "process:123",
            "WinForge",
            "C:\\Program Files\\WinForge\\WinForge.exe");
        var startupEntry = CreateStartupEntry(
            "startup:winforge",
            "WinForge Startup",
            "C:\\Program Files\\WinForge\\WinForge.exe");

        var assessment = new SoftwareIdentityEngine().Analyze([app, process, startupEntry]);

        var product = Assert.Single(assessment.Products);
        Assert.Equal("WinForge", product.DisplayName);
        Assert.Equal(SoftwareIdentityConfidence.High, product.Confidence);
        Assert.Equal(3, product.Components.Count);
        Assert.Empty(assessment.Conflicts);
    }

    [Fact]
    public void Similar_names_with_different_publishers_and_paths_remain_distinct_products()
    {
        var winforge = CreateInstalledApplication(
            "app:winforge-1",
            "WinForge",
            "WinForge Ltd",
            "C:\\Program Files\\WinForge",
            "WinForge");
        var contoso = CreateInstalledApplication(
            "app:winforge-2",
            "WinForge",
            "Contoso Software",
            "C:\\Program Files\\Contoso\\WinForge",
            "WinForge");

        var assessment = new SoftwareIdentityEngine().Analyze([winforge, contoso]);

        Assert.Equal(2, assessment.Products.Count);
        Assert.Equal(2, assessment.Products.SelectMany(product => product.Components).Count());
        Assert.All(assessment.Products, product => Assert.NotEmpty(product.Components));
    }

    [Fact]
    public void Assessment_service_exposes_reconciled_product_summary_for_snapshot_inventory()
    {
        var snapshot = new PassportSnapshot(
            new LatestScan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow,
                "software",
                InventoryScanStatus.Completed,
                2,
                2,
                []),
            [
                CreateInstalledApplication(
                    "app:winforge",
                    "WinForge",
                    "WinForge Ltd",
                    "C:\\Program Files\\WinForge",
                    "WinForge"),
                CreateProcess(
                    "process:123",
                    "WinForge",
                    "C:\\Program Files\\WinForge\\WinForge.exe"),
                CreateStartupEntry(
                    "startup:winforge",
                    "WinForge Startup",
                    "C:\\Program Files\\WinForge\\WinForge.exe")
            ]);

        var service = new SoftwareIdentityAssessmentService(new SoftwareIdentityEngine());
        var assessment = service.Analyze(snapshot);

        Assert.Single(assessment.Products);
        Assert.Equal("WinForge", assessment.Products[0].DisplayName);
        Assert.Contains("1 reconciled software product", service.BuildSummary(assessment), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Name_only_records_remain_low_confidence_and_are_not_treated_as_authoritative()
    {
        var component = new InventoryComponent(
            InventoryComponentType.InstalledApplication,
            "app:name-only",
            "WinForge",
            "Windows uninstall registry",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["displayName"] = "WinForge"
            });

        var assessment = new SoftwareIdentityEngine().Analyze([component]);

        var product = Assert.Single(assessment.Products);
        Assert.Equal(SoftwareIdentityConfidence.Low, product.Confidence);
        Assert.Contains("low-confidence", assessment.Warnings.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_name_and_publisher_with_different_install_locations_are_flagged_for_conflict()
    {
        var first = CreateInstalledApplication(
            "app:winforge-a",
            "WinForge",
            "WinForge Ltd",
            "C:\\Program Files\\WinForge",
            "WinForge");
        var second = CreateInstalledApplication(
            "app:winforge-b",
            "WinForge",
            "WinForge Ltd",
            "D:\\Tools\\WinForge",
            "WinForge");

        var assessment = new SoftwareIdentityEngine().Analyze([first, second]);

        var conflict = Assert.Single(assessment.Conflicts);
        Assert.Contains("Multiple discovered install paths", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, conflict.ComponentIdentityKeys.Count);
    }

    private static InventoryComponent CreateInstalledApplication(
        string identityKey,
        string displayName,
        string publisher,
        string installLocation,
        string packageIdentity)
    {
        return new InventoryComponent(
            InventoryComponentType.InstalledApplication,
            identityKey,
            displayName,
            "Windows uninstall registry",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["displayName"] = displayName,
                ["publisher"] = publisher,
                ["installLocation"] = installLocation,
                ["packageIdentity"] = packageIdentity,
                ["version"] = "1.2.3"
            });
    }

    private static InventoryComponent CreateProcess(string identityKey, string processName, string executablePath)
    {
        return new InventoryComponent(
            InventoryComponentType.Process,
            identityKey,
            processName,
            "Windows Process API",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["displayName"] = processName,
                ["executablePath"] = executablePath,
                ["publisher"] = "WinForge Ltd"
            });
    }

    private static InventoryComponent CreateStartupEntry(string identityKey, string displayName, string targetPath)
    {
        return new InventoryComponent(
            InventoryComponentType.StartupEntry,
            identityKey,
            displayName,
            "Startup registry",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["displayName"] = displayName,
                ["targetPath"] = targetPath,
                ["publisher"] = "WinForge Ltd"
            });
    }
}
