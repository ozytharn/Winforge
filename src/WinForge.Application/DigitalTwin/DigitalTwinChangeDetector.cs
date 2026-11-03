using WinForge.Domain.DigitalTwin;

namespace WinForge.Application.DigitalTwin;

public sealed class DigitalTwinChangeDetector
{
    public IReadOnlyList<DigitalTwinChange> Compare(
        DigitalTwinSnapshot previous,
        DigitalTwinSnapshot current,
        DigitalTwinSnapshot? confirmation = null) =>
        CompareDetailed(previous, current, confirmation).EntityChanges;

    public DigitalTwinComparison CompareDetailed(
        DigitalTwinSnapshot previous,
        DigitalTwinSnapshot current,
        DigitalTwinSnapshot? confirmation = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var previousEntities = previous.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var currentEntities = current.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var confirmationEntities = confirmation?.Entities.ToDictionary(
            entity => entity.Id,
            StringComparer.OrdinalIgnoreCase);
        var changes = new List<DigitalTwinChange>();
        var relationshipChanges = new List<DigitalTwinRelationshipChange>();
        var canAssessAbsence = HasComparableScopeEvidence(previous, current, confirmation);

        foreach (var entity in current.Entities)
        {
            if (!previousEntities.TryGetValue(entity.Id, out var previousEntity))
            {
                changes.Add(new DigitalTwinChange(
                    DigitalTwinChangeType.Added,
                    entity,
                    "Entity was observed in the newer snapshot but not in the comparison snapshot.",
                    false));
                continue;
            }

            var changed = !Equivalent(previousEntity, entity);
            var persistent = changed &&
                             confirmationEntities is not null &&
                             confirmationEntities.TryGetValue(entity.Id, out var confirmedEntity) &&
                             Equivalent(confirmedEntity, entity);
            changes.Add(new DigitalTwinChange(
                changed ? DigitalTwinChangeType.Changed : DigitalTwinChangeType.Unchanged,
                entity,
                changed
                    ? $"Changed fields: {DescribeEntityChanges(previousEntity, entity)}."
                    : "No tracked entity fields changed between these snapshots.",
                persistent));
        }

        foreach (var entity in previous.Entities.Where(entity => !currentEntities.ContainsKey(entity.Id)))
        {
            var remainsAbsent = confirmationEntities is not null &&
                                !confirmationEntities.ContainsKey(entity.Id);
            var potentiallyRemoved = canAssessAbsence && remainsAbsent;
            changes.Add(new DigitalTwinChange(
                potentiallyRemoved ? DigitalTwinChangeType.PotentiallyRemoved : DigitalTwinChangeType.NotObserved,
                entity,
                potentiallyRemoved
                    ? "Entity was absent in two consecutive snapshots with the same non-empty inventory scope. It may have been removed, but this is not proof."
                    : "Entity was not observed in the newer snapshot; the available same-scope follow-up evidence is insufficient to suggest removal.",
                false));
        }

        var previousRelationships = previous.Relationships.ToDictionary(RelationshipKey, StringComparer.OrdinalIgnoreCase);
        var currentRelationships = current.Relationships.ToDictionary(RelationshipKey, StringComparer.OrdinalIgnoreCase);
        var confirmationRelationships = confirmation?.Relationships.ToDictionary(
            RelationshipKey,
            StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in current.Relationships)
        {
            var key = RelationshipKey(relationship);
            if (!previousRelationships.TryGetValue(key, out var previousRelationship))
            {
                var persistent = confirmationRelationships is not null &&
                                 confirmationRelationships.TryGetValue(key, out var confirmedAdded) &&
                                 Equivalent(confirmedAdded, relationship);
                relationshipChanges.Add(new DigitalTwinRelationshipChange(
                    DigitalTwinRelationshipChangeType.Added,
                    relationship,
                    "Relationship evidence was present in the newer snapshot but not in the comparison snapshot.",
                    persistent));
                continue;
            }

            var changed = !Equivalent(previousRelationship, relationship);
            var persists = changed &&
                           confirmationRelationships is not null &&
                           confirmationRelationships.TryGetValue(key, out var confirmedChanged) &&
                           Equivalent(confirmedChanged, relationship);
            relationshipChanges.Add(new DigitalTwinRelationshipChange(
                changed ? DigitalTwinRelationshipChangeType.Changed : DigitalTwinRelationshipChangeType.Unchanged,
                relationship,
                changed
                    ? $"Changed relationship fields: {DescribeRelationshipChanges(previousRelationship, relationship)}. Current evidence source: {relationship.EvidenceSource}; evidence: {relationship.Evidence}"
                    : $"Relationship evidence is unchanged. Source: {relationship.EvidenceSource}; confidence: {relationship.Confidence:P0}.",
                persists));
        }

        foreach (var relationship in previous.Relationships.Where(edge =>
                     !currentRelationships.ContainsKey(RelationshipKey(edge))))
        {
            var key = RelationshipKey(relationship);
            var remainsAbsent = confirmationRelationships is not null &&
                                !confirmationRelationships.ContainsKey(key);
            var potentiallyRemoved = canAssessAbsence && remainsAbsent;
            relationshipChanges.Add(new DigitalTwinRelationshipChange(
                potentiallyRemoved
                    ? DigitalTwinRelationshipChangeType.PotentiallyRemoved
                    : DigitalTwinRelationshipChangeType.NotObserved,
                relationship,
                potentiallyRemoved
                    ? "Relationship evidence was absent in two consecutive snapshots with the same inventory scope; the relationship may have ended, but is not proven removed."
                    : "Relationship was not observed; current evidence does not establish whether it ended or collection was incomplete.",
                false));
        }

        return new DigitalTwinComparison(
            changes
                .OrderBy(change => change.Type)
                .ThenBy(change => change.Entity.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            relationshipChanges
                .OrderBy(change => change.Type)
                .ThenBy(change => change.Relationship.Type)
                .ThenBy(change => change.Relationship.SourceIdentity, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static bool HasComparableScopeEvidence(
        DigitalTwinSnapshot previous,
        DigitalTwinSnapshot current,
        DigitalTwinSnapshot? confirmation) =>
        confirmation is not null &&
        !string.IsNullOrWhiteSpace(previous.InventoryWatermarkScope) &&
        string.Equals(previous.InventoryWatermarkScope, current.InventoryWatermarkScope, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.InventoryWatermarkScope, confirmation.InventoryWatermarkScope, StringComparison.OrdinalIgnoreCase) &&
        previous.InventoryWatermarkScanId is not null &&
        current.InventoryWatermarkScanId is not null &&
        confirmation.InventoryWatermarkScanId is not null &&
        previous.InventoryWatermarkScanId != current.InventoryWatermarkScanId &&
        current.InventoryWatermarkScanId != confirmation.InventoryWatermarkScanId &&
        previous.InventoryWatermarkScanId != confirmation.InventoryWatermarkScanId &&
        previous.CreatedAt < current.CreatedAt &&
        current.CreatedAt < confirmation.CreatedAt;

    private static string RelationshipKey(DigitalTwinRelationship relationship) =>
        $"{relationship.Type}|{relationship.SourceId}|{relationship.TargetId}|{relationship.IsDirected}";

    private static bool Equivalent(DigitalTwinEntity left, DigitalTwinEntity right)
    {
        if (left.Type != right.Type ||
            !string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) ||
            !string.Equals(left.Source, right.Source, StringComparison.Ordinal) ||
            left.Availability != right.Availability ||
            left.Confidence != right.Confidence ||
            left.InventoryType != right.InventoryType ||
            left.Metadata.Count != right.Metadata.Count)
        {
            return false;
        }

        return left.Metadata.All(pair =>
            right.Metadata.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool Equivalent(DigitalTwinRelationship left, DigitalTwinRelationship right) =>
        left.SourceType == right.SourceType &&
        left.TargetType == right.TargetType &&
        left.Type == right.Type &&
        left.IsDirected == right.IsDirected &&
        string.Equals(left.SourceIdentity, right.SourceIdentity, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.TargetIdentity, right.TargetIdentity, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.EvidenceSource, right.EvidenceSource, StringComparison.Ordinal) &&
        string.Equals(left.Evidence, right.Evidence, StringComparison.Ordinal) &&
        left.Confidence == right.Confidence;

    private static string DescribeEntityChanges(DigitalTwinEntity previous, DigitalTwinEntity current)
    {
        var fields = new List<string>();
        if (previous.DisplayName != current.DisplayName) fields.Add("display name");
        if (previous.Source != current.Source) fields.Add("source");
        if (previous.Availability != current.Availability) fields.Add("availability");
        if (previous.Confidence != current.Confidence) fields.Add("confidence");
        if (previous.InventoryType != current.InventoryType) fields.Add("inventory type");
        var metadataKeys = previous.Metadata.Keys
            .Concat(current.Metadata.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(key =>
                !previous.Metadata.TryGetValue(key, out var oldValue) ||
                !current.Metadata.TryGetValue(key, out var newValue) ||
                !string.Equals(oldValue, newValue, StringComparison.Ordinal));
        fields.AddRange(metadataKeys.Select(key => $"metadata '{key}'"));
        return string.Join(", ", fields);
    }

    private static string DescribeRelationshipChanges(
        DigitalTwinRelationship previous,
        DigitalTwinRelationship current)
    {
        var fields = new List<string>();
        if (previous.EvidenceSource != current.EvidenceSource) fields.Add("evidence source");
        if (previous.Evidence != current.Evidence) fields.Add("evidence text");
        if (previous.Confidence != current.Confidence) fields.Add("confidence");
        return string.Join(", ", fields);
    }
}
