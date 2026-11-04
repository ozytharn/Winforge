using WinForge.Domain.Inventory;

namespace WinForge.Domain.Configuration;

public enum ConfigurationDriftClassification
{
    Added,
    Modified,
    PotentiallyRemoved,
    NotObserved,
    Unchanged
}

public sealed record ConfigurationDriftFieldChange(
    string FieldName,
    string? PreviousValue,
    string? CurrentValue,
    InventoryFieldAvailability PreviousAvailability,
    InventoryFieldAvailability CurrentAvailability,
    string Explanation);

public sealed record ConfigurationDriftChange(
    string EntityKind,
    string EntityIdentity,
    string DisplayName,
    string Source,
    ConfigurationDriftClassification Classification,
    string Summary,
    IReadOnlyList<ConfigurationDriftFieldChange> FieldChanges,
    decimal Confidence,
    bool IsObservationBased,
    DateTimeOffset ObservedAtUtc);

public sealed record ConfigurationDriftAssessment(
    IReadOnlyList<ConfigurationDriftChange> Changes,
    IReadOnlyList<string> Warnings,
    int AddedCount,
    int ModifiedCount,
    int PotentiallyRemovedCount,
    int NotObservedCount,
    int UnchangedCount);
