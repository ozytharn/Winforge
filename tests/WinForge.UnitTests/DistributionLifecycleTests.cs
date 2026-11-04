using WinForge.Application.Setup;

namespace WinForge.UnitTests;

public sealed class DistributionLifecycleTests
{
    [Fact]
    public void ApplicationDataLifecycleService_creates_first_run_layout_without_data_loss()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), "WinForgeLifecycleFirstRun", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(appDataDirectory, "winforge.db");
        var settingsPath = Path.Combine(appDataDirectory, "settings.json");

        var result = new ApplicationDataLifecycleService().EnsureInitialized(
            installRoot: appDataDirectory,
            appDataDirectory: appDataDirectory,
            databasePath: databasePath,
            settingsPath: settingsPath);

        Assert.Equal(InstallationLifecycleState.NeedsInitialization, result.State);
        Assert.True(result.IsFirstRun);
        Assert.True(File.Exists(databasePath));
        Assert.True(File.Exists(settingsPath));
        Assert.False(result.SettingsPreserved);
    }

    [Fact]
    public void ApplicationDataLifecycleService_preserves_existing_user_data()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), "WinForgeLifecycleExistingData", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataDirectory);

        var databasePath = Path.Combine(appDataDirectory, "winforge.db");
        var settingsPath = Path.Combine(appDataDirectory, "settings.json");
        File.WriteAllText(databasePath, "existing database");
        File.WriteAllText(settingsPath, "{\"theme\":\"Dark\"}");

        var result = new ApplicationDataLifecycleService().EnsureInitialized(
            installRoot: appDataDirectory,
            appDataDirectory: appDataDirectory,
            databasePath: databasePath,
            settingsPath: settingsPath);

        Assert.Equal(InstallationLifecycleState.Healthy, result.State);
        Assert.True(result.ExistingUserDataFound);
        Assert.True(result.SettingsPreserved);
        Assert.Equal("existing database", File.ReadAllText(databasePath));
    }

    [Fact]
    public void ApplicationDataLifecycleService_flags_missing_migration_metadata()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), "WinForgeLifecycleMigration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataDirectory);

        var databasePath = Path.Combine(appDataDirectory, "winforge.db");
        var settingsPath = Path.Combine(appDataDirectory, "settings.json");
        File.WriteAllText(databasePath, "database file exists");

        var result = new ApplicationDataLifecycleService().EnsureInitialized(
            installRoot: appDataDirectory,
            appDataDirectory: appDataDirectory,
            databasePath: databasePath,
            settingsPath: settingsPath);

        Assert.Equal(InstallationLifecycleState.MigrationRequired, result.State);
        Assert.True(File.Exists(Path.Combine(appDataDirectory, "migration-state.json")));
    }

    [Fact]
    public void FirstRunExperienceService_marks_seen_state_once()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), "WinForgeFirstRun", Guid.NewGuid().ToString("N"));
        var service = new FirstRunExperienceService();

        Assert.True(service.ShouldShowWelcome(appDataDirectory));
        service.MarkSeen(appDataDirectory);
        Assert.False(service.ShouldShowWelcome(appDataDirectory));
    }
}
