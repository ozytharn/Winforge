using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using WinForge.App.Commands;
using WinForge.Application.Diagnostics;
using WinForge.Application.Identity;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.App.ViewModels;

public sealed class PassportViewModel : ViewModelBase
{
    private readonly IPassportScanService _scanService;
    private readonly SoftwareIdentityAssessmentService _identityService;
    private readonly SystemHealthAssessmentService _systemHealthAssessmentService;
    private readonly SecurityPrivacyAssessmentService _securityPrivacyAssessmentService;
    private readonly ILogger<PassportViewModel> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private CancellationTokenSource? _scanCancellation;
    private bool _initialized;
    private bool _isBusy;
    private string _latestScanStatus = "Not scanned";
    private string _latestScanTime = "No scan has been run.";
    private string _lastSuccessfulScanText = "No successful scan has been recorded.";
    private string _coverageText = "Coverage is unknown until the first scan.";
    private string _progressMessage = "No scan is running.";
    private string _errorMessage = string.Empty;
    private string _footerText = "No inventory records.";
    private string _softwareIdentitySummary = "Software identity analysis is not available yet.";
    private string _systemHealthSummary = "System health analysis is not available yet.";
    private string _securityPrivacySummary = "Security and privacy assessment is not available yet.";
    private string _selectedCategory = "All categories";
    private InventoryScanGroup _selectedScanGroup = InventoryScanGroup.All;
    private string _searchText = string.Empty;
    private PassportComponentViewModel? _selectedComponent;
    private SecurityPrivacyFinding? _selectedSecurityFinding;

    public PassportViewModel(
        IPassportScanService scanService,
        SoftwareIdentityAssessmentService identityService,
        SystemHealthAssessmentService systemHealthAssessmentService,
        SecurityPrivacyAssessmentService securityPrivacyAssessmentService,
        ILogger<PassportViewModel> logger)
    {
        _scanService = scanService;
        _identityService = identityService;
        _systemHealthAssessmentService = systemHealthAssessmentService;
        _securityPrivacyAssessmentService = securityPrivacyAssessmentService;
        _logger = logger;
        ScanCommand = new AsyncRelayCommand(
            RunScanAsync,
            () => !IsBusy,
            HandleUnexpectedCommandError);
        CancelScanCommand = new RelayCommand(
            () => _scanCancellation?.Cancel(),
            () => IsBusy);
    }

    public ObservableCollection<PassportComponentViewModel> Components { get; } = [];

    public ObservableCollection<PassportComponentViewModel> FilteredComponents { get; } = [];

    public ObservableCollection<SecurityPrivacyFinding> SecurityPrivacyFindings { get; } = [];

    public ObservableCollection<string> Categories { get; } = ["All categories"];

    public ObservableCollection<InventoryScanGroup> ScanGroups { get; } =
        [
            InventoryScanGroup.All,
            InventoryScanGroup.Hardware,
            InventoryScanGroup.Software,
            InventoryScanGroup.System,
            InventoryScanGroup.Recovery
        ];

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanCommand.NotifyCanExecuteChanged();
                CancelScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInventoryEmpty => FilteredComponents.Count == 0;

    public string EmptyStateTitle => Components.Count == 0
        ? "No inventory has been collected yet"
        : "No components in this category";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string LatestScanStatus
    {
        get => _latestScanStatus;
        private set => SetProperty(ref _latestScanStatus, value);
    }

    public string LatestScanTime
    {
        get => _latestScanTime;
        private set => SetProperty(ref _latestScanTime, value);
    }

    public string LastSuccessfulScanText
    {
        get => _lastSuccessfulScanText;
        private set => SetProperty(ref _lastSuccessfulScanText, value);
    }

