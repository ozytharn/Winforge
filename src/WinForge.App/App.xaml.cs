using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinForge.Application.Actions;
using WinForge.Application.Inventory;
using WinForge.Application.DigitalTwin;
using WinForge.Application.Recovery;
using WinForge.Application.Monitoring;
using WinForge.App.ViewModels;
using WinForge.Infrastructure;
using WinForge.Infrastructure.Actions;

namespace WinForge.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices((_, services) =>
                {
                    services.AddWinForgeInfrastructure();
                    services.AddSingleton<IPassportScanService, PassportScanService>();
                    services.AddSingleton<DigitalTwinBuilder>();
                    services.AddSingleton<DigitalTwinGraph>();
                    services.AddSingleton<DigitalTwinChangeDetector>();
                    services.AddSingleton<DigitalTwinService>();
                    services.AddSingleton<RecoveryReadinessAssessor>();
                    services.AddSingleton<BackupInspectionService>();
                    services.AddSingleton<GuardianAssessment>();
                    services.AddSingleton<GuardianHistoryAnalyzer>();
                    services.AddSingleton<GuardianTelemetryService>();
                    services.AddSingleton<TrustedActionCatalog>();
                    services.AddSingleton<ActionBrokerPolicyEngine>();
                    services.AddSingleton<SimulatedSystemState>();
                    services.AddSingleton<IActionBrokerJournalStore, JsonFileActionBrokerJournalStore>();
                    services.AddSingleton<ActionBrokerService>();
                    services.AddSingleton<ActionBrokerViewModel>();
                    services.AddSingleton<PassportViewModel>();
                    services.AddSingleton<RecoveryViewModel>();
                    services.AddSingleton<BackupIntelligenceViewModel>();
                    services.AddSingleton<DigitalTwinViewModel>();
                    services.AddSingleton<GuardianViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(exception, "WinForge failed to start.");
            MessageBox.Show(
                $"WinForge could not start: {exception.Message}",
                "WinForge startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogError(exception, "WinForge host did not stop cleanly.");
            }
            finally
            {
                _host.Dispose();
            }
        }

        base.OnExit(e);
    }
}
