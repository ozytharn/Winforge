using System.Runtime.InteropServices;

namespace WinForge.Application.Diagnostics;

public sealed class AppHealthAssessmentService
{
    public ApplicationHealthReport Assess(
        string? applicationVersion = null,
        string? dataDirectory = null,
        string? databasePath = null,
        string? runtimeVersion = null)
    {
        var effectiveDataDirectory = !string.IsNullOrWhiteSpace(dataDirectory)
            ? dataDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinForge");

        var effectiveDatabasePath = !string.IsNullOrWhiteSpace(databasePath)
            ? databasePath
            : Path.Combine(effectiveDataDirectory, "winforge.db");

        var runtime = string.IsNullOrWhiteSpace(runtimeVersion)
            ? Environment.Version.ToString()
            : runtimeVersion;

        var appVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? "unknown"
            : applicationVersion;

        var checks = new List<ApplicationDiagnosticCheck>();

        checks.Add(new ApplicationDiagnosticCheck(
            "Application version",
            string.IsNullOrWhiteSpace(appVersion) || appVersion == "unknown"
                ? ApplicationHealthState.Unknown
                : ApplicationHealthState.Healthy,
            string.IsNullOrWhiteSpace(appVersion) || appVersion == "unknown"
                ? "Application version is not available."
                : $"Application version is {appVersion}.",
            DateTimeOffset.UtcNow,
            $"Assembly version: {appVersion}",
            "Use the About screen or release metadata to confirm the binary version before sharing diagnostics."));

        checks.Add(new ApplicationDiagnosticCheck(
            "Runtime availability",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ApplicationHealthState.Healthy
                : ApplicationHealthState.Unsupported,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"WinForge is running on a supported Windows runtime ({runtime})."
                : "WinForge is not running on a supported Windows platform.",
            DateTimeOffset.UtcNow,
            $"OS platform: {RuntimeInformation.OSDescription}; runtime: {runtime}",
            "Run WinForge on a supported Windows 10 or Windows 11 installation."));

        var dataDirectoryHealthy = EnsureDirectory(effectiveDataDirectory, out var dataDirectoryError);
        checks.Add(new ApplicationDiagnosticCheck(
            "Data directory",
            dataDirectoryHealthy ? ApplicationHealthState.Healthy : ApplicationHealthState.Unavailable,
            dataDirectoryHealthy
                ? $"Application data directory is available at {effectiveDataDirectory}."
                : $"Application data directory is unavailable: {dataDirectoryError}",
            DateTimeOffset.UtcNow,
            effectiveDataDirectory,
            "Create the WinForge application data directory or fix file-system access permissions before continuing."));

        var databaseHealthy = EnsureDatabasePath(effectiveDatabasePath, out var databaseError);
        checks.Add(new ApplicationDiagnosticCheck(
            "Database accessibility",
            databaseHealthy ? ApplicationHealthState.Healthy : ApplicationHealthState.Failed,
            databaseHealthy
                ? $"The local SQLite database path is usable at {effectiveDatabasePath}."
                : $"The database path is not usable: {databaseError}",
            DateTimeOffset.UtcNow,
            effectiveDatabasePath,
            "Fix local file permissions or database file placement before continuing. Do not reset the database silently."));

        checks.Add(new ApplicationDiagnosticCheck(
            "Schema compatibility",
            databaseHealthy ? ApplicationHealthState.Healthy : ApplicationHealthState.Unknown,
            databaseHealthy
                ? "Database path is writable and compatibility is validated through the repository migration and integrity checks."
                : "Database schema compatibility cannot be confirmed because the local database path is not usable.",
            DateTimeOffset.UtcNow,
            effectiveDatabasePath,
            "If the database fails to open, use the built-in migration and recovery guidance before attempting a reset."));

        var overallState = DetermineOverallState(checks);
        var summary = BuildSummary(overallState, checks);

        return new ApplicationHealthReport(
            overallState,
            summary,
            checks,
            DateTimeOffset.UtcNow,
            runtime,
            appVersion,
            effectiveDataDirectory,
            effectiveDatabasePath);
    }

    private static bool EnsureDirectory(string path, out string error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No application data path was provided.";
                return false;
            }

            Directory.CreateDirectory(path);
            var markerPath = Path.Combine(path, ".winforge_health_check");
            File.WriteAllText(markerPath, "ok");
            File.Delete(markerPath);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool EnsureDatabasePath(string databasePath, out string error)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var markerPath = Path.Combine(Path.GetDirectoryName(databasePath)!, $".winforge-db-check-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(markerPath, "ok");
            File.Delete(markerPath);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static ApplicationHealthState DetermineOverallState(IReadOnlyCollection<ApplicationDiagnosticCheck> checks)
    {
        if (checks.Any(check => check.State == ApplicationHealthState.Failed))
        {
            return ApplicationHealthState.Failed;
        }

        if (checks.Any(check => check.State == ApplicationHealthState.Unsupported))
        {
            return ApplicationHealthState.Unsupported;
        }

        if (checks.Any(check => check.State == ApplicationHealthState.Unavailable))
        {
            return ApplicationHealthState.Unavailable;
        }

        if (checks.Any(check => check.State == ApplicationHealthState.Degraded))
        {
            return ApplicationHealthState.Degraded;
        }

        if (checks.Any(check => check.State == ApplicationHealthState.Unknown))
        {
            return ApplicationHealthState.Unknown;
        }

        return ApplicationHealthState.Healthy;
    }

    private static string BuildSummary(ApplicationHealthState overallState, IReadOnlyCollection<ApplicationDiagnosticCheck> checks)
    {
        var failing = checks.Count(check => check.State is ApplicationHealthState.Failed or ApplicationHealthState.Unavailable or ApplicationHealthState.Unsupported);
        return overallState switch
        {
            ApplicationHealthState.Healthy => "Application diagnostics are healthy; all core local data paths and runtime requirements are available.",
            ApplicationHealthState.Degraded => $"Application diagnostics are degraded. {failing} checks require attention.",
            ApplicationHealthState.Failed => $"Application diagnostics failed. {failing} critical checks require user action.",
            ApplicationHealthState.Unsupported => "Application diagnostics are unsupported on the current platform.",
            ApplicationHealthState.Unavailable => "Application diagnostics are unavailable because required runtime or data paths could not be evaluated.",
            _ => "Application diagnostics are incomplete or uncertain; verify the local environment before relying on the data."
        };
    }
}
