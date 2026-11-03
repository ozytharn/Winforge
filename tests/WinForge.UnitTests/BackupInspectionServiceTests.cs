using WinForge.Application.Inventory;
using WinForge.Application.Recovery;
using WinForge.Domain.Inventory;
using WinForge.Domain.Recovery;

namespace WinForge.UnitTests;

public sealed class BackupInspectionServiceTests
{
    [Fact]
    public async Task Cancellation_is_persisted_as_incomplete_evidence_before_it_is_rethrown()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new InMemoryBackupInspectionRepository();
        var service = new BackupInspectionService(
            new UnusedPassportScanService(),
            new CancellingInspector(cancellation),
            repository);
        var candidate = new BackupCandidate(
            "backup-folder:C:WINDOWSIMAGEBACKUP",
            "Windows image candidate",
            BackupProvider.WindowsImageBackup,
            @"C:\WindowsImageBackup",
            "C:\\",
            DateTimeOffset.UtcNow,
            "Available",
            "Test fixture",
            null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.InspectAsync(candidate, cancellation.Token));

        var saved = Assert.IsType<BackupInspectionResult>(repository.SavedResult);
        Assert.Equal(BackupInspectionState.Cancelled, saved.State);
        Assert.False(saved.RecognizedStructure);
        Assert.Contains(saved.Limitations, limitation => limitation.Contains("not an integrity result", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CancellingInspector(CancellationTokenSource cancellation) : IBackupInspector
    {
        public Task<BackupInspectionResult> InspectAsync(
            BackupCandidate candidate,
            Guid inspectionId,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancelled inspector should not return a result.");
        }
    }

    private sealed class InMemoryBackupInspectionRepository : IBackupInspectionRepository
    {
        public BackupInspectionResult? SavedResult { get; private set; }

        public Task SaveInspectionAsync(BackupInspectionResult result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedResult = result;
            return Task.CompletedTask;
        }

        public Task<BackupInspectionResult?> GetInspectionAsync(
            Guid inspectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(SavedResult?.InspectionId == inspectionId ? SavedResult : null);

        public Task<IReadOnlyList<BackupInspectionSummary>> GetInspectionHistoryAsync(
            string candidateIdentity,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupInspectionSummary>>([]);
    }

    private sealed class UnusedPassportScanService : IPassportScanService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PassportSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ScanRecord> RunScanAsync(
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ScanRecord> RunScanAsync(
            InventoryScanGroup group,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
