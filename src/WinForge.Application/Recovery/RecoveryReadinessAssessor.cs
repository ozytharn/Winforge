using System.Globalization;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Recovery;

public enum RecoveryReadinessState
{
    Available,
    Gap,
    Attention,
    Unknown
}

public enum RecoveryFindingSeverity
{
    Informational,
    Low,
    Medium,
    High
}

public sealed record RecoveryReadinessFinding(
    string Category,
    RecoveryReadinessState State,
    RecoveryFindingSeverity Severity,
    string Explanation,
    string Recommendation,
    IReadOnlyList<string> Evidence,
    decimal Confidence);

public sealed record RecoveryReadinessReport(
    DateTimeOffset AssessedAt,
    IReadOnlyList<RecoveryReadinessFinding> Findings,
    string Summary);

public sealed class RecoveryReadinessAssessor
{
    public RecoveryReadinessReport Assess(IEnumerable<InventoryComponent> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var components = inventory.Where(IsRecoveryComponent).ToArray();
        var findings = new List<RecoveryReadinessFinding>
        {
            AssessWinRe(components),
            AssessSystemRestore(components),
            AssessBackups(components),
            AssessRecoveryStorage(components),
            AssessBitLocker(components),
            AssessServices(components),
            AssessBootConfiguration(components)
        };
        var gaps = findings.Count(finding => finding.State == RecoveryReadinessState.Gap);
        var attention = findings.Count(finding => finding.State == RecoveryReadinessState.Attention);
        var unknown = findings.Count(finding => finding.State == RecoveryReadinessState.Unknown);
        var summary = gaps + attention + unknown == 0
            ? "Discovery sources reported recovery evidence; this is not a recoverability guarantee."
            : $"Assessment found {gaps} source-reported gap(s), {attention} item(s) needing review, and {unknown} unknown/unavailable area(s). No readiness score is calculated.";
        return new RecoveryReadinessReport(DateTimeOffset.UtcNow, findings, summary);
    }

    private static RecoveryReadinessFinding AssessWinRe(IReadOnlyCollection<InventoryComponent> components)
    {
        var item = Find(components, InventoryComponentType.RecoveryEnvironment, "winre");
        if (item is null)
        {
            return Unknown("Windows Recovery Environment", "WinRE status has not been observed.");
        }

        var status = Detail(item, "windowsReStatus") ?? Detail(item, "status");
        var availability = Availability(item, "windowsReStatus");
        if (availability != InventoryFieldAvailability.Available)
        {
            return Unknown(
                "Windows Recovery Environment",
                "WinRE status is unavailable or inaccessible; this is not evidence that WinRE is disabled.",
                [Evidence(item)]);
        }

        return string.Equals(status, "Enabled", StringComparison.OrdinalIgnoreCase)
            ? Finding(
                "Windows Recovery Environment",
                RecoveryReadinessState.Available,
                RecoveryFindingSeverity.Informational,
                "Windows reported WinRE enabled; configuration status does not test recovery execution.",
                "Review WinRE status after significant Windows servicing or recovery changes.",
                [Evidence(item)],
                0.90m)
            : string.Equals(status, "Disabled", StringComparison.OrdinalIgnoreCase)
                ? Finding(
                    "Windows Recovery Environment",
                    RecoveryReadinessState.Gap,
                    RecoveryFindingSeverity.High,
                    "Windows reported WinRE disabled.",
                    "Review Windows recovery options and determine why WinRE is disabled before relying on local recovery.",
                    [Evidence(item)],
                    0.90m)
                : Unknown("Windows Recovery Environment", "WinRE status could not be interpreted.", [Evidence(item)]);
    }

