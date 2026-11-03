using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WinForge.Application.DigitalTwin;
using WinForge.Domain.DigitalTwin;
using WinForge.Domain.Inventory;
using WinForge.Infrastructure.Persistence;

namespace WinForge.IntegrationTests;

public sealed class DigitalTwinPersistenceTests
{
    [Fact]
    public async Task Snapshot_persistence_round_trips_entities_relationships_and_checksum()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForgeTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "inventory.db");
        try
        {
            var repository = new SqliteInventoryRepository(
                databasePath,
                NullLogger<SqliteInventoryRepository>.Instance);
            await repository.InitializeAsync(CancellationToken.None);
            var application = Entity(DigitalTwinEntityType.Application, "app:sample");
            var process = Entity(DigitalTwinEntityType.Process, "process:100:1000");
            var relationship = new DigitalTwinRelationship(
                process.Type,
                process.IdentityKey,
                application.Type,
                application.IdentityKey,
                DigitalTwinRelationshipType.AssociatedWith,
                "Process inventory",
                "Observed executable path association.",
                0.92m,
                true,
                DateTimeOffset.UtcNow);
            var entities = new[] { application, process };
            var relationships = new[] { relationship };
            var snapshot = new DigitalTwinSnapshot(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                "Software inventory",
                entities,
                relationships,
                DigitalTwinSnapshotHasher.ComputeChecksum(entities, relationships));

            await repository.SaveSnapshotAsync(snapshot, CancellationToken.None);
            var loaded = await repository.GetSnapshotAsync(snapshot.SnapshotId, CancellationToken.None);
            var history = await repository.GetHistoryAsync(10, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(snapshot.Checksum, loaded.Checksum);
            Assert.Equal(snapshot.Entities.Count, loaded.Entities.Count);
            Assert.Equal(snapshot.Relationships, loaded.Relationships);
            Assert.Equal(snapshot.InventoryWatermarkScanId, loaded.InventoryWatermarkScanId);
            Assert.Equal(snapshot.SnapshotId, Assert.Single(history).SnapshotId);

            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await verify.OpenAsync();
            await using var migrationCommand = verify.CreateCommand();
            migrationCommand.CommandText = "SELECT MAX(migration_id) FROM schema_migrations;";
            Assert.Equal(5L, (long)(await migrationCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Snapshot_reader_rejects_modified_entity_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForgeTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "inventory.db");
        try
        {
            var repository = new SqliteInventoryRepository(
                databasePath,
                NullLogger<SqliteInventoryRepository>.Instance);
            await repository.InitializeAsync(CancellationToken.None);
            var entity = Entity(DigitalTwinEntityType.Service, "service:tamper");
            var entities = new[] { entity };
            var snapshot = new DigitalTwinSnapshot(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                null,
                null,
                entities,
                [],
                DigitalTwinSnapshotHasher.ComputeChecksum(entities, []));
            await repository.SaveSnapshotAsync(snapshot, CancellationToken.None);

            await using (var connection = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = databasePath,
                                 Pooling = false
                             }.ToString()))
            {
                await connection.OpenAsync();
                await using var tamper = connection.CreateCommand();
                tamper.CommandText =
                    "UPDATE digital_twin_entities SET display_name = 'Changed' WHERE snapshot_id = $id;";
                tamper.Parameters.AddWithValue("$id", snapshot.SnapshotId.ToString("D"));
                await tamper.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => repository.GetSnapshotAsync(snapshot.SnapshotId, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Large_snapshot_entity_pages_are_stable_bounded_and_filterable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinForgeTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "inventory.db");
        try
        {
            var repository = new SqliteInventoryRepository(
                databasePath,
                NullLogger<SqliteInventoryRepository>.Instance);
            await repository.InitializeAsync(CancellationToken.None);
            var entities = Enumerable.Range(0, 2_500)
                .Select(index => Entity(
                    DigitalTwinEntityType.Service,
                    $"service:{index:D5}") with
                {
                    DisplayName = $"Service {index:D5}"
                })
                .ToArray();
            var relationships = Enumerable.Range(1, 2_499)
                .Select(index => new DigitalTwinRelationship(
                    DigitalTwinEntityType.Service,
                    $"service:{index - 1:D5}",
                    DigitalTwinEntityType.Service,
                    $"service:{index:D5}",
                    DigitalTwinRelationshipType.DependsOn,
                    "Synthetic fixture",
                    "Bounded integration fixture edge.",
                    0.9m,
                    true,
                    DateTimeOffset.UtcNow))
                .ToArray();
            var snapshot = new DigitalTwinSnapshot(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                null,
                null,
                entities,
                relationships,
                DigitalTwinSnapshotHasher.ComputeChecksum(entities, relationships));
            await repository.SaveSnapshotAsync(snapshot, CancellationToken.None);

            var first = await repository.GetEntityPageAsync(
                snapshot.SnapshotId,
                0,
                100,
                DigitalTwinEntityType.Service,
                null,
                CancellationToken.None);
            var second = await repository.GetEntityPageAsync(
                snapshot.SnapshotId,
                100,
                100,
                DigitalTwinEntityType.Service,
                null,
                CancellationToken.None);
            var match = await repository.GetEntityPageAsync(
                snapshot.SnapshotId,
                0,
                100,
                DigitalTwinEntityType.Service,
                "service:02499",
                CancellationToken.None);

            Assert.Equal(2_500, first.TotalCount);
            Assert.Equal(100, first.Entities.Count);
            Assert.True(first.HasNext);
            Assert.False(first.HasPrevious);
            Assert.Equal(100, second.Entities.Count);
            Assert.True(second.HasPrevious);
            Assert.NotEqual(first.Entities[0].Id, second.Entities[0].Id);
            Assert.Single(match.Entities);
            Assert.Equal("service:02499", match.Entities[0].IdentityKey);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.GetEntityPageAsync(
                    snapshot.SnapshotId,
                    0,
                    251,
                    null,
                    null,
                    CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static DigitalTwinEntity Entity(DigitalTwinEntityType type, string key) =>
        new(
            type,
            key,
            key,
            "Synthetic source",
            DateTimeOffset.UtcNow,
            InventoryFieldAvailability.Available,
            0.9m,
            new Dictionary<string, string?> { ["state"] = "Running" },
            type == DigitalTwinEntityType.Service ? InventoryComponentType.Service : null);
}
