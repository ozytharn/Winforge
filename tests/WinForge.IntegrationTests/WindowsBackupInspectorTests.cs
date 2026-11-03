using System.Text;
using WinForge.Domain.Inventory;
using WinForge.Domain.Recovery;
using WinForge.Infrastructure.Recovery;

namespace WinForge.IntegrationTests;

public sealed class WindowsBackupInspectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WinForgeBackupInspectorTests",
        Guid.NewGuid().ToString("N"));
    private readonly WindowsBackupInspector _inspector = new();

    [Fact]
    public async Task Windows_image_backup_validates_bounded_xml_and_container_header_only()
    {
        var root = Path.Combine(_directory, "WindowsImageBackup");
        var backupSet = Path.Combine(root, "test-machine", "Backup 2025-01-01");
        Directory.CreateDirectory(backupSet);
        await File.WriteAllTextAsync(Path.Combine(backupSet, "BackupSpecs.xml"), "<BackupSpecs><Set>test</Set></BackupSpecs>");
        var imagePath = Path.Combine(backupSet, "backup.vhdx");
        var image = new byte[1024];
        Encoding.ASCII.GetBytes("vhdxfile").CopyTo(image, 0);
        await File.WriteAllBytesAsync(imagePath, image);

        var result = await InspectAsync(
            Candidate(BackupProvider.WindowsImageBackup, root),
            CancellationToken.None);

        Assert.Equal(BackupInspectionState.Verified, result.State);
        Assert.True(result.RecognizedStructure);
        Assert.Equal(1, result.DiscoveredStructureGroups);
        Assert.Equal(1024, result.BackupContainerBytes);
        Assert.Contains(result.Checks, check =>
            check.Name.StartsWith("Container header", StringComparison.Ordinal) &&
            check.State == BackupCheckState.Passed);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("not mounted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Malformed_manifest_and_invalid_container_are_reported_without_claiming_recoverability()
    {
        var root = Path.Combine(_directory, "WindowsImageBackup");
        var backupSet = Path.Combine(root, "test-machine", "Backup broken");
        Directory.CreateDirectory(backupSet);
        await File.WriteAllTextAsync(Path.Combine(backupSet, "BackupSpecs.xml"), "<BackupSpecs>");
        await File.WriteAllBytesAsync(Path.Combine(backupSet, "broken.vhdx"), new byte[1024]);

        var result = await InspectAsync(
            Candidate(BackupProvider.WindowsImageBackup, root),
            CancellationToken.None);

        Assert.Equal(BackupInspectionState.Failed, result.State);
        Assert.Contains(result.Checks, check => check.State == BackupCheckState.Failed);
        Assert.DoesNotContain(result.Limitations, limitation =>
            limitation.Contains("restoreability established", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task File_history_valid_configuration_is_warning_because_payload_integrity_is_unsupported()
    {
        var root = Path.Combine(_directory, "FileHistory");
        var configuration = Path.Combine(root, "user", "computer", "Configuration");
        Directory.CreateDirectory(configuration);
        await File.WriteAllTextAsync(Path.Combine(configuration, "Config1.xml"), "<HistoryConfig><Version>1</Version></HistoryConfig>");
        Directory.CreateDirectory(Path.Combine(root, "user", "computer", "Data"));

        var result = await InspectAsync(Candidate(BackupProvider.FileHistory, root), CancellationToken.None);

        Assert.Equal(BackupInspectionState.Warning, result.State);
        Assert.True(result.RecognizedStructure);
        Assert.Contains(result.Checks, check =>
            check.Name == "File History payload integrity" &&
            check.State == BackupCheckState.Skipped);
        Assert.Null(result.BackupContainerBytes);
    }

    [Fact]
    public async Task Unknown_and_inaccessible_candidates_are_distinguished()
    {
        var unsupported = await InspectAsync(
            new BackupCandidate("unknown", "Unknown", BackupProvider.Unsupported, null, null,
                DateTimeOffset.UtcNow, "Unknown", "Fixture", null),
            CancellationToken.None);
        var missingRoot = await InspectAsync(
            Candidate(
                BackupProvider.WindowsImageBackup,
                Path.Combine(_directory, "WindowsImageBackup"),
                createIdentity: false),
            CancellationToken.None);

        Assert.Equal(BackupInspectionState.Unsupported, unsupported.State);
        Assert.Equal(BackupInspectionState.Inaccessible, missingRoot.State);
    }

    [Fact]
    public async Task Large_structure_stops_at_the_documented_root_entry_bound()
    {
        var root = Path.Combine(_directory, "WindowsImageBackup");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 300; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"Backup group {index:D3}"));
        }

        var result = await InspectAsync(
            Candidate(BackupProvider.WindowsImageBackup, root),
            CancellationToken.None);

        Assert.Equal(BackupInspectionState.Incomplete, result.State);
        Assert.Equal(32, result.DiscoveredStructureGroups);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("root entry limit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Limitations, limitation => limitation.Contains("backup group limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Windows_image_backup_stops_after_the_backup_set_limit()
    {
        var root = Path.Combine(_directory, "WindowsImageBackup");
        for (var groupIndex = 0; groupIndex < 2; groupIndex++)
        {
            var group = Path.Combine(root, $"test-machine-{groupIndex}");
            Directory.CreateDirectory(group);
            for (var index = 0; index < 64; index++)
            {
                Directory.CreateDirectory(Path.Combine(group, $"Backup set {index:D2}"));
            }
        }

        var result = await InspectAsync(
            Candidate(BackupProvider.WindowsImageBackup, root),
            CancellationToken.None);

        Assert.Equal(BackupInspectionState.Incomplete, result.State);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("backup set limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_is_checked_before_opening_a_candidate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InspectAsync(
                Candidate(BackupProvider.WindowsImageBackup, Path.Combine(_directory, "WindowsImageBackup")),
                cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task<BackupInspectionResult> InspectAsync(
        BackupCandidate candidate,
        CancellationToken cancellationToken) =>
        _inspector.InspectAsync(candidate, Guid.NewGuid(), DateTimeOffset.UtcNow, cancellationToken);

    private static BackupCandidate Candidate(
        BackupProvider provider,
        string path,
        bool createIdentity = true)
    {
        var leaf = provider == BackupProvider.WindowsImageBackup ? "WindowsImageBackup" : "FileHistory";
        var root = Path.GetPathRoot(path)!.TrimEnd('\\', ':');
        var identity = $"backup-folder:{root}:{(createIdentity ? leaf : "WindowsImageBackup").ToUpperInvariant()}";

        return new BackupCandidate(
            identity,
            provider.ToString(),
            provider,
            path,
            "C:\\",
            DateTimeOffset.UtcNow,
            "Available",
            "Temporary integration fixture",
            null);
    }
}
