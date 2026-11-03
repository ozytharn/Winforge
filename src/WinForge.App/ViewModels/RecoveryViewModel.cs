using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using WinForge.App.Commands;
using WinForge.Application.Inventory;
using WinForge.Application.Recovery;
using WinForge.Domain.Inventory;

namespace WinForge.App.ViewModels;

public sealed class RecoveryViewModel : ViewModelBase
{
    private readonly IPassportScanService _scanService;
    private readonly ILogger<RecoveryViewModel> _logger;
    private readonly RecoveryReadinessAssessor _readinessAssessor;
    private CancellationTokenSource? _scanCancellation;
    private bool _isBusy;
    private bool _initialized;
    private string _statusText = "Recovery discovery has not been run.";
    private string _lastObservedText = "No recovery inventory observations are stored.";
    private string _warningText = "Discovery is read-only. A discovered restore point or backup is not proof that recovery will succeed.";
    private string _assessmentSummaryText = "Readiness has not been assessed.";
    private string _errorMessage = string.Empty;
    private string _footerText = "No recovery items discovered.";
    private PassportComponentViewModel? _selectedItem;

    public RecoveryViewModel(
        IPassportScanService scanService,
        RecoveryReadinessAssessor readinessAssessor,
        ILogger<RecoveryViewModel> logger)
    {
        _scanService = scanService;
        _readinessAssessor = readinessAssessor;
        _logger = logger;
        DiscoverCommand = new AsyncRelayCommand(
            DiscoverAsync,
            () => !IsBusy,
            HandleUnexpectedError);
        CancelCommand = new RelayCommand(
            () => _scanCancellation?.Cancel(),
            () => IsBusy);
    }

    public ObservableCollection<PassportComponentViewModel> Items { get; } = [];

    public AsyncRelayCommand DiscoverCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                DiscoverCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasItems => Items.Count > 0;

    public bool IsInventoryEmpty => Items.Count == 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastObservedText
    {
        get => _lastObservedText;
        private set => SetProperty(ref _lastObservedText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set => SetProperty(ref _warningText, value);
    }

    public string AssessmentSummaryText
    {
        get => _assessmentSummaryText;
        private set => SetProperty(ref _assessmentSummaryText, value);
    }

    public string AssessmentDetailsText { get; private set; } =
        "No recovery evidence is available for assessment.";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string FooterText
    {
        get => _footerText;
        private set => SetProperty(ref _footerText, value);
    }

    public PassportComponentViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(SelectedDetails));
            }
        }
    }

    public string SelectedDetails =>
        $"{SelectedItem?.DetailsText ?? "Select an item to inspect discovery source, status, availability, timestamp, and limitations."}{Environment.NewLine}{Environment.NewLine}READINESS ASSESSMENT{Environment.NewLine}{AssessmentDetailsText}";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            await _scanService.InitializeAsync(CancellationToken.None);
            await LoadSnapshotAsync();
            _initialized = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Recovery discovery could not load its local inventory.");
            ErrorMessage = $"Recovery discovery could not load its local inventory: {exception.Message}";
        }
    }

    private async Task DiscoverAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        _scanCancellation = new CancellationTokenSource();
        try
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            if (!_initialized)
            {
                return;
            }

            var scan = await _scanService.RunScanAsync(
                InventoryScanGroup.Recovery,
                progress: null,
                _scanCancellation.Token);
            await LoadSnapshotAsync();
            StatusText = scan.Status switch
            {
                InventoryScanStatus.Completed => $"Discovery completed ({scan.AttemptedCollectors} collector).",
                InventoryScanStatus.Partial => "Discovery completed with unavailable or unsupported sources.",
                InventoryScanStatus.Failed => "Discovery failed; prior recovery observations were retained.",
                InventoryScanStatus.Cancelled => "Discovery cancelled; completed data was retained.",
                _ => "Discovery status is unavailable."
            };
        }
        catch (OperationCanceledException) when (_scanCancellation.IsCancellationRequested)
        {
            StatusText = "Discovery cancelled before results were recorded.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Recovery discovery failed.");
            ErrorMessage = $"Recovery discovery failed: {exception.Message}";
            StatusText = "Discovery failed; existing recovery observations were retained.";
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            IsBusy = false;
        }
    }

    private async Task LoadSnapshotAsync()
    {
        var snapshot = await _scanService.GetSnapshotAsync(CancellationToken.None);
        var recoveryTypes = new HashSet<InventoryComponentType>
        {
            InventoryComponentType.RecoveryEnvironment,
            InventoryComponentType.RestorePoint,
            InventoryComponentType.BackupCatalogItem,
            InventoryComponentType.RecoveryPartition,
            InventoryComponentType.RecoveryVolume,
            InventoryComponentType.BitLockerVolume,
            InventoryComponentType.RecoveryService,
            InventoryComponentType.RecoveryEvent
        };
        Items.Clear();
        foreach (var component in snapshot.Components
                     .Where(component => recoveryTypes.Contains(component.Type))
                     .OrderBy(component => component.Category, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(component => component.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new PassportComponentViewModel(
                component,
                comparisonStatus: null,
                comparisonText: "Recovery history is retained; item absence is not interpreted as removal."));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsInventoryEmpty));
        OnPropertyChanged(nameof(SelectedDetails));
        FooterText = $"{Items.Count:N0} recovery discovery observations retained.";
        var latestObserved = snapshot.Components
            .Where(component => recoveryTypes.Contains(component.Type))
            .Select(component => (DateTimeOffset?)component.ObservedAt)
            .Max();
        LastObservedText = latestObserved is { } time
            ? $"Latest recovery item observed {time.ToLocalTime():g}"
            : "No recovery inventory observations are stored.";
        var assessment = _readinessAssessor.Assess(snapshot.Components.Where(
            component => recoveryTypes.Contains(component.Type)));
        AssessmentSummaryText = assessment.Summary;
        AssessmentDetailsText = string.Join(
            Environment.NewLine,
            assessment.Findings.Select(finding =>
                $"{finding.State} — {finding.Category}: {finding.Explanation} Recommendation: {finding.Recommendation} Evidence: {string.Join("; ", finding.Evidence)}"));
        OnPropertyChanged(nameof(AssessmentDetailsText));
        OnPropertyChanged(nameof(SelectedDetails));

        if (snapshot.ComparisonScan?.Scope == "Recovery discovery")
        {
            StatusText = $"Last recovery discovery: {snapshot.ComparisonScan.Status}.";
        }
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "An unexpected Recovery Center operation error occurred.");
        ErrorMessage = $"The operation failed: {exception.Message}";
    }
}
