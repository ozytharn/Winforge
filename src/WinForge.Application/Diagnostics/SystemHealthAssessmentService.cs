using WinForge.Application.Monitoring;
using WinForge.Application.Recovery;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Diagnostics;

public enum SystemHealthState
{
    Healthy,
    Degraded,
    Critical,
    Unknown,
    Unavailable,
    Incomplete
}

public sealed record SystemHealthFinding(
    string Identity,
    string Category,
    SystemHealthState State,
    string Severity,
    double Confidence,
    string Message,
    IReadOnlyList<string> Evidence,
    string Source,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> RelatedEntities,
    string SuggestedInvestigation);

public sealed record StorageDriverAssessment(
    int StorageDeviceCount,
    int HealthyStorageCount,
    int DegradedStorageCount,
    int UnknownStorageCount,
    int DriverPackageCount,
    int DriverWarningCount,
    IReadOnlyList<string> Warnings,
    SystemHealthState OverallState = SystemHealthState.Unknown,
    IReadOnlyList<SystemHealthFinding>? Findings = null)
{
    public bool HasIssues => OverallState is SystemHealthState.Degraded or SystemHealthState.Critical or SystemHealthState.Unknown or SystemHealthState.Incomplete || DegradedStorageCount > 0 || DriverWarningCount > 0 || UnknownStorageCount > 0;
}

public sealed class SystemHealthAssessmentService
{
    public StorageDriverAssessment Analyze(
        IEnumerable<InventoryComponent> inventory,
        IReadOnlyList<GuardianFinding>? guardianFindings = null,
        RecoveryReadinessReport? recoveryReport = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var findings = new List<SystemHealthFinding>();
        var storageComponents = inventory
            .Where(component => component.Type == InventoryComponentType.StorageHealth)
            .ToArray();
        var storageSummary = EvaluateStorageComponents(storageComponents, findings);

        var driverPackages = inventory
            .Where(component => component.Type == InventoryComponentType.DriverPackage)
            .ToArray();

        foreach (var driverPackage in driverPackages)
        {
            if (!IsSignedDriver(driverPackage, out var signingIssue, out var state, out var confidence))
            {
                findings.Add(new SystemHealthFinding(
                    driverPackage.IdentityKey,
                    "Driver health",
                    state,
                    state == SystemHealthState.Critical ? "Critical" : "Degraded",
                    confidence,
                    signingIssue,
                    [driverPackage.Source, driverPackage.DisplayName],
                    driverPackage.Source,
                    driverPackage.ObservedAt,
                    [driverPackage.DisplayName],
                    "Review driver signatures, Windows Update history, and device manager warnings for the affected device."));
            }
        }

        if (guardianFindings is not null)
        {
            foreach (var finding in guardianFindings)
            {
                findings.Add(new SystemHealthFinding(
                    finding.Category,
                    "Guardian telemetry",
                    ConvertGuardianState(finding.State),
                    finding.State.ToString(),
                    ConvertGuardianConfidence(finding.State),
                    finding.Message,
                    [finding.Source],
                    finding.Source,
                    finding.ObservedAt,
                    [finding.Category],
                    "Inspect the latest telemetry sample and compare it with recent observations to confirm whether the impact is persistent."));
            }
        }

        if (recoveryReport is not null)
        {
            foreach (var finding in recoveryReport.Findings)
            {
                findings.Add(new SystemHealthFinding(
                    finding.Category,
                    "Recovery readiness",
                    ConvertRecoveryState(finding.State),
                    finding.Severity.ToString(),
                    (double)finding.Confidence,
                    finding.Explanation,
                    finding.Evidence,
                    "Recovery readiness assessment",
                    recoveryReport.AssessedAt,
                    [finding.Category],
                    finding.Recommendation));
            }
        }

        var driverWarnings = findings
            .Where(finding => finding.Category == "Driver health")
            .Select(finding => finding.Message)
            .ToArray();

        var overallState = DetermineOverallState(findings, storageSummary.StorageDeviceCount, driverPackages.Length);
        return new StorageDriverAssessment(
            storageSummary.StorageDeviceCount,
            storageSummary.HealthyStorageCount,
            storageSummary.DegradedStorageCount,
            storageSummary.UnknownStorageCount,
            driverPackages.Length,
            driverWarnings.Length,
            driverWarnings,
            overallState,
            findings);
    }

