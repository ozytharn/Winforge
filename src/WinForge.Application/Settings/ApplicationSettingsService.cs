using System.Text.Json;

namespace WinForge.Application.Settings;

public sealed record WinForgeSettings(
    string Theme,
    string StartupBehavior,
    int GuardianSampleRetentionDays,
    int MaxTelemetrySamples,
    bool EnableDiagnosticExports,
    bool ShowFirstRunGuidance,
    bool AllowBackgroundScans)
{
    public static WinForgeSettings Default { get; } = new(
        Theme: "System",
        StartupBehavior: "ShowOverview",
        GuardianSampleRetentionDays: 30,
        MaxTelemetrySamples: 500,
        EnableDiagnosticExports: true,
        ShowFirstRunGuidance: true,
        AllowBackgroundScans: false);

    public WinForgeSettings Validate()
    {
        var theme = string.IsNullOrWhiteSpace(Theme) ? "System" : Theme;
        var startup = string.IsNullOrWhiteSpace(StartupBehavior) ? "ShowOverview" : StartupBehavior;
        var retention = GuardianSampleRetentionDays is < 1 or > 365 ? 30 : GuardianSampleRetentionDays;
        var sampleLimit = MaxTelemetrySamples is < 10 or > 10_000 ? 500 : MaxTelemetrySamples;
        return new WinForgeSettings(
            Theme: theme,
            StartupBehavior: startup,
            GuardianSampleRetentionDays: retention,
            MaxTelemetrySamples: sampleLimit,
            EnableDiagnosticExports: EnableDiagnosticExports,
            ShowFirstRunGuidance: ShowFirstRunGuidance,
            AllowBackgroundScans: AllowBackgroundScans);
    }
}

public sealed class ApplicationSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public ApplicationSettingsService(string? settingsPath = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinForge",
            "settings.json"));

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public WinForgeSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            var defaults = WinForgeSettings.Default.Validate();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<WinForgeSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new InvalidOperationException("Saved settings file is empty or invalid.");
            }

            return settings.Validate();
        }
        catch (Exception)
        {
            var defaults = WinForgeSettings.Default.Validate();
            Save(defaults);
            return defaults;
        }
    }

    public void Save(WinForgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validated = settings.Validate();
        var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(validated, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
