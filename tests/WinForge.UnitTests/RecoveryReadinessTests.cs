using WinForge.Application.Recovery;
using WinForge.Domain.Inventory;

namespace WinForge.UnitTests;

public sealed class RecoveryReadinessTests
{
    [Fact]
    public void Inaccessible_recovery_sources_remain_unknown_not_gaps()
    {
        var report = new RecoveryReadinessAssessor().Assess(
        [
            RecoveryItem(
                InventoryComponentType.RecoveryEnvironment,
                "winre",
                new Dictionary<string, string?>
                {
                    ["windowsReStatus"] = null,
                    ["availability"] = InventoryFieldAvailability.Inaccessible.ToString(),
                    ["status"] = "Status unavailable"
                },
                InventoryFieldAvailability.Inaccessible)
        ]);

        var winRe = Assert.Single(report.Findings, finding =>
            finding.Category == "Windows Recovery Environment");
        Assert.Equal(RecoveryReadinessState.Unknown, winRe.State);
        Assert.DoesNotContain(report.Findings, finding => finding.State == RecoveryReadinessState.Gap);
    }

    [Fact]
    public void Winre_disabled_and_empty_restore_point_catalog_are_explicit_gaps()
    {
        var report = new RecoveryReadinessAssessor().Assess(
        [
            RecoveryItem(
                InventoryComponentType.RecoveryEnvironment,
                "winre",
                new Dictionary<string, string?>
                {
                    ["windowsReStatus"] = "Disabled",
                    ["availability"] = InventoryFieldAvailability.Available.ToString(),
                    ["status"] = "Disabled"
                },
                InventoryFieldAvailability.Available),
            RecoveryItem(
                InventoryComponentType.RestorePoint,
                "restore-point-catalog",
                new Dictionary<string, string?>
                {
                    ["availability"] = InventoryFieldAvailability.Missing.ToString(),
                    ["status"] = "No restore points reported"
                },
                InventoryFieldAvailability.Missing)
        ]);

        Assert.Equal(
            2,
            report.Findings.Count(finding => finding.State == RecoveryReadinessState.Gap));
        Assert.Contains("No readiness score", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backup_directory_candidate_is_never_assessed_as_verified()
    {
        var report = new RecoveryReadinessAssessor().Assess(
        [
            RecoveryItem(
                InventoryComponentType.BackupCatalogItem,
                "backup-folder:C:WINDOWSIMAGEBACKUP",
                new Dictionary<string, string?>
                {
                    ["availability"] = InventoryFieldAvailability.Available.ToString(),
                    ["status"] = "Folder detected; backup not verified",
                    ["dataSource"] = "Read-only directory check"
                },
                InventoryFieldAvailability.Available)
        ]);

        var backup = Assert.Single(report.Findings, finding => finding.Category == "Backup discovery");
        Assert.Equal(RecoveryReadinessState.Attention, backup.State);
        Assert.Contains("unverified", backup.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    private static InventoryComponent RecoveryItem(
        InventoryComponentType type,
        string identity,
        IReadOnlyDictionary<string, string?> details,
        InventoryFieldAvailability availability) =>
        new(type, identity, identity, "Synthetic recovery source", 2, DateTimeOffset.UtcNow, details)
        {
            Category = "Recovery",
            FieldAvailability = details.ToDictionary(
                pair => pair.Key,
                _ => availability,
                StringComparer.Ordinal)
        };
}