    public string BuildSummary(StorageDriverAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (assessment.StorageDeviceCount == 0 && assessment.DriverPackageCount == 0)
        {
            return "No storage or driver inventory data was observed.";
        }

        var stateText = assessment.OverallState switch
        {
            SystemHealthState.Critical => "Overall health is critical",
            SystemHealthState.Degraded => "Overall health is degraded",
            SystemHealthState.Unknown => "Overall health is unknown",
            SystemHealthState.Unavailable => "Overall health is unavailable",
            SystemHealthState.Incomplete => "Overall health is incomplete",
            _ => "Overall health is healthy"
        };

        var storageText = assessment.StorageDeviceCount == 0
            ? "0 storage device(s) observed"
            : $"{assessment.StorageDeviceCount:N0} storage device(s): {assessment.HealthyStorageCount:N0} healthy, {assessment.DegradedStorageCount:N0} degraded, {assessment.UnknownStorageCount:N0} unavailable";

        var driverText = assessment.DriverPackageCount == 0
            ? "0 driver package(s) observed"
            : $"{assessment.DriverPackageCount:N0} driver package(s); {assessment.DriverWarningCount:N0} signing or health alert(s)";

        var issueText = assessment.HasIssues
            ? "evidence indicates attention is warranted."
            : "no storage or driver issues were detected.";

        return $"{stateText}; {storageText}; {driverText}; {issueText}";
    }

    private static StorageStateSummary EvaluateStorageComponents(
        IEnumerable<InventoryComponent> storageComponents,
        ICollection<SystemHealthFinding> findings)
    {
        var summary = new StorageStateSummary();
        foreach (var component in storageComponents)
        {
            var state = EvaluateStorageComponent(component);
            switch (state)
            {
                case StorageState.Healthy:
                    summary.HealthyStorageCount++;
                    break;
                case StorageState.Degraded:
                    summary.DegradedStorageCount++;
                    findings.Add(new SystemHealthFinding(
                        component.IdentityKey,
                        "Storage health",
                        SystemHealthState.Degraded,
                        "Degraded",
                        0.72,
                        $"Storage '{component.DisplayName}' reported health warnings or limited capacity.",
                        component.Details.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"{pair.Key}={pair.Value}").Take(5).ToArray(),
                        component.Source,
                        component.ObservedAt,
                        [component.DisplayName],
                        "Review volume health details, free-space trends, and any recent Windows event noise for the affected storage device."));
                    break;
                case StorageState.Unknown:
                    summary.UnknownStorageCount++;
                    findings.Add(new SystemHealthFinding(
                        component.IdentityKey,
                        "Storage health",
                        SystemHealthState.Unknown,
                        "Unknown",
                        0.45,
                        $"Storage '{component.DisplayName}' reported incomplete or unavailable health evidence.",
                        component.Details.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"{pair.Key}={pair.Value}").Take(5).ToArray(),
                        component.Source,
                        component.ObservedAt,
                        [component.DisplayName],
                        "Check whether the storage provider is reporting health data and compare with recent snapshots."));
                    break;
            }

