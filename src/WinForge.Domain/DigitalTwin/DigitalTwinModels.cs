using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinForge.Domain.Inventory;

namespace WinForge.Domain.DigitalTwin;

public enum DigitalTwinEntityType
{
    Hardware,
    Device,
    Driver,
    Application,
    Executable,
    Service,
    Process,
    StartupEntry,
    ScheduledTask,
    Volume,
    RecoveryCapability,
    Configuration
}

public enum DigitalTwinRelationshipType
{
    DependsOn,
    Runs,
    Launches,
    UsesPackage,
    AssociatedWith,
    RepresentsDevice
}

public enum DigitalTwinImpactLevel
{
    Direct,
    Indirect
}

public enum DigitalTwinChangeType
{
    Added,
    Changed,
    PotentiallyRemoved,
    NotObserved,
    Unchanged
}

public enum DigitalTwinRelationshipChangeType
{
    Added,
    Changed,
    PotentiallyRemoved,
    NotObserved,
    Unchanged
}

public sealed record DigitalTwinEntity(
    DigitalTwinEntityType Type,
    string IdentityKey,
    string DisplayName,
    string Source,
    DateTimeOffset LastObservedAt,
    InventoryFieldAvailability Availability,
    decimal Confidence,
    IReadOnlyDictionary<string, string?> Metadata,
    InventoryComponentType? InventoryType = null)
{
    public string Id => DigitalTwinIdentity.CreateId(Type, IdentityKey);
}

public sealed record DigitalTwinRelationship(
    DigitalTwinEntityType SourceType,
    string SourceIdentity,
    DigitalTwinEntityType TargetType,
    string TargetIdentity,
    DigitalTwinRelationshipType Type,
    string EvidenceSource,
    string Evidence,
    decimal Confidence,
    bool IsDirected,
    DateTimeOffset LastObservedAt)
{
    public string SourceId => DigitalTwinIdentity.CreateId(SourceType, SourceIdentity);

    public string TargetId => DigitalTwinIdentity.CreateId(TargetType, TargetIdentity);
}

public sealed record DigitalTwinImpactRecord(
    DigitalTwinEntity Entity,
    DigitalTwinImpactLevel Level,
    int Distance,
    DigitalTwinRelationshipType RelationshipType,
    string Reason,
    decimal Confidence,
    IReadOnlyList<string> Path);

public sealed record DigitalTwinImpactAnalysis(
    DigitalTwinEntity? SourceEntity,
    IReadOnlyList<DigitalTwinImpactRecord> DirectlyAffected,
    IReadOnlyList<DigitalTwinImpactRecord> IndirectlyAffected,
    string Summary,
    bool WasTruncated,
    int TotalImpactedEntities);

public sealed record DigitalTwinSnapshot(
    Guid SnapshotId,
    DateTimeOffset CreatedAt,
    Guid? InventoryWatermarkScanId,
    string? InventoryWatermarkScope,
    IReadOnlyList<DigitalTwinEntity> Entities,
    IReadOnlyList<DigitalTwinRelationship> Relationships,
    string Checksum);

public sealed record DigitalTwinSnapshotSummary(
    Guid SnapshotId,
    DateTimeOffset CreatedAt,
    Guid? InventoryWatermarkScanId,
    string? InventoryWatermarkScope,
    int EntityCount,
    int RelationshipCount,
    string Checksum);

public sealed record DigitalTwinEntityPage(
    IReadOnlyList<DigitalTwinEntity> Entities,
    int Offset,
    int PageSize,
    int TotalCount)
{
    public bool HasPrevious => Offset > 0;

    public bool HasNext => Offset + Entities.Count < TotalCount;
}

public sealed record DigitalTwinChange(
    DigitalTwinChangeType Type,
    DigitalTwinEntity Entity,
    string Explanation,
    bool PersistentAcrossSnapshots);

public sealed record DigitalTwinRelationshipChange(
    DigitalTwinRelationshipChangeType Type,
    DigitalTwinRelationship Relationship,
    string Explanation,
    bool PersistentAcrossSnapshots);

public sealed record DigitalTwinComparison(
    IReadOnlyList<DigitalTwinChange> EntityChanges,
    IReadOnlyList<DigitalTwinRelationshipChange> RelationshipChanges);

public sealed record DigitalTwinTraversalResult(
    IReadOnlyList<DigitalTwinEntity> Entities,
    IReadOnlyList<DigitalTwinRelationship> Relationships,
    bool WasTruncated);

public sealed record DigitalTwinCycleResult(
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    bool WasTruncated);

public static class DigitalTwinIdentity
{
    public static string CreateId(DigitalTwinEntityType type, string identityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        return $"{type}:{identityKey.Trim().ToUpperInvariant()}";
    }
}

public static class DigitalTwinSnapshotHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ComputeChecksum(
        IReadOnlyList<DigitalTwinEntity> entities,
        IReadOnlyList<DigitalTwinRelationship> relationships)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entity in entities
                     .OrderBy(item => item.Type)
                     .ThenBy(item => item.IdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, "entity");
            Append(hash, entity.Type.ToString());
            Append(hash, entity.IdentityKey);
            Append(hash, entity.DisplayName);
            Append(hash, entity.Source);
            Append(hash, entity.LastObservedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entity.Availability.ToString());
            Append(hash, entity.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entity.InventoryType?.ToString());
            foreach (var field in entity.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, field.Key);
                Append(hash, field.Value);
            }

            Append(hash, null);
        }

        foreach (var relationship in relationships
                     .OrderBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Type))
        {
            Append(hash, "relationship");
            Append(hash, relationship.SourceType.ToString());
            Append(hash, relationship.SourceIdentity);
            Append(hash, relationship.TargetType.ToString());
            Append(hash, relationship.TargetIdentity);
            Append(hash, relationship.Type.ToString());
            Append(hash, relationship.EvidenceSource);
            Append(hash, relationship.Evidence);
            Append(hash, relationship.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, relationship.IsDirected ? "true" : "false");
            Append(hash, relationship.LastObservedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, null);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        hash.AppendData(encoded);
        hash.AppendData([0]);
    }
}
