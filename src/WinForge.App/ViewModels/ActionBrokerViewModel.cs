using System.Collections.ObjectModel;
using System.Text.Json;
using WinForge.App.Commands;
using WinForge.Application.Actions;
using WinForge.Domain.Actions;

namespace WinForge.App.ViewModels;

public sealed record ActionBrokerCatalogRow(
    string ActionId,
    string Name,
    string Description,
    string RiskLevel,
    string Category,
    string SupportedTargets,
    bool IsAvailable,
    string AvailabilityReason);

public sealed record ActionBrokerJournalRow(
    string Timestamp,
    string ActionId,
    string EventType,
    string State,
    string Summary,
    string Details,
    string RequestId);

public sealed class ActionBrokerViewModel : ViewModelBase
{
    private readonly ActionBrokerService _service;
    private bool _isBusy;
    private bool _isExecuting;
    private bool _isSettingActionDefaults;
    private string _selectedActionId = string.Empty;
    private string _selectedTargetType = "preference";
    private string _targetIdentity = "theme";
    private string _targetDisplayName = "Theme";
    private string _parameterJson = "{\"name\":\"theme\",\"value\":\"dark\"}";
    private string _userIntent = "Update the theme in simulation.";
    private string _correlationId = $"broker-{Guid.NewGuid():N}";
    private string _policyVersion = "1.0.0";
    private string _statusMessage = "Ready to prepare a simulation request.";
    private string _validationError = string.Empty;
    private string _previewText = "Preview not generated yet.";
    private string _approvalText = "No approval has been created yet.";
    private string _executionText = "No execution has been run yet.";
    private string _currentPreviewDigest = string.Empty;
    private ActionCatalogEntry? _selectedAction;
    private ActionRequest? _currentRequest;
    private ActionApproval? _currentApproval;
    private ActionExecutionRecord? _lastExecution;
    private ActionBrokerJournalRow? _selectedJournalEntry;
    private CancellationTokenSource? _executionCancellation;

