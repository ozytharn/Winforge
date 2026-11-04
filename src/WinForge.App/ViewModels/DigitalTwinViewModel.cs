using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using WinForge.App.Commands;
using WinForge.Application.DigitalTwin;
using WinForge.Application.Inventory;
using WinForge.Domain.DigitalTwin;

namespace WinForge.App.ViewModels;

public sealed class DigitalTwinViewModel : ViewModelBase
{
    private const int EntityPageSize = 100;
    private const int MaximumRelationshipChangesDisplayed = 1_000;

    private readonly IPassportScanService _scanService;
    private readonly DigitalTwinService _digitalTwinService;
    private readonly ILogger<DigitalTwinViewModel> _logger;
    private DigitalTwinSnapshot? _currentSnapshot;
    private DigitalTwinEntity? _selectedEntity;
    private DigitalTwinSnapshotSummary? _selectedHistoryEntry;
    private DigitalTwinSnapshotSummary? _comparisonHistoryEntry;
    private string _selectedCategory = "All categories";
    private string _selectedRelationshipType = "All relationships";
    private string _searchText = string.Empty;
    private string _statusText = "Digital Twin snapshots have not been loaded.";
    private string _detailsText = "Create or open a snapshot to inspect entities and evidence-backed relationships.";
    private string _changeText = "Snapshot comparisons are not available yet.";
    private string _comparisonDetailsText = "Compare two snapshots to see evidence-backed changes.";
    private string _errorMessage = string.Empty;
    private string _footerText = "No Digital Twin snapshot loaded.";
    private bool _isBusy;
    private bool _inventoryAvailable;
    private bool _initialized;
    private int _pageIndex;

    public DigitalTwinViewModel(
        IPassportScanService scanService,
        DigitalTwinService digitalTwinService,
        ILogger<DigitalTwinViewModel> logger)
    {
        _scanService = scanService;
        _digitalTwinService = digitalTwinService;
        _logger = logger;
        EntityCategories = ["All categories", .. Enum.GetNames<DigitalTwinEntityType>()];
        RelationshipTypes = ["All relationships", .. Enum.GetNames<DigitalTwinRelationshipType>()];
        CreateSnapshotCommand = new AsyncRelayCommand(
            CreateSnapshotAsync,
            () => !IsBusy && _inventoryAvailable,
            HandleUnexpectedError);
        LoadSnapshotCommand = new AsyncRelayCommand(
            LoadSelectedSnapshotAsync,
            () => !IsBusy && SelectedHistoryEntry is not null,
            HandleUnexpectedError);
        CompareSnapshotsCommand = new AsyncRelayCommand(
            CompareSelectedSnapshotsAsync,
            () => !IsBusy && SelectedHistoryEntry is not null && ComparisonHistoryEntry is not null,
            HandleUnexpectedError);
        ShowDependenciesCommand = new RelayCommand(
            ShowDependencies,
            () => !IsBusy && _currentSnapshot is not null && SelectedEntity is not null);
        AnalyzeImpactCommand = new RelayCommand(
            AnalyzeImpact,
            () => !IsBusy && _currentSnapshot is not null && SelectedEntity is not null);
        PreviousPageCommand = new RelayCommand(
            () => ChangePage(_pageIndex - 1),
            () => !IsBusy && _pageIndex > 0);
        NextPageCommand = new RelayCommand(
            () => ChangePage(_pageIndex + 1),
            () => !IsBusy && (_pageIndex + 1) * EntityPageSize < FilteredEntityCount);
    }

    public ObservableCollection<DigitalTwinEntity> Entities { get; } = [];

    public ObservableCollection<DigitalTwinSnapshotSummary> History { get; } = [];

    public ObservableCollection<DigitalTwinRelationshipChange> RelationshipChanges { get; } = [];

    public IReadOnlyList<string> EntityCategories { get; }

    public IReadOnlyList<string> RelationshipTypes { get; }

    public AsyncRelayCommand CreateSnapshotCommand { get; }

    public AsyncRelayCommand LoadSnapshotCommand { get; }

    public AsyncRelayCommand CompareSnapshotsCommand { get; }

    public RelayCommand ShowDependenciesCommand { get; }

