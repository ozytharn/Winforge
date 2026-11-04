namespace WinForge.Application.Setup;

public sealed record FirstRunStep(
    string Title,
    string Description,
    string Detail);

public sealed class FirstRunExperienceService
{
    public const string FirstRunMarkerFileName = "first-run-seen.json";

    public IReadOnlyList<FirstRunStep> GetChecklist()
    {
        return new[]
        {
            new FirstRunStep(
                "Local-first by design",
                "WinForge reads the local Windows system state and stores evidence in your user profile.",
                "No privileged service is required and the app does not perform sweeping system remediation."),
            new FirstRunStep(
                "Read-only access",
                "Inventory, telemetry, health, and security observations are collected without altering Windows settings.",
                "The app may surface recommendations, but it does not silently modify drivers, services, or registry values."),
            new FirstRunStep(
                "Safe diagnostics",
                "If a scan is incomplete or unavailable, WinForge reports limitations rather than pretending the state is healthy.",
                "Missing data is marked as unavailable or unknown rather than resolved."),
            new FirstRunStep(
                "Data ownership",
                "Your app settings, logs, database, and export files stay under your user profile unless you explicitly move them.",
                "Installation and uninstall steps preserve user data unless you choose to remove it intentionally.")
        };
    }

    public bool ShouldShowWelcome(string? appDataDirectory = null)
    {
        var effectiveAppDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinForge")
            : Path.GetFullPath(appDataDirectory);

        Directory.CreateDirectory(effectiveAppDataDirectory);
        var markerPath = Path.Combine(effectiveAppDataDirectory, FirstRunMarkerFileName);
        return !File.Exists(markerPath);
    }

    public void MarkSeen(string? appDataDirectory = null)
    {
        var effectiveAppDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinForge")
            : Path.GetFullPath(appDataDirectory);

        Directory.CreateDirectory(effectiveAppDataDirectory);
        var markerPath = Path.Combine(effectiveAppDataDirectory, FirstRunMarkerFileName);
        File.WriteAllText(markerPath, $"{{\"seenAtUtc\":\"{DateTimeOffset.UtcNow:O}\"}}\n");
    }
}
