using WinForge.Domain.DigitalTwin;

namespace WinForge.Application.DigitalTwin;

public sealed class DigitalTwinGraph
{
    public const int MaximumTraversalDepth = 8;
    public const int MaximumTraversalNodes = 1000;
    public const int MaximumTraversalRelationships = 4000;

    public DigitalTwinTraversalResult FindDependencies(
        DigitalTwinSnapshot snapshot,
        string entityId,
        int maximumDepth = 4,
        int maximumNodes = 250,
        int maximumRelationships = 1000)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateLimits(maximumDepth, maximumNodes, maximumRelationships);
        var entities = snapshot.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        if (!entities.ContainsKey(entityId))
        {
            return new DigitalTwinTraversalResult([], [], false);
        }

        var supportedRelationships = snapshot.Relationships
            .Where(edge => edge.Type == DigitalTwinRelationshipType.DependsOn && edge.IsDirected)
            .Take(maximumRelationships + 1)
            .ToArray();
        var truncated = supportedRelationships.Length > maximumRelationships;
        var edges = supportedRelationships
            .Take(maximumRelationships)
            .GroupBy(edge => edge.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityId };
        var includedEdges = new List<DigitalTwinRelationship>();
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((entityId, 0));

        while (queue.TryDequeue(out var current))
        {
            if (current.Depth >= maximumDepth || !edges.TryGetValue(current.Id, out var outgoing))
            {
                continue;
            }

            foreach (var edge in outgoing)
            {
                if (!entities.ContainsKey(edge.TargetId))
                {
                    continue;
                }

                includedEdges.Add(edge);
                if (visited.Contains(edge.TargetId))
                {
                    continue;
                }

                if (visited.Count >= maximumNodes)
                {
                    truncated = true;
                    break;
                }

                visited.Add(edge.TargetId);
                queue.Enqueue((edge.TargetId, current.Depth + 1));
            }

            if (truncated)
            {
                break;
            }
        }

        var found = visited
            .Select(id => entities[id])
            .OrderBy(entity => entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DigitalTwinTraversalResult(found, includedEdges, truncated);
    }

    public DigitalTwinCycleResult FindCycles(
        DigitalTwinSnapshot snapshot,
        int maximumNodes = MaximumTraversalNodes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateLimits(1, maximumNodes, MaximumTraversalRelationships);
        var entities = snapshot.Entities
            .Take(maximumNodes)
            .ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var truncated = snapshot.Entities.Count > maximumNodes;
        var supportedRelationships = snapshot.Relationships
            .Where(edge => edge.IsDirected && edge.Type == DigitalTwinRelationshipType.DependsOn &&
                           entities.ContainsKey(edge.SourceId) && entities.ContainsKey(edge.TargetId))
            .Take(MaximumTraversalRelationships + 1)
            .ToArray();
        truncated |= supportedRelationships.Length > MaximumTraversalRelationships;
        var adjacency = supportedRelationships
            .Take(MaximumTraversalRelationships)
            .GroupBy(edge => edge.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var cycles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityId in entities.Keys)
        {
            if (state.ContainsKey(entityId))
            {
                continue;
            }

            Visit(entityId);
        }

        return new DigitalTwinCycleResult(cycles.Values.ToArray(), truncated);

        void Visit(string entityId)
        {
            state[entityId] = 1;
            stack.Add(entityId);
            if (adjacency.TryGetValue(entityId, out var targets))
            {
                foreach (var target in targets)
                {
                    if (!state.TryGetValue(target, out var targetState))
                    {
                        Visit(target);
                    }
                    else if (targetState == 1)
                    {
                        var start = stack.FindIndex(item => string.Equals(item, target, StringComparison.OrdinalIgnoreCase));
                        if (start >= 0)
                        {
                            var cycle = stack.Skip(start).Append(target).ToArray();
                            var key = string.Join("|", cycle.Take(cycle.Length - 1).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                            cycles.TryAdd(key, cycle);
                        }
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[entityId] = 2;
        }
    }

    public IReadOnlyList<DigitalTwinEntity> FindOrphans(DigitalTwinSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var connected = snapshot.Relationships
            .SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Entities
            .Where(entity => !connected.Contains(entity.Id))
            .OrderBy(entity => entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<DigitalTwinEntity> FilterByCategory(
        DigitalTwinSnapshot snapshot,
        IEnumerable<DigitalTwinEntityType> categories) =>
        snapshot.Entities
            .Where(entity => categories.Contains(entity.Type))
            .OrderBy(entity => entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateLimits(int depth, int nodes, int relationships)
    {
        if (depth < 1 || depth > MaximumTraversalDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), $"Depth must be between 1 and {MaximumTraversalDepth}.");
        }

        if (nodes < 1 || nodes > MaximumTraversalNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(nodes), $"Node limit must be between 1 and {MaximumTraversalNodes}.");
        }

        if (relationships < 1 || relationships > MaximumTraversalRelationships)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relationships),
                $"Relationship limit must be between 1 and {MaximumTraversalRelationships}.");
        }
    }
}
