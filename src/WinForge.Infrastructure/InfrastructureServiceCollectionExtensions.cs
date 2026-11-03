using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WinForge.Application.Inventory;
using WinForge.Application.DigitalTwin;
using WinForge.Application.Monitoring;
using WinForge.Application.Recovery;
using WinForge.Infrastructure.Collectors;
using WinForge.Infrastructure.Monitoring;
using WinForge.Infrastructure.Persistence;
using WinForge.Infrastructure.Recovery;

namespace WinForge.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWinForgeInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IInventoryCollector, OperatingSystemInventoryCollector>();
        services.AddSingleton<IInventoryCollector, ProcessorInventoryCollector>();
        services.AddSingleton<IInventoryCollector, MemoryModuleInventoryCollector>();
        services.AddSingleton<IInventoryCollector, PnpDeviceInventoryCollector>();
        services.AddSingleton<IInventoryCollector, SetupApiDeviceInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WmiHardwareInventoryCollector>();
        services.AddSingleton<IInventoryCollector, DriverPackageInventoryCollector>();
        services.AddSingleton<IInventoryCollector, StorageHealthInventoryCollector>();
        services.AddSingleton<IInventoryCollector, NvmeHealthInventoryCollector>();
        services.AddSingleton<IInventoryCollector, MonitorEdidInventoryCollector>();
        services.AddSingleton<IInventoryCollector, MonitorDisplayParametersCollector>();
        services.AddSingleton<IInventoryCollector, KeyboardInventoryCollector>();
        services.AddSingleton<IInventoryCollector, PointingDeviceInventoryCollector>();
        services.AddSingleton<IInventoryCollector, FirmwareInventoryCollector>();
        services.AddSingleton<IInventoryCollector, ThermalSensorInventoryCollector>();
        services.AddSingleton<IInventoryCollector, AudioEndpointInventoryCollector>();
        services.AddSingleton<IInventoryCollector, InstalledApplicationInventoryCollector>();
        services.AddSingleton<IInventoryCollector, StartupInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WindowsServicesInventoryCollector>();
        services.AddSingleton<IInventoryCollector, ProcessInventoryCollector>();
        services.AddSingleton<IInventoryCollector, ScheduledTaskInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WindowsUpdatesInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WindowsFeaturesInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WindowsBootConfigurationInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WindowsOperatingSystemDetailsInventoryCollector>();
        services.AddSingleton<IInventoryCollector, WindowsActivationInventoryCollector>();
        services.AddSingleton<IInventoryCollector, EnvironmentVariablesInventoryCollector>();
        services.AddSingleton<IInventoryCollector, RecoveryDiscoveryCollector>();
        services.AddSingleton<IGuardianTelemetryCollector, WindowsGuardianTelemetryCollector>();
        services.AddSingleton<IBackupInspector, WindowsBackupInspector>();
        services.AddSingleton<SqliteInventoryRepository>(provider =>
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinForge");
            var databasePath = Path.Combine(dataDirectory, "winforge.db");
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteInventoryRepository>>();
            return new SqliteInventoryRepository(databasePath, logger);
        });
        services.AddSingleton<IInventoryRepository>(provider =>
            provider.GetRequiredService<SqliteInventoryRepository>());
        services.AddSingleton<IDigitalTwinRepository>(provider =>
            provider.GetRequiredService<SqliteInventoryRepository>());
        services.AddSingleton<IBackupInspectionRepository>(provider =>
            provider.GetRequiredService<SqliteInventoryRepository>());
        services.AddSingleton<IGuardianTelemetryRepository>(provider =>
            provider.GetRequiredService<SqliteInventoryRepository>());

        return services;
    }
}