    private static RecoveryReadinessFinding AssessSystemRestore(IReadOnlyCollection<InventoryComponent> components)
    {
        var points = components.Where(component =>
                component.Type == InventoryComponentType.RestorePoint &&
                !component.IdentityKey.Equals("restore-point-catalog", StringComparison.OrdinalIgnoreCase) &&
                Availability(component, "availability") == InventoryFieldAvailability.Available)
            .ToArray();
        var catalog = Find(components, InventoryComponentType.RestorePoint, "restore-point-catalog");
        var configuration = Find(components, InventoryComponentType.RecoveryEnvironment, "system-restore-configuration");
        if (points.Length > 0)
        {
            return Finding(
                "System Restore",
                RecoveryReadinessState.Attention,
                RecoveryFindingSeverity.Medium,
                $"{points.Length} restore point(s) were reported. Their integrity, accessibility, and restore outcome were not tested.",
                "Review restore-point freshness and separately confirm the relevant volume protection configuration.",
                points.Take(5).Select(Evidence).ToArray(),
                0.88m);
        }

        if (catalog is not null &&
            Detail(catalog, "status")?.Contains("No restore points", StringComparison.OrdinalIgnoreCase) == true &&
            Availability(catalog, "availability") == InventoryFieldAvailability.Missing)
        {
            return Finding(
                "System Restore",
                RecoveryReadinessState.Gap,
                RecoveryFindingSeverity.Medium,
                "The System Restore provider returned no restore points during discovery.",
                "Review whether System Restore is appropriate and configured for the system volume; provider absence is not a full recovery assessment.",
                [Evidence(catalog), configuration is null ? "System Restore configuration was not observed." : Evidence(configuration)],
                0.78m);
        }

        return Unknown(
            "System Restore",
            "Restore points or configuration could not be confirmed.",
            new[] { catalog, configuration }.Where(item => item is not null).Select(item => Evidence(item!)).ToArray());
    }

