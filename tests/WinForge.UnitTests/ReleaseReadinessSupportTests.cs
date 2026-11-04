using System.Text.Json;
using WinForge.Application.Diagnostics;
using WinForge.Application.Settings;

namespace WinForge.UnitTests;

public sealed class ReleaseReadinessSupportTests
{
    [Fact]
    public void AppHealthAssessmentService_reports_healthy_local_environment()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "WinForgeHealthTests", Guid.NewGuid().ToString("N"));
        var report = new AppHealthAssessmentService().Assess(
            applicationVersion: "0.9.0",
            dataDirectory: tempDirectory,
            databasePath: Path.Combine(tempDirectory, "winforge.db"));

        Assert.Equal(ApplicationHealthState.Healthy, report.OverallState);
        Assert.Contains("Application diagnostics are healthy", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Checks, check => check.Name == "Runtime availability" && check.State == ApplicationHealthState.Healthy);
    }

    [Fact]
    public void AppHealthAssessmentService_reports_unavailable_when_directory_is_invalid()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), "bad<name>");
        var report = new AppHealthAssessmentService().Assess(
            applicationVersion: "0.9.0",
            dataDirectory: invalidPath,
            databasePath: Path.Combine(invalidPath, "winforge.db"));

        Assert.True(report.OverallState is ApplicationHealthState.Unavailable or ApplicationHealthState.Failed);
        Assert.Contains(report.Checks, check => check.Name == "Data directory" && check.State == ApplicationHealthState.Unavailable);
    }

    [Fact]
    public void ApplicationSettingsService_round_trips_and_validates_values()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "WinForgeSettingsTests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDirectory, "settings.json");
        var service = new ApplicationSettingsService(settingsPath);

        var updated = WinForgeSettings.Default with
        {
            Theme = "Dark",
            StartupBehavior = "ShowPassport",
            GuardianSampleRetentionDays = 45,
            MaxTelemetrySamples = 250,
            AllowBackgroundScans = true,
            EnableDiagnosticExports = true,
            ShowFirstRunGuidance = false
        };

        service.Save(updated);
        var loaded = service.Load();

        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("ShowPassport", loaded.StartupBehavior);
        Assert.Equal(45, loaded.GuardianSampleRetentionDays);
        Assert.Equal(250, loaded.MaxTelemetrySamples);
        Assert.True(loaded.AllowBackgroundScans);
        Assert.False(loaded.ShowFirstRunGuidance);
    }

    [Fact]
    public void DiagnosticExportService_generates_privacy_safe_report_and_manifest()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "WinForgeDiagnosticExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var health = new AppHealthAssessmentService().Assess(
            applicationVersion: "0.9.0",
            dataDirectory: tempDirectory,
            databasePath: Path.Combine(tempDirectory, "winforge.db"));

        var export = new DiagnosticExportService().Export(
            Path.Combine(tempDirectory, "export"),
            health,
            databasePath: Path.Combine(tempDirectory, "winforge.db"),
            installationRoot: tempDirectory);

        Assert.True(File.Exists(export.HumanReadableReportPath));
        Assert.True(File.Exists(export.StructuredReportPath));
        Assert.True(File.Exists(export.ManifestPath));
        Assert.Contains("WinForge diagnostics export", File.ReadAllText(export.HumanReadableReportPath), StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(File.ReadAllText(export.StructuredReportPath));
        var summary = document.RootElement.GetProperty("applicationHealth").GetProperty("summary").GetString() ?? string.Empty;
        Assert.Contains("Application diagnostics are healthy", summary, StringComparison.OrdinalIgnoreCase);

        using var manifest = JsonDocument.Parse(File.ReadAllText(export.ManifestPath));
        Assert.True(manifest.RootElement.GetProperty("files").GetArrayLength() >= 2);
    }
}
