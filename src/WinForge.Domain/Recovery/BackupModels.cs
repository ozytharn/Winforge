namespace WinForge.Domain.Recovery;

public enum BackupProvider
{
    WindowsImageBackup,
    FileHistory,
    Unsupported
}

public enum BackupInspectionState
{
    Verified,
    Warning,
    Failed,
    Incomplete,
    Unsupported,
    Inaccessible,
    Unknown,
    Cancelled
}

public enum BackupCheckState
{
    Passed,
    Warning,
    Failed,
    Skipped,
    Inaccessible
}

public sealed record BackupCandidate(
    string Identity,
    string DisplayName,
    BackupProvider Provider,
    string? Location,
    string? Volume,
    DateTimeOffset LastDiscoveredAt,
    string Availability,
    string Source,
    string? Limitation);

public sealed record BackupInspectionCheck(
    string Name,
    BackupCheckState State,
    string Explanation,
    string Source,
    DateTimeOffset CheckedAt);

public sealed record BackupInspectionResult(
    Guid InspectionId,
    string CandidateIdentity,
    BackupProvider Provider,
    string? Location,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    BackupInspectionState State,
    string Summary,
    bool RecognizedStructure,
    int? DiscoveredStructureGroups,
    long? MetadataBytesRead,
    long? BackupContainerBytes,
    IReadOnlyList<BackupInspectionCheck> Checks,
    IReadOnlyList<string> Limitations);

public sealed record BackupInspectionSummary(
    Guid InspectionId,
    string CandidateIdentity,
    BackupProvider Provider,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    BackupInspectionState State,
    string Summary);

public sealed record BackupInspectionComparison(
    BackupInspectionSummary Current,
    BackupInspectionSummary Previous,
    bool StateChanged,
    bool SummaryChanged);