    private static RecoveryReadinessFinding AssessBackups(IReadOnlyCollection<InventoryComponent> components)
    {
        var candidates = components.Where(component =>
                component.Type == InventoryComponentType.BackupCatalogItem &&
                component.IdentityKey.StartsWith("backup-folder:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var visible = candidates.Where(component =>
            Availability(component, "availability") == InventoryFieldAvailability.Available).ToArray();
        if (visible.Length > 0)
        {
            return Finding(
                "Backup discovery",
                RecoveryReadinessState.Attention,
                RecoveryFindingSeverity.Medium,
                $"{visible.Length} backup folder candidate(s) were found; backup contents, completeness, integrity, date, and restoreability are unverified.",
                "Use the backup product's supported catalog and integrity/restore-test workflow. Do not treat folder presence as a verified backup.",
                visible.Take(5).Select(Evidence).ToArray(),
                0.75m);
        }

        var catalog = Find(components, InventoryComponentType.BackupCatalogItem, "windows-backup-version-catalog");
        return Unknown(
            "Backup discovery",
            "No accessible backup candidate was confirmed; the Windows Backup version catalog was not queried with elevated permissions.",
            catalog is null ? [] : [Evidence(catalog)]);
    }

    private static RecoveryReadinessFinding AssessRecoveryStorage(IReadOnlyCollection<InventoryComponent> components)
    {
        var volumes = components.Where(component =>
                component.Type == InventoryComponentType.RecoveryVolume &&
                Availability(component, "availability") == InventoryFieldAvailability.Available &&
                long.TryParse(Detail(component, "availableFreeBytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .ToArray();
        if (volumes.Length == 0)
        {
            return Unknown(
                "Recovery storage capacity",
                "No accessible mounted-volume capacity observation is available; unmounted partitions and reserved recovery space may not be represented.");
        }

        var evidence = volumes
            .OrderByDescending(volume => ParseLong(Detail(volume, "availableFreeBytes")))
            .Take(5)
            .Select(volume =>
                $"{Detail(volume, "driveName") ?? volume.DisplayName}: {Detail(volume, "availableFreeBytes")} free of {Detail(volume, "totalBytes")} bytes.")
            .ToArray();
        return Finding(
            "Recovery storage capacity",
            RecoveryReadinessState.Attention,
            RecoveryFindingSeverity.Low,
            $"{volumes.Length} mounted volume(s) reported free-space capacity. No universal sufficiency threshold is applied.",
            "Compare available space with the expected size and destination requirements of the specific recovery method.",
            evidence,
            0.94m);
    }

    private static RecoveryReadinessFinding AssessBitLocker(IReadOnlyCollection<InventoryComponent> components)
    {
        var volumes = components.Where(component => component.Type == InventoryComponentType.BitLockerVolume).ToArray();
        if (volumes.Length == 0)
        {
            return Unknown(
                "BitLocker considerations",
                "BitLocker status could not be confirmed. Recovery keys are not collected.");
        }

        var protectedVolumes = volumes.Where(volume =>
            Detail(volume, "protectionStatus")?.Equals("Protected", StringComparison.OrdinalIgnoreCase) == true &&
            Availability(volume, "protectionStatus") == InventoryFieldAvailability.Available).ToArray();
        var evidence = volumes.Select(Evidence).Take(5).ToArray();
        return Finding(
            "BitLocker considerations",
            RecoveryReadinessState.Attention,
            RecoveryFindingSeverity.Medium,
            protectedVolumes.Length > 0
                ? $"{protectedVolumes.Length} volume(s) reported BitLocker protection. WinForge does not inspect recovery keys or key protectors."
                : "Volume protection metadata was reported, but WinForge does not establish recovery-key availability.",
            "Independently verify that recovery information is available through the user's approved, secure recovery-key channel.",
            evidence,
            protectedVolumes.Length > 0 ? 0.88m : 0.55m);
    }

    private static RecoveryReadinessFinding AssessServices(IReadOnlyCollection<InventoryComponent> components)
    {
        var services = components.Where(component =>
                component.Type == InventoryComponentType.RecoveryService &&
                !component.IdentityKey.Equals("recovery-service-catalog", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return services.Length == 0
            ? Unknown(
                "Recovery-related services",
                "Selected recovery service metadata was not available. Service stopped/disabled state is not treated as a readiness verdict.")
            : Finding(
                "Recovery-related services",
                RecoveryReadinessState.Attention,
                RecoveryFindingSeverity.Low,
                $"{services.Length} selected recovery-related service record(s) were observed. Service state alone does not establish that a recovery workflow will work.",
                "Review service details only in the context of a specific, documented Windows recovery workflow.",
                services.Take(5).Select(Evidence).ToArray(),
                0.72m);
    }

    private static RecoveryReadinessFinding AssessBootConfiguration(
        IReadOnlyCollection<InventoryComponent> components)
    {
        var boot = components.FirstOrDefault(component =>
            component.Type == InventoryComponentType.RecoveryEnvironment &&
            component.IdentityKey.StartsWith("windows-boot-configuration", StringComparison.OrdinalIgnoreCase));
        return boot is null || Availability(boot, "availability") != InventoryFieldAvailability.Available
            ? Unknown(
                "Boot configuration",
                "WMI boot configuration metadata was not available; this is not a complete BCD or boot-repair assessment.",
                boot is null ? [] : [Evidence(boot)])
            : Finding(
                "Boot configuration",
                RecoveryReadinessState.Available,
                RecoveryFindingSeverity.Informational,
                "WMI reported Windows boot configuration metadata; the full boot configuration and bootability were not tested.",
                "Treat this metadata as diagnostic context only.",
                [Evidence(boot)],
                0.78m);
    }

    private static bool IsRecoveryComponent(InventoryComponent component) =>
        component.Type is InventoryComponentType.RecoveryEnvironment or InventoryComponentType.RestorePoint or
            InventoryComponentType.BackupCatalogItem or InventoryComponentType.RecoveryPartition or
            InventoryComponentType.RecoveryVolume or InventoryComponentType.BitLockerVolume or
            InventoryComponentType.RecoveryService or InventoryComponentType.RecoveryEvent;

    private static InventoryComponent? Find(
        IEnumerable<InventoryComponent> components,
        InventoryComponentType type,
        string identity) =>
        components.FirstOrDefault(component =>
            component.Type == type &&
            component.IdentityKey.Equals(identity, StringComparison.OrdinalIgnoreCase));

    private static InventoryFieldAvailability Availability(InventoryComponent component, string key)
    {
        var detail = Detail(component, key);
        if (key.Equals("availability", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<InventoryFieldAvailability>(detail, out var parsed))
        {
            return parsed;
        }

        return component.FieldAvailability.TryGetValue(key, out var value)
            ? value
            : detail is null
                ? InventoryFieldAvailability.Unknown
                : InventoryFieldAvailability.Available;
    }

    private static string? Detail(InventoryComponent component, string key)
    {
        var matchingKey = component.Details.Keys.FirstOrDefault(
            candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
        return matchingKey is null ? null : component.Details[matchingKey];
    }

    private static string Evidence(InventoryComponent component) =>
        $"{component.DisplayName} [{component.Source}; {Detail(component, "status") ?? "status not reported"}; {Availability(component, "availability")}].";

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static RecoveryReadinessFinding Unknown(
        string category,
        string explanation,
        IReadOnlyList<string>? evidence = null) =>
        Finding(
            category,
            RecoveryReadinessState.Unknown,
            RecoveryFindingSeverity.Medium,
            explanation,
            "Retry discovery with the required Windows provider available; do not interpret unavailable data as a missing capability.",
            evidence ?? [],
            0.20m);

    private static RecoveryReadinessFinding Finding(
        string category,
        RecoveryReadinessState state,
        RecoveryFindingSeverity severity,
        string explanation,
        string recommendation,
        IReadOnlyList<string> evidence,
        decimal confidence) =>
        new(category, state, severity, explanation, recommendation, evidence, confidence);
}