    public string CoverageText
    {
        get => _coverageText;
        private set => SetProperty(ref _coverageText, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set => SetProperty(ref _progressMessage, value);
    }

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

    public string SoftwareIdentitySummary
    {
        get => _softwareIdentitySummary;
        private set => SetProperty(ref _softwareIdentitySummary, value);
    }

    public string SystemHealthSummary
    {
        get => _systemHealthSummary;
        private set => SetProperty(ref _systemHealthSummary, value);
    }

    public string SecurityPrivacySummary
    {
        get => _securityPrivacySummary;
        private set => SetProperty(ref _securityPrivacySummary, value);
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                RefreshFilteredComponents();
            }
        }
    }

    public InventoryScanGroup SelectedScanGroup
    {
        get => _selectedScanGroup;
        set
        {
            if (SetProperty(ref _selectedScanGroup, value))
            {
                RefreshFilteredComponents();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilteredComponents();
            }
        }
    }

    public PassportComponentViewModel? SelectedComponent
    {
        get => _selectedComponent;
        set
        {
            if (SetProperty(ref _selectedComponent, value))
            {
                OnPropertyChanged(nameof(SelectedDetails));
            }
        }
    }

    public string SelectedDetails => SelectedComponent?.DetailsText ?? "Select a component to inspect its collected fields.";

    public SecurityPrivacyFinding? SelectedSecurityFinding
    {
        get => _selectedSecurityFinding;
        set
        {
            if (SetProperty(ref _selectedSecurityFinding, value))
            {
                OnPropertyChanged(nameof(SelectedSecurityFindingDetails));
            }
        }
    }

    public string SelectedSecurityFindingDetails =>
        SelectedSecurityFinding is null
            ? "Select a security or privacy finding to inspect its evidence, limitations, and suggested next steps."
            : SecurityPrivacyAssessmentService.BuildFindingDetails(SelectedSecurityFinding);

    public async Task InitializeAsync()
    {
        await _initializationLock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                await _scanService.InitializeAsync(CancellationToken.None);
                await RefreshSnapshotAsync();
                ErrorMessage = string.Empty;
                _initialized = true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "PC Passport could not load its local inventory.");
                ErrorMessage = $"PC Passport could not load its local inventory: {exception.Message}";
                LatestScanStatus = "Database unavailable";
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task RunScanAsync()
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
                ProgressMessage = "The database is unavailable; correct the issue and try again.";
                return;
            }

            var progress = new Progress<ScanProgress>(update =>
            {
                ProgressMessage = update.Message;
                CoverageText = $"{update.CompletedCollectors} of {update.TotalCollectors} collectors attempted";
            });

            var scan = await _scanService.RunScanAsync(
                SelectedScanGroup,
                progress,
                _scanCancellation.Token);
            await RefreshSnapshotAsync();
            ProgressMessage = scan.Status switch
            {
                InventoryScanStatus.Completed => "Scan completed.",
                InventoryScanStatus.Partial => "Scan completed with limited coverage.",
                InventoryScanStatus.Failed => "Scan failed; prior inventory was retained.",
                InventoryScanStatus.Cancelled => "Scan cancelled; completed results were retained.",
                _ => "Scan state is unavailable."
            };
        }
        catch (OperationCanceledException) when (_scanCancellation.IsCancellationRequested)
        {
            ProgressMessage = "Scan cancelled before results could be recorded.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PC Passport scan failed.");
            ErrorMessage = $"The scan could not be completed: {exception.Message}";
            ProgressMessage = "Scan failed; existing inventory was not cleared.";
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            IsBusy = false;
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        var snapshot = await _scanService.GetSnapshotAsync(CancellationToken.None);
        LastSuccessfulScanText = snapshot.ComparisonScan is { } successfulScan
            ? $"{successfulScan.Status} — {successfulScan.CompletedAt?.ToLocalTime().ToString("g") ?? successfulScan.StartedAt.ToLocalTime().ToString("g")} ({successfulScan.Scope})"
            : "No successful scan has been recorded.";
        var currentScanComponents = ToIdentityMap(snapshot.ComparisonComponents);
        var previousScanComponents = ToIdentityMap(snapshot.PreviousComparisonComponents);
        Components.Clear();
        Categories.Clear();
        Categories.Add("All categories");
        foreach (var component in snapshot.Components)
        {
            var key = GetIdentity(component);
            var wasObserved = currentScanComponents.ContainsKey(key);
            InventoryComparisonStatus? comparisonStatus = null;
            var comparisonText = "No successful scan available for comparison.";
            if (snapshot.ComparisonScan is not null)
            {
                if (!IsInComparisonScope(component, snapshot.ComparisonScan.Scope))
                {
                    comparisonText = "No comparable scan exists for this inventory group.";
                }
                else if (!wasObserved)
                {
                    comparisonStatus = InventoryComparisonStatus.NotObserved;
                    comparisonText = "Not observed in the latest successful scan; retained from history.";
                }
                else if (snapshot.PreviousComparisonScan is null)
                {
                    comparisonText = "No previous successful scan is available.";
                }
                else if (!previousScanComponents.TryGetValue(key, out var previous))
                {
                    comparisonStatus = InventoryComparisonStatus.New;
                    comparisonText = "First observed in the latest successful scan.";
                }
                else if (HasSameObservedData(component, previous))
                {
                    comparisonStatus = InventoryComparisonStatus.Unchanged;
                    comparisonText = "Unchanged since the previous successful scan.";
                }
                else
                {
                    comparisonStatus = InventoryComparisonStatus.Changed;
                    comparisonText = "Observed details changed since the previous successful scan.";
                }
            }

            Components.Add(new PassportComponentViewModel(component, comparisonStatus, comparisonText));
        }

        foreach (var category in Components
                     .Select(component => component.Category)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(category => category, StringComparer.OrdinalIgnoreCase))
        {
            Categories.Add(category);
        }

        if (!Categories.Contains(SelectedCategory, StringComparer.OrdinalIgnoreCase))
        {
            SelectedCategory = "All categories";
        }
        else
        {
            RefreshFilteredComponents();
        }

        OnPropertyChanged(nameof(IsInventoryEmpty));
        OnPropertyChanged(nameof(EmptyStateTitle));
        FooterText = $"{FilteredComponents.Count:N0} of {Components.Count:N0} component observations shown.";
        SoftwareIdentitySummary = _identityService.BuildSummary(_identityService.Analyze(snapshot));
        var healthAssessment = _systemHealthAssessmentService.Analyze(snapshot.Components);
        var securityAssessment = _securityPrivacyAssessmentService.Analyze(snapshot.Components);
        SystemHealthSummary = $"{_systemHealthAssessmentService.BuildSummary(healthAssessment)}; Security: {securityAssessment.Summary}";
        SecurityPrivacySummary = securityAssessment.Summary;
        SecurityPrivacyFindings.Clear();
        foreach (var finding in securityAssessment.Findings.OrderBy(finding => finding.Category, StringComparer.OrdinalIgnoreCase))
        {
            SecurityPrivacyFindings.Add(finding);
        }
        if (SecurityPrivacyFindings.Count > 0 && SelectedSecurityFinding is null)
        {
            SelectedSecurityFinding = SecurityPrivacyFindings[0];
        }

        if (snapshot.LatestScan is not { } latest)
        {
            LatestScanStatus = "Not scanned";
            LatestScanTime = "No scan has been run.";
            CoverageText = "Coverage is unknown until the first scan.";
            return;
        }

        LatestScanStatus = latest.Status.ToString();
        LatestScanTime = latest.CompletedAt?.ToLocalTime().ToString("g")
            ?? $"Started {latest.StartedAt.ToLocalTime():g}; completion was not recorded.";
        CoverageText =
            $"{latest.AttemptedCollectors} of {latest.TotalCollectors} collectors attempted; " +
            $"{snapshot.Components.Count:N0} known components retained.";

        if (latest.Issues.Count > 0)
        {
            ErrorMessage = string.Join(
                Environment.NewLine,
                latest.Issues.Select(issue => $"{issue.CollectorId}: {issue.Message}"));
        }
    }

    private static Dictionary<(InventoryComponentType Type, string Identity), InventoryComponent> ToIdentityMap(
        IReadOnlyList<InventoryComponent> components) =>
        components
            .GroupBy(GetIdentity)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(component => component.ObservedAt).First());

    private static (InventoryComponentType Type, string Identity) GetIdentity(InventoryComponent component) =>
        (component.Type, component.IdentityKey.Trim().ToUpperInvariant());

    private static bool HasSameObservedData(InventoryComponent current, InventoryComponent previous) =>
        string.Equals(current.Category, previous.Category, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.DisplayName, previous.DisplayName, StringComparison.Ordinal) &&
        current.Details.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(previous.Details.OrderBy(pair => pair.Key, StringComparer.Ordinal)) &&
        current.FieldAvailability.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(previous.FieldAvailability.OrderBy(pair => pair.Key, StringComparer.Ordinal));

    private void RefreshFilteredComponents()
    {
        var search = SearchText.Trim();
        FilteredComponents.Clear();
        foreach (var component in Components.Where(component =>
                     IsInSelectedGroup(component) &&
                     (SelectedCategory.Equals("All categories", StringComparison.OrdinalIgnoreCase) ||
                      component.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase)) &&
                     MatchesSearch(component, search)))
        {
            FilteredComponents.Add(component);
        }

        if (SelectedComponent is not null && !FilteredComponents.Contains(SelectedComponent))
        {
            SelectedComponent = null;
        }

        OnPropertyChanged(nameof(IsInventoryEmpty));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(SelectedDetails));
        FooterText = $"{FilteredComponents.Count:N0} of {Components.Count:N0} component observations shown.";
    }

    private bool IsInSelectedGroup(PassportComponentViewModel component) =>
        SelectedScanGroup == InventoryScanGroup.All ||
        (Enum.TryParse<InventoryComponentType>(component.Type, out var componentType) &&
         GetGroup(componentType) == SelectedScanGroup);

    private static bool IsInComparisonScope(InventoryComponent component, string comparisonScope) =>
        comparisonScope == "All PC Passport inventory" ||
        GetScope(GetGroup(component.Type)).Equals(comparisonScope, StringComparison.Ordinal);

    private static InventoryScanGroup GetGroup(InventoryComponentType componentType) =>
        componentType switch
        {
            InventoryComponentType.InstalledApplication or InventoryComponentType.StoreApplication or
                InventoryComponentType.Service or InventoryComponentType.Process or
                InventoryComponentType.StartupEntry or InventoryComponentType.ScheduledTask =>
                InventoryScanGroup.Software,
            InventoryComponentType.WindowsUpdate or InventoryComponentType.WindowsFeature or
                InventoryComponentType.EnvironmentVariable or InventoryComponentType.WindowsSystemInfo =>
                InventoryScanGroup.System,
            InventoryComponentType.RecoveryEnvironment or InventoryComponentType.RestorePoint or
                InventoryComponentType.BackupCatalogItem or InventoryComponentType.RecoveryPartition or
                InventoryComponentType.RecoveryVolume or InventoryComponentType.BitLockerVolume or
                InventoryComponentType.RecoveryService or InventoryComponentType.RecoveryEvent =>
                InventoryScanGroup.Recovery,
            _ => InventoryScanGroup.Hardware
        };

    private static string GetScope(InventoryScanGroup group) =>
        group switch
        {
            InventoryScanGroup.Hardware => "Hardware and driver inventory",
            InventoryScanGroup.Software => "Software, services, processes, startup, and scheduled-task inventory",
            InventoryScanGroup.System => "Windows system information, updates, features, and configuration",
            InventoryScanGroup.Recovery => "Recovery discovery",
            _ => "All PC Passport inventory"
        };

    private static bool MatchesSearch(PassportComponentViewModel component, string search) =>
        string.IsNullOrEmpty(search) ||
        component.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        component.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        component.Type.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
        component.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        component.Details.Values.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    private void HandleUnexpectedCommandError(Exception exception)
    {
        _logger.LogError(exception, "An unexpected PC Passport command error occurred.");
        ErrorMessage = $"The operation failed: {exception.Message}";
    }
}