    public RelayCommand AnalyzeImpactCommand { get; }

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => Entities.Count == 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private IEnumerable<DigitalTwinEntity> MatchingEntities =>
        Entities.Where(entity =>
                (SelectedCategory == "All categories" ||
                 string.Equals(entity.Type.ToString(), SelectedCategory, StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(SearchText) ||
                 entity.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                 entity.IdentityKey.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                 entity.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entity => entity.Type)
            .ThenBy(entity => entity.DisplayName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<DigitalTwinEntity> FilteredEntities =>
        MatchingEntities.Skip(_pageIndex * EntityPageSize).Take(EntityPageSize);

    public int FilteredEntityCount => MatchingEntities.Count();

    public string PageStatusText
    {
        get
        {
            var count = FilteredEntityCount;
            var pageCount = Math.Max(1, (count + EntityPageSize - 1) / EntityPageSize);
            return $"Page {_pageIndex + 1} of {pageCount} — {count:N0} matching entities";
        }
    }

    public DigitalTwinSnapshotSummary? SelectedHistoryEntry
    {
        get => _selectedHistoryEntry;
        set
        {
            if (SetProperty(ref _selectedHistoryEntry, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public DigitalTwinSnapshotSummary? ComparisonHistoryEntry
    {
        get => _comparisonHistoryEntry;
        set
        {
            if (SetProperty(ref _comparisonHistoryEntry, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string SelectedRelationshipType
    {
        get => _selectedRelationshipType;
        set
        {
            if (SetProperty(ref _selectedRelationshipType, value))
            {
                OnPropertyChanged(nameof(FilteredRelationshipChanges));
            }
        }
    }

    public IEnumerable<DigitalTwinRelationshipChange> FilteredRelationshipChanges =>
        RelationshipChanges.Where(change =>
            SelectedRelationshipType == "All relationships" ||
            string.Equals(change.Relationship.Type.ToString(), SelectedRelationshipType, StringComparison.Ordinal));

    public DigitalTwinEntity? SelectedEntity
    {
        get => _selectedEntity;
        set
        {
            if (SetProperty(ref _selectedEntity, value))
            {
                UpdateSelectedDetails();
                UpdateCommandStates();
            }
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(FilteredEntities));
                ResetPage();
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
                OnPropertyChanged(nameof(FilteredEntities));
                ResetPage();
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

    public string ChangeText
    {
        get => _changeText;
        private set => SetProperty(ref _changeText, value);
    }

    public string ComparisonDetailsText
    {
        get => _comparisonDetailsText;
        private set => SetProperty(ref _comparisonDetailsText, value);
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

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            await _scanService.InitializeAsync(CancellationToken.None);
            var inventory = await _scanService.GetSnapshotAsync(CancellationToken.None);
            _inventoryAvailable = inventory.Components.Count > 0;
            await RefreshHistoryAsync();
            if (History.Count > 0)
            {
                SelectedHistoryEntry = History[0];
                await LoadSnapshotAsync(History[0].SnapshotId, CancellationToken.None);
            }

            _initialized = true;
            StatusText = _inventoryAvailable
                ? History.Count > 0
                    ? $"Loaded latest Digital Twin snapshot from {History[0].CreatedAt.ToLocalTime():g}."
                    : "Inventory is available. Create a Digital Twin snapshot to begin."
                : "Run a PC Passport scan before creating a Digital Twin snapshot.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Digital Twin initialization failed.");
            ErrorMessage = $"Digital Twin data could not be loaded: {exception.Message}";
            StatusText = "Digital Twin initialization failed.";
        }
        finally
        {
            UpdateCommandStates();
        }
    }

    private async Task CreateSnapshotAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var inventory = await _scanService.GetSnapshotAsync(CancellationToken.None);
            _inventoryAvailable = inventory.Components.Count > 0;
            if (!_inventoryAvailable)
            {
                StatusText = "No inventory observations are available. Run a PC Passport scan first.";
                return;
            }

            var created = await _digitalTwinService.CreateSnapshotAsync(inventory, CancellationToken.None);
            await RefreshHistoryAsync();
            SelectedHistoryEntry = History.FirstOrDefault(entry => entry.SnapshotId == created.SnapshotId);
            SetCurrentSnapshot(created);
            StatusText = $"Snapshot created from {created.Entities.Count:N0} entities and {created.Relationships.Count:N0} evidence-backed relationships.";
            FooterText = $"Created {created.CreatedAt.ToLocalTime():g}; checksum {created.Checksum}.";
            await CompareWithPreviousAsync(created.SnapshotId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Digital Twin snapshot creation failed.");
            ErrorMessage = $"Snapshot creation failed: {exception.Message}";
            StatusText = "Snapshot creation failed; existing snapshots were retained.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSelectedSnapshotAsync()
    {
        if (SelectedHistoryEntry is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await LoadSnapshotAsync(SelectedHistoryEntry.SnapshotId, CancellationToken.None);
            await CompareWithPreviousAsync(SelectedHistoryEntry.SnapshotId);
            StatusText = $"Loaded snapshot from {SelectedHistoryEntry.CreatedAt.ToLocalTime():g}.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Digital Twin snapshot could not be loaded.");
            ErrorMessage = $"Snapshot could not be loaded: {exception.Message}";
            StatusText = "Snapshot integrity validation or loading failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompareSelectedSnapshotsAsync()
    {
        if (SelectedHistoryEntry is null || ComparisonHistoryEntry is null ||
            SelectedHistoryEntry.SnapshotId == ComparisonHistoryEntry.SnapshotId)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var baseline = ComparisonHistoryEntry;
            var current = SelectedHistoryEntry;
            if (baseline.CreatedAt >= current.CreatedAt)
            {
                ErrorMessage = "Choose an older comparison snapshot and a newer current snapshot.";
                return;
            }

            var comparison = await _digitalTwinService.CompareDetailedAsync(
                baseline.SnapshotId,
                current.SnapshotId,
                CancellationToken.None);
            await DisplayComparisonAsync(comparison);
            StatusText = $"Compared snapshots from {baseline.CreatedAt.ToLocalTime():g} and {current.CreatedAt.ToLocalTime():g}.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Selected Digital Twin snapshots could not be compared.");
            ErrorMessage = $"Snapshot comparison failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var history = await _digitalTwinService.GetHistoryAsync(50, CancellationToken.None);
        History.Clear();
        foreach (var item in history)
        {
            History.Add(item);
        }
    }

    private async Task LoadSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _digitalTwinService.GetSnapshotAsync(snapshotId, cancellationToken)
            .ConfigureAwait(true)
            ?? throw new KeyNotFoundException($"Snapshot '{snapshotId}' no longer exists.");
        SetCurrentSnapshot(snapshot);
        FooterText = $"{snapshot.Entities.Count:N0} entities; {snapshot.Relationships.Count:N0} relationships; checksum verified.";
    }

    private void SetCurrentSnapshot(DigitalTwinSnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        Entities.Clear();
        foreach (var entity in snapshot.Entities)
        {
            Entities.Add(entity);
        }

        _pageIndex = 0;
        SelectedEntity = null;
        OnPropertyChanged(nameof(FilteredEntities));
        OnPropertyChanged(nameof(FilteredEntityCount));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(IsEmpty));
        UpdateCommandStates();
    }

    private async Task CompareWithPreviousAsync(Guid currentSnapshotId)
    {
        var index = History.ToList().FindIndex(entry => entry.SnapshotId == currentSnapshotId);
        if (index < 0 || index + 1 >= History.Count)
        {
            ChangeText = "No previous Digital Twin snapshot is available for comparison.";
            return;
        }

        var comparison = await _digitalTwinService.CompareDetailedAsync(
            History[index + 1].SnapshotId,
            currentSnapshotId,
            CancellationToken.None);
        await DisplayComparisonAsync(comparison);
    }

    private async Task DisplayComparisonAsync(DigitalTwinComparison comparison)
    {
        var display = await Task.Run(() => CreateComparisonDisplay(comparison));
        ChangeText = display.Summary;

        RelationshipChanges.Clear();
        foreach (var relationshipChange in display.RelationshipChanges)
        {
            RelationshipChanges.Add(relationshipChange);
        }

        OnPropertyChanged(nameof(FilteredRelationshipChanges));
        ComparisonDetailsText = display.EntityDetails;
    }

    private static ComparisonDisplay CreateComparisonDisplay(DigitalTwinComparison comparison)
    {
        var changes = comparison.EntityChanges;
        var added = changes.Count(change => change.Type == DigitalTwinChangeType.Added);
        var changed = changes.Count(change => change.Type == DigitalTwinChangeType.Changed);
        var potentiallyRemoved = changes.Count(change => change.Type == DigitalTwinChangeType.PotentiallyRemoved);
        var notObserved = changes.Count(change => change.Type == DigitalTwinChangeType.NotObserved);
        var persistent = changes.Count(change => change.PersistentAcrossSnapshots);
        var relationshipAdded = comparison.RelationshipChanges.Count(change =>
            change.Type == DigitalTwinRelationshipChangeType.Added);
        var relationshipChanged = comparison.RelationshipChanges.Count(change =>
            change.Type == DigitalTwinRelationshipChangeType.Changed);
        var relationshipNotObserved = comparison.RelationshipChanges.Count(change =>
            change.Type == DigitalTwinRelationshipChangeType.NotObserved);
        var relationshipRemoved = comparison.RelationshipChanges.Count(change =>
            change.Type == DigitalTwinRelationshipChangeType.PotentiallyRemoved);
        var summary =
            $"Entities: {added:N0} added, {changed:N0} changed, {potentiallyRemoved:N0} potentially removed, {notObserved:N0} not observed " +
            $"({persistent:N0} changes confirmed by a later snapshot). Relationships: {relationshipAdded:N0} added, " +
            $"{relationshipChanged:N0} changed, {relationshipRemoved:N0} potentially removed, {relationshipNotObserved:N0} not observed." +
            (comparison.RelationshipChanges.Count > MaximumRelationshipChangesDisplayed
                ? $" Relationship detail list is capped at {MaximumRelationshipChangesDisplayed:N0} rows; counts include all compared relationships."
                : string.Empty);

        var details = new StringBuilder();
        foreach (var change in changes
                     .Where(change => change.Type != DigitalTwinChangeType.Unchanged)
                     .Take(30))
        {
            details.AppendLine($"{change.Type}: {change.Entity.DisplayName} ({change.Entity.Type})")
                .AppendLine($"  {change.Explanation}")
                .AppendLine($"  Source: {change.Entity.Source}; observed: {change.Entity.LastObservedAt.ToLocalTime():g}; confidence: {change.Entity.Confidence:P0}");
        }

        var entityDetails = details.Length == 0
            ? "No entity changes were detected between these snapshots."
            : details.ToString();
        return new ComparisonDisplay(
            summary,
            comparison.RelationshipChanges.Take(MaximumRelationshipChangesDisplayed).ToArray(),
            entityDetails);
    }

    private void ShowDependencies()
    {
        if (_currentSnapshot is null || SelectedEntity is null)
        {
            return;
        }

        var result = _digitalTwinService.FindDependencies(_currentSnapshot, SelectedEntity.Id);
        var dependencies = result.Entities.Where(entity =>
            !string.Equals(entity.Id, SelectedEntity.Id, StringComparison.OrdinalIgnoreCase));
        DetailsText = FormatDetails(SelectedEntity, dependencies, result.Relationships);
        if (result.WasTruncated)
        {
            DetailsText += $"{Environment.NewLine}{Environment.NewLine}Traversal limit reached; results are incomplete.";
        }
    }

    private void AnalyzeImpact()
    {
        if (_currentSnapshot is null || SelectedEntity is null)
        {
            return;
        }

        var impact = _digitalTwinService.AnalyzeImpact(_currentSnapshot, SelectedEntity.Id, 3, 50, 150);
        var text = new StringBuilder()
            .AppendLine($"Impact assessment for {SelectedEntity.DisplayName} ({SelectedEntity.Type})")
            .AppendLine($"Summary: {impact.Summary}")
            .AppendLine();

        if (impact.DirectlyAffected.Count > 0)
        {
            text.AppendLine("Directly affected:");
            foreach (var record in impact.DirectlyAffected)
            {
                text.AppendLine(
                    $"- {record.Entity.Type}: {record.Entity.DisplayName} ({record.Entity.Id}) [{record.Confidence:P0}; {record.RelationshipType}] {record.Reason}");
            }

            text.AppendLine();
        }
        else
        {
            text.AppendLine("Directly affected: none within the currently observed relationship graph.");
            text.AppendLine();
        }

        if (impact.IndirectlyAffected.Count > 0)
        {
            text.AppendLine("Indirectly affected:");
            foreach (var record in impact.IndirectlyAffected)
            {
                text.AppendLine(
                    $"- {record.Entity.Type}: {record.Entity.DisplayName} ({record.Entity.Id}) [{record.Confidence:P0}; {record.RelationshipType}; depth {record.Distance}] {record.Reason}");
            }
        }
        else
        {
            text.AppendLine("Indirectly affected: none in the traversed graph.");
        }

        if (impact.WasTruncated)
        {
            text.AppendLine();
            text.AppendLine("Impact traversal was capped; the results are directional and informational, not proof of causation.");
        }

        DetailsText = text.ToString().TrimEnd();
    }

    private void UpdateSelectedDetails()
    {
        if (SelectedEntity is null || _currentSnapshot is null)
        {
            DetailsText = "Select an entity to inspect source, availability, confidence, metadata, and relationship evidence.";
            return;
        }

        var relationships = _currentSnapshot.Relationships
            .Where(edge => edge.SourceId.Equals(SelectedEntity.Id, StringComparison.OrdinalIgnoreCase) ||
                           edge.TargetId.Equals(SelectedEntity.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        DetailsText = FormatDetails(SelectedEntity, [], relationships);
    }

    private static string FormatDetails(
        DigitalTwinEntity entity,
        IEnumerable<DigitalTwinEntity> dependencies,
        IEnumerable<DigitalTwinRelationship> relationships)
    {
        var text = new StringBuilder()
            .AppendLine($"{entity.Type}: {entity.DisplayName}")
            .AppendLine($"Identity: {entity.IdentityKey}")
            .AppendLine($"Source: {entity.Source}")
            .AppendLine($"Last observed: {entity.LastObservedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}")
            .AppendLine($"Availability: {entity.Availability}")
            .AppendLine($"Confidence: {entity.Confidence:P0}");
        foreach (var pair in entity.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"{pair.Key}: {pair.Value ?? entity.Availability.ToString()}");
        }

        var dependencyList = dependencies.ToArray();
        if (dependencyList.Length > 0)
        {
            text.AppendLine().AppendLine("Indirect/direct dependencies:");
            foreach (var dependency in dependencyList)
            {
                text.AppendLine($"- {dependency.Type}: {dependency.DisplayName}");
            }
        }

        var relationshipList = relationships.ToArray();
        text.AppendLine().AppendLine($"Relationship evidence ({relationshipList.Length}):");
        foreach (var relationship in relationshipList)
        {
            var direction = relationship.SourceId.Equals(entity.Id, StringComparison.OrdinalIgnoreCase)
                ? $"→ {relationship.Type} → {relationship.TargetType}:{relationship.TargetIdentity}"
                : $"← {relationship.SourceType}:{relationship.SourceIdentity} {relationship.Type} →";
            text.AppendLine($"{direction} [{relationship.Confidence:P0}; {relationship.EvidenceSource}] {relationship.Evidence}");
        }

        return text.ToString().TrimEnd();
    }

    private void UpdateCommandStates()
    {
        CreateSnapshotCommand.NotifyCanExecuteChanged();
        LoadSnapshotCommand.NotifyCanExecuteChanged();
        CompareSnapshotsCommand.NotifyCanExecuteChanged();
        ShowDependenciesCommand.NotifyCanExecuteChanged();
        AnalyzeImpactCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void ResetPage()
    {
        _pageIndex = 0;
        OnPropertyChanged(nameof(FilteredEntities));
        OnPropertyChanged(nameof(FilteredEntityCount));
        OnPropertyChanged(nameof(PageStatusText));
        UpdateCommandStates();
    }

    private void ChangePage(int pageIndex)
    {
        var maximumPage = Math.Max(0, (FilteredEntityCount - 1) / EntityPageSize);
        _pageIndex = Math.Clamp(pageIndex, 0, maximumPage);
        SelectedEntity = null;
        OnPropertyChanged(nameof(FilteredEntities));
        OnPropertyChanged(nameof(PageStatusText));
        UpdateCommandStates();
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "An unexpected Digital Twin operation failed.");
        ErrorMessage = $"The operation failed: {exception.Message}";
    }

    private sealed record ComparisonDisplay(
        string Summary,
        IReadOnlyList<DigitalTwinRelationshipChange> RelationshipChanges,
        string EntityDetails);
}
