using System.Collections.Specialized;
using System.ComponentModel;
using WinForge.App.Commands;
using WinForge.Application.Monitoring;

namespace WinForge.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        PassportViewModel passport,
        RecoveryViewModel recovery,
        DigitalTwinViewModel digitalTwin,
        GuardianViewModel guardian,
        BackupIntelligenceViewModel backupIntelligence,
        ActionBrokerViewModel actionBroker)
    {
        Passport = passport;
        Recovery = recovery;
        DigitalTwin = digitalTwin;
        Guardian = guardian;
        BackupIntelligence = backupIntelligence;
        ActionBroker = actionBroker;

        NavigatePassportCommand = new RelayCommand(() => SelectedTabIndex = 1);
        NavigateRecoveryCommand = new RelayCommand(() => SelectedTabIndex = 2);
        NavigateBackupsCommand = new RelayCommand(() => SelectedTabIndex = 3);
        NavigateGuardianCommand = new RelayCommand(() => SelectedTabIndex = 4);
        NavigateDigitalTwinCommand = new RelayCommand(() => SelectedTabIndex = 5);
        NavigateActionBrokerCommand = new RelayCommand(() => SelectedTabIndex = 6);

        Passport.PropertyChanged += ChildPropertyChanged;
        Recovery.PropertyChanged += ChildPropertyChanged;
        DigitalTwin.PropertyChanged += ChildPropertyChanged;
        Guardian.PropertyChanged += ChildPropertyChanged;
        BackupIntelligence.PropertyChanged += ChildPropertyChanged;
        ActionBroker.PropertyChanged += ChildPropertyChanged;
        Guardian.ActiveAlerts.CollectionChanged += ChildCollectionChanged;
        Guardian.Findings.CollectionChanged += ChildCollectionChanged;
        BackupIntelligence.Candidates.CollectionChanged += ChildCollectionChanged;
    }

    public PassportViewModel Passport { get; }

    public RecoveryViewModel Recovery { get; }

    public DigitalTwinViewModel DigitalTwin { get; }

    public GuardianViewModel Guardian { get; }

    public BackupIntelligenceViewModel BackupIntelligence { get; }

    public ActionBrokerViewModel ActionBroker { get; }

    public RelayCommand NavigatePassportCommand { get; }

    public RelayCommand NavigateRecoveryCommand { get; }

    public RelayCommand NavigateBackupsCommand { get; }

    public RelayCommand NavigateGuardianCommand { get; }

    public RelayCommand NavigateDigitalTwinCommand { get; }

    public RelayCommand NavigateActionBrokerCommand { get; }

    private int _selectedTabIndex;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public string PassportSummary =>
        $"{Passport.LatestScanStatus}; last successful scan: {Passport.LastSuccessfulScanText}; " +
        $"{Passport.Components.Count:N0} retained component observations.";

    public string RecoverySummary =>
        $"{Recovery.AssessmentSummaryText} {Recovery.LastObservedText}";

    public string BackupSummary
    {
        get
        {
            var inspected = BackupIntelligence.Candidates
                .Where(candidate => candidate.LatestInspectionState != "Uninspected")
                .GroupBy(candidate => candidate.LatestInspectionState)
                .Select(group => $"{group.Key}: {group.Count():N0}");
            var inspectionSummary = string.Join(", ", inspected);
            return $"{BackupIntelligence.Candidates.Count:N0} discovered candidate(s); " +
                   (inspectionSummary.Length == 0 ? "none inspected yet. " : $"{inspectionSummary}. ") +
                   BackupIntelligence.StatusText;
        }
    }

    public string DigitalTwinSummary =>
        $"{DigitalTwin.StatusText} {DigitalTwin.FooterText}";

    public string ActionBrokerSummary =>
        $"{ActionBroker.StatusMessage} {ActionBroker.ExecutionStateText}";

    public string GuardianSummary
    {
        get
        {
            var trends = Guardian.HistoricalSummaries
                .Where(summary => summary.PreviousPeriodAverage != "Unavailable")
                .Take(5)
                .Select(summary =>
                    $"{summary.Metric}: latest {summary.Latest}; prior avg {summary.PreviousPeriodAverage}");
            var trendText = string.Join("; ", trends);
            return $"{Guardian.StatusText} {Guardian.CpuText}; {Guardian.MemoryText}; {Guardian.StorageText}; " +
                   $"{Guardian.NetworkText}; {Guardian.ProcessText}; {Guardian.GpuText}. " +
                   $"History: {Guardian.HistoricalSummaryText}" +
                   (trendText.Length == 0 ? string.Empty : $" Trends: {trendText}.");
        }
    }

    public string TimestampSummary =>
        $"Passport: {Passport.LastSuccessfulScanText} | Recovery: {Recovery.LastObservedText} | " +
        $"Guardian: {Guardian.LastSampleText} | Digital Twin: {LatestDigitalTwinSnapshotText} | " +
        $"Backup inspection: {LatestBackupInspectionText}";

    public string LatestDigitalTwinSnapshotText =>
        DigitalTwin.History.FirstOrDefault()?.CreatedAt.ToLocalTime().ToString("g") ?? "No snapshot";

    public string LatestBackupInspectionText =>
        BackupIntelligence.Candidates
            .Select(candidate => candidate.LatestInspectionAt)
            .Max()
            ?.ToLocalTime()
            .ToString("g") ?? "No inspection";

    public string WarningsSummary
    {
        get
        {
            var warnings = new List<string>();
            AddError(warnings, "Passport", Passport.ErrorMessage);
            AddError(warnings, "Recovery", Recovery.ErrorMessage);
            AddError(warnings, "Backup catalog", BackupIntelligence.ErrorMessage);
            AddError(warnings, "Guardian", Guardian.ErrorMessage);
            AddError(warnings, "Digital Twin", DigitalTwin.ErrorMessage);
            warnings.AddRange(Guardian.ActiveAlerts.Select(alert =>
                $"{alert.State}: {alert.Category} — {alert.Message} ({alert.Source}; observed {alert.ObservedAt.ToLocalTime():g})"));
            warnings.AddRange(Guardian.Findings
                .Where(finding => finding.State == GuardianFindingState.Unknown)
                .Select(finding => $"Unavailable: {finding.Category} — {finding.Message} ({finding.Source})"));
            return warnings.Count == 0
                ? "No current reported errors, active threshold alerts, or unknown Guardian assessments."
                : string.Join(Environment.NewLine, warnings);
        }
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshSummaries();

    private void ChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSummaries();

    private void RefreshSummaries()
    {
        OnPropertyChanged(nameof(PassportSummary));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(BackupSummary));
        OnPropertyChanged(nameof(DigitalTwinSummary));
        OnPropertyChanged(nameof(ActionBrokerSummary));
        OnPropertyChanged(nameof(GuardianSummary));
        OnPropertyChanged(nameof(TimestampSummary));
        OnPropertyChanged(nameof(WarningsSummary));
    }

    private static void AddError(ICollection<string> warnings, string name, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            warnings.Add($"{name}: {error}");
        }
    }
}
