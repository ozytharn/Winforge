using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.Application.Monitoring;

public enum GuardianFindingState
{
    Informational,
    Warning,
    Critical,
    Unknown
}

public sealed record GuardianFinding(
    string Category,
    GuardianFindingState State,
    string Message,
    string Source,
    DateTimeOffset ObservedAt);

public sealed class GuardianAssessment
{
    public IReadOnlyList<GuardianFinding> Evaluate(
        GuardianTelemetrySnapshot snapshot,
        GuardianThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);
        thresholds.Validate();

        var findings = new List<GuardianFinding>();
        AddPercentageFinding(
            findings,
            "CPU",
            snapshot.CpuUsagePercent,
            thresholds.CpuWarningPercent,
            thresholds.CpuCriticalPercent,
            "CPU usage is at or above the configured warning threshold.",
            snapshot.CollectedAt);

        if (snapshot.AvailableMemoryBytes.Availability != InventoryFieldAvailability.Available ||
            snapshot.TotalMemoryBytes.Availability != InventoryFieldAvailability.Available ||
            snapshot.AvailableMemoryBytes.Value is not { } available ||
            snapshot.TotalMemoryBytes.Value is not { } total ||
            total == 0 ||
            available > total)
        {
            findings.Add(Unknown(
                "Memory",
                "Memory usage could not be calculated from available physical-memory readings.",
                snapshot.AvailableMemoryBytes,
                snapshot.CollectedAt));
        }
        else
        {
            var usedPercent = (1d - (double)available / total) * 100;
            AddPercentageFinding(
                findings,
                "Memory",
                new TelemetryReading<double>(
                    usedPercent,
                    InventoryFieldAvailability.Available,
                    $"{snapshot.AvailableMemoryBytes.Source} + {snapshot.TotalMemoryBytes.Source}"),
                thresholds.MemoryWarningPercent,
                thresholds.MemoryCriticalPercent,
                "Memory usage is at or above the configured warning threshold.",
                snapshot.CollectedAt);
        }

        if (snapshot.StorageEnumeration.Availability != InventoryFieldAvailability.Available ||
            snapshot.StorageVolumes.Count == 0)
        {
            findings.Add(Unknown(
                "Storage",
                snapshot.StorageEnumeration.Limitation ?? "No fixed storage volumes were available for capacity assessment.",
                snapshot.StorageEnumeration.Source,
                snapshot.CollectedAt));
        }
        else
        {
            foreach (var volume in snapshot.StorageVolumes)
            {
                if (volume.Availability != InventoryFieldAvailability.Available ||
                    volume.TotalBytes is not { } totalBytes ||
                    volume.FreeBytes is not { } freeBytes ||
                    totalBytes == 0 ||
                    freeBytes > totalBytes)
                {
                    findings.Add(Unknown(
                        $"Storage {volume.Name}",
                        volume.Limitation ?? "Volume capacity could not be read.",
                        volume.Source,
                        snapshot.CollectedAt));
                    continue;
                }

                var freePercent = (double)freeBytes / totalBytes * 100;
                findings.Add(new GuardianFinding(
                    $"Storage {volume.Name}",
                    freePercent < thresholds.MinimumFreeStorageCriticalPercent
                        ? GuardianFindingState.Critical
                        : freePercent < thresholds.MinimumFreeStoragePercent
                            ? GuardianFindingState.Warning
                            : GuardianFindingState.Informational,
                    freePercent < thresholds.MinimumFreeStorageCriticalPercent
                        ? $"Free space ({freePercent:F1}%) is below the configured critical {thresholds.MinimumFreeStorageCriticalPercent:F1}% threshold."
                        : freePercent < thresholds.MinimumFreeStoragePercent
                            ? $"Free space ({freePercent:F1}%) is below the configured {thresholds.MinimumFreeStoragePercent:F1}% warning threshold."
                            : $"Free space is {freePercent:F1}% ({FormatBytes(freeBytes)} available).",
                    volume.Source,
                    snapshot.CollectedAt));
            }
        }

        findings.Add(new GuardianFinding(
            "System uptime",
            snapshot.SystemUptime.Availability == InventoryFieldAvailability.Available
                ? GuardianFindingState.Informational
                : GuardianFindingState.Unknown,
            snapshot.SystemUptime.Value is { } uptime
                ? $"Windows has been running for {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m."
                : snapshot.SystemUptime.Limitation ?? "System uptime is unavailable.",
            snapshot.SystemUptime.Source,
            snapshot.CollectedAt));

        return findings;
    }

    private static void AddPercentageFinding(
        ICollection<GuardianFinding> findings,
        string category,
        TelemetryReading<double> reading,
        double threshold,
        double criticalThreshold,
        string warning,
        DateTimeOffset observedAt)
    {
        if (reading.Availability != InventoryFieldAvailability.Available || reading.Value is not { } value)
        {
            findings.Add(Unknown(
                category,
                reading.Limitation ?? $"{category} reading is unavailable.",
                reading.Source,
                observedAt));
            return;
        }

        var state = value >= criticalThreshold
            ? GuardianFindingState.Critical
            : value >= threshold
                ? GuardianFindingState.Warning
                : GuardianFindingState.Informational;
        findings.Add(new GuardianFinding(
            category,
            state,
            state == GuardianFindingState.Critical
                ? $"Critical threshold crossed. Current reading: {value:F1}% (warning {threshold:F1}%, critical {criticalThreshold:F1}%)."
                : state == GuardianFindingState.Warning
                    ? $"{warning} Current reading: {value:F1}%."
                    : $"Current reading: {value:F1}%.",
            reading.Source,
            observedAt));
    }

    private static GuardianFinding Unknown<T>(
        string category,
        string message,
        TelemetryReading<T> reading,
        DateTimeOffset observedAt)
        where T : struct =>
        Unknown(category, message, reading.Source, observedAt);

    private static GuardianFinding Unknown(
        string category,
        string message,
        string source,
        DateTimeOffset observedAt) =>
        new(category, GuardianFindingState.Unknown, message, source, observedAt);

    private static string FormatBytes(ulong bytes)
    {
        var gibibytes = bytes / 1024d / 1024 / 1024;
        return $"{gibibytes:F1} GiB";
    }
}