            summary.StorageDeviceCount++;
        }

        return summary;
    }

    private static StorageState EvaluateStorageComponent(InventoryComponent component)
    {
        var healthStatus = GetDetail(component, "healthStatus") ?? GetDetail(component, "HealthStatus") ?? GetDetail(component, "status");
        if (!string.IsNullOrWhiteSpace(healthStatus))
        {
            if (LooksHealthy(healthStatus))
            {
                return StorageState.Healthy;
            }

            if (LooksDegraded(healthStatus))
            {
                return StorageState.Degraded;
            }

            if (LooksUnknown(healthStatus))
            {
                return StorageState.Unknown;
            }
        }

        if (TryReadPercentage(component, "percentageUsed", out var usedPercent) &&
            usedPercent >= 80d)
        {
            return StorageState.Degraded;
        }

        if (TryReadPercentage(component, "availableSparePercent", out var sparePercent) &&
            sparePercent <= 20d)
        {
            return StorageState.Degraded;
        }

        if (TryReadThresholdValue(component, "criticalWarning", out var criticalWarning) &&
            criticalWarning > 0)
        {
            return StorageState.Degraded;
        }

        if (HasAnyStorageMetric(component))
        {
            return StorageState.Healthy;
        }

        return StorageState.Unknown;
    }

    private static bool IsSignedDriver(
        InventoryComponent component,
        out string issue,
        out SystemHealthState state,
        out double confidence)
    {
        state = SystemHealthState.Healthy;
        confidence = 0.88;
        var isSignedValue = GetDetail(component, "isSigned") ?? GetDetail(component, "IsSigned");
        if (bool.TryParse(isSignedValue, out var isSigned))
        {
            if (isSigned)
            {
                issue = $"Driver package '{component.DisplayName}' is signed and appears valid.";
                return true;
            }

            state = SystemHealthState.Degraded;
            confidence = 0.82;
            issue = $"Driver package '{component.DisplayName}' is not signed or includes a signing warning.";
            return false;
        }

        var signer = GetDetail(component, "signer") ?? GetDetail(component, "Signer");
        if (!string.IsNullOrWhiteSpace(signer))
        {
            if (LooksSigned(signer))
            {
                issue = $"Driver package '{component.DisplayName}' is associated with a trusted signer.";
                return true;
            }

            state = SystemHealthState.Degraded;
            confidence = 0.74;
            issue = $"Driver package '{component.DisplayName}' reports a signer state of '{signer}'.";
            return false;
        }

        state = SystemHealthState.Unknown;
        confidence = 0.52;
        issue = $"Driver package '{component.DisplayName}' has no signer metadata available.";
        return false;
    }

    private static SystemHealthState DetermineOverallState(
        IReadOnlyCollection<SystemHealthFinding> findings,
        int storageDeviceCount,
        int driverPackageCount)
    {
        if (findings.Any(finding => finding.State == SystemHealthState.Critical))
        {
            return SystemHealthState.Critical;
        }

        if (findings.Any(finding => finding.State == SystemHealthState.Degraded))
        {
            return SystemHealthState.Degraded;
        }

        if (findings.Any(finding => finding.State is SystemHealthState.Unknown or SystemHealthState.Unavailable or SystemHealthState.Incomplete))
        {
            return SystemHealthState.Unknown;
        }

        if (storageDeviceCount == 0 && driverPackageCount == 0)
        {
            return SystemHealthState.Incomplete;
        }

        return SystemHealthState.Healthy;
    }

    private static SystemHealthState ConvertGuardianState(GuardianFindingState state) => state switch
    {
        GuardianFindingState.Critical => SystemHealthState.Critical,
        GuardianFindingState.Warning => SystemHealthState.Degraded,
        GuardianFindingState.Unknown => SystemHealthState.Unknown,
        _ => SystemHealthState.Healthy
    };

    private static double ConvertGuardianConfidence(GuardianFindingState state) => state switch
    {
        GuardianFindingState.Critical => 0.9,
        GuardianFindingState.Warning => 0.8,
        GuardianFindingState.Unknown => 0.55,
        _ => 0.72
    };

    private static SystemHealthState ConvertRecoveryState(RecoveryReadinessState state) => state switch
    {
        RecoveryReadinessState.Gap => SystemHealthState.Critical,
        RecoveryReadinessState.Attention => SystemHealthState.Degraded,
        RecoveryReadinessState.Unknown => SystemHealthState.Unknown,
        _ => SystemHealthState.Healthy
    };

    private static string? GetDetail(InventoryComponent component, string key) =>
        component.Details.TryGetValue(key, out var value) ? value : null;

    private static bool TryReadPercentage(InventoryComponent component, string key, out double value)
    {
        value = 0;
        var detail = GetDetail(component, key);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        return double.TryParse(detail, out value);
    }

    private static bool TryReadThresholdValue(InventoryComponent component, string key, out double value)
    {
        value = 0;
        var detail = GetDetail(component, key);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        return double.TryParse(detail, out value);
    }

    private static bool HasAnyStorageMetric(InventoryComponent component)
    {
        var metrics = new[]
        {
            "healthStatus",
            "HealthStatus",
            "status",
            "criticalWarning",
            "percentageUsed",
            "availableSparePercent",
            "temperature",
            "Temperature",
            "powerOnHours",
            "PowerOnHours"
        };

        return metrics.Any(key => !string.IsNullOrWhiteSpace(GetDetail(component, key)));
    }

    private static bool LooksHealthy(string value) =>
        value.Contains("healthy", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("good", StringComparison.OrdinalIgnoreCase);

    private static bool LooksDegraded(string value) =>
        value.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("degraded", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("critical", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("bad", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("unhealthy", StringComparison.OrdinalIgnoreCase);

    private static bool LooksUnknown(string value) =>
        value.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("unavailable", StringComparison.OrdinalIgnoreCase);

    private static bool LooksSigned(string signer) =>
        signer.Contains("trusted", StringComparison.OrdinalIgnoreCase) ||
        signer.Contains("valid", StringComparison.OrdinalIgnoreCase) ||
        signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        signer.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        signer.Contains("signed", StringComparison.OrdinalIgnoreCase);

    private enum StorageState
    {
        Healthy,
        Degraded,
        Unknown
    }

    private sealed class StorageStateSummary
    {
        public int StorageDeviceCount { get; set; }
        public int HealthyStorageCount { get; set; }
        public int DegradedStorageCount { get; set; }
        public int UnknownStorageCount { get; set; }
    }
}
