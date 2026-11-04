using WinForge.Application.DigitalTwin;
using WinForge.Domain.DigitalTwin;
using WinForge.Domain.Inventory;

namespace WinForge.UnitTests;

public sealed class DigitalTwinTests
{
    [Fact]
    public void Builder_creates_only_relationships_supported_by_inventory_evidence()
    {
        var application = Component(
            InventoryComponentType.InstalledApplication,
            "registered-app:app",
            "Example",
            "Registry",
            new Dictionary<string, string?> { ["installLocation"] = @"C:\Apps\Example" });
        var process = Component(
            InventoryComponentType.Process,
            "process:100:1000",
            "example",
            "Process API",
            new Dictionary<string, string?>
            {
                ["processId"] = "100",
                ["parentProcessId"] = "50",
                ["executablePath"] = @"C:\Apps\Example\example.exe",
                ["associatedApplicationIdentity"] = application.IdentityKey,
                ["associationEvidence"] = "Path under registered location"
            });
        var parent = Component(
            InventoryComponentType.Process,
            "process:50:900",
            "parent",
            "Process API",
            new Dictionary<string, string?> { ["processId"] = "50" });
        var service = Component(
            InventoryComponentType.Service,
            "service:example",
            "Example Service",
            "Win32_Service",
            new Dictionary<string, string?>
            {
                ["Name"] = "ExampleService",
                ["executablePath"] = @"C:\Apps\Example\service.exe",
                ["Dependencies"] = "BaseService"
            });
        var dependency = Component(
            InventoryComponentType.Service,
            "service:base",
            "Base Service",
            "Win32_Service",
            new Dictionary<string, string?> { ["Name"] = "BaseService" });
        var device = Component(
            InventoryComponentType.Device,
            @"PCI\VEN_1234&DEV_0001",
            "Example device",
            "SetupAPI",
            new Dictionary<string, string?> { ["deviceInstanceId"] = @"PCI\VEN_1234&DEV_0001" });
        var driver = Component(
            InventoryComponentType.DriverPackage,
            "driver:oem1.inf",
            "Example driver",
            "SetupAPI",
            new Dictionary<string, string?> { ["associatedDeviceIds"] = @"PCI\VEN_1234&DEV_0001" });
        var inventory = new PassportSnapshot(
            new LatestScan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "Software and hardware",
                InventoryScanStatus.Completed,
                2,
                2,
                []),
            [application, process, parent, service, dependency, device, driver]);

        var snapshot = new DigitalTwinBuilder().Build(inventory);

        Assert.Contains(snapshot.Entities, entity =>
            entity.Type == DigitalTwinEntityType.Executable &&
            entity.IdentityKey.Equals(@"C:\Apps\Example\example.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Relationships, edge =>
            edge.Type == DigitalTwinRelationshipType.DependsOn &&
            edge.SourceIdentity == process.IdentityKey &&
            edge.TargetIdentity == parent.IdentityKey);
        Assert.Contains(snapshot.Relationships, edge =>
            edge.Type == DigitalTwinRelationshipType.AssociatedWith &&
            edge.SourceIdentity == process.IdentityKey &&
            edge.TargetIdentity == application.IdentityKey);
        Assert.Contains(snapshot.Relationships, edge =>
            edge.Type == DigitalTwinRelationshipType.AssociatedWith &&
            edge.SourceIdentity == application.IdentityKey &&
            edge.TargetIdentity == service.IdentityKey);
        Assert.Contains(snapshot.Relationships, edge =>
            edge.Type == DigitalTwinRelationshipType.DependsOn &&
            edge.SourceIdentity == service.IdentityKey &&
            edge.TargetIdentity == dependency.IdentityKey);
        Assert.Contains(snapshot.Relationships, edge =>
            edge.Type == DigitalTwinRelationshipType.UsesPackage &&
            edge.SourceIdentity == device.IdentityKey &&
            edge.TargetIdentity == driver.IdentityKey);
        Assert.All(snapshot.Relationships, edge =>
        {
            Assert.NotEmpty(edge.Evidence);
            Assert.NotEmpty(edge.EvidenceSource);
            Assert.InRange(edge.Confidence, 0m, 1m);
        });
    }

