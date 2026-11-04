using WinForge.Domain.DigitalTwin;

namespace WinForge.Application.DigitalTwin;

public sealed class DigitalTwinImpactAnalyzer
{
    public const int MaximumImpactDepth = 5;
    public const int MaximumImpactNodes = 200;
    public const int MaximumImpactRelationships = 500;

    public DigitalTwinImpactAnalysis Analyze(
        DigitalTwinSnapshot snapshot,
        string entityId,
        int maximumDepth = 3,
        int maximumNodes = 50,
        int maximumRelationships = 150)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ValidateLimits(maximumDepth, maximumNodes, maximumRelationships);

        var source = snapshot.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return new DigitalTwinImpactAnalysis(
                null,
                [],
                [],
                $"No Digital Twin entity with identity '{entityId}' is present in the selected snapshot.",
                false,
                0);
        }

        var adjacency = BuildAdjacency(snapshot, maximumRelationships);
        var direct = new List<DigitalTwinImpactRecord>();
        var directSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incident = snapshot.Relationships
            .Where(edge => string.Equals(edge.SourceId, entityId, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(edge.TargetId, entityId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(edge => edge.Type)
            .ThenBy(edge => edge.LastObservedAt)
            .Take(maximumRelationships + 1)
            .ToArray();

        foreach (var edge in incident.Take(maximumRelationships))
        {
            var otherId = string.Equals(edge.SourceId, entityId, StringComparison.OrdinalIgnoreCase)
                ? edge.TargetId
                : edge.SourceId;
            if (!directSeen.Add(otherId))
            {
                continue;
            }

            var other = snapshot.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, otherId, StringComparison.OrdinalIgnoreCase));
            if (other is null)
            {
                continue;
            }

            direct.Add(new DigitalTwinImpactRecord(
                other,
                DigitalTwinImpactLevel.Direct,
                1,
                edge.Type,
                $"The selected entity is directly connected by a {edge.Type} relationship using evidence from {edge.EvidenceSource}. This remains an informational impact signal, not a causal claim.",
                ClampConfidence(edge.Confidence),
                [source.DisplayName, other.DisplayName]));
        }

        var indirect = new List<DigitalTwinImpactRecord>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityId };
        var queue = new Queue<(string CurrentId, int Distance, List<string> Path)>();
        foreach (var record in direct)
        {
            var path = new List<string> { source.DisplayName, record.Entity.DisplayName };
            queue.Enqueue((record.Entity.Id, 1, path));
            visited.Add(record.Entity.Id);
        }

        while (queue.Count > 0 && indirect.Count < maximumNodes)
        {
            var current = queue.Dequeue();
            if (current.Distance >= maximumDepth)
            {
                continue;
            }

            if (!adjacency.TryGetValue(current.CurrentId, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                var neighborId = string.Equals(edge.SourceId, current.CurrentId, StringComparison.OrdinalIgnoreCase)
                    ? edge.TargetId
                    : edge.SourceId;
                if (string.Equals(neighborId, entityId, StringComparison.OrdinalIgnoreCase) ||
                    !visited.Add(neighborId))
                {
                    continue;
                }

                var neighbor = snapshot.Entities.FirstOrDefault(entity =>
                    string.Equals(entity.Id, neighborId, StringComparison.OrdinalIgnoreCase));
                if (neighbor is null)
                {
                    continue;
                }

                var path = new List<string>(current.Path)
                {
                    neighbor.DisplayName
                };
                var confidence = ClampConfidence((edge.Confidence + source.Confidence + neighbor.Confidence) / 3m);
                indirect.Add(new DigitalTwinImpactRecord(
                    neighbor,
                    DigitalTwinImpactLevel.Indirect,
                    current.Distance + 1,
                    edge.Type,
                    $"The selected entity is connected through an indirect {edge.Type} path. Confidence is reduced by traversal distance and remains informational.",
                    confidence,
                    path));

                if (current.Distance + 1 < maximumDepth)
                {
                    queue.Enqueue((neighborId, current.Distance + 1, path));
                }
            }
        }

        var totalImpacted = direct.Count + indirect.Count;
        var wasTruncated = direct.Count >= maximumRelationships ||
                           indirect.Count >= maximumNodes ||
                           incident.Length > maximumRelationships;

        var summary = BuildSummary(source, direct, indirect, wasTruncated, maximumDepth, maximumNodes);
        return new DigitalTwinImpactAnalysis(
            source,
            direct,
            indirect,
            summary,
            wasTruncated,
            totalImpacted);
    }

    private static Dictionary<string, List<DigitalTwinRelationship>> BuildAdjacency(
        DigitalTwinSnapshot snapshot,
        int maximumRelationships)
    {
        var adjacency = new Dictionary<string, List<DigitalTwinRelationship>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in snapshot.Relationships
                     .OrderBy(item => item.Type)
                     .ThenBy(item => item.LastObservedAt)
                     .Take(maximumRelationships + 1))
        {
            Add(edge.SourceId, edge);
            Add(edge.TargetId, edge);
        }

        return adjacency;

        void Add(string entityId, DigitalTwinRelationship relationship)
        {
            if (!adjacency.TryGetValue(entityId, out var list))
            {
                list = [];
                adjacency[entityId] = list;
            }

            list.Add(relationship);
        }
    }

    private static string BuildSummary(
        DigitalTwinEntity source,
        IReadOnlyList<DigitalTwinImpactRecord> direct,
        IReadOnlyList<DigitalTwinImpactRecord> indirect,
        bool wasTruncated,
        int maximumDepth,
        int maximumNodes)
    {
        var directCount = direct.Count;
        var indirectCount = indirect.Count;
        var suffix = wasTruncated
            ? $" The analysis is capped at depth {maximumDepth} and {maximumNodes} nodes; some potential impacts were omitted."
            : string.Empty;
        return $"{source.DisplayName} has {directCount} direct adjacent impact candidates and {indirectCount} indirect impact candidates across the current snapshot.{suffix}";
    }

    private static decimal ClampConfidence(decimal value) => Math.Clamp(value, 0m, 1m);

    private static void ValidateLimits(int depth, int nodes, int relationships)
    {
        if (depth < 1 || depth > MaximumImpactDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), $"Depth must be between 1 and {MaximumImpactDepth}.");
        }

        if (nodes < 1 || nodes > MaximumImpactNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(nodes), $"Node limit must be between 1 and {MaximumImpactNodes}.");
        }

        if (relationships < 1 || relationships > MaximumImpactRelationships)
        {
            throw new ArgumentOutOfRangeException(nameof(relationships), $"Relationship limit must be between 1 and {MaximumImpactRelationships}.");
        }
    }
}
