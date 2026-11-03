using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Extensions.Logging;
using WinForge.App.Commands;
using WinForge.Application.Recovery;
using WinForge.Domain.Recovery;

namespace WinForge.App.ViewModels;

public sealed class BackupIntelligenceViewModel : ViewModelBase
{
    private readonly BackupInspectionService _service;
    private readonly ILogger<BackupIntelligenceViewModel> _logger;
    private CancellationTokenSource? _inspectionCancellation;
    private BackupCandidateViewModel? _selectedCandidate;
    private BackupInspectionSummary? _selectedInspection;
    private string _selectedProvider = "All providers";
    private string _selectedState = "All states";
    private string _statusText = "Load the Recovery discovery catalog to inspect supported backup metadata.";
    private string _detailsText = "Select a discovered candidate to review its inspection history and limitations.";
    private string _comparisonText = "No inspection comparison has been requested.";
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private bool _isInspecting;

    public BackupIntelligenceViewModel(
        BackupInspectionService service,
        ILogger<BackupIntelligenceViewModel> logger)
    {
        _service = service;
        _logger = logger;
        ProviderFilters = ["All providers", nameof(BackupProvider.WindowsImageBackup), nameof(BackupProvider.FileHistory)];
        StateFilters = ["All states", "Uninspected", .. Enum.GetNames<BackupInspectionState>()];
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync, () => !IsBusy, HandleUnexpectedError);
        LoadHistoryCommand = new AsyncRelayCommand(
            LoadSelectedCandidateHistoryAsync,
            () => !IsBusy && SelectedCandidate is not null,
            HandleUnexpectedError);
        InspectCommand = new AsyncRelayCommand(
            InspectSelectedCandidateAsync,
            () => !IsBusy && SelectedCandidate?.Candidate.Location is not null &&
                  string.Equals(SelectedCandidate.Candidate.Availability, "Available", StringComparison.OrdinalIgnoreCase),
            HandleUnexpectedError);
        CancelCommand = new RelayCommand(
            () => _inspectionCancellation?.Cancel(),
            () => IsInspecting);
        LoadInspectionCommand = new AsyncRelayCommand(
            LoadSelectedInspectionAsync,
            () => !IsBusy && SelectedInspection is not null,
            HandleUnexpectedError);
        CompareCommand = new AsyncRelayCommand(
            CompareLatestAsync,
            () => !IsBusy && SelectedCandidate is not null,
            HandleUnexpectedError);
    }

    public ObservableCollection<BackupCandidateViewModel> Candidates { get; } = [];

    public ObservableCollection<BackupInspectionSummary> InspectionHistory { get; } = [];

    public IReadOnlyList<string> ProviderFilters { get; }

    public IReadOnlyList<string> StateFilters { get; }

    public AsyncRelayCommand RefreshCatalogCommand { get; }

    public AsyncRelayCommand LoadHistoryCommand { get; }

    public AsyncRelayCommand InspectCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand LoadInspectionCommand { get; }

    public AsyncRelayCommand CompareCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool IsInspecting
    {
        get => _isInspecting;
        private set
        {
            if (SetProperty(ref _isInspecting, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool HasCandidates => Candidates.Count > 0;

    public bool IsEmpty => !FilteredCandidates.Any();

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public IEnumerable<BackupCandidateViewModel> FilteredCandidates =>
        Candidates.Where(candidate =>
            (_selectedProvider == "All providers" ||
             string.Equals(candidate.Candidate.Provider.ToString(), _selectedProvider, StringComparison.Ordinal)) &&
            (_selectedState == "All states" ||
             _selectedState == "Uninspected" && candidate.LatestInspectionState == "Uninspected" ||
             string.Equals(candidate.LatestInspectionState, _selectedState, StringComparison.Ordinal)));

    public BackupCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value))
            {
                InspectionHistory.Clear();
                SelectedInspection = null;
                DetailsText = value is null
                    ? "Select a discovered candidate to review its inspection history and limitations."
                    : BuildCandidateDetails(value.Candidate);
                ComparisonText = "Select Compare to compare the latest result with the prior inspection.";
                OnPropertyChanged(nameof(IsEmpty));
                UpdateCommandStates();
            }
        }
    }

    public BackupInspectionSummary? SelectedInspection
    {
        get => _selectedInspection;
        set
        {
            if (SetProperty(ref _selectedInspection, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(FilteredCandidates));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string SelectedState
    {
        get => _selectedState;
        set
        {
            if (SetProperty(ref _selectedState, value))
            {
                OnPropertyChanged(nameof(FilteredCandidates));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailsText
    {
        get => _detailsText;
        private set => SetProperty(ref _detailsText, value);
    }

    public string ComparisonText
    {
        get => _comparisonText;
        private set => SetProperty(ref _comparisonText, value);
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

    public async Task InitializeAsync() => await RefreshCatalogAsync();

    private async Task RefreshCatalogAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var selectedIdentity = SelectedCandidate?.Candidate.Identity;
            var candidates = await _service.GetCandidatesAsync(CancellationToken.None);
            Candidates.Clear();
            foreach (var candidate in candidates)
            {
                var history = await _service.GetHistoryAsync(candidate.Identity, 1, CancellationToken.None);
                var latest = history.FirstOrDefault();
                Candidates.Add(new BackupCandidateViewModel(
                    candidate,
                    latest?.State.ToString() ?? "Uninspected",
                    latest?.CompletedAt));
            }

            SelectedCandidate = selectedIdentity is null
                ? null
                : Candidates.FirstOrDefault(candidate => candidate.Candidate.Identity == selectedIdentity);
            OnPropertyChanged(nameof(FilteredCandidates));
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(IsEmpty));
            StatusText = Candidates.Count == 0
                ? "No WindowsImageBackup or File History candidates are present in the latest Recovery discovery observations."
                : $"{Candidates.Count:N0} bounded backup candidate(s) loaded from Recovery discovery.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backup candidate catalog could not be loaded.");
            ErrorMessage = $"Backup candidate catalog could not be loaded: {exception.Message}";
            StatusText = "Catalog refresh failed; no candidate data was changed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSelectedCandidateHistoryAsync()
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var history = await _service.GetHistoryAsync(
                SelectedCandidate.Candidate.Identity,
                100,
                CancellationToken.None);
            InspectionHistory.Clear();
            foreach (var entry in history)
            {
                InspectionHistory.Add(entry);
            }

            SelectedInspection = InspectionHistory.FirstOrDefault();
            if (SelectedInspection is not null)
            {
                await LoadInspectionAsync(SelectedInspection.InspectionId, CancellationToken.None);
            }
            else
            {
                DetailsText = BuildCandidateDetails(SelectedCandidate.Candidate) +
                              $"{Environment.NewLine}{Environment.NewLine}No inspection history is stored.";
            }

            StatusText = $"Loaded {InspectionHistory.Count:N0} historical inspection result(s).";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backup inspection history could not be loaded.");
            ErrorMessage = $"Inspection history could not be loaded: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InspectSelectedCandidateAsync()
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        IsBusy = true;
        IsInspecting = true;
        ErrorMessage = string.Empty;
        _inspectionCancellation = new CancellationTokenSource();
        try
        {
            var result = await _service.InspectAsync(
                SelectedCandidate.Candidate,
                _inspectionCancellation.Token);
            StatusText = $"{result.State}: {result.Summary}";
            await RefreshCandidateHistoryAsync();
        }
        catch (OperationCanceledException) when (_inspectionCancellation.IsCancellationRequested)
        {
            StatusText = "Inspection cancelled. Its incomplete result was retained in history.";
            await RefreshCandidateHistoryAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backup inspection failed.");
            ErrorMessage = $"Backup inspection failed: {exception.Message}";
            StatusText = "Inspection failed; the candidate and previous history were preserved.";
        }
        finally
        {
            _inspectionCancellation.Dispose();
            _inspectionCancellation = null;
            IsInspecting = false;
            IsBusy = false;
        }
    }

    private async Task RefreshCandidateHistoryAsync()
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        var history = await _service.GetHistoryAsync(
            SelectedCandidate.Candidate.Identity,
            100,
            CancellationToken.None);
        InspectionHistory.Clear();
        foreach (var entry in history)
        {
            InspectionHistory.Add(entry);
        }

        SelectedInspection = InspectionHistory.FirstOrDefault();
        if (SelectedInspection is not null)
        {
            await LoadInspectionAsync(SelectedInspection.InspectionId, CancellationToken.None);
        }

        var selectedIdentity = SelectedCandidate.Candidate.Identity;
        var index = Candidates.ToList().FindIndex(candidate => candidate.Candidate.Identity == selectedIdentity);
        if (index >= 0)
        {
            var latest = InspectionHistory.FirstOrDefault();
            Candidates[index].UpdateLatestInspection(
                latest?.State.ToString() ?? "Uninspected",
                latest?.CompletedAt);
            OnPropertyChanged(nameof(FilteredCandidates));
        }
    }

    private async Task LoadSelectedInspectionAsync()
    {
        if (SelectedInspection is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await LoadInspectionAsync(SelectedInspection.InspectionId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backup inspection detail could not be loaded.");
            ErrorMessage = $"Inspection detail could not be loaded: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadInspectionAsync(Guid inspectionId, CancellationToken cancellationToken)
    {
        var result = await _service.GetInspectionAsync(inspectionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Inspection '{inspectionId}' was not found.");
        var text = new StringBuilder()
            .AppendLine($"{result.Provider}: {result.State}")
            .AppendLine($"Location: {result.Location ?? "Unavailable"}")
            .AppendLine($"Inspection time: {result.CompletedAt.ToLocalTime():g}")
            .AppendLine($"Result: {result.Summary}")
            .AppendLine($"Recognized structure: {result.RecognizedStructure}")
            .AppendLine($"Structure groups: {result.DiscoveredStructureGroups?.ToString() ?? "Not reliably available"}")
            .AppendLine($"Metadata bytes read: {result.MetadataBytesRead?.ToString("N0") ?? "None"}")
            .AppendLine($"Observed container length: {result.BackupContainerBytes?.ToString("N0") ?? "Not measured"}")
            .AppendLine()
            .AppendLine("CHECKS");
        foreach (var check in result.Checks)
        {
            text.AppendLine($"- {check.State} — {check.Name}: {check.Explanation} (source: {check.Source}; {check.CheckedAt.ToLocalTime():g})");
        }

        text.AppendLine().AppendLine("LIMITATIONS");
        foreach (var limitation in result.Limitations)
        {
            text.AppendLine($"- {limitation}");
        }

        DetailsText = text.ToString().TrimEnd();
    }

    private async Task CompareLatestAsync()
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var comparison = await _service.CompareLatestAsync(
                SelectedCandidate.Candidate.Identity,
                CancellationToken.None);
            ComparisonText = comparison is null
                ? "At least two inspections are required for comparison."
                : $"Latest vs previous ({comparison.Previous.CompletedAt.ToLocalTime():g}): " +
                  $"state {(comparison.StateChanged ? "changed" : "unchanged")}; " +
                  $"summary {(comparison.SummaryChanged ? "changed" : "unchanged")}. " +
                  $"Previous: {comparison.Previous.State} — {comparison.Previous.Summary}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backup inspection results could not be compared.");
            ErrorMessage = $"Inspection comparison failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildCandidateDetails(BackupCandidate candidate) =>
        $"{candidate.DisplayName}{Environment.NewLine}" +
        $"Provider: {candidate.Provider}{Environment.NewLine}" +
        $"Location: {candidate.Location ?? "Unavailable"}{Environment.NewLine}" +
        $"Volume: {candidate.Volume ?? "Not reported"}{Environment.NewLine}" +
        $"Last discovery observation: {candidate.LastDiscoveredAt.ToLocalTime():g}{Environment.NewLine}" +
        $"Availability: {candidate.Availability}{Environment.NewLine}" +
        $"Source: {candidate.Source}{Environment.NewLine}" +
        $"Discovery limitation: {candidate.Limitation ?? "No additional limitation reported."}{Environment.NewLine}" +
        "Candidate discovery does not validate backup contents or restoreability.";

    private void UpdateCommandStates()
    {
        RefreshCatalogCommand.NotifyCanExecuteChanged();
        LoadHistoryCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadInspectionCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "An unexpected backup intelligence operation failed.");
        ErrorMessage = $"The operation failed: {exception.Message}";
    }
}
