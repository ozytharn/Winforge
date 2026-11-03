using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WinForge.Application.DigitalTwin;
using WinForge.Application.Inventory;
using WinForge.Application.Monitoring;
using WinForge.Application.Recovery;
using WinForge.Domain.DigitalTwin;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;
using WinForge.Domain.Recovery;

namespace WinForge.Infrastructure.Persistence;

public sealed class SqliteInventoryRepository :
    IInventoryRepository,
    IDigitalTwinRepository,
    IBackupInspectionRepository,
    IGuardianTelemetryRepository
{
    private const int CurrentMigration = 5;
    private const string InitialSchema = """
        CREATE TABLE scans (
            scan_id TEXT PRIMARY KEY NOT NULL,
            started_utc TEXT NOT NULL,
            completed_utc TEXT,
            scope TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('InProgress', 'Completed', 'Partial', 'Failed', 'Cancelled')),
            attempted_collectors INTEGER NOT NULL DEFAULT 0,
            total_collectors INTEGER NOT NULL CHECK (total_collectors >= 0)
        );

        CREATE TABLE components (
            component_id TEXT PRIMARY KEY NOT NULL,
            component_type TEXT NOT NULL,
            identity_key TEXT NOT NULL,
            display_name TEXT NOT NULL,
            source TEXT NOT NULL,
            first_seen_utc TEXT NOT NULL,
            last_verified_utc TEXT NOT NULL,
            UNIQUE (component_type, identity_key)
        );

        CREATE TABLE observations (
            observation_id TEXT PRIMARY KEY NOT NULL,
            scan_id TEXT NOT NULL REFERENCES scans(scan_id),
            component_id TEXT NOT NULL REFERENCES components(component_id),
            observed_utc TEXT NOT NULL,
            details_schema_version INTEGER NOT NULL CHECK (details_schema_version > 0),
            details_json TEXT NOT NULL,
            UNIQUE (scan_id, component_id)
        );

        CREATE TABLE collector_issues (
            issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
            scan_id TEXT NOT NULL REFERENCES scans(scan_id),
            collector_id TEXT NOT NULL,
            code TEXT NOT NULL,
            message TEXT NOT NULL
        );

        CREATE INDEX ix_observations_component_time
            ON observations(component_id, observed_utc DESC);
        CREATE INDEX ix_scans_started
            ON scans(started_utc DESC);
        """;
    private const string CategoryMigration = """
        ALTER TABLE components ADD COLUMN category TEXT NOT NULL DEFAULT 'General';
        CREATE INDEX ix_components_category ON components(category);
        """;
    private const string DigitalTwinMigration = """
        CREATE TABLE digital_twin_snapshots (
            snapshot_id TEXT PRIMARY KEY NOT NULL,
            created_utc TEXT NOT NULL,
            inventory_watermark_scan_id TEXT,
            inventory_watermark_scope TEXT,
            entity_count INTEGER NOT NULL CHECK (entity_count >= 0),
            relationship_count INTEGER NOT NULL CHECK (relationship_count >= 0),
            checksum TEXT NOT NULL
        );

        CREATE TABLE digital_twin_entities (
            snapshot_id TEXT NOT NULL REFERENCES digital_twin_snapshots(snapshot_id) ON DELETE CASCADE,
            entity_type TEXT NOT NULL,
            identity_key TEXT NOT NULL,
            display_name TEXT NOT NULL,
            source TEXT NOT NULL,
            last_observed_utc TEXT NOT NULL,
            availability TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            inventory_type TEXT,
            metadata_json TEXT NOT NULL,
            PRIMARY KEY (snapshot_id, entity_type, identity_key)
        );

        CREATE TABLE digital_twin_relationships (
            snapshot_id TEXT NOT NULL REFERENCES digital_twin_snapshots(snapshot_id) ON DELETE CASCADE,
            source_type TEXT NOT NULL,
            source_identity TEXT NOT NULL,
            target_type TEXT NOT NULL,
            target_identity TEXT NOT NULL,
            relationship_type TEXT NOT NULL,
            evidence_source TEXT NOT NULL,
            evidence TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            is_directed INTEGER NOT NULL CHECK (is_directed IN (0, 1)),
            last_observed_utc TEXT NOT NULL,
            PRIMARY KEY (
                snapshot_id, source_type, source_identity, target_type, target_identity, relationship_type
            )
        );

        CREATE INDEX ix_digital_twin_snapshots_created
            ON digital_twin_snapshots(created_utc DESC);
        CREATE INDEX ix_digital_twin_entities_category
            ON digital_twin_entities(snapshot_id, entity_type, display_name COLLATE NOCASE);
        CREATE INDEX ix_digital_twin_relationships_source
            ON digital_twin_relationships(snapshot_id, source_type, source_identity);
        CREATE INDEX ix_digital_twin_relationships_target
            ON digital_twin_relationships(snapshot_id, target_type, target_identity);
        """;
    private const string BackupInspectionMigration = """
        CREATE TABLE backup_inspections (
            inspection_id TEXT PRIMARY KEY NOT NULL,
            candidate_identity TEXT NOT NULL,
            provider TEXT NOT NULL,
            location TEXT,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NOT NULL,
            state TEXT NOT NULL,
            summary TEXT NOT NULL,
            recognized_structure INTEGER NOT NULL CHECK (recognized_structure IN (0, 1)),
            discovered_structure_groups INTEGER,
            metadata_bytes_read INTEGER,
            backup_container_bytes INTEGER,
            checks_json TEXT NOT NULL,
            limitations_json TEXT NOT NULL
        );

        CREATE INDEX ix_backup_inspections_candidate_time
            ON backup_inspections(candidate_identity, completed_utc DESC, inspection_id DESC);
        """;
    private const string GuardianTelemetryMigration = """
        CREATE TABLE guardian_telemetry_samples (
            sample_id TEXT PRIMARY KEY NOT NULL,
            collected_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL
        );

        CREATE INDEX ix_guardian_telemetry_samples_collected
            ON guardian_telemetry_samples(collected_utc DESC, sample_id DESC);
        """;

    private static readonly string InitialSchemaChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(InitialSchema)));
    private static readonly string CategoryMigrationChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CategoryMigration)));
    private static readonly string DigitalTwinMigrationChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(DigitalTwinMigration)));
    private static readonly string BackupInspectionMigrationChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(BackupInspectionMigration)));
    private static readonly string GuardianTelemetryMigrationChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(GuardianTelemetryMigration)));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteInventoryRepository> _logger;

    public SqliteInventoryRepository(
        string databasePath,
        ILogger<SqliteInventoryRepository> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(directory);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                migration_id INTEGER PRIMARY KEY NOT NULL,
                checksum TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """,
            cancellationToken,
            transaction).ConfigureAwait(false);

        var newestMigration = await ReadNewestMigrationAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (newestMigration > CurrentMigration)
        {
            throw new InvalidDataException(
                $"The database schema version {newestMigration} is newer than the supported version {CurrentMigration}.");
        }

        if (newestMigration == 0)
        {
            await ExecuteAsync(connection, InitialSchema, cancellationToken, transaction).ConfigureAwait(false);
            await RecordMigrationAsync(
                connection,
                transaction,
                1,
                InitialSchemaChecksum,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ValidateMigrationAsync(
                connection,
                transaction,
                1,
                InitialSchemaChecksum,
                cancellationToken).ConfigureAwait(false);
        }

        var categoryMigrationChecksum = await ReadMigrationChecksumAsync(
            connection,
            transaction,
            2,
            cancellationToken).ConfigureAwait(false);
        if (categoryMigrationChecksum is null)
        {
            await ExecuteAsync(connection, CategoryMigration, cancellationToken, transaction).ConfigureAwait(false);
            await RecordMigrationAsync(
                connection,
                transaction,
                2,
                CategoryMigrationChecksum,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(categoryMigrationChecksum, CategoryMigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Database migration 2 has a checksum that does not match this application.");
        }

        var digitalTwinMigrationChecksum = await ReadMigrationChecksumAsync(
            connection,
            transaction,
            3,
            cancellationToken).ConfigureAwait(false);
        if (digitalTwinMigrationChecksum is null)
        {
            await ExecuteAsync(connection, DigitalTwinMigration, cancellationToken, transaction).ConfigureAwait(false);
            await RecordMigrationAsync(
                connection,
                transaction,
                3,
                DigitalTwinMigrationChecksum,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(digitalTwinMigrationChecksum, DigitalTwinMigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Database migration 3 has a checksum that does not match this application.");
        }

        var backupInspectionMigrationChecksum = await ReadMigrationChecksumAsync(
            connection,
            transaction,
            4,
            cancellationToken).ConfigureAwait(false);
        if (backupInspectionMigrationChecksum is null)
        {
            await ExecuteAsync(connection, BackupInspectionMigration, cancellationToken, transaction).ConfigureAwait(false);
            await RecordMigrationAsync(
                connection,
                transaction,
                4,
                BackupInspectionMigrationChecksum,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(
                     backupInspectionMigrationChecksum,
                     BackupInspectionMigrationChecksum,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Database migration 4 has a checksum that does not match this application.");
        }

        var guardianTelemetryMigrationChecksum = await ReadMigrationChecksumAsync(
            connection,
            transaction,
            5,
            cancellationToken).ConfigureAwait(false);
        if (guardianTelemetryMigrationChecksum is null)
        {
            await ExecuteAsync(connection, GuardianTelemetryMigration, cancellationToken, transaction).ConfigureAwait(false);
            await RecordMigrationAsync(
                connection,
                transaction,
                5,
                GuardianTelemetryMigrationChecksum,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(
                     guardianTelemetryMigrationChecksum,
                     GuardianTelemetryMigrationChecksum,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Database migration 5 has a checksum that does not match this application.");
        }

        transaction.Commit();
        _logger.LogInformation("WinForge database initialized at {DatabasePath}.", _databasePath);
    }

    public async Task StartScanAsync(
        Guid scanId,
        DateTimeOffset startedAt,
        string scope,
        int totalCollectors,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO scans (
                scan_id, started_utc, scope, status, attempted_collectors, total_collectors
            ) VALUES ($id, $started, $scope, 'InProgress', 0, $total);
            """;
        command.Parameters.AddWithValue("$id", scanId.ToString("D"));
        command.Parameters.AddWithValue("$started", FormatTimestamp(startedAt));
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$total", totalCollectors);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteScanAsync(ScanRecord scan, CancellationToken cancellationToken)
    {
        if (scan.Status == InventoryScanStatus.InProgress)
        {
            throw new ArgumentException("A completed scan cannot retain the InProgress status.", nameof(scan));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        await using (var updateScan = connection.CreateCommand())
        {
            updateScan.Transaction = transaction;
            updateScan.CommandText =
                """
                UPDATE scans
                SET completed_utc = $completed,
                    status = $status,
                    attempted_collectors = $attempted,
                    total_collectors = $total
                WHERE scan_id = $id AND status = 'InProgress';
                """;
            updateScan.Parameters.AddWithValue("$completed", FormatTimestamp(scan.CompletedAt));
            updateScan.Parameters.AddWithValue("$status", scan.Status.ToString());
            updateScan.Parameters.AddWithValue("$attempted", scan.AttemptedCollectors);
            updateScan.Parameters.AddWithValue("$total", scan.TotalCollectors);
            updateScan.Parameters.AddWithValue("$id", scan.ScanId.ToString("D"));

            if (await updateScan.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Scan {scan.ScanId} was not in progress.");
            }
        }

        foreach (var component in scan.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var componentId = await UpsertComponentAsync(
                connection,
                transaction,
                component,
                cancellationToken).ConfigureAwait(false);

            await using var observation = connection.CreateCommand();
            observation.Transaction = transaction;
            observation.CommandText =
                """
                INSERT INTO observations (
                    observation_id, scan_id, component_id, observed_utc, details_schema_version, details_json
                ) VALUES ($observation, $scan, $component, $observed, $details_version, $details);
                """;
            observation.Parameters.AddWithValue("$observation", Guid.NewGuid().ToString("D"));
            observation.Parameters.AddWithValue("$scan", scan.ScanId.ToString("D"));
            observation.Parameters.AddWithValue("$component", componentId);
            observation.Parameters.AddWithValue("$observed", FormatTimestamp(component.ObservedAt));
            observation.Parameters.AddWithValue("$details_version", component.DetailsSchemaVersion);
            if (component.DetailsSchemaVersion is not 1 and not 2)
            {
                throw new InvalidDataException(
                    $"Unsupported inventory details schema version {component.DetailsSchemaVersion}.");
            }

            var detailsPayload = component.DetailsSchemaVersion == 1
                ? JsonSerializer.Serialize(component.Details, JsonOptions)
                : JsonSerializer.Serialize(
                    new InventoryObservationPayload(
                        component.Details.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                        component.FieldAvailability.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal),
                        component.DisplayName,
                        component.Category,
                        component.Source),
                    JsonOptions);
            observation.Parameters.AddWithValue("$details", detailsPayload);
            await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var issue in scan.Issues)
        {
            await using var insertIssue = connection.CreateCommand();
            insertIssue.Transaction = transaction;
            insertIssue.CommandText =
                "INSERT INTO collector_issues (scan_id, collector_id, code, message) VALUES ($scan, $collector, $code, $message);";
            insertIssue.Parameters.AddWithValue("$scan", scan.ScanId.ToString("D"));
            insertIssue.Parameters.AddWithValue("$collector", issue.CollectorId);
            insertIssue.Parameters.AddWithValue("$code", issue.Code);
            insertIssue.Parameters.AddWithValue("$message", issue.Message);
            await insertIssue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task<PassportSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var latestScan = await ReadLatestScanAsync(connection, cancellationToken).ConfigureAwait(false);
        var components = await ReadLatestComponentsAsync(connection, cancellationToken).ConfigureAwait(false);
        var comparableScans = await ReadComparableScansAsync(connection, cancellationToken).ConfigureAwait(false);
        var currentComparisonComponents = comparableScans.Count > 0
            ? await ReadComponentsForScanAsync(connection, comparableScans[0].ScanId, cancellationToken)
                .ConfigureAwait(false)
            : [];
        var previousComparisonComponents = comparableScans.Count > 1
            ? await ReadComponentsForScanAsync(connection, comparableScans[1].ScanId, cancellationToken)
                .ConfigureAwait(false)
            : [];
        return new PassportSnapshot(latestScan, components)
        {
            ComparisonScan = comparableScans.ElementAtOrDefault(0),
            PreviousComparisonScan = comparableScans.ElementAtOrDefault(1),
            ComparisonComponents = currentComparisonComponents,
            PreviousComparisonComponents = previousComparisonComponents
        };
    }

    public async Task SaveSnapshotAsync(
        DigitalTwinSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var calculatedChecksum = DigitalTwinSnapshotHasher.ComputeChecksum(
            snapshot.Entities,
            snapshot.Relationships);
        if (!string.Equals(calculatedChecksum, snapshot.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Digital Twin snapshot checksum does not match its contents.");
        }

        var entityIds = snapshot.Entities
            .Select(entity => entity.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Relationships.Any(relationship =>
                !entityIds.Contains(relationship.SourceId) || !entityIds.Contains(relationship.TargetId)))
        {
            throw new InvalidDataException("The Digital Twin snapshot contains a relationship to an unknown entity.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using (var insertSnapshot = connection.CreateCommand())
        {
            insertSnapshot.Transaction = transaction;
            insertSnapshot.CommandText =
                """
                INSERT INTO digital_twin_snapshots (
                    snapshot_id, created_utc, inventory_watermark_scan_id, inventory_watermark_scope,
                    entity_count, relationship_count, checksum
                ) VALUES ($id, $created, $scan, $scope, $entities, $relationships, $checksum);
                """;
            insertSnapshot.Parameters.AddWithValue("$id", snapshot.SnapshotId.ToString("D"));
            insertSnapshot.Parameters.AddWithValue("$created", FormatTimestamp(snapshot.CreatedAt));
            insertSnapshot.Parameters.AddWithValue(
                "$scan",
                snapshot.InventoryWatermarkScanId is { } scanId ? scanId.ToString("D") : DBNull.Value);
            insertSnapshot.Parameters.AddWithValue("$scope", (object?)snapshot.InventoryWatermarkScope ?? DBNull.Value);
            insertSnapshot.Parameters.AddWithValue("$entities", snapshot.Entities.Count);
            insertSnapshot.Parameters.AddWithValue("$relationships", snapshot.Relationships.Count);
            insertSnapshot.Parameters.AddWithValue("$checksum", snapshot.Checksum);
            await insertSnapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entity in snapshot.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var insertEntity = connection.CreateCommand();
            insertEntity.Transaction = transaction;
            insertEntity.CommandText =
                """
                INSERT INTO digital_twin_entities (
                    snapshot_id, entity_type, identity_key, display_name, source, last_observed_utc,
                    availability, confidence, inventory_type, metadata_json
                ) VALUES (
                    $snapshot, $type, $identity, $name, $source, $observed,
                    $availability, $confidence, $inventory_type, $metadata
                );
                """;
            insertEntity.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId.ToString("D"));
            insertEntity.Parameters.AddWithValue("$type", entity.Type.ToString());
            insertEntity.Parameters.AddWithValue("$identity", entity.IdentityKey);
            insertEntity.Parameters.AddWithValue("$name", entity.DisplayName);
            insertEntity.Parameters.AddWithValue("$source", entity.Source);
            insertEntity.Parameters.AddWithValue("$observed", FormatTimestamp(entity.LastObservedAt));
            insertEntity.Parameters.AddWithValue("$availability", entity.Availability.ToString());
            insertEntity.Parameters.AddWithValue("$confidence", (double)entity.Confidence);
            insertEntity.Parameters.AddWithValue(
                "$inventory_type",
                entity.InventoryType is { } inventoryType ? inventoryType.ToString() : DBNull.Value);
            insertEntity.Parameters.AddWithValue(
                "$metadata",
                JsonSerializer.Serialize(
                    entity.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    JsonOptions));
            await insertEntity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var relationship in snapshot.Relationships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var insertRelationship = connection.CreateCommand();
            insertRelationship.Transaction = transaction;
            insertRelationship.CommandText =
                """
                INSERT INTO digital_twin_relationships (
                    snapshot_id, source_type, source_identity, target_type, target_identity,
                    relationship_type, evidence_source, evidence, confidence, is_directed, last_observed_utc
                ) VALUES (
                    $snapshot, $source_type, $source_identity, $target_type, $target_identity,
                    $relationship_type, $evidence_source, $evidence, $confidence, $directed, $observed
                );
                """;
            insertRelationship.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId.ToString("D"));
            insertRelationship.Parameters.AddWithValue("$source_type", relationship.SourceType.ToString());
            insertRelationship.Parameters.AddWithValue("$source_identity", relationship.SourceIdentity);
            insertRelationship.Parameters.AddWithValue("$target_type", relationship.TargetType.ToString());
            insertRelationship.Parameters.AddWithValue("$target_identity", relationship.TargetIdentity);
            insertRelationship.Parameters.AddWithValue("$relationship_type", relationship.Type.ToString());
            insertRelationship.Parameters.AddWithValue("$evidence_source", relationship.EvidenceSource);
            insertRelationship.Parameters.AddWithValue("$evidence", relationship.Evidence);
            insertRelationship.Parameters.AddWithValue("$confidence", (double)relationship.Confidence);
            insertRelationship.Parameters.AddWithValue("$directed", relationship.IsDirected ? 1 : 0);
            insertRelationship.Parameters.AddWithValue("$observed", FormatTimestamp(relationship.LastObservedAt));
            await insertRelationship.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task<DigitalTwinSnapshot?> GetSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset createdAt;
        Guid? watermarkScanId;
        string? watermarkScope;
        string expectedChecksum;
        int expectedEntityCount;
        int expectedRelationshipCount;
        await using (var readSnapshot = connection.CreateCommand())
        {
            readSnapshot.CommandText =
                """
                SELECT created_utc, inventory_watermark_scan_id, inventory_watermark_scope,
                       entity_count, relationship_count, checksum
                FROM digital_twin_snapshots WHERE snapshot_id = $id;
                """;
            readSnapshot.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await using var reader = await readSnapshot.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            createdAt = ParseTimestamp(reader.GetString(0));
            watermarkScanId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            watermarkScope = reader.IsDBNull(2) ? null : reader.GetString(2);
            expectedEntityCount = reader.GetInt32(3);
            expectedRelationshipCount = reader.GetInt32(4);
            expectedChecksum = reader.GetString(5);
        }

        var entities = new List<DigitalTwinEntity>(expectedEntityCount);
        await using (var readEntities = connection.CreateCommand())
        {
            readEntities.CommandText =
                """
                SELECT entity_type, identity_key, display_name, source, last_observed_utc,
                       availability, confidence, inventory_type, metadata_json
                FROM digital_twin_entities
                WHERE snapshot_id = $id
                ORDER BY entity_type, identity_key COLLATE NOCASE;
                """;
            readEntities.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await using var reader = await readEntities.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, string?>>(
                                   reader.GetString(8),
                                   JsonOptions)
                               ?? throw new InvalidDataException("A Digital Twin entity has invalid metadata.");
                entities.Add(new DigitalTwinEntity(
                    ParseEnum<DigitalTwinEntityType>(reader.GetString(0), "entity type"),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseTimestamp(reader.GetString(4)),
                    ParseEnum<InventoryFieldAvailability>(reader.GetString(5), "field availability"),
                    Convert.ToDecimal(reader.GetDouble(6), CultureInfo.InvariantCulture),
                    metadata,
                    reader.IsDBNull(7)
                        ? null
                        : ParseEnum<InventoryComponentType>(reader.GetString(7), "inventory component type")));
            }
        }

        var relationships = new List<DigitalTwinRelationship>(expectedRelationshipCount);
        await using (var readRelationships = connection.CreateCommand())
        {
            readRelationships.CommandText =
                """
                SELECT source_type, source_identity, target_type, target_identity, relationship_type,
                       evidence_source, evidence, confidence, is_directed, last_observed_utc
                FROM digital_twin_relationships
                WHERE snapshot_id = $id
                ORDER BY source_type, source_identity COLLATE NOCASE, target_type, target_identity COLLATE NOCASE;
                """;
            readRelationships.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await using var reader = await readRelationships.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                relationships.Add(new DigitalTwinRelationship(
                    ParseEnum<DigitalTwinEntityType>(reader.GetString(0), "relationship source type"),
                    reader.GetString(1),
                    ParseEnum<DigitalTwinEntityType>(reader.GetString(2), "relationship target type"),
                    reader.GetString(3),
                    ParseEnum<DigitalTwinRelationshipType>(reader.GetString(4), "relationship type"),
                    reader.GetString(5),
                    reader.GetString(6),
                    Convert.ToDecimal(reader.GetDouble(7), CultureInfo.InvariantCulture),
                    reader.GetInt32(8) != 0,
                    ParseTimestamp(reader.GetString(9))));
            }
        }

        if (entities.Count != expectedEntityCount || relationships.Count != expectedRelationshipCount)
        {
            throw new InvalidDataException("Digital Twin snapshot record counts do not match the stored metadata.");
        }

        var calculatedChecksum = DigitalTwinSnapshotHasher.ComputeChecksum(entities, relationships);
        if (!string.Equals(calculatedChecksum, expectedChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Digital Twin snapshot integrity validation failed.");
        }

        return new DigitalTwinSnapshot(
            snapshotId,
            createdAt,
            watermarkScanId,
            watermarkScope,
            entities,
            relationships,
            expectedChecksum);
    }

    public async Task<IReadOnlyList<DigitalTwinSnapshotSummary>> GetHistoryAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Snapshot history limit must be between 1 and 100.");
        }

        var snapshots = new List<DigitalTwinSnapshotSummary>(limit);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT snapshot_id, created_utc, inventory_watermark_scan_id, inventory_watermark_scope,
                   entity_count, relationship_count, checksum
            FROM digital_twin_snapshots
            ORDER BY created_utc DESC, snapshot_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(new DigitalTwinSnapshotSummary(
                Guid.Parse(reader.GetString(0)),
                ParseTimestamp(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }

        return snapshots;
    }

    public async Task<DigitalTwinEntityPage> GetEntityPageAsync(
        Guid snapshotId,
        int offset,
        int pageSize,
        DigitalTwinEntityType? category,
        string? searchText,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (pageSize is < 1 or > 250)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Digital Twin page size must be between 1 and 250.");
        }

        if (category is { } entityType && !Enum.IsDefined(entityType))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (searchText?.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(searchText), "Search text cannot exceed 256 characters.");
        }

        var normalizedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT 1 FROM digital_twin_snapshots WHERE snapshot_id = $id;";
        existsCommand.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
        if (await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Digital Twin snapshot '{snapshotId}' was not found.");
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText =
            """
            SELECT COUNT(*)
            FROM digital_twin_entities
            WHERE snapshot_id = $id
              AND ($category IS NULL OR entity_type = $category)
              AND ($search IS NULL OR
                   instr(lower(display_name), lower($search)) > 0 OR
                   instr(lower(identity_key), lower($search)) > 0 OR
                   instr(lower(source), lower($search)) > 0);
            """;
        AddEntityPageParameters(countCommand, snapshotId, category, normalizedSearch);
        var totalCount = checked((int)(long)(await countCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L));

        var entities = new List<DigitalTwinEntity>(Math.Min(pageSize, totalCount));
        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText =
            """
            SELECT entity_type, identity_key, display_name, source, last_observed_utc, availability,
                   confidence, inventory_type, metadata_json
            FROM digital_twin_entities
            WHERE snapshot_id = $id
              AND ($category IS NULL OR entity_type = $category)
              AND ($search IS NULL OR
                   instr(lower(display_name), lower($search)) > 0 OR
                   instr(lower(identity_key), lower($search)) > 0 OR
                   instr(lower(source), lower($search)) > 0)
            ORDER BY display_name COLLATE NOCASE, identity_key COLLATE NOCASE
            LIMIT $limit OFFSET $offset;
            """;
        AddEntityPageParameters(pageCommand, snapshotId, category, normalizedSearch);
        pageCommand.Parameters.AddWithValue("$limit", pageSize);
        pageCommand.Parameters.AddWithValue("$offset", offset);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(8), JsonOptions)
                ?? throw new InvalidDataException("A Digital Twin entity contains invalid metadata.");
            entities.Add(new DigitalTwinEntity(
                ParseEnum<DigitalTwinEntityType>(reader.GetString(0), "entity type"),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseTimestamp(reader.GetString(4)),
                ParseEnum<InventoryFieldAvailability>(reader.GetString(5), "field availability"),
                Convert.ToDecimal(reader.GetDouble(6), CultureInfo.InvariantCulture),
                metadata,
                reader.IsDBNull(7)
                    ? null
                    : ParseEnum<InventoryComponentType>(reader.GetString(7), "inventory component type")));
        }

        return new DigitalTwinEntityPage(entities, offset, pageSize, totalCount);
    }

    public async Task SaveInspectionAsync(
        BackupInspectionResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateInspectionResult(result);
        var checksJson = JsonSerializer.Serialize(result.Checks, JsonOptions);
        var limitationsJson = JsonSerializer.Serialize(result.Limitations, JsonOptions);
        if (checksJson.Length + limitationsJson.Length > 64 * 1024)
        {
            throw new ArgumentException("Backup inspection details exceed the 64 KiB persistence bound.", nameof(result));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO backup_inspections (
                inspection_id, candidate_identity, provider, location, started_utc, completed_utc,
                state, summary, recognized_structure, discovered_structure_groups, metadata_bytes_read,
                backup_container_bytes, checks_json, limitations_json
            ) VALUES (
                $id, $candidate, $provider, $location, $started, $completed,
                $state, $summary, $recognized, $groups, $metadataBytes,
                $containerBytes, $checks, $limitations
            );
            """;
        command.Parameters.AddWithValue("$id", result.InspectionId.ToString("D"));
        command.Parameters.AddWithValue("$candidate", result.CandidateIdentity);
        command.Parameters.AddWithValue("$provider", result.Provider.ToString());
        command.Parameters.AddWithValue("$location", (object?)result.Location ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", FormatTimestamp(result.StartedAt));
        command.Parameters.AddWithValue("$completed", FormatTimestamp(result.CompletedAt));
        command.Parameters.AddWithValue("$state", result.State.ToString());
        command.Parameters.AddWithValue("$summary", result.Summary);
        command.Parameters.AddWithValue("$recognized", result.RecognizedStructure ? 1 : 0);
        command.Parameters.AddWithValue("$groups", (object?)result.DiscoveredStructureGroups ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataBytes", (object?)result.MetadataBytesRead ?? DBNull.Value);
        command.Parameters.AddWithValue("$containerBytes", (object?)result.BackupContainerBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$checks", checksJson);
        command.Parameters.AddWithValue("$limitations", limitationsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<BackupInspectionResult?> GetInspectionAsync(
        Guid inspectionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT inspection_id, candidate_identity, provider, location, started_utc, completed_utc,
                   state, summary, recognized_structure, discovered_structure_groups, metadata_bytes_read,
                   backup_container_bytes, checks_json, limitations_json
            FROM backup_inspections
            WHERE inspection_id = $id;
            """;
        command.Parameters.AddWithValue("$id", inspectionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var result = new BackupInspectionResult(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseEnum<BackupProvider>(reader.GetString(2), "backup provider"),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ParseTimestamp(reader.GetString(4)),
                ParseTimestamp(reader.GetString(5)),
                ParseEnum<BackupInspectionState>(reader.GetString(6), "inspection state"),
                reader.GetString(7),
                reader.GetInt32(8) != 0,
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                JsonSerializer.Deserialize<List<BackupInspectionCheck>>(reader.GetString(12), JsonOptions)
                    ?? throw new InvalidDataException("Backup inspection checks are invalid."),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(13), JsonOptions)
                    ?? throw new InvalidDataException("Backup inspection limitations are invalid."));
            ValidateInspectionResult(result);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new InvalidDataException(
                $"Backup inspection '{inspectionId}' contains invalid persisted metadata.",
                exception);
        }
    }

    public async Task<IReadOnlyList<BackupInspectionSummary>> GetInspectionHistoryAsync(
        string candidateIdentity,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateIdentity);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Backup inspection history limit must be between 1 and 100.");
        }

        var history = new List<BackupInspectionSummary>(limit);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT inspection_id, candidate_identity, provider, started_utc, completed_utc, state, summary
            FROM backup_inspections
            WHERE candidate_identity = $candidate
            ORDER BY completed_utc DESC, inspection_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$candidate", candidateIdentity);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(new BackupInspectionSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseEnum<BackupProvider>(reader.GetString(2), "backup provider"),
                ParseTimestamp(reader.GetString(3)),
                ParseTimestamp(reader.GetString(4)),
                ParseEnum<BackupInspectionState>(reader.GetString(5), "inspection state"),
                reader.GetString(6)));
        }

        return history;
    }

    public async Task SaveSampleAsync(
        GuardianTelemetrySnapshot snapshot,
        GuardianRetentionPolicy retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(retention);
        retention.Validate();
        ValidateTelemetrySnapshot(snapshot);
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > 64 * 1024)
        {
            throw new ArgumentException("Guardian telemetry sample exceeds the 64 KiB persistence bound.", nameof(snapshot));
        }

        var cutoff = DateTimeOffset.UtcNow - retention.MaximumAge;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO guardian_telemetry_samples (sample_id, collected_utc, payload_json)
                VALUES ($id, $collected, $payload);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$collected", FormatTimestamp(snapshot.CollectedAt));
            insert.Parameters.AddWithValue("$payload", payload);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText =
                """
                DELETE FROM guardian_telemetry_samples
                WHERE collected_utc < $cutoff
                   OR sample_id NOT IN (
                       SELECT sample_id
                       FROM guardian_telemetry_samples
                       ORDER BY collected_utc DESC, sample_id DESC
                       LIMIT $maximum
                   );
                """;
            prune.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
            prune.Parameters.AddWithValue("$maximum", retention.MaximumSamples);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public async Task<IReadOnlyList<GuardianTelemetrySnapshot>> GetSamplesAsync(
        GuardianTelemetryRange range,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range.From > range.To)
        {
            throw new ArgumentException("Telemetry query start must not be later than its end.", nameof(range));
        }

        if (range.MaximumSamples is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Telemetry query limit must be from 1 through 10,000 samples.");
        }

        var samples = new List<GuardianTelemetrySnapshot>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json
            FROM guardian_telemetry_samples
            WHERE collected_utc >= $from AND collected_utc <= $to
            ORDER BY collected_utc DESC, sample_id DESC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$from", FormatTimestamp(range.From));
        command.Parameters.AddWithValue("$to", FormatTimestamp(range.To));
        command.Parameters.AddWithValue("$maximum", range.MaximumSamples);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var sample = JsonSerializer.Deserialize<GuardianTelemetrySnapshot>(reader.GetString(0), JsonOptions)
                    ?? throw new InvalidDataException("Guardian telemetry sample payload is empty.");
                ValidateTelemetrySnapshot(sample);
                samples.Add(sample);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException("Persisted Guardian telemetry contains an invalid sample.", exception);
            }
        }

        return samples.OrderBy(sample => sample.CollectedAt).ToArray();
    }

    public async Task<GuardianTelemetrySnapshot?> GetLatestSampleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json
            FROM guardian_telemetry_samples
            ORDER BY collected_utc DESC, sample_id DESC
            LIMIT 1;
            """;
        var payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        try
        {
            var sample = JsonSerializer.Deserialize<GuardianTelemetrySnapshot>(payload, JsonOptions)
                ?? throw new InvalidDataException("Guardian telemetry sample payload is empty.");
            ValidateTelemetrySnapshot(sample);
            return sample;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException("The latest Guardian telemetry sample is invalid.", exception);
        }
    }

    public async Task<string> CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullDestinationPath, _databasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The backup destination cannot be the active database.", nameof(destinationPath));
        }

        if (File.Exists(fullDestinationPath))
        {
            throw new IOException("The backup destination already exists; choose a new file name.");
        }

        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The backup path has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullDestinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();

            using var source = new SqliteConnection(_connectionString);
            using var destination = new SqliteConnection(destinationConnectionString);
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);

            using var integrityCommand = destination.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrityResult = integrityCommand.ExecuteScalar() as string;
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The database backup failed its integrity check: {integrityResult ?? "no result"}.");
            }

            _logger.LogInformation("Created and integrity-checked database backup at {BackupPath}.", fullDestinationPath);
            return fullDestinationPath;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> UpsertComponentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InventoryComponent component,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO components (
                component_id, component_type, identity_key, display_name, source, first_seen_utc, last_verified_utc, category
            ) VALUES ($id, $type, $identity, $name, $source, $observed, $observed, $category)
            ON CONFLICT (component_type, identity_key) DO UPDATE SET
                display_name = excluded.display_name,
                source = excluded.source,
                category = excluded.category,
                last_verified_utc = excluded.last_verified_utc
            RETURNING component_id;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$type", component.Type.ToString());
        command.Parameters.AddWithValue("$identity", component.IdentityKey);
        command.Parameters.AddWithValue("$name", component.DisplayName);
        command.Parameters.AddWithValue("$source", component.Source);
        command.Parameters.AddWithValue("$observed", FormatTimestamp(component.ObservedAt));
        command.Parameters.AddWithValue("$category", component.Category);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SQLite did not return the reconciled component ID.");
    }

    private static async Task<LatestScan?> ReadLatestScanAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        Guid? scanId = null;
        DateTimeOffset startedAt = default;
        DateTimeOffset? completedAt = null;
        string? scope = null;
        InventoryScanStatus status = InventoryScanStatus.InProgress;
        var attempted = 0;
        var total = 0;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT scan_id, started_utc, completed_utc, scope, status, attempted_collectors, total_collectors
                FROM scans
                ORDER BY started_utc DESC, scan_id DESC
                LIMIT 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                scanId = Guid.Parse(reader.GetString(0));
                startedAt = ParseTimestamp(reader.GetString(1));
                completedAt = reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2));
                scope = reader.GetString(3);
                status = ParseStatus(reader.GetString(4));
                attempted = reader.GetInt32(5);
                total = reader.GetInt32(6);
            }
        }

        if (scanId is null)
        {
            return null;
        }

        var issues = new List<CollectorIssue>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT collector_id, code, message FROM collector_issues WHERE scan_id = $scan ORDER BY issue_id;";
            command.Parameters.AddWithValue("$scan", scanId.Value.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new CollectorIssue(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return new LatestScan(
            scanId.Value,
            startedAt,
            completedAt,
            scope!,
            status,
            attempted,
            total,
            issues);
    }

    private static Task<IReadOnlyList<InventoryComponent>> ReadLatestComponentsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        ReadComponentsAsync(connection, null, cancellationToken);

    private static Task<IReadOnlyList<InventoryComponent>> ReadComponentsForScanAsync(
        SqliteConnection connection,
        Guid scanId,
        CancellationToken cancellationToken) =>
        ReadComponentsAsync(connection, scanId, cancellationToken);

    private static async Task<IReadOnlyList<InventoryComponent>> ReadComponentsAsync(
        SqliteConnection connection,
        Guid? scanId,
        CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.component_type, c.identity_key, c.display_name, c.source, c.category,
                   o.observed_utc, o.details_schema_version, o.details_json
            FROM components AS c
            JOIN observations AS o ON o.component_id = c.component_id AND (
                ($scan IS NOT NULL AND o.scan_id = $scan)
                OR ($scan IS NULL AND o.observation_id = (
                    SELECT latest.observation_id
                    FROM observations AS latest
                    WHERE latest.component_id = c.component_id
                    ORDER BY latest.observed_utc DESC, latest.observation_id DESC
                    LIMIT 1
                ))
            )
            ORDER BY c.component_type, c.display_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue(
            "$scan",
            scanId is null ? DBNull.Value : scanId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schemaVersion = reader.GetInt32(6);
            var detailsJson = reader.GetString(7);
            var displayName = reader.GetString(2);
            var category = reader.GetString(4);
            var source = reader.GetString(3);
            Dictionary<string, string?> details;
            Dictionary<string, InventoryFieldAvailability> availability;
            if (schemaVersion == 1)
            {
                details = JsonSerializer.Deserialize<Dictionary<string, string?>>(detailsJson, JsonOptions)
                    ?? throw new InvalidDataException("An inventory observation contains an invalid details payload.");
                availability = details.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value is null
                        ? InventoryFieldAvailability.Unknown
                        : InventoryFieldAvailability.Available,
                    StringComparer.Ordinal);
            }
            else if (schemaVersion == 2)
            {
                var payload = JsonSerializer.Deserialize<InventoryObservationPayload>(detailsJson, JsonOptions)
                    ?? throw new InvalidDataException("An inventory observation contains an invalid details payload.");
                details = payload.Details;
                availability = payload.FieldAvailability;
                displayName = payload.DisplayName;
                category = payload.Category;
                source = payload.Source;
            }
            else
            {
                throw new InvalidDataException(
                    $"Unsupported inventory details schema version {schemaVersion}.");
            }
            components.Add(new InventoryComponent(
                ParseComponentType(reader.GetString(0)),
                reader.GetString(1),
                displayName,
                source,
                schemaVersion,
                ParseTimestamp(reader.GetString(5)),
                details)
            {
                Category = category,
                FieldAvailability = availability
            });
        }

        return components;
    }

    private static async Task<IReadOnlyList<LatestScan>> ReadComparableScansAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var scans = new List<LatestScan>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT scan_id, started_utc, completed_utc, scope, status, attempted_collectors, total_collectors
            FROM scans AS candidate
            WHERE status IN ('Completed', 'Partial')
              AND scope = (
                  SELECT latest_success.scope
                  FROM scans AS latest_success
                  WHERE latest_success.status IN ('Completed', 'Partial')
                  ORDER BY latest_success.started_utc DESC, latest_success.scan_id DESC
                  LIMIT 1
              )
            ORDER BY candidate.started_utc DESC, candidate.scan_id DESC
            LIMIT 2;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scans.Add(new LatestScan(
                Guid.Parse(reader.GetString(0)),
                ParseTimestamp(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)),
                reader.GetString(3),
                ParseStatus(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                []));
        }

        return scans;
    }

    private static InventoryComponentType ParseComponentType(string value) =>
        Enum.TryParse<InventoryComponentType>(value, ignoreCase: false, out var type)
            ? type
            : throw new InvalidDataException($"Unknown inventory component type '{value}'.");

    private static InventoryScanStatus ParseStatus(string value) =>
        Enum.TryParse<InventoryScanStatus>(value, ignoreCase: false, out var status)
            ? status
            : throw new InvalidDataException($"Unknown inventory scan status '{value}'.");

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"Persisted {fieldName} '{value}' is invalid.");

    private static async Task<int> ReadNewestMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(migration_id), 0) FROM schema_migrations;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadMigrationChecksumAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int migrationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT checksum FROM schema_migrations WHERE migration_id = $id;";
        command.Parameters.AddWithValue("$id", migrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task ValidateMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int migrationId,
        string expectedChecksum,
        CancellationToken cancellationToken)
    {
        var existingChecksum = await ReadMigrationChecksumAsync(
            connection,
            transaction,
            migrationId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(existingChecksum, expectedChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Database migration {migrationId} has a checksum that does not match this application.");
        }
    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int migrationId,
        string checksum,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO schema_migrations (migration_id, checksum, applied_utc) VALUES ($id, $checksum, $applied);";
        command.Parameters.AddWithValue("$id", migrationId);
        command.Parameters.AddWithValue("$checksum", checksum);
        command.Parameters.AddWithValue("$applied", FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record InventoryObservationPayload(
        Dictionary<string, string?> Details,
        Dictionary<string, InventoryFieldAvailability> FieldAvailability,
        string DisplayName,
        string Category,
        string Source);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateTelemetrySnapshot(GuardianTelemetrySnapshot snapshot)
    {
        static bool ValidReading<T>(TelemetryReading<T> reading)
            where T : struct =>
            Enum.IsDefined(reading.Availability) &&
            !string.IsNullOrWhiteSpace(reading.Source) &&
            reading.Source.Length <= 1024 &&
            (reading.Limitation is null || reading.Limitation.Length <= 2048) &&
            (reading.Value is not double number || double.IsFinite(number)) &&
            (reading.Interval is null || reading.Interval >= TimeSpan.Zero);

        if (snapshot.CollectedAt == default ||
            !ValidReading(snapshot.CpuUsagePercent) ||
            !ValidReading(snapshot.AvailableMemoryBytes) ||
            !ValidReading(snapshot.TotalMemoryBytes) ||
            !ValidReading(snapshot.SystemUptime) ||
            !ValidReading(snapshot.StorageEnumeration) ||
            !ValidReading(snapshot.ProcessEnumeration) ||
            snapshot.StorageVolumes.Count > 64 ||
            snapshot.NetworkInterfaces.Count > 64 ||
            snapshot.Processes.Count > 128 ||
            snapshot.GpuAdapters.Count > 32 ||
            snapshot.StorageVolumes.Any(volume =>
                string.IsNullOrWhiteSpace(volume.Name) ||
                volume.Name.Length > 512 ||
                !Enum.IsDefined(volume.Availability) ||
                string.IsNullOrWhiteSpace(volume.Source) ||
                !ValidReading(new TelemetryReading<ulong>(
                    volume.FreeBytes,
                    volume.Availability,
                    volume.Source,
                    volume.Limitation)) ||
                !ValidReading(new TelemetryReading<ulong>(
                    volume.TotalBytes,
                    volume.Availability,
                    volume.Source,
                    volume.Limitation))) ||
            snapshot.NetworkInterfaces.Any(network =>
                string.IsNullOrWhiteSpace(network.Identity) ||
                network.Identity.Length > 1024 ||
                string.IsNullOrWhiteSpace(network.Name) ||
                network.Name.Length > 512 ||
                !ValidReading(network.SentBytesPerSecond) ||
                !ValidReading(network.ReceivedBytesPerSecond)) ||
            snapshot.Processes.Any(process =>
                string.IsNullOrWhiteSpace(process.Identity) ||
                process.Identity.Length > 1024 ||
                string.IsNullOrWhiteSpace(process.Name) ||
                process.Name.Length > 512 ||
                process.ProcessId < 0 ||
                !ValidReading(process.CpuPercent) ||
                !ValidReading(process.WorkingSetBytes)) ||
            snapshot.GpuAdapters.Any(gpu =>
                string.IsNullOrWhiteSpace(gpu.Identity) ||
                gpu.Identity.Length > 1024 ||
                string.IsNullOrWhiteSpace(gpu.Name) ||
                gpu.Name.Length > 512 ||
                !Enum.IsDefined(gpu.Availability) ||
                string.IsNullOrWhiteSpace(gpu.Source) ||
                !ValidReading(gpu.UtilizationPercent) ||
                !ValidReading(gpu.DedicatedMemoryBytes) ||
                !ValidReading(gpu.TemperatureCelsius)))
        {
            throw new ArgumentException("Guardian telemetry sample metadata is invalid or exceeds a persistence bound.", nameof(snapshot));
        }
    }

    private static void ValidateInspectionResult(BackupInspectionResult result)
    {
        if (result.InspectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(result.CandidateIdentity) ||
            result.CandidateIdentity.Length > 1024 ||
            !Enum.IsDefined(result.Provider) ||
            !Enum.IsDefined(result.State) ||
            string.IsNullOrWhiteSpace(result.Summary) ||
            result.Summary.Length > 2048 ||
            result.Location?.Length > 4096 ||
            result.CompletedAt < result.StartedAt ||
            result.DiscoveredStructureGroups is < 0 ||
            result.MetadataBytesRead is < 0 ||
            result.BackupContainerBytes is < 0 ||
            result.Checks.Count > 128 ||
            result.Limitations.Count > 64 ||
            result.Checks.Any(check =>
                string.IsNullOrWhiteSpace(check.Name) ||
                check.Name.Length > 256 ||
                !Enum.IsDefined(check.State) ||
                string.IsNullOrWhiteSpace(check.Explanation) ||
                check.Explanation.Length > 2048 ||
                string.IsNullOrWhiteSpace(check.Source) ||
                check.Source.Length > 1024) ||
            result.Limitations.Any(limitation =>
                string.IsNullOrWhiteSpace(limitation) ||
                limitation.Length > 2048))
        {
            throw new ArgumentException("Backup inspection metadata is invalid or exceeds a persistence bound.", nameof(result));
        }
    }

    private static void AddEntityPageParameters(
        SqliteCommand command,
        Guid snapshotId,
        DigitalTwinEntityType? category,
        string? searchText)
    {
        command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
        command.Parameters.AddWithValue("$category", (object?)category?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", (object?)searchText ?? DBNull.Value);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
