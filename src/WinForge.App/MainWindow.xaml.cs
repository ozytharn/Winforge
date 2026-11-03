using System.Windows;
using System.ComponentModel;
using WinForge.App.ViewModels;

namespace WinForge.App;

public partial class MainWindow : Window
{
    private readonly PassportViewModel _passportViewModel;
    private readonly RecoveryViewModel _recoveryViewModel;
    private readonly DigitalTwinViewModel _digitalTwinViewModel;
    private readonly GuardianViewModel _guardianViewModel;
    private readonly BackupIntelligenceViewModel _backupIntelligenceViewModel;
    private readonly ActionBrokerViewModel _actionBrokerViewModel;
    private bool _shutdownStarted;
    private bool _shutdownReady;

    public MainWindow(
        PassportViewModel passportViewModel,
        RecoveryViewModel recoveryViewModel,
        DigitalTwinViewModel digitalTwinViewModel,
        GuardianViewModel guardianViewModel,
        BackupIntelligenceViewModel backupIntelligenceViewModel,
        ActionBrokerViewModel actionBrokerViewModel)
    {
        InitializeComponent();
        _passportViewModel = passportViewModel;
        _recoveryViewModel = recoveryViewModel;
        _digitalTwinViewModel = digitalTwinViewModel;
        _guardianViewModel = guardianViewModel;
        _backupIntelligenceViewModel = backupIntelligenceViewModel;
        _actionBrokerViewModel = actionBrokerViewModel;
        DataContext = new MainWindowViewModel(
            passportViewModel,
            recoveryViewModel,
            digitalTwinViewModel,
            guardianViewModel,
            backupIntelligenceViewModel,
            actionBrokerViewModel);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _passportViewModel.InitializeAsync();
        await _recoveryViewModel.InitializeAsync();
        await _backupIntelligenceViewModel.InitializeAsync();
        await _digitalTwinViewModel.InitializeAsync();
        await _guardianViewModel.InitializeAsync();
        await _actionBrokerViewModel.InitializeAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownReady)
        {
            e.Cancel = true;
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            await _actionBrokerViewModel.StopAsync();
            _guardianViewModel.Stop();
            _shutdownReady = true;
            Close();
            return;
        }

        base.OnClosing(e);
    }
}
