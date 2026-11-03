namespace WinForge.Domain.Actions;

public enum ActionRiskLevel
{
    Informational = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
    Critical = 4
}

public enum ActionExecutionState
{
    Proposed = 0,
    Approved = 1,
    Rejected = 2,
    Executing = 3,
    Verified = 4,
    RolledBack = 5,
    Failed = 6,
    Interrupted = 7,
    Cancelled = 8,
    Succeeded = 9
}

public enum ActionApprovalState
{
    Active = 0,
    Consumed = 1,
    Expired = 2,
    Rejected = 3
}

public sealed record ActionCatalogEntry(
    string ActionId,
    string Name,
    string Version,
    string Category,
    ActionRiskLevel RiskLevel,
    bool RequiresApproval,
    bool SimulationOnly,
    bool AllowsProtectedTargets,
    string Description,
    string[] ProtectedComponentExclusions,
    bool SupportsRollback)
{
    public string DisplayName => Name;
}

public sealed record ActionTarget(
    string TargetType,
    string Identity,
    string DisplayName,
    string? Scope = null,
    bool IsProtected = false,
    string Source = "WinForge simulation state")
{
    public string NormalizedIdentity => Identity.Trim();
}

public sealed record ActionRequest(
    string RequestId,
    string ActionId,
    string ActionName,
    ActionTarget Target,
    string ParameterJson,
    string UserIntent,
    string CorrelationId,
    string PolicyVersion,
    ActionRiskLevel RiskLevel,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PreviewDigest,
    string CanonicalPreview);

public sealed record ActionApproval(
    string ApprovalId,
    string RequestId,
    string ActionId,
    string PreviewDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ApproverId,
    string PolicyVersion,
    ActionApprovalState State,
    bool OneTimeUse = true,
    DateTimeOffset? ConsumedAtUtc = null);

public sealed record ActionExecutionRecord(
    string ExecutionId,
    string RequestId,
    string ActionId,
    ActionExecutionState State,
    string Summary,
    string? Details,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool VerificationPassed,
    bool RollbackApplied,
    bool SimulationOnly,
    string? EvidenceJson = null);

public sealed record ActionJournalEntry(
    string JournalId,
    string RequestId,
    string ActionId,
    string EventType,
    string State,
    DateTimeOffset TimestampUtc,
    string Summary,
    string Details);

public sealed record ActionBrokerStateSnapshot(
    string SnapshotId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, string> Preferences,
    IReadOnlyDictionary<string, bool> StartupEntries,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> TransientArtifacts);