    [Fact]
    public void Graph_finds_dependencies_cycles_orphans_and_enforces_traversal_limits()
    {
        var a = Entity(DigitalTwinEntityType.Service, "service:a");
        var b = Entity(DigitalTwinEntityType.Service, "service:b");
        var c = Entity(DigitalTwinEntityType.Service, "service:c");
        var orphan = Entity(DigitalTwinEntityType.Application, "application:orphan");
        var edges = new[]
        {
            Relationship(a, b),
            Relationship(b, c),
            Relationship(c, a)
        };
        var snapshot = Snapshot([a, b, c, orphan], edges);
        var graph = new DigitalTwinGraph();

        var traversal = graph.FindDependencies(snapshot, a.Id);
        var cycles = graph.FindCycles(snapshot);
        var orphans = graph.FindOrphans(snapshot);

        Assert.Equal(3, traversal.Entities.Count);
        Assert.False(traversal.WasTruncated);
        Assert.Single(cycles.Cycles);
        Assert.Single(orphans);
        Assert.Equal(orphan.Id, orphans[0].Id);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => graph.FindDependencies(snapshot, a.Id, maximumDepth: 99));
    }

    [Fact]
    public void Impact_analyzer_identifies_direct_and_indirect_influence_without_claiming_causation()
    {
        var app = Entity(DigitalTwinEntityType.Application, "application:app");
        var service = Entity(DigitalTwinEntityType.Service, "service:svc");
        var dependency = Entity(DigitalTwinEntityType.Service, "service:database");
        var driver = Entity(DigitalTwinEntityType.Driver, "driver:storage");
        var snapshot = Snapshot(
            [app, service, dependency, driver],
            [
                Relationship(app, service),
                Relationship(service, dependency),
                Relationship(service, driver, DigitalTwinRelationshipType.UsesPackage),
                Relationship(driver, service, DigitalTwinRelationshipType.AssociatedWith)
            ]);

        var impact = new DigitalTwinImpactAnalyzer().Analyze(snapshot, app.Id, maximumDepth: 3, maximumNodes: 25, maximumRelationships: 25);

        Assert.Equal(app.Id, impact.SourceEntity!.Id);
        Assert.Contains(impact.DirectlyAffected, record => record.Entity.Id == service.Id);
        Assert.Contains(impact.IndirectlyAffected, record => record.Entity.Id == dependency.Id || record.Entity.Id == driver.Id);
        Assert.All(impact.DirectlyAffected.Concat(impact.IndirectlyAffected), record =>
            Assert.InRange(record.Confidence, 0m, 1m));
        Assert.Contains("direct", impact.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Impact_analyzer_respects_depth_and_relationship_limits()
    {
        var source = Entity(DigitalTwinEntityType.Application, "application:root");
        var affected = Enumerable.Range(0, 80)
            .Select(index => Entity(DigitalTwinEntityType.Service, $"service:{index:D3}"))
            .ToArray();
        var relationships = affected
            .Select((entity, index) => Relationship(source, entity, DigitalTwinRelationshipType.DependsOn))
            .ToArray();
        var snapshot = Snapshot([source, .. affected], relationships);

        var impact = new DigitalTwinImpactAnalyzer().Analyze(
            snapshot,
            source.Id,
            maximumDepth: 2,
            maximumNodes: 10,
            maximumRelationships: 5);

        Assert.True(impact.WasTruncated);
        Assert.True(impact.DirectlyAffected.Count <= 5);
        Assert.True(impact.TotalImpactedEntities <= 10 + 5);
    }

    [Fact]
    public void Change_detector_reports_missing_entities_as_not_observed_and_tracks_persistence()
    {
        var oldEntity = Entity(DigitalTwinEntityType.Service, "service:old");
        var currentEntity = Entity(DigitalTwinEntityType.Service, "service:current", "New state");
        var priorSnapshot = Snapshot([oldEntity, currentEntity with
        {
            Metadata = new Dictionary<string, string?> { ["state"] = "Old state" }
        }], []);
        var middleSnapshot = Snapshot([currentEntity with
        {
            Metadata = new Dictionary<string, string?> { ["state"] = "New state" }
        }], []);
        var currentSnapshot = Snapshot([currentEntity with
        {
            Metadata = new Dictionary<string, string?> { ["state"] = "New state" }
        }], []);

        var changes = new DigitalTwinChangeDetector().Compare(
            priorSnapshot,
            currentSnapshot,
            middleSnapshot);

        Assert.Contains(changes, change =>
            change.Type == DigitalTwinChangeType.NotObserved &&
            change.Entity.Id == oldEntity.Id &&
            !change.Explanation.Contains("removed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(changes, change =>
            change.Type == DigitalTwinChangeType.Changed &&
            change.Entity.Id == currentEntity.Id &&
            change.Explanation.Contains("metadata 'state'", StringComparison.Ordinal) &&
            change.PersistentAcrossSnapshots);
    }

    [Fact]
    public void Change_detector_reports_potential_removal_only_after_same_scope_follow_up_and_compares_relationships()
    {
        var firstEntity = Entity(DigitalTwinEntityType.Service, "service:first");
        var secondEntity = Entity(DigitalTwinEntityType.Service, "service:second");
        var originalRelationship = Relationship(firstEntity, secondEntity);
        var updatedRelationship = originalRelationship with
        {
            Evidence = "Updated dependency evidence.",
            LastObservedAt = DateTimeOffset.UtcNow
        };
        var start = DateTimeOffset.UtcNow.AddMinutes(-3);
        var previous = ScopedSnapshot(
            start,
            [firstEntity, secondEntity],
            [originalRelationship]);
        var current = ScopedSnapshot(
            start.AddMinutes(1),
            [secondEntity],
            [updatedRelationship]);
        var confirmation = ScopedSnapshot(
            start.AddMinutes(2),
            [secondEntity],
            [updatedRelationship]);

        var comparison = new DigitalTwinChangeDetector().CompareDetailed(previous, current, confirmation);

        Assert.Contains(comparison.EntityChanges, change =>
            change.Type == DigitalTwinChangeType.PotentiallyRemoved &&
            change.Entity.Id == firstEntity.Id &&
            change.Explanation.Contains("may have been removed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comparison.RelationshipChanges, change =>
            change.Type == DigitalTwinRelationshipChangeType.Changed &&
            change.PersistentAcrossSnapshots);

        var firstComparisonOnly = new DigitalTwinChangeDetector().CompareDetailed(previous, current);
        Assert.Contains(firstComparisonOnly.EntityChanges, change =>
            change.Type == DigitalTwinChangeType.NotObserved && change.Entity.Id == firstEntity.Id);
    }

    [Fact]
    public void Large_graph_traversals_enforce_node_relationship_and_cycle_limits()
    {
        var entities = Enumerable.Range(0, 1_500)
            .Select(index => Entity(DigitalTwinEntityType.Service, $"service:{index:D4}"))
            .ToArray();
        var relationships = entities.Skip(1)
            .Select(entity => new DigitalTwinRelationship(
                entities[0].Type,
                entities[0].IdentityKey,
                entity.Type,
                entity.IdentityKey,
                DigitalTwinRelationshipType.DependsOn,
                "Synthetic large graph",
                "Test evidence.",
                0.9m,
                true,
                DateTimeOffset.UtcNow))
            .ToArray();
        var snapshot = Snapshot(entities, relationships);
        var graph = new DigitalTwinGraph();

        var nodeBounded = graph.FindDependencies(
            snapshot,
            entities[0].Id,
            maximumDepth: 2,
            maximumNodes: 50,
            maximumRelationships: 250);
        var relationshipBounded = graph.FindDependencies(
            snapshot,
            entities[0].Id,
            maximumDepth: 2,
            maximumNodes: 1_000,
            maximumRelationships: 20);
        var cycles = graph.FindCycles(snapshot, maximumNodes: 50);

        Assert.Equal(50, nodeBounded.Entities.Count);
        Assert.True(nodeBounded.Relationships.Count <= 250);
        Assert.True(nodeBounded.WasTruncated);
        Assert.Equal(20, relationshipBounded.Relationships.Count);
        Assert.True(relationshipBounded.WasTruncated);
        Assert.True(cycles.WasTruncated);
    }

    private static InventoryComponent Component(
        InventoryComponentType type,
        string identity,
        string name,
        string source,
        IReadOnlyDictionary<string, string?> details) =>
        new(type, identity, name, source, 2, DateTimeOffset.UtcNow, details)
        {
            Category = type.ToString(),
            FieldAvailability = details.ToDictionary(
                pair => pair.Key,
                _ => InventoryFieldAvailability.Available,
                StringComparer.Ordinal)
        };

    private static DigitalTwinEntity Entity(
        DigitalTwinEntityType type,
        string identity,
        string state = "Old state") =>
        new(
            type,
            identity,
            identity,
            "Test source",
            DateTimeOffset.UtcNow,
            InventoryFieldAvailability.Available,
            0.9m,
            new Dictionary<string, string?> { ["state"] = state });

    private static DigitalTwinRelationship Relationship(
        DigitalTwinEntity source,
        DigitalTwinEntity target,
        DigitalTwinRelationshipType type = DigitalTwinRelationshipType.DependsOn) =>
        new(
            source.Type,
            source.IdentityKey,
            target.Type,
            target.IdentityKey,
            type,
            "Synthetic relationship evidence",
            $"{source.IdentityKey} {type} {target.IdentityKey}.",
            0.9m,
            true,
            DateTimeOffset.UtcNow);

    private static DigitalTwinSnapshot Snapshot(
        IReadOnlyList<DigitalTwinEntity> entities,
        IReadOnlyList<DigitalTwinRelationship> relationships) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            null,
            entities,
            relationships,
            DigitalTwinSnapshotHasher.ComputeChecksum(entities, relationships));

    private static DigitalTwinSnapshot ScopedSnapshot(
        DateTimeOffset createdAt,
        IReadOnlyList<DigitalTwinEntity> entities,
        IReadOnlyList<DigitalTwinRelationship> relationships) =>
        new(
            Guid.NewGuid(),
            createdAt,
            Guid.NewGuid(),
            "All",
            entities,
            relationships,
            DigitalTwinSnapshotHasher.ComputeChecksum(entities, relationships));
}
