using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinForge.Domain.Actions;

namespace WinForge.Application.Actions;

public interface IActionBrokerJournalStore
{
    Task<IReadOnlyList<ActionJournalEntry>> LoadJournalAsync(CancellationToken cancellationToken);
    Task SaveJournalEntryAsync(ActionJournalEntry entry, CancellationToken cancellationToken);
    Task SaveStateSnapshotAsync(ActionBrokerStateSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed record ActionValidationResult(bool IsValid, string? Code, string? Message)
{
    public static ActionValidationResult Valid => new(true, null, null);
}

public sealed class SimulatedSystemState
{
    public Dictionary<string, string> Preferences { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["theme"] = "light",
        ["notification-level"] = "balanced"
    };

    public Dictionary<string, bool> StartupEntries { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["observability-panel"] = true,
        ["maintenance-checklist"] = false
    };

    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui-refresh-interval"] = "30s",
        ["simulated-network-mode"] = "offline"
    };

    public List<string> TransientArtifacts { get; } = new()
    {
        "tmp\\analysis-summary.txt",
        "tmp\\recovery-report.log"
    };

    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "diagnostic-service",
        "update-service"
    };

    public HashSet<string> Drivers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "display-driver",
        "storage-driver"
    };

    public ActionBrokerStateSnapshot CaptureSnapshot()
    {
        return new ActionBrokerStateSnapshot(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(Preferences, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StartupEntries, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase),
            new List<string>(TransientArtifacts));
    }
}

public sealed class ActionRequestValidator
{
    public const string SupportedPolicyVersion = "1.0.0";
    private const int MaximumParameterBytes = 8 * 1024;
    private static readonly TimeSpan MaximumRequestLifetime = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _timeProvider;

