namespace WinForge.Domain.Identity;

public enum SoftwareIdentityConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed record SoftwareEvidence(
    string Source,
    string EvidenceType,
    string? Value,
    DateTimeOffset ObservedAtUtc);

public sealed record SoftwareComponentAssociation(
    string ComponentType,
    string ComponentIdentityKey,
    string DisplayName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? PackageIdentity);

public sealed record SoftwareIdentityRecord(
    string LogicalProductId,
    string DisplayName,
    string? Publisher,
    string? Version,
    string? PackageIdentity,
    string? InstallLocation,
    string CanonicalKey,
    SoftwareIdentityConfidence Confidence,
    IReadOnlyList<SoftwareEvidence> Evidence,
    IReadOnlyList<SoftwareComponentAssociation> Components);

public sealed record SoftwareIdentityConflict(
    string ConflictId,
    string ProductName,
    string Message,
    IReadOnlyList<string> ComponentIdentityKeys);

public sealed record SoftwareIdentityAssessment(
    IReadOnlyList<SoftwareIdentityRecord> Products,
    IReadOnlyList<SoftwareIdentityConflict> Conflicts,
    IReadOnlyList<string> Warnings);
