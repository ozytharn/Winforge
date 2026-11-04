using System.Text.Json;

namespace WinForge.Application.Setup;

public enum InstallationLifecycleState
{
    Healthy,
    NeedsInitialization,
    MigrationRequired,
    Failed
}

public sealed record ApplicationDataLifecycleResult(
    string InstallRoot,
    string AppDataDirectory,
    string DatabasePath,
    string SettingsPath,
    bool IsFirstRun,
    bool ExistingUserDataFound,
    bool SettingsPreserved,
    bool MigrationRequired,
    InstallationLifecycleState State,
    string Summary);

public sealed class ApplicationDataLifecycleService
{
    public const string DefaultAppDataFolderName = "WinForge";

    public ApplicationDataLifecycleResult EnsureInitialized(
        string? installRoot = null,
        string? appDataDirectory = null,
        string? databasePath = null,
        string? settingsPath = null)
    {
        var effectiveInstallRoot = string.IsNullOrWhiteSpace(installRoot)
            ? AppDomain.CurrentDomain.BaseDirectory
            : Path.GetFullPath(installRoot);

        var effectiveAppDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultAppDataFolderName)
            : Path.GetFullPath(appDataDirectory);

        var effectiveDatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(effectiveAppDataDirectory, "winforge.db")
            : Path.GetFullPath(databasePath);

        var effectiveSettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(effectiveAppDataDirectory, "settings.json")
            : Path.GetFullPath(settingsPath);

        Directory.CreateDirectory(effectiveAppDataDirectory);

        var databaseExists = File.Exists(effectiveDatabasePath);
        var settingsExists = File.Exists(effectiveSettingsPath);
        var firstRun = !databaseExists && !settingsExists;
        var migrationRequired = databaseExists && !File.Exists(Path.Combine(effectiveAppDataDirectory, "migration-state.json"));

        var migrationStatePath = Path.Combine(effectiveAppDataDirectory, "migration-state.json");
        var migrationState = new
        {
            version = "0.9.0",
            databasePath = effectiveDatabasePath,
            settingsPath = effectiveSettingsPath,
            state = databaseExists ? "database-present" : "new-install",
            updatedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            File.WriteAllText(migrationStatePath, JsonSerializer.Serialize(migrationState, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            return new ApplicationDataLifecycleResult(
                effectiveInstallRoot,
                effectiveAppDataDirectory,
                effectiveDatabasePath,
                effectiveSettingsPath,
                firstRun,
                databaseExists || settingsExists,
                settingsExists,
                migrationRequired,
                InstallationLifecycleState.Failed,
                "WinForge could not persist migration metadata. User data remains untouched and installation should not continue without review.");
        }

        if (databaseExists && settingsExists)
        {
            return new ApplicationDataLifecycleResult(
                effectiveInstallRoot,
                effectiveAppDataDirectory,
                effectiveDatabasePath,
                effectiveSettingsPath,
                false,
                true,
                true,
                false,
                InstallationLifecycleState.Healthy,
                "Existing WinForge user data was detected and preserved. No reset or destructive migration was performed.");
        }

        if (firstRun)
        {
            if (!File.Exists(effectiveDatabasePath))
            {
                File.WriteAllText(effectiveDatabasePath, string.Empty);
            }

            if (!File.Exists(effectiveSettingsPath))
            {
                File.WriteAllText(effectiveSettingsPath, "{\n  \"theme\": \"System\",\n  \"startupBehavior\": \"ShowOverview\"\n}\n");
            }

            return new ApplicationDataLifecycleResult(
                effectiveInstallRoot,
                effectiveAppDataDirectory,
                effectiveDatabasePath,
                effectiveSettingsPath,
                true,
                false,
                false,
                false,
                InstallationLifecycleState.NeedsInitialization,
                "This is the first run. A clean WinForge data layout was created without deleting any existing user data.");
        }

        if (migrationRequired)
        {
            return new ApplicationDataLifecycleResult(
                effectiveInstallRoot,
                effectiveAppDataDirectory,
                effectiveDatabasePath,
                effectiveSettingsPath,
                false,
                true,
                settingsExists,
                true,
                InstallationLifecycleState.MigrationRequired,
                "Existing user data was detected but migration metadata was missing; resolution requires review rather than silent reset.");
        }

        return new ApplicationDataLifecycleResult(
            effectiveInstallRoot,
            effectiveAppDataDirectory,
            effectiveDatabasePath,
            effectiveSettingsPath,
            false,
            true,
            settingsExists,
            false,
            InstallationLifecycleState.Healthy,
            "WinForge data layout is available and user data remains preserved.");
    }

    public bool CreateFallbackBackup(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultAppDataFolderName,
            "backups");

        Directory.CreateDirectory(backupDirectory);

        var backupPath = Path.Combine(
            backupDirectory,
            $"{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak");

        File.Copy(path, backupPath, overwrite: false);
        return File.Exists(backupPath);
    }
}