    public ActionBrokerViewModel(ActionBrokerService service)
    {
        _service = service;
        Actions = new ObservableCollection<ActionBrokerCatalogRow>();
        Journal = new ObservableCollection<ActionBrokerJournalRow>();

        GeneratePreviewCommand = new AsyncRelayCommand(GeneratePreviewAsync, () => !IsBusy && !IsExecuting && SelectedAction is not null, HandleUnexpectedError);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => !IsBusy && !IsExecuting && CurrentRequest is not null && CurrentApproval is null, HandleUnexpectedError);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => !IsBusy && !IsExecuting && CurrentRequest is not null && CurrentApproval is not null, HandleUnexpectedError);
        CancelCommand = new RelayCommand(CancelExecution, () => IsExecuting);
        RefreshJournalCommand = new AsyncRelayCommand(RefreshJournalAsync, () => !IsBusy && !IsExecuting, HandleUnexpectedError);

        LoadActions();
    }

    public ObservableCollection<ActionBrokerCatalogRow> Actions { get; }

    public ObservableCollection<ActionBrokerJournalRow> Journal { get; }

    public AsyncRelayCommand GeneratePreviewCommand { get; }

    public AsyncRelayCommand ApproveCommand { get; }

    public AsyncRelayCommand ExecuteCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand RefreshJournalCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                GeneratePreviewCommand.NotifyCanExecuteChanged();
                ApproveCommand.NotifyCanExecuteChanged();
                ExecuteCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                RefreshJournalCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
                GeneratePreviewCommand.NotifyCanExecuteChanged();
                ApproveCommand.NotifyCanExecuteChanged();
                ExecuteCommand.NotifyCanExecuteChanged();
                RefreshJournalCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ActionCatalogEntry? SelectedAction
    {
        get => _selectedAction;
        private set
        {
            if (SetProperty(ref _selectedAction, value))
            {
                OnPropertyChanged(nameof(SelectedActionId));
                OnPropertyChanged(nameof(SelectedActionName));
                OnPropertyChanged(nameof(SelectedActionDescription));
                OnPropertyChanged(nameof(AvailableTargetTypes));
            }
        }
    }

    public string SelectedActionId
    {
        get => _selectedActionId;
        set
        {
            if (SetProperty(ref _selectedActionId, value))
            {
                var action = _service.AvailableActions.FirstOrDefault(item => string.Equals(item.ActionId, value, StringComparison.OrdinalIgnoreCase));
                SelectedAction = action;
                if (action is not null)
                {
                    SetActionDefaults(action);
                }

                InvalidatePreview();
            }
        }
    }

    public string SelectedActionName => SelectedAction?.Name ?? "No action selected";

    public string SelectedActionDescription => SelectedAction?.Description ?? "Choose an action to see the description.";

    public string SelectedTargetType
    {
        get => _selectedTargetType;
        set => SetInputProperty(ref _selectedTargetType, value);
    }

    public string TargetIdentity
    {
        get => _targetIdentity;
        set => SetInputProperty(ref _targetIdentity, value);
    }

    public string TargetDisplayName
    {
        get => _targetDisplayName;
        set => SetInputProperty(ref _targetDisplayName, value);
    }

    public string ParameterJson
    {
        get => _parameterJson;
        set => SetInputProperty(ref _parameterJson, value);
    }

    public string UserIntent
    {
        get => _userIntent;
        set => SetInputProperty(ref _userIntent, value);
    }

    public string CorrelationId
    {
        get => _correlationId;
        set => SetInputProperty(ref _correlationId, value);
    }

    public string PolicyVersion
    {
        get => _policyVersion;
        set => SetInputProperty(ref _policyVersion, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public string ApprovalText
    {
        get => _approvalText;
        private set => SetProperty(ref _approvalText, value);
    }

    public string ExecutionText
    {
        get => _executionText;
        private set => SetProperty(ref _executionText, value);
    }

    public string CurrentPreviewDigest
    {
        get => _currentPreviewDigest;
        private set => SetProperty(ref _currentPreviewDigest, value);
    }

    public ActionRequest? CurrentRequest
    {
        get => _currentRequest;
        private set
        {
            if (SetProperty(ref _currentRequest, value))
            {
                ApproveCommand.NotifyCanExecuteChanged();
                ExecuteCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CurrentRequestSummary));
            }
        }
    }

    public ActionApproval? CurrentApproval
    {
        get => _currentApproval;
        private set
        {
            if (SetProperty(ref _currentApproval, value))
            {
                ExecuteCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CurrentApprovalSummary));
            }
        }
    }

    public ActionExecutionRecord? LastExecution
    {
        get => _lastExecution;
        private set
        {
            if (SetProperty(ref _lastExecution, value))
            {
                OnPropertyChanged(nameof(ExecutionStateText));
            }
        }
    }

    public ActionBrokerJournalRow? SelectedJournalEntry
    {
        get => _selectedJournalEntry;
        set
        {
            if (SetProperty(ref _selectedJournalEntry, value))
            {
                OnPropertyChanged(nameof(JournalDetailsText));
            }
        }
    }

    public string JournalDetailsText => SelectedJournalEntry is null
        ? "Select a journal event to inspect its details."
        : $"Request: {SelectedJournalEntry.RequestId}{Environment.NewLine}" +
          $"Event: {SelectedJournalEntry.EventType} / {SelectedJournalEntry.State}{Environment.NewLine}" +
          $"{SelectedJournalEntry.Summary}{Environment.NewLine}{SelectedJournalEntry.Details}";

    public string CurrentRequestSummary => CurrentRequest is null ? "No request created" : $"{CurrentRequest.ActionName} → {CurrentRequest.Target.Identity}";

    public string CurrentApprovalSummary => CurrentApproval is null ? "No approval created" : $"Approved by {CurrentApproval.ApproverId}; expires {CurrentApproval.ExpiresAtUtc:O}";

    public string ExecutionStateText => LastExecution is null ? "Not executed" : LastExecution.State.ToString();

    public IReadOnlyList<string> AvailableTargetTypes => SelectedAction?.ActionId switch
    {
        "user-preferences.update" => ["preference"],
        "startup-entry.toggle.current-user" => ["startup-entry"],
        "settings.apply.allowlisted" => ["setting"],
        "workspace.cleanup.transient-artifacts" => ["workspace"],
        "service.inspect" => ["service"],
        "driver.inspect" => ["driver"],
        "read-only.inspect.backup-catalog" => ["backup-catalog"],
        "read-only.inspect.recovery" => ["recovery"],
        _ => Array.Empty<string>()
    };

    public Task InitializeAsync() => RefreshJournalAsync();

    public async Task GeneratePreviewAsync()
    {
        IsBusy = true;
        try
        {
            ValidationError = string.Empty;
            var action = SelectedAction ?? throw new InvalidOperationException("Select a simulation action before generating a preview.");
            if (string.IsNullOrWhiteSpace(TargetIdentity))
            {
                ValidationError = "Target identity is required.";
                StatusMessage = "Validation failed before preview generation.";
                return;
            }

            if (string.IsNullOrWhiteSpace(ParameterJson) || !IsValidJson(ParameterJson))
            {
                ValidationError = "Parameter JSON must be valid JSON.";
                StatusMessage = "Validation failed before preview generation.";
                return;
            }

            if (string.IsNullOrWhiteSpace(UserIntent))
            {
                ValidationError = "User intent is required.";
                StatusMessage = "Validation failed before preview generation.";
                return;
            }

            var request = await _service.CreateRequestAsync(
                action.ActionId,
                SelectedTargetType,
                TargetIdentity,
                TargetDisplayName,
                ParameterJson,
                UserIntent,
                CorrelationId,
                PolicyVersion,
                CancellationToken.None);

            CurrentRequest = request;
            CurrentApproval = null;
            CurrentPreviewDigest = request.PreviewDigest;
            PreviewText = BuildPreviewText(request);
            StatusMessage = "Preview generated and bound to the exact request payload.";
            ValidationError = string.Empty;
        }
        catch (Exception exception)
        {
            ValidationError = exception.Message;
            StatusMessage = "Preview generation failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApproveAsync()
    {
        IsBusy = true;
        try
        {
            var request = CurrentRequest ?? throw new InvalidOperationException("Generate a preview before approving the action.");
            CurrentApproval = await _service.CreateApprovalAsync(request, "current-user", CancellationToken.None);
            ApprovalText = $"Approved: {CurrentApproval.ApprovalId}; expires {CurrentApproval.ExpiresAtUtc:O}; one-time use; preview digest {CurrentApproval.PreviewDigest[..12]}";
            StatusMessage = "Approval was created and bound to the exact preview.";
        }
        catch (Exception exception)
        {
            ValidationError = exception.Message;
            StatusMessage = "Approval failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExecuteAsync()
    {
        IsBusy = true;
        IsExecuting = true;
        _executionCancellation?.Dispose();
        _executionCancellation = new CancellationTokenSource();
        try
        {
            var request = CurrentRequest ?? throw new InvalidOperationException("Generate a preview before execution.");
            var approval = CurrentApproval ?? throw new InvalidOperationException("Approve the request before execution.");
            var execution = await _service.ExecuteAsync(request, approval, _executionCancellation.Token);
            LastExecution = execution;
            CurrentApproval = null;
            ExecutionText =
                $"{execution.State} (simulation-only): {execution.Summary}; simulated verification passed={execution.VerificationPassed}; " +
                $"simulated rollback applied={execution.RollbackApplied} (this does not prove real rollback capability).";
            StatusMessage = execution.State == ActionExecutionState.Succeeded
                ? "Simulation completed successfully; simulated verification passed and the journal recorded the result. No Windows settings were changed."
                : $"Execution ended in {execution.State}.";
            try
            {
                await LoadJournalCoreAsync();
            }
            catch (Exception exception)
            {
                ValidationError = $"The simulation result is {execution.State}, but the journal could not be refreshed: {exception.Message}";
                StatusMessage = $"Simulation result: {execution.State}. Journal refresh failed.";
            }
        }
        catch (OperationCanceledException)
        {
            CurrentApproval = null;
            StatusMessage = "Execution was cancelled before it entered the simulation executor.";
            ExecutionText = "Cancelled before execution; no state transition was made.";
        }
        catch (Exception exception)
        {
            ValidationError = exception.Message;
            StatusMessage = "Execution failed.";
        }
        finally
        {
            IsExecuting = false;
            _executionCancellation?.Dispose();
            _executionCancellation = null;
            IsBusy = false;
        }
    }

    public async Task StopAsync()
    {
        CancelExecution();
        await _service.WaitForIdleAsync().ConfigureAwait(true);
    }

    public async Task RefreshJournalAsync()
    {
        IsBusy = true;
        try
        {
            await LoadJournalCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ValidationError = exception.Message;
            StatusMessage = "Loading the execution journal failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadJournalCoreAsync()
    {
        var entries = await _service.ReadJournalAsync(CancellationToken.None).ConfigureAwait(true);
        Journal.Clear();
        foreach (var entry in entries.OrderByDescending(item => item.TimestampUtc).Take(30))
        {
            Journal.Add(new ActionBrokerJournalRow(
                entry.TimestampUtc.ToLocalTime().ToString("g"),
                entry.ActionId,
                entry.EventType,
                entry.State,
                entry.Summary,
                entry.Details,
                entry.RequestId));
        }

        SelectedJournalEntry = Journal.FirstOrDefault();
        if (Journal.Count == 0)
        {
            StatusMessage = "No broker journal entries were found yet.";
        }
    }

    private void LoadActions()
    {
        Actions.Clear();
        foreach (var action in _service.AvailableActions)
        {
            Actions.Add(new ActionBrokerCatalogRow(
                action.ActionId,
                action.Name,
                action.Description,
                action.RiskLevel.ToString(),
                action.Category,
                GetDefaultTargetType(action),
                true,
                "Available in the simulation-only broker scope."));
        }

        if (Actions.Count > 0)
        {
            SelectedActionId = Actions[0].ActionId;
        }
    }

    private static string GetDefaultTargetType(ActionCatalogEntry action)
    {
        return action.ActionId switch
        {
            "user-preferences.update" => "preference",
            "startup-entry.toggle.current-user" => "startup-entry",
            "settings.apply.allowlisted" => "setting",
            "workspace.cleanup.transient-artifacts" => "workspace",
            "service.inspect" => "service",
            "driver.inspect" => "driver",
            "read-only.inspect.backup-catalog" => "backup-catalog",
            "read-only.inspect.recovery" => "recovery",
            _ => "preference"
        };
    }

    private void SetActionDefaults(ActionCatalogEntry action)
    {
        _isSettingActionDefaults = true;
        try
        {
            var (identity, displayName, parameters, intent) = action.ActionId switch
            {
                "user-preferences.update" => ("theme", "Theme", "{\"name\":\"theme\",\"value\":\"dark\"}", "Update a user preference in simulation."),
                "startup-entry.toggle.current-user" => ("observability-panel", "Observability panel", "{\"name\":\"observability-panel\",\"enabled\":true}", "Toggle a simulated current-user startup entry."),
                "settings.apply.allowlisted" => ("ui-refresh-interval", "UI refresh interval", "{\"name\":\"ui-refresh-interval\",\"value\":\"10s\"}", "Apply an allowlisted simulated setting."),
                "workspace.cleanup.transient-artifacts" => ("tmp", "Temporary workspace artifacts", "{\"artifact\":\"tmp\\\\analysis-summary.txt\"}", "Remove one transient artifact from simulation."),
                "service.inspect" => ("diagnostic-service", "Diagnostic service", "{}", "Inspect a simulated service record."),
                "driver.inspect" => ("display-driver", "Display driver", "{}", "Inspect a simulated driver record."),
                "read-only.inspect.backup-catalog" => ("backup-catalog", "Backup catalog", "{}", "Inspect simulated backup catalog metadata."),
                "read-only.inspect.recovery" => ("recovery-readiness", "Recovery readiness", "{}", "Inspect simulated recovery readiness evidence."),
                _ => (string.Empty, action.Name, "{}", string.Empty)
            };

            SelectedTargetType = GetDefaultTargetType(action);
            TargetIdentity = identity;
            TargetDisplayName = displayName;
            ParameterJson = parameters;
            UserIntent = intent;
        }
        finally
        {
            _isSettingActionDefaults = false;
        }
    }

    private bool SetInputProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        if (!_isSettingActionDefaults)
        {
            InvalidatePreview();
        }

        return true;
    }

    private void InvalidatePreview()
    {
        if (CurrentRequest is null && CurrentApproval is null)
        {
            GeneratePreviewCommand.NotifyCanExecuteChanged();
            return;
        }

        CurrentRequest = null;
        CurrentApproval = null;
        CurrentPreviewDigest = string.Empty;
        PreviewText = "Inputs changed; generate a new preview before approval or execution.";
        ApprovalText = "Approval invalidated because the proposed action changed.";
        StatusMessage = "Action inputs changed. A fresh preview and explicit approval are required.";
        ApproveCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void CancelExecution()
    {
        _executionCancellation?.Cancel();
    }

    private static bool IsValidJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildPreviewText(ActionRequest request)
    {
        var risk = request.RiskLevel.ToString();
        var summary = $"Action: {request.ActionName}\n" +
                      $"Target: {request.Target.TargetType} :: {request.Target.Identity}\n" +
                      $"Display name: {request.Target.DisplayName}\n" +
                      $"Parameters: {request.ParameterJson}\n" +
                      $"Expected simulated effect: {DescribeExpectedEffect(request)}\n" +
                      $"Risk: {risk}\n" +
                      $"Policy: allowed by simulation-only policy version {request.PolicyVersion}; request passed core validation.\n" +
                      $"Policy version: {request.PolicyVersion}\n" +
                      $"Correlation id: {request.CorrelationId}\n" +
                      $"Preview digest: {request.PreviewDigest}\n" +
                      $"Canonical preview: {request.CanonicalPreview}";
        return summary;
    }

    private static string DescribeExpectedEffect(ActionRequest request)
    {
        using var parameters = JsonDocument.Parse(request.ParameterJson);
        var root = parameters.RootElement;
        return request.ActionId switch
        {
            "user-preferences.update" =>
                $"Set simulated preference '{root.GetProperty("name").GetString()}' to '{root.GetProperty("value").GetString()}'.",
            "startup-entry.toggle.current-user" =>
                $"Set simulated startup entry '{root.GetProperty("name").GetString()}' enabled={root.GetProperty("enabled").GetBoolean()}.",
            "settings.apply.allowlisted" =>
                $"Set simulated setting '{root.GetProperty("name").GetString()}' to '{root.GetProperty("value").GetString()}'.",
            "workspace.cleanup.transient-artifacts" =>
                $"Remove simulated workspace artifact '{root.GetProperty("artifact").GetString()}'.",
            "service.inspect" or "driver.inspect" or "read-only.inspect.backup-catalog" or "read-only.inspect.recovery" =>
                $"Inspect synthetic metadata for '{request.Target.Identity}'; no simulated state change is expected.",
            _ => "No simulation effect is registered; the core must deny this action."
        };
    }

    private void HandleUnexpectedError(Exception exception)
    {
        ValidationError = exception.Message;
        StatusMessage = "An unexpected broker error occurred.";
    }
}