    public ActionRequestValidator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ActionValidationResult Validate(ActionRequest request, ActionCatalogEntry action)
    {
        if (request is null || action is null || request.Target is null)
        {
            return new(false, "request-missing", "Request, target, and catalog action are required.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return new(false, "request-missing-id", "Request id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ActionId))
        {
            return new(false, "request-missing-action", "Action id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Target.Identity))
        {
            return new(false, "target-missing-identity", "Target identity is required.");
        }

        if (request.Target.DisplayName is null || request.UserIntent is null || request.CorrelationId is null ||
            request.Target.Identity.Length > 256 ||
            request.Target.DisplayName.Length > 256 ||
            request.UserIntent.Length > 1024 ||
            request.CorrelationId.Length > 128)
        {
            return new(false, "request-field-too-large", "Target, intent, or correlation fields exceed their allowed lengths.");
        }

        if (request.Target.Identity != request.Target.Identity.Trim() ||
            string.IsNullOrWhiteSpace(request.Target.TargetType) ||
            request.Target.TargetType != request.Target.TargetType.Trim().ToLowerInvariant())
        {
            return new(false, "target-not-normalized", "Target identity and type must use their normalized form.");
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return new(false, "request-missing-correlation", "Correlation id is required.");
        }

        var now = _timeProvider.GetUtcNow();
        if (request.CreatedAtUtc > now || request.ExpiresAtUtc <= now ||
            request.ExpiresAtUtc <= request.CreatedAtUtc ||
            request.ExpiresAtUtc - request.CreatedAtUtc > MaximumRequestLifetime)
        {
            return new(false, "request-expired", "Request authorization is expired or has an invalid lifetime.");
        }

        if (request.ActionId != action.ActionId ||
            request.ActionName != action.Name ||
            request.RiskLevel != action.RiskLevel)
        {
            return new(false, "action-mismatch", "Action identity, name, or risk does not match the trusted catalog.");
        }

        if (!string.Equals(request.PolicyVersion, SupportedPolicyVersion, StringComparison.Ordinal))
        {
            return new(false, "policy-version-unsupported", "Request policy version is not supported.");
        }

        var parameterResult = NormalizeParameters(request.ActionId, request.Target, request.ParameterJson, out var normalizedParameters);
        if (!parameterResult.IsValid)
        {
            return parameterResult;
        }

        if (!string.Equals(request.ParameterJson, normalizedParameters, StringComparison.Ordinal))
        {
            return new(false, "parameters-not-normalized", "Parameters must use the canonical normalized representation.");
        }

        var computedPreview = ComputeCanonicalPreview(request);
        if (!string.Equals(request.CanonicalPreview, computedPreview, StringComparison.Ordinal))
        {
            return new(false, "preview-mismatch", "The preview hash does not match the canonical preview.");
        }

        var computedDigest = ComputePreviewDigest(computedPreview);
        if (!string.Equals(request.PreviewDigest, computedDigest, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "preview-digest-mismatch", "Request preview digest does not match the canonical preview.");
        }

        return ActionValidationResult.Valid;
    }

    public static ActionValidationResult NormalizeParameters(
        string actionId,
        ActionTarget target,
        string? parameterJson,
        out string normalizedParameters)
    {
        normalizedParameters = string.Empty;
        if (target is null || string.IsNullOrWhiteSpace(parameterJson) ||
            Encoding.UTF8.GetByteCount(parameterJson) > MaximumParameterBytes)
        {
            return new(false, "request-invalid-json", "Parameter payload is required and must be at most 8 KiB.");
        }

        try
        {
            using var document = JsonDocument.Parse(parameterJson, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(false, "request-invalid-json", "Parameters must be a JSON object.");
            }

            var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            switch (actionId)
            {
                case "user-preferences.update":
                case "settings.apply.allowlisted":
                    if (target.TargetType != (actionId == "user-preferences.update" ? "preference" : "setting") ||
                        !TryGetExactProperties(root, ["name", "value"]) ||
                        !TryGetString(root, "name", out var name) ||
                        !TryGetString(root, "value", out var value) ||
                        string.IsNullOrWhiteSpace(value) ||
                        !string.Equals(name.Trim(), target.NormalizedIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        return new(false, "parameters-invalid", "Name/value parameters must be strings and name must match the selected target.");
                    }

                    values["name"] = target.NormalizedIdentity;
                    values["value"] = value;
                    break;

                case "startup-entry.toggle.current-user":
                    if (target.TargetType != "startup-entry" ||
                        !TryGetExactProperties(root, ["enabled", "name"]) ||
                        !TryGetString(root, "name", out var entryName) ||
                        !root.TryGetProperty("enabled", out var enabledValue) ||
                        enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        !string.Equals(entryName.Trim(), target.NormalizedIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        return new(false, "parameters-invalid", "Startup parameters must contain a matching string name and boolean enabled value.");
                    }

                    values["enabled"] = enabledValue.GetBoolean();
                    values["name"] = target.NormalizedIdentity;
                    break;

                case "workspace.cleanup.transient-artifacts":
                    if (target.TargetType != "workspace" ||
                        !TryGetExactProperties(root, ["artifact"]) ||
                        !TryGetString(root, "artifact", out var artifact))
                    {
                        return new(false, "parameters-invalid", "Workspace cleanup requires one string artifact parameter.");
                    }

                    var normalizedArtifact = artifact.Replace('/', '\\').Trim();
                    var normalizedRoot = target.NormalizedIdentity.Replace('/', '\\').TrimEnd('\\') + "\\";
                    if (normalizedArtifact.Length == 0 ||
                        normalizedArtifact.Split('\\').Any(segment => segment is "." or "..") ||
                        !normalizedArtifact.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return new(false, "parameters-target-mismatch", "Artifact must be inside the selected workspace target.");
                    }

                    values["artifact"] = normalizedArtifact;
                    break;

                case "service.inspect":
                case "driver.inspect":
                case "read-only.inspect.backup-catalog":
                case "read-only.inspect.recovery":
                    var requiredTargetType = actionId switch
                    {
                        "service.inspect" => "service",
                        "driver.inspect" => "driver",
                        "read-only.inspect.backup-catalog" => "backup-catalog",
                        _ => "recovery"
                    };
                    if (target.TargetType != requiredTargetType || !TryGetExactProperties(root, []))
                    {
                        return new(false, "parameters-invalid", "This read-only inspection accepts no parameters and requires its registered target type.");
                    }

                    break;

                default:
                    return new(false, "unknown-action", "Action is not defined by the parameter validator.");
            }

            normalizedParameters = JsonSerializer.Serialize(values);
            return ActionValidationResult.Valid;
        }
        catch (JsonException)
        {
            return new(false, "request-invalid-json", "Parameter payload is not valid bounded JSON.");
        }
    }

    private static bool TryGetExactProperties(JsonElement element, IReadOnlyCollection<string> expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == expected.Count &&
               actual.Distinct(StringComparer.Ordinal).Count() == actual.Length &&
               actual.All(name => expected.Contains(name, StringComparer.Ordinal));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    public static string ComputeCanonicalPreview(ActionRequest request)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action_id"] = request.ActionId,
            ["action_name"] = request.ActionName,
            ["target_type"] = request.Target.TargetType,
            ["target_identity"] = request.Target.NormalizedIdentity,
            ["target_display_name"] = request.Target.DisplayName,
            ["scope"] = request.Target.Scope ?? string.Empty,
            ["correlation_id"] = request.CorrelationId,
            ["policy_version"] = request.PolicyVersion,
            ["risk_level"] = request.RiskLevel.ToString(),
            ["user_intent"] = request.UserIntent,
            ["parameters"] = string.IsNullOrWhiteSpace(request.ParameterJson) ? "{}" : request.ParameterJson
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string ComputePreviewDigest(string canonicalPreview) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPreview))).ToLowerInvariant();
}

public sealed class ActionBrokerPolicyEngine
{
    public ActionValidationResult Evaluate(ActionRequest request, ActionCatalogEntry action)
    {
        if (request.Target.IsProtected)
        {
            return new(false, "protected-target", "Protected targets are not available in the simulation action catalog.");
        }

        if (request.ActionId != action.ActionId || request.RiskLevel != action.RiskLevel)
        {
            return new(false, "action-mismatch", "Action request does not match the trusted catalog definition.");
        }

        if (!action.SimulationOnly)
        {
            return new(false, "non-simulation-action", "Only simulation-only actions are allowed in this scope.");
        }

        if (request.RiskLevel > action.RiskLevel)
        {
            return new(false, "risk-level-mismatch", "The request risk level exceeds the catalog definition.");
        }

        var expectedTargetType = action.ActionId switch
        {
            "user-preferences.update" => "preference",
            "startup-entry.toggle.current-user" => "startup-entry",
            "settings.apply.allowlisted" => "setting",
            "workspace.cleanup.transient-artifacts" => "workspace",
            "service.inspect" => "service",
            "driver.inspect" => "driver",
            "read-only.inspect.backup-catalog" => "backup-catalog",
            "read-only.inspect.recovery" => "recovery",
            _ => string.Empty
        };
        if (expectedTargetType.Length == 0 || request.Target.TargetType != expectedTargetType)
        {
            return new(false, "target-type-denied", "Target type is not permitted for this action.");
        }

        return ActionValidationResult.Valid;
    }
}

internal sealed class ActionBrokerSimulationCoordinator
{
    private readonly SimulatedSystemState _state;

    public ActionBrokerSimulationCoordinator(SimulatedSystemState state)
    {
        _state = state;
    }

    public bool IsTargetPresent(ActionRequest request)
    {
        return request.Target.TargetType switch
        {
            "preference" => _state.Preferences.ContainsKey(request.Target.NormalizedIdentity),
            "startup-entry" => _state.StartupEntries.ContainsKey(request.Target.NormalizedIdentity),
            "setting" => _state.Settings.ContainsKey(request.Target.NormalizedIdentity),
            "workspace" => IsWorkspaceArtifactPresent(request),
            "service" => _state.Services.Contains(request.Target.NormalizedIdentity),
            "driver" => _state.Drivers.Contains(request.Target.NormalizedIdentity),
            "backup-catalog" => request.Target.Identity == "backup-catalog",
            "recovery" => request.Target.Identity == "recovery-readiness",
            _ => false
        };
    }

    private bool IsWorkspaceArtifactPresent(ActionRequest request)
    {
        using var document = JsonDocument.Parse(request.ParameterJson);
        var artifact = document.RootElement.GetProperty("artifact").GetString();
        return artifact is not null && _state.TransientArtifacts.Contains(artifact, StringComparer.OrdinalIgnoreCase);
    }

    public ActionExecutionRecord Execute(ActionRequest request, ActionCatalogEntry action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var start = DateTimeOffset.UtcNow;
        var evidence = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action_id"] = request.ActionId,
            ["target_type"] = request.Target.TargetType,
            ["target_identity"] = request.Target.Identity,
            ["mode"] = "simulation-only"
        };

        string? previous = null;
        switch (request.ActionId)
        {
            case "user-preferences.update":
            {
                var payload = JsonDocument.Parse(request.ParameterJson).RootElement;
                var key = payload.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? request.Target.Identity : request.Target.Identity;
                var value = payload.TryGetProperty("value", out var valueElement) ? valueElement.GetString() ?? "updated" : "updated";
                previous = _state.Preferences.TryGetValue(key, out var current) ? current : null;
                _state.Preferences[key] = value;
                evidence["updated_key"] = key;
                evidence["updated_value"] = value;
                evidence["previous_value"] = previous;
                break;
            }
            case "workspace.cleanup.transient-artifacts":
            {
                var payload = JsonDocument.Parse(request.ParameterJson).RootElement;
                var key = payload.TryGetProperty("artifact", out var artifactValue)
                    ? artifactValue.GetString() ?? request.Target.Identity
                    : request.Target.Identity;
                previous = _state.TransientArtifacts.FirstOrDefault(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
                _state.TransientArtifacts.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
                evidence["artifact_removed"] = key;
                evidence["previous_value"] = previous;
                break;
            }
            case "startup-entry.toggle.current-user":
            {
                var payload = JsonDocument.Parse(request.ParameterJson).RootElement;
                var key = payload.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? request.Target.Identity : request.Target.Identity;
                var newValue = payload.TryGetProperty("enabled", out var enabledValue) && enabledValue.ValueKind == JsonValueKind.True;
                previous = _state.StartupEntries.TryGetValue(key, out var current) ? current.ToString() : null;
                _state.StartupEntries[key] = newValue;
                evidence["startup_entry"] = key;
                evidence["enabled"] = newValue;
                evidence["previous_value"] = previous;
                break;
            }
            case "settings.apply.allowlisted":
            {
                var payload = JsonDocument.Parse(request.ParameterJson).RootElement;
                var key = payload.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? request.Target.Identity : request.Target.Identity;
                var value = payload.TryGetProperty("value", out var valueElement) ? valueElement.GetString() ?? "updated" : "updated";
                previous = _state.Settings.TryGetValue(key, out var current) ? current : null;
                _state.Settings[key] = value;
                evidence["updated_key"] = key;
                evidence["updated_value"] = value;
                evidence["previous_value"] = previous;
                break;
            }
            case "service.inspect":
            case "driver.inspect":
            case "read-only.inspect.backup-catalog":
            case "read-only.inspect.recovery":
                evidence["inspection_only"] = true;
                break;
            default:
                throw new InvalidOperationException($"Action '{request.ActionId}' is not implemented in the simulation executor.");
        }

        var verification = Verify(request, action, evidence);
        var completedAt = DateTimeOffset.UtcNow;
        if (!verification)
        {
            var rollbackSucceeded = false;
            if (action.SupportsRollback)
            {
                rollbackSucceeded = Rollback(request, action, previous);
            }

            return new ActionExecutionRecord(
                Guid.NewGuid().ToString("N"),
                request.RequestId,
                request.ActionId,
                ActionExecutionState.Failed,
                rollbackSucceeded ? "Verification failed and the rollback succeeded." : "Verification failed; rollback was unavailable or not applied.",
                rollbackSucceeded ? "The simulation state was automatically restored to its previous value." : "The simulation state was left at the last verified value.",
                start,
                completedAt,
                false,
                rollbackSucceeded,
                true,
                JsonSerializer.Serialize(evidence));
        }

        return new ActionExecutionRecord(
            Guid.NewGuid().ToString("N"),
            request.RequestId,
            request.ActionId,
            ActionExecutionState.Succeeded,
            "Simulation action completed and verification passed.",
            "The simulated state change matched the expected postcondition.",
            start,
            completedAt,
            true,
            false,
            true,
            JsonSerializer.Serialize(evidence));
    }

    public bool Verify(ActionRequest request, ActionCatalogEntry action, IReadOnlyDictionary<string, object?> evidence)
    {
        if (request.ActionId == "service.inspect" || request.ActionId == "driver.inspect" || request.ActionId == "read-only.inspect.backup-catalog" || request.ActionId == "read-only.inspect.recovery")
        {
            return true;
        }

        if (request.ActionId == "user-preferences.update")
        {
            var key = evidence["updated_key"] as string;
            var expectedValue = evidence["updated_value"] as string;
            return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(expectedValue) && string.Equals(_state.Preferences.GetValueOrDefault(key), expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        if (request.ActionId == "workspace.cleanup.transient-artifacts")
        {
            var key = evidence["artifact_removed"] as string;
            return !string.IsNullOrWhiteSpace(key) && _state.TransientArtifacts.All(item => !item.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        if (request.ActionId == "startup-entry.toggle.current-user")
        {
            var key = evidence["startup_entry"] as string;
            var expectedValue = (bool?)evidence["enabled"] ?? false;
            return !string.IsNullOrWhiteSpace(key) && _state.StartupEntries.TryGetValue(key, out var actual) && actual == expectedValue;
        }

        if (request.ActionId == "settings.apply.allowlisted")
        {
            var key = evidence["updated_key"] as string;
            var expectedValue = evidence["updated_value"] as string;
            return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(expectedValue) && string.Equals(_state.Settings.GetValueOrDefault(key), expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public bool Rollback(ActionRequest request, ActionCatalogEntry action, string? previousValue)
    {
        if (string.IsNullOrWhiteSpace(previousValue))
        {
            return false;
        }

        try
        {
            switch (request.ActionId)
            {
                case "user-preferences.update":
                    var key = request.Target.Identity;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return false;
                    }

                    _state.Preferences[key] = previousValue;
                    return true;
                case "workspace.cleanup.transient-artifacts":
                    _state.TransientArtifacts.Add(previousValue);
                    return true;
                case "startup-entry.toggle.current-user":
                    var entryKey = request.Target.Identity;
                    if (string.IsNullOrWhiteSpace(entryKey))
                    {
                        return false;
                    }

                    _state.StartupEntries[entryKey] = bool.TryParse(previousValue, out var flag) && flag;
                    return true;
                case "settings.apply.allowlisted":
                    var settingKey = request.Target.Identity;
                    if (string.IsNullOrWhiteSpace(settingKey))
                    {
                        return false;
                    }

                    _state.Settings[settingKey] = previousValue;
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ActionBrokerService
{
    private readonly TrustedActionCatalog _catalog;
    private readonly ActionBrokerPolicyEngine _policyEngine;
    private readonly ActionBrokerSimulationCoordinator _simulator;
    private readonly SimulatedSystemState _state;
    private readonly IActionBrokerJournalStore _journalStore;
    private readonly TimeProvider _timeProvider;
    private readonly ActionRequestValidator _validator;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<string, ActionRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionApproval> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionExecutionRecord> _executions = new(StringComparer.Ordinal);

    public ActionBrokerService(
        TrustedActionCatalog catalog,
        ActionBrokerPolicyEngine policyEngine,
        SimulatedSystemState state,
        IActionBrokerJournalStore journalStore,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _policyEngine = policyEngine;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _simulator = new ActionBrokerSimulationCoordinator(state);
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _validator = new ActionRequestValidator(_timeProvider);
    }

    public IReadOnlyList<ActionCatalogEntry> AvailableActions => _catalog.Entries;

    public ActionBrokerStateSnapshot CurrentState => _state.CaptureSnapshot();

    public async Task<ActionRequest> CreateRequestAsync(
        string actionId,
        string targetType,
        string targetIdentity,
        string targetDisplayName,
        string parameterJson,
        string userIntent,
        string correlationId,
        string policyVersion = "1.0.0",
        CancellationToken cancellationToken = default)
    {
        var action = _catalog.Get(actionId);
        var requestId = Guid.NewGuid().ToString("N");
        var createdAt = _timeProvider.GetUtcNow();
        var target = new ActionTarget(
            targetType?.Trim().ToLowerInvariant() ?? string.Empty,
            targetIdentity?.Trim() ?? string.Empty,
            targetDisplayName?.Trim() ?? string.Empty,
            "current-user",
            false,
            "WinForge simulation state");
        var request = new ActionRequest(
            requestId,
            action.ActionId,
            action.Name,
            target,
            parameterJson ?? string.Empty,
            userIntent?.Trim() ?? string.Empty,
            correlationId?.Trim() ?? string.Empty,
            policyVersion?.Trim() ?? string.Empty,
            action.RiskLevel,
            createdAt,
            createdAt.AddMinutes(10),
            string.Empty,
            string.Empty);

        var parameterResult = ActionRequestValidator.NormalizeParameters(
            request.ActionId,
            request.Target,
            request.ParameterJson,
            out var normalizedParameters);
        if (!parameterResult.IsValid)
        {
            throw new InvalidOperationException(parameterResult.Message ?? "Action parameters are invalid.");
        }

        request = request with { ParameterJson = normalizedParameters };
        var canonicalPreview = ActionRequestValidator.ComputeCanonicalPreview(request);
        var digest = ActionRequestValidator.ComputePreviewDigest(canonicalPreview);
        var finalized = request with
        {
            PreviewDigest = digest,
            CanonicalPreview = canonicalPreview
        };

        var validation = _validator.Validate(finalized, action);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message ?? "Failed to create the action request.");
        }

        var policyValidation = _policyEngine.Evaluate(finalized, action);
        if (!policyValidation.IsValid)
        {
            throw new InvalidOperationException(policyValidation.Message ?? "The action is denied by policy.");
        }

        var journalEntry = new ActionJournalEntry(
            Guid.NewGuid().ToString("N"),
            finalized.RequestId,
            finalized.ActionId,
            "proposed",
            "Proposed",
            _timeProvider.GetUtcNow(),
            "Action request created",
            $"Action '{finalized.ActionName}' was prepared and bound to target '{finalized.Target.Identity}'.");

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _journalStore.SaveJournalEntryAsync(journalEntry, cancellationToken).ConfigureAwait(false);
            _requests.Add(finalized.RequestId, finalized);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        return finalized;
    }

    public async Task<ActionApproval> CreateApprovalAsync(ActionRequest request, string approverId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(approverId))
        {
            throw new ArgumentException("Approver identity is required.", nameof(approverId));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_requests.TryGetValue(request.RequestId, out var registeredRequest) || request != registeredRequest)
            {
                throw new InvalidOperationException("Only the exact request created by this broker instance can be approved.");
            }

            var action = _catalog.Get(request.ActionId);
            var validation = _validator.Validate(request, action);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(validation.Message ?? "Request validation failed before approval was issued.");
            }

            var policyValidation = _policyEngine.Evaluate(request, action);
            if (!policyValidation.IsValid)
            {
                throw new InvalidOperationException(policyValidation.Message ?? "The action is denied by policy.");
            }

            if (_approvals.ContainsKey(request.RequestId) || _executions.ContainsKey(request.RequestId))
            {
                throw new InvalidOperationException("This request already has an approval or execution record.");
            }

            var approval = new ActionApproval(
                Guid.NewGuid().ToString("N"),
                request.RequestId,
                request.ActionId,
                request.PreviewDigest,
                _timeProvider.GetUtcNow(),
                request.ExpiresAtUtc,
                approverId.Trim(),
                request.PolicyVersion,
                ActionApprovalState.Active,
                true,
                null);

            await _journalStore.SaveJournalEntryAsync(
                new ActionJournalEntry(
                    Guid.NewGuid().ToString("N"),
                    request.RequestId,
                    request.ActionId,
                    "approved",
                    "Approved",
                    _timeProvider.GetUtcNow(),
                    "Action request approved",
                    $"Approval '{approval.ApprovalId}' was issued and expires at {approval.ExpiresAtUtc:O}."),
                cancellationToken).ConfigureAwait(false);
            _approvals.Add(request.RequestId, approval);
            return approval;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<ActionExecutionRecord> ExecuteAsync(
        ActionRequest request,
        ActionApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approval);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedAt = _timeProvider.GetUtcNow();
            if (!_requests.TryGetValue(request.RequestId, out var registeredRequest) || request != registeredRequest)
            {
                return await RejectAsync(request, "request-mismatch", "The request is not the exact request issued by this broker instance.").ConfigureAwait(false);
            }

            if (!_approvals.TryGetValue(request.RequestId, out var registeredApproval) || approval != registeredApproval ||
                registeredApproval.State != ActionApprovalState.Active ||
                !registeredApproval.OneTimeUse ||
                registeredApproval.RequestId != request.RequestId ||
                registeredApproval.ActionId != request.ActionId ||
                registeredApproval.PreviewDigest != request.PreviewDigest ||
                registeredApproval.PolicyVersion != request.PolicyVersion ||
                registeredApproval.ExpiresAtUtc != request.ExpiresAtUtc)
            {
                return await RejectAsync(request, "approval-mismatch", "Approval is missing, modified, consumed, or not bound to this exact request.").ConfigureAwait(false);
            }

            if (registeredApproval.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                return await RejectAsync(request, "approval-expired", "The request-bound approval has expired.").ConfigureAwait(false);
            }

            if (_executions.ContainsKey(request.RequestId))
            {
                return await RejectAsync(request, "duplicate-execution", "This request has already been processed.").ConfigureAwait(false);
            }

            var action = _catalog.Get(request.ActionId);
            var validation = _validator.Validate(request, action);
            if (!validation.IsValid)
            {
                return await RejectAsync(request, validation.Code ?? "request-invalid", validation.Message ?? "Request validation failed.").ConfigureAwait(false);
            }

            var policyValidation = _policyEngine.Evaluate(request, action);
            if (!policyValidation.IsValid)
            {
                return await RejectAsync(request, policyValidation.Code ?? "policy-denied", policyValidation.Message ?? "Policy denied the request.").ConfigureAwait(false);
            }

            var existingJournal = await _journalStore.LoadJournalAsync(cancellationToken).ConfigureAwait(false);
            if (existingJournal.Any(entry =>
                    entry.RequestId == request.RequestId &&
                    entry.EventType is "started" or "completed" or "failed" or "cancelled" or "interrupted"))
            {
                return await RejectAsync(request, "replay-detected", "Persistent journal history shows this request has already entered execution.").ConfigureAwait(false);
            }

            if (!_simulator.IsTargetPresent(request))
            {
                return await RejectAsync(request, "target-stale", $"Simulation target '{request.Target.Identity}' is absent or no longer matches the approved parameters.").ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var executionId = Guid.NewGuid().ToString("N");
            await _journalStore.SaveJournalEntryAsync(
                new ActionJournalEntry(
                    Guid.NewGuid().ToString("N"),
                    request.RequestId,
                    request.ActionId,
                    "started",
                    "Executing",
                    startedAt,
                    "Simulation execution started.",
                    $"Execution '{executionId}' entered the isolated simulation executor."),
                cancellationToken).ConfigureAwait(false);

            _approvals[request.RequestId] = registeredApproval with
            {
                State = ActionApprovalState.Consumed,
                ConsumedAtUtc = _timeProvider.GetUtcNow()
            };

            ActionExecutionRecord record;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                record = _simulator.Execute(request, action, cancellationToken) with { ExecutionId = executionId };
            }
            catch (OperationCanceledException)
            {
                record = new ActionExecutionRecord(
                    executionId,
                    request.RequestId,
                    request.ActionId,
                    ActionExecutionState.Interrupted,
                    "Simulation execution was cancelled before a state transition completed.",
                    "The one-time approval was consumed; no real Windows state was accessed or changed.",
                    startedAt,
                    DateTimeOffset.UtcNow,
                    false,
                    false,
                    true,
                    null);
            }

            _executions[request.RequestId] = record;
            var terminalEvent = record.State switch
            {
                ActionExecutionState.Succeeded => "completed",
                ActionExecutionState.Interrupted or ActionExecutionState.Cancelled => "cancelled",
                _ => "failed"
            };

            try
            {
                await _journalStore.SaveStateSnapshotAsync(_state.CaptureSnapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                record = record with
                {
                    State = ActionExecutionState.Failed,
                    Summary = "Simulation completed, but its state snapshot could not be persisted.",
                    Details = "The execution journal will record the persistence failure; no real Windows state was changed."
                };
                _executions[request.RequestId] = record;
                terminalEvent = "failed";
            }

            await _journalStore.SaveJournalEntryAsync(
                new ActionJournalEntry(
                    Guid.NewGuid().ToString("N"),
                    request.RequestId,
                    request.ActionId,
                    terminalEvent,
                    record.State.ToString(),
                    _timeProvider.GetUtcNow(),
                    record.Summary,
                    record.Details ?? "No additional detail."),
                CancellationToken.None).ConfigureAwait(false);

            return record;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyList<ActionJournalEntry>> ReadJournalAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await _journalStore.LoadJournalAsync(cancellationToken).ConfigureAwait(false);
            var terminalRequestIds = entries
                .Where(entry => entry.EventType is "completed" or "failed" or "cancelled" or "interrupted")
                .Select(entry => entry.RequestId)
                .ToHashSet(StringComparer.Ordinal);
            var interruptedStarts = entries
                .Where(entry => entry.EventType == "started")
                .GroupBy(entry => entry.RequestId, StringComparer.Ordinal)
                .Where(group => !terminalRequestIds.Contains(group.Key))
                .Select(group => group.OrderByDescending(entry => entry.TimestampUtc).First())
                .ToArray();

            foreach (var started in interruptedStarts)
            {
                await _journalStore.SaveJournalEntryAsync(
                    new ActionJournalEntry(
                        Guid.NewGuid().ToString("N"),
                        started.RequestId,
                        started.ActionId,
                        "interrupted",
                        ActionExecutionState.Interrupted.ToString(),
                        _timeProvider.GetUtcNow(),
                        "A prior simulation execution did not record a terminal result.",
                        "The application restarted while the simulation was in progress. No real Windows state was changed; the prior in-memory simulation state may not have been retained."),
                    cancellationToken).ConfigureAwait(false);
            }

            return interruptedStarts.Length == 0
                ? entries
                : await _journalStore.LoadJournalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _lifecycleGate.Release();
    }

    public ActionBrokerStateSnapshot CaptureStateSnapshot()
    {
        return _state.CaptureSnapshot();
    }

    private async Task<ActionExecutionRecord> RejectAsync(
        ActionRequest request,
        string reasonCode,
        string details)
    {
        var now = _timeProvider.GetUtcNow();
        var record = new ActionExecutionRecord(
            Guid.NewGuid().ToString("N"),
            request.RequestId,
            request.ActionId,
            ActionExecutionState.Rejected,
            "Action request was rejected.",
            details,
            now,
            now,
            false,
            false,
            true,
            JsonSerializer.Serialize(new { reason = reasonCode, mode = "simulation-only" }));

        await _journalStore.SaveJournalEntryAsync(
            new ActionJournalEntry(
                Guid.NewGuid().ToString("N"),
                request.RequestId,
                request.ActionId,
                "rejected",
                record.State.ToString(),
                now,
                record.Summary,
                $"{reasonCode}: {details}"),
            CancellationToken.None).ConfigureAwait(false);
        return record;
    }
}
