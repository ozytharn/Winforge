using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WinForge.Application.Inventory;
using WinForge.Application.Monitoring;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;
using WinForge.Domain.Recovery;
using WinForge.Infrastructure.Collectors;
using WinForge.Infrastructure.Persistence;

namespace WinForge.IntegrationTests;

public sealed class SqliteInventoryRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WinForgeTests",
        Guid.NewGuid().ToString("N"));
    private readonly SqliteInventoryRepository _repository;

    public SqliteInventoryRepositoryTests()
    {
        _repository = new SqliteInventoryRepository(
            Path.Combine(_directory, "inventory.db"),
            NullLogger<SqliteInventoryRepository>.Instance);
    }

    [Fact]
    public async Task Failed_scan_preserves_known_components_and_records_history()
    {
        await _repository.InitializeAsync(CancellationToken.None);

        var firstScanId = Guid.NewGuid();
        var firstObservedAt = DateTimeOffset.UtcNow;
        await _repository.StartScanAsync(firstScanId, firstObservedAt, "devices", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                firstScanId,
                firstObservedAt,
                InventoryScanStatus.Completed,
                [CreateDevice("device:stable-id", "Known device")],
                []),
            CancellationToken.None);

        var failedScanId = Guid.NewGuid();
        var failedObservedAt = DateTimeOffset.UtcNow;
        await _repository.StartScanAsync(failedScanId, failedObservedAt, "devices", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                failedScanId,
                failedObservedAt,
                InventoryScanStatus.Failed,
                [],
                [new CollectorIssue("devices", "access_denied", "Device inventory was inaccessible.")]),
            CancellationToken.None);

        var snapshot = await _repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Single(snapshot.Components);
        Assert.Equal("device:stable-id", snapshot.Components[0].IdentityKey);
        Assert.Equal(InventoryScanStatus.Failed, snapshot.LatestScan?.Status);
        Assert.Equal(failedScanId, snapshot.LatestScan?.ScanId);
        Assert.Single(snapshot.LatestScan!.Issues);
    }

    [Fact]
    public async Task Database_backup_is_integrity_checked_and_contains_scan_data()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var scanId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        await _repository.StartScanAsync(scanId, startedAt, "devices", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                scanId,
                startedAt,
                InventoryScanStatus.Completed,
                [CreateDevice("device:backup-test", "Backup test device")],
                []),
            CancellationToken.None);

        var backupPath = await _repository.CreateBackupAsync(
            Path.Combine(_directory, "backups", "inventory-backup.db"),
            CancellationToken.None);

        Assert.True(File.Exists(backupPath));
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await backup.OpenAsync();
        await using var command = backup.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scans;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Guardian_telemetry_round_trips_and_retention_bounds_sample_count()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var start = DateTimeOffset.UtcNow.AddMinutes(-101);
        var retention = new GuardianRetentionPolicy(TimeSpan.FromDays(30), 100);
        for (var index = 0; index < 101; index++)
        {
            await _repository.SaveSampleAsync(
                CreateTelemetrySample(start.AddMinutes(index), index),
                retention,
                CancellationToken.None);
        }

        var samples = await _repository.GetSamplesAsync(
            new GuardianTelemetryRange(start, DateTimeOffset.UtcNow, 100),
            CancellationToken.None);
        var latest = await _repository.GetLatestSampleAsync(CancellationToken.None);

        Assert.Equal(100, samples.Count);
        Assert.Equal(start.AddMinutes(1), samples[0].CollectedAt);
        Assert.Equal(start.AddMinutes(100), latest?.CollectedAt);
        Assert.Equal("adapter:test", Assert.Single(latest!.GpuAdapters).Identity);
        Assert.Equal("process:123:456", Assert.Single(latest.Processes).Identity);
        Assert.Equal("network:test", Assert.Single(latest.NetworkInterfaces).Identity);
    }

    [Fact]
    public async Task Guardian_telemetry_query_is_bounded_and_age_retention_prunes_old_samples()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await _repository.SaveSampleAsync(
            CreateTelemetrySample(now.AddDays(-2), 1),
            new GuardianRetentionPolicy(TimeSpan.FromDays(365), 100),
            CancellationToken.None);
        await _repository.SaveSampleAsync(
            CreateTelemetrySample(now.AddMinutes(-1), 2),
            new GuardianRetentionPolicy(TimeSpan.FromDays(1), 100),
            CancellationToken.None);

        var result = await _repository.GetSamplesAsync(
            new GuardianTelemetryRange(now.AddDays(-3), now, 1),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(now.AddMinutes(-1), result[0].CollectedAt);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _repository.GetSamplesAsync(
                new GuardianTelemetryRange(now.AddHours(-1), now, 10_001),
                CancellationToken.None));
    }

    [Fact]
    public async Task Migration_from_schema_one_preserves_existing_inventory_and_observations()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var scanId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        await _repository.StartScanAsync(scanId, startedAt, "legacy devices", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                scanId,
                startedAt,
                InventoryScanStatus.Completed,
                [CreateDevice("device:legacy", "Legacy device")],
                []),
            CancellationToken.None);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = Path.Combine(_directory, "inventory.db"),
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                """
                DROP TABLE backup_inspections;
                DROP TABLE guardian_telemetry_samples;
                DROP TABLE digital_twin_relationships;
                DROP TABLE digital_twin_entities;
                DROP TABLE digital_twin_snapshots;
                DROP INDEX ix_components_category;
                ALTER TABLE components DROP COLUMN category;
                DELETE FROM schema_migrations WHERE migration_id >= 2;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var upgradedRepository = new SqliteInventoryRepository(
            Path.Combine(_directory, "inventory.db"),
            NullLogger<SqliteInventoryRepository>.Instance);
        await upgradedRepository.InitializeAsync(CancellationToken.None);
        var snapshot = await upgradedRepository.GetSnapshotAsync(CancellationToken.None);

        var existingComponent = Assert.Single(snapshot.Components);
        Assert.Equal("device:legacy", existingComponent.IdentityKey);
        Assert.Equal("Legacy device", existingComponent.DisplayName);
        Assert.Equal("General", existingComponent.Category);
        Assert.Equal("OK", existingComponent.Details["status"]);
        Assert.Equal(scanId, snapshot.LatestScan?.ScanId);
        Assert.Equal(scanId, snapshot.ComparisonScan?.ScanId);

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "inventory.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verify.OpenAsync();
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(migration_id) FROM schema_migrations;";
        Assert.Equal(5L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Migration_from_schema_two_applies_only_newer_migrations_and_preserves_inventory()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var scanId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        await _repository.StartScanAsync(scanId, startedAt, "legacy devices", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                scanId,
                startedAt,
                InventoryScanStatus.Completed,
                [CreateDevice("device:schema-two", "Schema two device")],
                []),
            CancellationToken.None);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = Path.Combine(_directory, "inventory.db"),
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                """
                DROP TABLE backup_inspections;
                DROP TABLE guardian_telemetry_samples;
                DROP TABLE digital_twin_relationships;
                DROP TABLE digital_twin_entities;
                DROP TABLE digital_twin_snapshots;
                DELETE FROM schema_migrations WHERE migration_id >= 3;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var upgradedRepository = new SqliteInventoryRepository(
            Path.Combine(_directory, "inventory.db"),
            NullLogger<SqliteInventoryRepository>.Instance);
        await upgradedRepository.InitializeAsync(CancellationToken.None);
        var snapshot = await upgradedRepository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("device:schema-two", Assert.Single(snapshot.Components).IdentityKey);
        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "inventory.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verify.OpenAsync();
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id IN (1, 2, 3, 4, 5);";
        Assert.Equal(5L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Fresh_database_registers_each_migration_once_with_stable_checksums()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var mirror = new SqliteInventoryRepository(
            Path.Combine(_directory, "mirror.db"),
            NullLogger<SqliteInventoryRepository>.Instance);
        await mirror.InitializeAsync(CancellationToken.None);

        static async Task<(long Count, string Checksums)> ReadMigrationsAsync(string path)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT migration_id, checksum FROM schema_migrations ORDER BY migration_id;";
            await using var reader = await command.ExecuteReaderAsync();
            var checksums = new List<string>();
            while (await reader.ReadAsync())
            {
                Assert.Equal(checksums.Count + 1, reader.GetInt32(0));
                checksums.Add(reader.GetString(1));
            }

            return (checksums.Count, string.Join(",", checksums));
        }

        var first = await ReadMigrationsAsync(Path.Combine(_directory, "inventory.db"));
        var second = await ReadMigrationsAsync(Path.Combine(_directory, "mirror.db"));

        Assert.Equal(5, first.Count);
        Assert.Equal(first.Checksums, second.Checksums);
        Assert.All(first.Checksums.Split(','), checksum => Assert.Matches("^[A-F0-9]{64}$", checksum));
    }

    [Fact]
    public async Task Migration_from_schema_three_adds_backup_history_without_changing_earlier_checksums()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var databasePath = Path.Combine(_directory, "inventory.db");
        Dictionary<int, string> legacyChecksums;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = databasePath,
                         Mode = SqliteOpenMode.ReadOnly,
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT migration_id, checksum FROM schema_migrations WHERE migration_id <= 3;";
            await using var reader = await command.ExecuteReaderAsync();
            legacyChecksums = new Dictionary<int, string>();
            while (await reader.ReadAsync())
            {
                legacyChecksums.Add(reader.GetInt32(0), reader.GetString(1));
            }
        }

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = databasePath,
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                "DROP TABLE guardian_telemetry_samples; DROP TABLE backup_inspections; DELETE FROM schema_migrations WHERE migration_id >= 4;";
            await downgrade.ExecuteNonQueryAsync();
        }

        await _repository.InitializeAsync(CancellationToken.None);
        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT migration_id, checksum FROM schema_migrations ORDER BY migration_id;";
        await using var verifyReader = await verifyCommand.ExecuteReaderAsync();
        var migrations = new List<(int Id, string Checksum)>();
        while (await verifyReader.ReadAsync())
        {
            migrations.Add((verifyReader.GetInt32(0), verifyReader.GetString(1)));
        }

        Assert.Equal(5, migrations.Count);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(legacyChecksums[index + 1], migrations[index].Checksum);
        }

        Assert.Equal(4, migrations[3].Id);
        Assert.Equal(5, migrations[4].Id);
    }

    [Fact]
    public async Task Migration_from_schema_four_adds_guardian_history_and_preserves_prior_checksums()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var databasePath = Path.Combine(_directory, "inventory.db");
        Dictionary<int, string> legacyChecksums;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = databasePath,
                         Mode = SqliteOpenMode.ReadOnly,
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT migration_id, checksum FROM schema_migrations WHERE migration_id <= 4;";
            await using var reader = await command.ExecuteReaderAsync();
            legacyChecksums = new Dictionary<int, string>();
            while (await reader.ReadAsync())
            {
                legacyChecksums.Add(reader.GetInt32(0), reader.GetString(1));
            }
        }

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = databasePath,
                         Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP TABLE guardian_telemetry_samples; DELETE FROM schema_migrations WHERE migration_id = 5;";
            await downgrade.ExecuteNonQueryAsync();
        }

        var upgradedRepository = new SqliteInventoryRepository(
            databasePath,
            NullLogger<SqliteInventoryRepository>.Instance);
        await upgradedRepository.InitializeAsync(CancellationToken.None);

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT migration_id, checksum FROM schema_migrations ORDER BY migration_id;";
        await using var verifyReader = await verifyCommand.ExecuteReaderAsync();
        var migrations = new List<(int Id, string Checksum)>();
        while (await verifyReader.ReadAsync())
        {
            migrations.Add((verifyReader.GetInt32(0), verifyReader.GetString(1)));
        }

        Assert.Equal(5, migrations.Count);
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(legacyChecksums[index + 1], migrations[index].Checksum);
        }

        Assert.Equal(5, migrations[4].Id);
    }

    [Fact]
    public async Task Backup_inspection_history_persists_across_repository_instances()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var inspectedAt = DateTimeOffset.UtcNow;
        var inspection = new BackupInspectionResult(
            Guid.NewGuid(),
            "backup-folder:C:WINDOWSIMAGEBACKUP",
            BackupProvider.WindowsImageBackup,
            @"C:\WindowsImageBackup",
            inspectedAt,
            inspectedAt.AddSeconds(1),
            BackupInspectionState.Warning,
            "Metadata checks passed but the backup is not fully validated.",
            true,
            1,
            256,
            4096,
            [
                new BackupInspectionCheck(
                    "Manifest XML",
                    BackupCheckState.Passed,
                    "The bounded XML parse succeeded.",
                    "Fixture",
                    inspectedAt)
            ],
            ["Restoreability was not established."]);
        await _repository.SaveInspectionAsync(inspection, CancellationToken.None);

        var restartedRepository = new SqliteInventoryRepository(
            Path.Combine(_directory, "inventory.db"),
            NullLogger<SqliteInventoryRepository>.Instance);
        await restartedRepository.InitializeAsync(CancellationToken.None);
        var loaded = await restartedRepository.GetInspectionAsync(inspection.InspectionId, CancellationToken.None);
        var history = await restartedRepository.GetInspectionHistoryAsync(
            inspection.CandidateIdentity,
            10,
            CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(inspection.InspectionId, loaded.InspectionId);
        Assert.Equal(inspection.Provider, loaded.Provider);
        Assert.Equal(inspection.State, loaded.State);
        Assert.Equal(inspection.MetadataBytesRead, loaded.MetadataBytesRead);
        Assert.Equal(inspection.Checks, loaded.Checks);
        Assert.Equal(inspection.Limitations, loaded.Limitations);
        Assert.Collection(history, item =>
        {
            Assert.Equal(inspection.InspectionId, item.InspectionId);
            Assert.Equal(inspection.State, item.State);
        });
    }

    [Fact]
    public async Task Version_two_observations_round_trip_category_and_field_availability()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var scanId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var component = new InventoryComponent(
            InventoryComponentType.Motherboard,
            "motherboard:test",
            "Test board",
            "WMI",
            2,
            startedAt,
            new Dictionary<string, string?> { ["manufacturer"] = null, ["product"] = "Example" })
        {
            Category = "Motherboard",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["manufacturer"] = InventoryFieldAvailability.Missing,
                ["product"] = InventoryFieldAvailability.Available
            }
        };
        await _repository.StartScanAsync(scanId, startedAt, "hardware", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(scanId, startedAt, InventoryScanStatus.Completed, [component], []),
            CancellationToken.None);

        var snapshot = await _repository.GetSnapshotAsync(CancellationToken.None);

        var saved = Assert.Single(snapshot.Components);
        Assert.Equal("Motherboard", saved.Category);
        Assert.Equal(InventoryFieldAvailability.Missing, saved.FieldAvailability["manufacturer"]);
        Assert.Equal(InventoryFieldAvailability.Available, saved.FieldAvailability["product"]);
    }

    [Fact]
    public async Task Snapshot_returns_two_successful_scan_states_for_cautious_history_comparison()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var firstScanId = Guid.NewGuid();
        var firstObservedAt = DateTimeOffset.UtcNow;
        var firstComponent = CreateDevice("device:history", "Original name") with
        {
            DetailsSchemaVersion = 2,
            Category = "Storage",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["status"] = InventoryFieldAvailability.Available
            }
        };
        await _repository.StartScanAsync(firstScanId, firstObservedAt, "hardware", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                firstScanId,
                firstObservedAt,
                InventoryScanStatus.Completed,
                [firstComponent],
                []),
            CancellationToken.None);

        var secondScanId = Guid.NewGuid();
        var secondObservedAt = firstObservedAt.AddMinutes(1);
        var updatedComponent = firstComponent with
        {
            DisplayName = "Updated name",
            Source = "SetupAPI",
            ObservedAt = secondObservedAt
        };
        await _repository.StartScanAsync(secondScanId, secondObservedAt, "hardware", 1, CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                secondScanId,
                secondObservedAt,
                InventoryScanStatus.Partial,
                [updatedComponent],
                [new CollectorIssue("battery", "no_instances_reported", "No battery instance was reported.")]),
            CancellationToken.None);

        var snapshot = await _repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(secondScanId, snapshot.ComparisonScan?.ScanId);
        Assert.Equal(InventoryScanStatus.Partial, snapshot.ComparisonScan?.Status);
        Assert.Equal(firstScanId, snapshot.PreviousComparisonScan?.ScanId);
        Assert.Equal("Updated name", Assert.Single(snapshot.ComparisonComponents).DisplayName);
        Assert.Equal("Original name", Assert.Single(snapshot.PreviousComparisonComponents).DisplayName);
        Assert.Equal("Test fixture", snapshot.PreviousComparisonComponents[0].Source);
    }

    [Fact]
    public async Task Comparison_history_does_not_mix_independent_scan_scopes()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var hardwareId = Guid.NewGuid();
        var softwareId = Guid.NewGuid();
        var hardwareTime = DateTimeOffset.UtcNow;
        var softwareTime = hardwareTime.AddMinutes(1);
        await _repository.StartScanAsync(
            hardwareId,
            hardwareTime,
            "Hardware and driver inventory",
            1,
            CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                hardwareId,
                hardwareTime,
                InventoryScanStatus.Completed,
                [CreateDevice("device:scope-test", "Hardware device")],
                []) with { Scope = "Hardware and driver inventory" },
            CancellationToken.None);
        await _repository.StartScanAsync(
            softwareId,
            softwareTime,
            "Software, services, processes, startup, and scheduled-task inventory",
            1,
            CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                softwareId,
                softwareTime,
                InventoryScanStatus.Completed,
                [
                    new InventoryComponent(
                        InventoryComponentType.InstalledApplication,
                        "app:scope-test",
                        "Software item",
                        "Test fixture",
                        2,
                        softwareTime,
                        new Dictionary<string, string?>())
                ],
                []) with
            {
                Scope = "Software, services, processes, startup, and scheduled-task inventory"
            },
            CancellationToken.None);

        var snapshot = await _repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(softwareId, snapshot.ComparisonScan?.ScanId);
        Assert.Null(snapshot.PreviousComparisonScan);
        Assert.Equal(
            InventoryComponentType.InstalledApplication,
            Assert.Single(snapshot.ComparisonComponents).Type);
    }

    [Fact]
    public async Task Service_state_changes_are_kept_as_scope_matched_historical_observations()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var firstScanId = Guid.NewGuid();
        var firstTime = DateTimeOffset.UtcNow;
        var firstService = new InventoryComponent(
            InventoryComponentType.Service,
            "service:example",
            "Example service",
            "WMI",
            2,
            firstTime,
            new Dictionary<string, string?> { ["State"] = "Running" })
        {
            Category = "Services",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["State"] = InventoryFieldAvailability.Available
            }
        };
        await _repository.StartScanAsync(
            firstScanId,
            firstTime,
            "Software, services, processes, startup, and scheduled-task inventory",
            1,
            CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                firstScanId,
                firstTime,
                InventoryScanStatus.Completed,
                [firstService],
                []) with
            {
                Scope = "Software, services, processes, startup, and scheduled-task inventory"
            },
            CancellationToken.None);

        var secondScanId = Guid.NewGuid();
        var secondTime = firstTime.AddMinutes(1);
        await _repository.StartScanAsync(
            secondScanId,
            secondTime,
            "Software, services, processes, startup, and scheduled-task inventory",
            1,
            CancellationToken.None);
        await _repository.CompleteScanAsync(
            CreateScan(
                secondScanId,
                secondTime,
                InventoryScanStatus.Completed,
                [
                    firstService with
                    {
                        ObservedAt = secondTime,
                        Details = new Dictionary<string, string?> { ["State"] = "Stopped" }
                    }
                ],
                []) with
            {
                Scope = "Software, services, processes, startup, and scheduled-task inventory"
            },
            CancellationToken.None);

        var snapshot = await _repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(secondScanId, snapshot.ComparisonScan?.ScanId);
        Assert.Equal(firstScanId, snapshot.PreviousComparisonScan?.ScanId);
        Assert.Equal("Stopped", Assert.Single(snapshot.ComparisonComponents).Details["State"]);
        Assert.Equal("Running", Assert.Single(snapshot.PreviousComparisonComponents).Details["State"]);
    }

    [Fact]
    public async Task Passport_scan_collects_real_windows_inventory_and_persists_it()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var service = new PassportScanService(
            [
                new OperatingSystemInventoryCollector(NullLoggerFactory.Instance),
                new ProcessorInventoryCollector(NullLoggerFactory.Instance),
                new MemoryModuleInventoryCollector(NullLoggerFactory.Instance),
                new PnpDeviceInventoryCollector(NullLoggerFactory.Instance),
                new SetupApiDeviceInventoryCollector(NullLogger<SetupApiDeviceInventoryCollector>.Instance),
                new WmiHardwareInventoryCollector(NullLoggerFactory.Instance),
                new DriverPackageInventoryCollector(NullLoggerFactory.Instance),
                new StorageHealthInventoryCollector(NullLoggerFactory.Instance),
                new NvmeHealthInventoryCollector(NullLogger<NvmeHealthInventoryCollector>.Instance),
                new MonitorEdidInventoryCollector(NullLoggerFactory.Instance),
                new MonitorDisplayParametersCollector(NullLoggerFactory.Instance),
                new KeyboardInventoryCollector(NullLoggerFactory.Instance),
                new PointingDeviceInventoryCollector(NullLoggerFactory.Instance),
                new FirmwareInventoryCollector(NullLogger<FirmwareInventoryCollector>.Instance),
                new ThermalSensorInventoryCollector(NullLoggerFactory.Instance),
                new AudioEndpointInventoryCollector(NullLogger<AudioEndpointInventoryCollector>.Instance)
            ],
            _repository,
            NullLogger<PassportScanService>.Instance);

        var scan = await service.RunScanAsync(progress: null, CancellationToken.None);
        var snapshot = await _repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(16, scan.TotalCollectors);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.OperatingSystem);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.Processor);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.MemoryModule);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.Device);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.MemorySummary);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.Bios);
        Assert.Contains(
            snapshot.Components,
            component => component.Type == InventoryComponentType.DriverPackage);
        Assert.True(
            snapshot.Components.Any(component => component.Type == InventoryComponentType.Firmware) ||
            scan.Issues.Any(issue => issue.CollectorId == "firmware-mode"));
        Assert.True(
            snapshot.Components.Any(component => component.Type == InventoryComponentType.AudioEndpoint) ||
            scan.Issues.Any(issue => issue.CollectorId == "audio-endpoints"));
        Assert.True(
            snapshot.Components.Any(component => component.Type == InventoryComponentType.StorageHealth) ||
            scan.Issues.Any(issue => issue.CollectorId == "storage-health"));
        Assert.True(
            snapshot.Components.Any(component =>
                component.Type == InventoryComponentType.StorageHealth &&
                component.Source == "Storage protocol-specific IOCTL") ||
            scan.Issues.Any(issue => issue.CollectorId == "nvme-health"));
        Assert.True(
            snapshot.Components.Any(component => component.Type == InventoryComponentType.Sensor) ||
            scan.Issues.Any(issue => issue.CollectorId == "thermal-sensors"));
        Assert.All(
            snapshot.Components.Where(component => component.Type == InventoryComponentType.DriverPackage),
            component =>
            {
                Assert.Equal("Drivers", component.Category);
                Assert.Contains("infName", component.Details.Keys);
                Assert.Contains("signer", component.Details.Keys);
                Assert.Contains("isSigned", component.Details.Keys);
            });
        Assert.Contains(
            snapshot.Components,
            component => component.Source.Contains("SetupAPI", StringComparison.Ordinal));
        Assert.NotEmpty(snapshot.Components);
        Assert.Equal(scan.ScanId, snapshot.LatestScan?.ScanId);
        Assert.Equal(scan.Status, snapshot.LatestScan?.Status);
        Assert.True(
            scan.Status is InventoryScanStatus.Completed or InventoryScanStatus.Partial,
            $"Expected a completed or partial scan, but received {scan.Status}.");
    }

    [Fact]
    public async Task Setupapi_enumerates_present_devices_with_instance_ids_and_property_states()
    {
        var collector = new SetupApiDeviceInventoryCollector(
            NullLogger<SetupApiDeviceInventoryCollector>.Instance);

        var firstResult = await collector.CollectAsync(CancellationToken.None);
        var secondResult = await collector.CollectAsync(CancellationToken.None);

        Assert.NotEmpty(firstResult.Components);
        var devices = firstResult.Components
            .Where(component => component.Type == InventoryComponentType.Device)
            .ToArray();
        Assert.NotEmpty(devices);
        Assert.All(devices, component =>
        {
            Assert.Equal(InventoryComponentType.Device, component.Type);
            Assert.Equal("SetupAPI", component.Source);
            Assert.False(string.IsNullOrWhiteSpace(component.Details["instanceId"]));
            Assert.Contains("hardwareIds", component.Details.Keys);
            Assert.Contains("compatibleIds", component.Details.Keys);
            Assert.Contains("deviceStatus", component.Details.Keys);
            Assert.Contains("deviceStatus", component.FieldAvailability.Keys);
        });
        Assert.All(devices, component =>
        {
            Assert.Contains("driverInfPath", component.Details.Keys);
            Assert.Contains("driverProvider", component.Details.Keys);
            Assert.Contains("driverVersion", component.Details.Keys);
            Assert.Contains("driverDate", component.Details.Keys);
        });
        var driverPackages = firstResult.Components
            .Where(component => component.Type == InventoryComponentType.DriverPackage)
            .ToArray();
        Assert.NotEmpty(driverPackages);
        Assert.All(driverPackages, component =>
        {
            Assert.Equal("SetupAPI", component.Source);
            Assert.Equal("Drivers", component.Category);
            Assert.Equal(InventoryFieldAvailability.Unsupported, component.FieldAvailability["signatureStatus"]);
        });
        Assert.Equal(
            firstResult.Components.Count(component => component.Type == InventoryComponentType.Device),
            secondResult.Components.Count(component => component.Type == InventoryComponentType.Device));
    }

    [Fact]
    public async Task Setupapi_collector_honours_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var collector = new SetupApiDeviceInventoryCollector(
            NullLogger<SetupApiDeviceInventoryCollector>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(cancellation.Token));
    }

    [Fact]
    public async Task Wmi_driver_inventory_reports_packages_or_explicit_provider_failure()
    {
        var collector = new DriverPackageInventoryCollector(NullLoggerFactory.Instance);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.True(result.Components.Count > 0 || result.Issues.Count > 0);
        Assert.All(result.Components, component =>
        {
            Assert.Equal(InventoryComponentType.DriverPackage, component.Type);
            Assert.Equal("Drivers", component.Category);
            Assert.Contains("isSigned", component.Details.Keys);
            Assert.Contains("signer", component.Details.Keys);
        });
    }

    [Fact]
    public async Task Extended_hardware_collectors_return_data_or_explicit_coverage_issues()
    {
        IInventoryCollector[] collectors =
        [
            new StorageHealthInventoryCollector(NullLoggerFactory.Instance),
            new NvmeHealthInventoryCollector(NullLogger<NvmeHealthInventoryCollector>.Instance),
            new MonitorEdidInventoryCollector(NullLoggerFactory.Instance),
            new MonitorDisplayParametersCollector(NullLoggerFactory.Instance),
            new KeyboardInventoryCollector(NullLoggerFactory.Instance),
            new PointingDeviceInventoryCollector(NullLoggerFactory.Instance),
            new FirmwareInventoryCollector(NullLogger<FirmwareInventoryCollector>.Instance),
            new ThermalSensorInventoryCollector(NullLoggerFactory.Instance),
            new AudioEndpointInventoryCollector(NullLogger<AudioEndpointInventoryCollector>.Instance)
        ];

        foreach (var collector in collectors)
        {
            var result = await collector.CollectAsync(CancellationToken.None);
            Assert.True(
                result.Components.Count > 0 || result.Issues.Count > 0,
                $"{collector.CollectorId} returned neither data nor a coverage issue.");
            if (collector.CollectorId == "audio-endpoints")
            {
                Assert.DoesNotContain(result.Issues, issue => issue.Code == "audio_endpoint_query_failed");
                Assert.NotEmpty(result.Components);
            }

            if (collector.CollectorId == "nvme-health")
            {
                foreach (var component in result.Components)
                {
                    Assert.Contains("criticalWarning", component.Details.Keys);
                    Assert.Contains("percentageUsed", component.Details.Keys);
                    Assert.Contains("powerOnHours", component.Details.Keys);
                    Assert.Contains(
                        component.FieldAvailability["protocol"],
                        new[]
                        {
                            InventoryFieldAvailability.Available,
                            InventoryFieldAvailability.Unsupported,
                            InventoryFieldAvailability.Inaccessible,
                            InventoryFieldAvailability.Unavailable
                        });
                    if (component.FieldAvailability["protocol"] == InventoryFieldAvailability.Available)
                    {
                        Assert.Matches("^0x[0-9A-F]{2}$", component.Details["criticalWarning"]!);
                    }
                }

                if (collector.CollectorId == "monitor-edid")
                {
                    Assert.NotEmpty(result.Components);
                    Assert.All(result.Components, component =>
                    {
                        Assert.Contains("ManufacturerName", component.Details.Keys);
                        Assert.Contains("ProductCodeID", component.Details.Keys);
                        Assert.Contains("SerialNumberID", component.Details.Keys);
                    });
                }

                if (collector.CollectorId == "monitor-display-parameters")
                {
                    Assert.NotEmpty(result.Components);
                    Assert.All(result.Components, component =>
                    {
                        Assert.Contains("MaxHorizontalImageSize", component.Details.Keys);
                        Assert.Contains("MaxVerticalImageSize", component.Details.Keys);
                    });
                }

                if (collector.CollectorId is "keyboards" or "pointing-devices" or "storage-health" or "nvme-health")
                {
                    Assert.NotEmpty(result.Components);
                }

                if (collector.CollectorId == "firmware-mode" && result.Components.Count > 0)
                {
                    Assert.Contains(
                        result.Components[0].Details["bootMode"],
                        new[] { "BIOS", "UEFI" });
                }

                if (collector.CollectorId == "firmware-mode")
                {
                    Assert.NotEmpty(result.Components);
                }

                if (collector.CollectorId == "audio-endpoints")
                {
                    Assert.All(result.Components, component =>
                    {
                        Assert.Contains("endpointId", component.Details.Keys);
                        Assert.Contains("dataFlow", component.Details.Keys);
                        Assert.Contains("state", component.Details.Keys);
                    });
                }
            }
        }
    }

    [Fact]
    public async Task Extended_hardware_collectors_honour_pre_cancelled_tokens()
    {
        IInventoryCollector[] collectors =
        [
            new StorageHealthInventoryCollector(NullLoggerFactory.Instance),
            new NvmeHealthInventoryCollector(NullLogger<NvmeHealthInventoryCollector>.Instance),
            new MonitorEdidInventoryCollector(NullLoggerFactory.Instance),
            new MonitorDisplayParametersCollector(NullLoggerFactory.Instance),
            new KeyboardInventoryCollector(NullLoggerFactory.Instance),
            new PointingDeviceInventoryCollector(NullLoggerFactory.Instance),
            new FirmwareInventoryCollector(NullLogger<FirmwareInventoryCollector>.Instance),
            new ThermalSensorInventoryCollector(NullLoggerFactory.Instance),
            new AudioEndpointInventoryCollector(NullLogger<AudioEndpointInventoryCollector>.Instance)
        ];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (var collector in collectors)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => collector.CollectAsync(cancellation.Token));
        }
    }

    [Fact]
    public async Task Operating_system_collector_reads_a_real_windows_record()
    {
        var collector = new OperatingSystemInventoryCollector(NullLoggerFactory.Instance);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Contains(
            result.Components,
            component => component.Type == InventoryComponentType.OperatingSystem);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ScanRecord CreateScan(
        Guid scanId,
        DateTimeOffset startedAt,
        InventoryScanStatus status,
        IReadOnlyList<InventoryComponent> components,
        IReadOnlyList<CollectorIssue> issues) =>
        new(
            scanId,
            startedAt,
            DateTimeOffset.UtcNow,
            "devices",
            status,
            1,
            1,
            components,
            issues);

    private static InventoryComponent CreateDevice(string identity, string name) =>
        new(
            InventoryComponentType.Device,
            identity,
            name,
            "Test fixture",
            1,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["status"] = "OK" });

    private static GuardianTelemetrySnapshot CreateTelemetrySample(DateTimeOffset collectedAt, int index) =>
        new(
            collectedAt,
            new TelemetryReading<double>(index, InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<ulong>(1024, InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<ulong>(2048, InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<TimeSpan>(TimeSpan.FromHours(1), InventoryFieldAvailability.Available, "test"),
            new TelemetryReading<int>(1, InventoryFieldAvailability.Available, "test"),
            [
                new GuardianStorageVolume(
                    "C:\\",
                    "System",
                    4096,
                    2048,
                    InventoryFieldAvailability.Available,
                    "test")
            ])
        {
            NetworkInterfaces =
            [
                new GuardianNetworkInterface(
                    "network:test",
                    "Test adapter",
                    "Up",
                    new TelemetryReading<double>(12, InventoryFieldAvailability.Available, "test"),
                    new TelemetryReading<double>(34, InventoryFieldAvailability.Available, "test"))
            ],
            Processes =
            [
                new GuardianProcessObservation(
                    "process:123:456",
                    123,
                    "test-process",
                    collectedAt.AddHours(-1),
                    new TelemetryReading<double>(null, InventoryFieldAvailability.Unavailable, "test"),
                    new TelemetryReading<ulong>(2048, InventoryFieldAvailability.Available, "test"))
            ],
            GpuAdapters =
            [
                new GuardianGpuAdapter(
                    "adapter:test",
                    "Test GPU",
                    "test",
                    InventoryFieldAvailability.Available,
                    new TelemetryReading<double>(null, InventoryFieldAvailability.Unsupported, "test", "Unsupported."),
                    new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, "test", "Unsupported."),
                    new TelemetryReading<double>(null, InventoryFieldAvailability.Unsupported, "test", "Unsupported."))
            ]
        };
}
