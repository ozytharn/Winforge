using WinForge.Application.Diagnostics;
using WinForge.Domain.Inventory;

namespace WinForge.UnitTests;

public sealed class SecurityPrivacyAssessmentServiceTests
{
    [Fact]
    public void Analyze_marks_running_security_controls_as_healthy()
    {
        var assessment = new SecurityPrivacyAssessmentService().Analyze(
        [
            CreateService("WinDefend", "Running"),
            CreateService("MpsSvc", "Running"),
            CreateSystemInfo("true", "true"),
            CreateBitLockerVolume("Protected"),
            CreateStartupEntry("Startup1"),
            CreateScheduledTask("Daily sync")
        ]);

        Assert.Equal(SecurityPrivacyAssessmentState.Healthy, assessment.OverallState);
        Assert.Contains(assessment.Findings, finding => finding.Category == "Defender" && finding.State == SecurityPrivacyAssessmentState.Healthy);
        Assert.Contains(assessment.Findings, finding => finding.Category == "Firewall" && finding.State == SecurityPrivacyAssessmentState.Healthy);
    }

    [Fact]
    public void Analyze_reports_degraded_firewall_and_bitlocker_status()
    {
        var assessment = new SecurityPrivacyAssessmentService().Analyze(
        [
            CreateService("WinDefend", "Running"),
            CreateService("MpsSvc", "Stopped"),
            CreateSystemInfo("false", "false"),
            CreateBitLockerVolume("Off")
        ]);

        Assert.Equal(SecurityPrivacyAssessmentState.Degraded, assessment.OverallState);
        Assert.Contains(assessment.Findings, finding => finding.Category == "Firewall" && finding.State == SecurityPrivacyAssessmentState.Degraded);
        Assert.Contains(assessment.Findings, finding => finding.Category == "BitLocker" && finding.State == SecurityPrivacyAssessmentState.Degraded);
    }

    [Fact]
    public void Analyze_marks_missing_security_data_as_unknown()
    {
        var assessment = new SecurityPrivacyAssessmentService().Analyze(
        [
            new InventoryComponent(
                InventoryComponentType.OperatingSystem,
                "os:baseline",
                "Windows",
                "WMI",
                1,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["Caption"] = "Windows 11"
                })
        ]);

        Assert.Equal(SecurityPrivacyAssessmentState.Unknown, assessment.OverallState);
        Assert.Contains(assessment.Findings, finding => finding.Category == "Defender" || finding.Category == "Firewall" || finding.Category == "Windows Update");
    }

    [Fact]
    public void Analyze_reports_privacy_settings_when_they_are_enabled()
    {
        var assessment = new SecurityPrivacyAssessmentService().Analyze(
        [
            new InventoryComponent(
                InventoryComponentType.WindowsSystemInfo,
                "privacy:one",
                "Privacy settings",
                "Registry",
                1,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["privacyAdvertisingIdEnabled"] = "true",
                    ["privacyDiagnosticsEnabled"] = "true"
                })
        ]);

        Assert.Equal(SecurityPrivacyAssessmentState.Degraded, assessment.OverallState);
        var privacyFinding = Assert.Single(assessment.Findings, finding => finding.Category == "Privacy");
        Assert.Equal(SecurityPrivacyAssessmentState.Degraded, privacyFinding.State);
        Assert.Contains("Privacy-affecting settings are enabled", privacyFinding.Message);
    }

    [Fact]
    public void Analyze_marks_privacy_settings_as_healthy_when_restricted()
    {
        var assessment = new SecurityPrivacyAssessmentService().Analyze(
        [
            new InventoryComponent(
                InventoryComponentType.WindowsSystemInfo,
                "privacy:two",
                "Privacy settings",
                "Registry",
                1,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["privacyAdvertisingIdEnabled"] = "false",
                    ["privacyDiagnosticsEnabled"] = "0",
                    ["privacyLocationServicesEnabled"] = "disabled"
                })
        ]);

        var privacyFinding = Assert.Single(assessment.Findings, finding => finding.Category == "Privacy");
        Assert.Equal(SecurityPrivacyAssessmentState.Healthy, privacyFinding.State);
        Assert.Contains("disabled or restricted", privacyFinding.Message);
    }

    [Fact]
    public void BuildFindingDetails_surfaces_evidence_and_next_steps()
    {
        var finding = new SecurityPrivacyFinding(
            "Firewall",
            SecurityPrivacyAssessmentState.Degraded,
            "Warning",
            0.8,
            "The Windows Firewall service appears disabled.",
            ["State=Stopped", "StartMode=Disabled"],
            "Windows service inventory",
            DateTimeOffset.UtcNow,
            ["MpsSvc", "Windows Firewall"],
            "This is metadata-only evidence and may not reflect a true active policy issue.",
            "Check the firewall profile controls and confirm whether the device is expected to have public network filtering.");

        var details = SecurityPrivacyAssessmentService.BuildFindingDetails(finding);

        Assert.Contains("Category: Firewall", details);
        Assert.Contains("Evidence:", details);
        Assert.Contains("State=Stopped", details);
        Assert.Contains("Suggested investigation:", details);
        Assert.Contains("MpsSvc", details);
    }

    private static InventoryComponent CreateService(string name, string state) =>
        new(
            InventoryComponentType.Service,
            $"service:{name}",
            name,
            "Windows service inventory",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["Name"] = name,
                ["State"] = state,
                ["Status"] = state
            });

    private static InventoryComponent CreateSystemInfo(string uacEnabled, string secureBootEnabled) =>
        new(
            InventoryComponentType.WindowsSystemInfo,
            "system:baseline",
            "Windows system",
            "Windows system inventory",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["uacEnabled"] = uacEnabled,
                ["secureBootEnabled"] = secureBootEnabled
            });

    private static InventoryComponent CreateBitLockerVolume(string protectionStatus) =>
        new(
            InventoryComponentType.BitLockerVolume,
            "bitlocker:system",
            "BitLocker system volume",
            "BitLocker inventory",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["protectionStatus"] = protectionStatus
            });

    private static InventoryComponent CreateStartupEntry(string name) =>
        new(
            InventoryComponentType.StartupEntry,
            $"startup:{name}",
            name,
            "Startup inventory",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["Name"] = name
            });

    private static InventoryComponent CreateScheduledTask(string name) =>
        new(
            InventoryComponentType.ScheduledTask,
            $"task:{name}",
            name,
            "Task scheduler",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["Name"] = name
            });
}
