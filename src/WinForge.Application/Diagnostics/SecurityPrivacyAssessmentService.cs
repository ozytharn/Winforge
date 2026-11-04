using System.Security;
using Microsoft.Win32;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Diagnostics;

public enum SecurityPrivacyAssessmentState
{
    Healthy,
    Degraded,
    Critical,
    Unknown,
    Unavailable,
    Incomplete
}

public sealed record SecurityPrivacyFinding(
    string Category,
    SecurityPrivacyAssessmentState State,
    string Severity,
    double Confidence,
    string Message,
    IReadOnlyList<string> Evidence,
    string Source,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> RelatedEntities,
    string Limitations,
    string SuggestedInvestigation);

public sealed record SecurityPrivacyAssessment(
    SecurityPrivacyAssessmentState OverallState,
    IReadOnlyList<SecurityPrivacyFinding> Findings,
    string Summary);

public sealed class SecurityPrivacyAssessmentService
{
    public static string BuildFindingDetails(SecurityPrivacyFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var evidence = finding.Evidence.Count == 0
            ? "No direct evidence lines were captured for this finding."
            : string.Join(Environment.NewLine, finding.Evidence.Select((line, index) => $"  {index + 1}. {line}"));

        var related = finding.RelatedEntities.Count == 0
            ? "No related entities were recorded."
            : string.Join(", ", finding.RelatedEntities);

        return string.Join(
            Environment.NewLine,
            [
                $"Category: {finding.Category}",
                $"State: {finding.State}",
                $"Severity: {finding.Severity}",
                $"Confidence: {finding.Confidence:P0}",
                $"Observed: {finding.ObservedAt:yyyy-MM-dd HH:mm} UTC",
                $"Source: {finding.Source}",
                $"Message: {finding.Message}",
                "Evidence:",
                evidence,
                $"Limitations: {finding.Limitations}",
                $"Related entities: {related}",
                $"Suggested investigation: {finding.SuggestedInvestigation}"
            ]);
    }

    public SecurityPrivacyAssessment Analyze(IEnumerable<InventoryComponent> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var components = inventory.ToArray();
        var findings = new List<SecurityPrivacyFinding>();

        EvaluateDefender(components, findings);
        EvaluateFirewall(components, findings);
        EvaluateWindowsUpdate(components, findings);
        EvaluateBitLocker(components, findings);
        EvaluateUacAndSecureBoot(components, findings);
        EvaluateStartupPersistence(components, findings);
        EvaluatePrivacyConfiguration(components, findings);
        EvaluateSecurityProviderMetadata(components, findings);

        var overallState = DetermineOverallState(findings);
        return new SecurityPrivacyAssessment(
            overallState,
            findings.DistinctBy(finding => (finding.Category, finding.Message, finding.Source)).ToArray(),
            BuildSummary(findings, overallState));
    }

    public string BuildSummary(SecurityPrivacyAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return assessment.Summary;
    }

    private static void EvaluateDefender(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var defender = components
            .Where(component => component.Type == InventoryComponentType.Service)
            .FirstOrDefault(component => IsServiceName(component, "WinDefend", "SecurityHealthService"));

        if (defender is null)
        {
            AddFinding(
                findings,
                "Defender",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.55,
                "Microsoft Defender status was not observed in the current scan.",
                ["No Defender service record was returned by the current inventory."],
                "Windows service inventory",
                DateTimeOffset.UtcNow,
                ["WinDefend", "Microsoft Defender"],
                "This does not prove Defender is disabled; the service was simply not observed.",
                "Open the Windows Security app and confirm Defender state from a supported, current provider readout.");
            return;
        }

        var stateText = GetServiceState(defender);
        var state = ToSecurityState(stateText, isDefender: true);
        AddFinding(
            findings,
            "Defender",
            state,
            state == SecurityPrivacyAssessmentState.Healthy ? "Healthy" : state == SecurityPrivacyAssessmentState.Degraded ? "Warning" : "Unknown",
            state == SecurityPrivacyAssessmentState.Healthy ? 0.92 : 0.76,
            state switch
            {
                SecurityPrivacyAssessmentState.Healthy => "Microsoft Defender is observed as running and available.",
                SecurityPrivacyAssessmentState.Degraded => $"Microsoft Defender appears to be unavailable or disabled ({stateText}).",
                _ => "Microsoft Defender status could not be interpreted from the current service metadata."
            },
            GatherEvidence(defender),
            defender.Source,
            defender.ObservedAt,
            ["WinDefend", "Microsoft Defender"],
            "A running service does not confirm protection quality or real-time scanning status.",
            "Review the current Windows Security dashboard and confirm whether Defender is actively protecting the device.");
    }

    private static void EvaluateFirewall(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var firewall = components
            .Where(component => component.Type == InventoryComponentType.Service)
            .FirstOrDefault(component => IsServiceName(component, "MpsSvc", "SharedAccess", "Firewall"));

        if (firewall is null)
        {
            AddFinding(
                findings,
                "Firewall",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.52,
                "Windows Firewall status was not observed in this scan.",
                ["No firewall service record was returned by the current inventory."],
                "Windows service inventory",
                DateTimeOffset.UtcNow,
                ["MpsSvc", "Windows Firewall"],
                "This is not evidence that the firewall is disabled; the provider simply did not report it.",
                "Open Windows Security > Firewall & network protection to verify the active profile state.");
            return;
        }

        var stateText = GetServiceState(firewall);
        var state = ToSecurityState(stateText, isDefender: false);
        AddFinding(
            findings,
            "Firewall",
            state,
            state == SecurityPrivacyAssessmentState.Healthy ? "Healthy" : state == SecurityPrivacyAssessmentState.Degraded ? "Warning" : "Unknown",
            state == SecurityPrivacyAssessmentState.Healthy ? 0.9 : 0.75,
            state switch
            {
                SecurityPrivacyAssessmentState.Healthy => "The Windows Firewall service is reported as running.",
                SecurityPrivacyAssessmentState.Degraded => $"The Windows Firewall service appears unavailable or disabled ({stateText}).",
                _ => "The firewall service state could not be interpreted from available metadata."
            },
            GatherEvidence(firewall),
            firewall.Source,
            firewall.ObservedAt,
            ["MpsSvc", "Windows Firewall"],
            "A running firewall service does not confirm profile policy completeness or remote access restrictions.",
            "Confirm the effective firewall profile and whether the current configuration is expected for this device.");
    }

    private static void EvaluateWindowsUpdate(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var updateComponents = components.Where(component => component.Type == InventoryComponentType.WindowsUpdate).ToArray();
        if (updateComponents.Length == 0)
        {
            AddFinding(
                findings,
                "Windows Update",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.5,
                "Windows Update evidence was not collected in this scan.",
                ["No Windows update inventory was returned."],
                "Windows update inventory",
                DateTimeOffset.UtcNow,
                ["Windows Update"],
                "Inventory absence is not proof that updates are not installed or pending.",
                "Check Windows Update history and any pending or hidden quality updates in Settings.");
            return;
        }

        AddFinding(
            findings,
            "Windows Update",
            SecurityPrivacyAssessmentState.Unknown,
            "Informational",
            0.68,
            $"{updateComponents.Length:N0} Windows update entries were observed. Patch freshness, pending status, and update health remain unverified.",
            updateComponents.Take(5).SelectMany(GatherEvidence).ToArray(),
            "Windows update inventory",
            updateComponents.Max(component => component.ObservedAt),
            ["Windows Update"],
            "This is not an installed/healthy patch assessment; it only confirms update records were observed.",
            "Review Windows Update history and current patch status rather than inferring health from update presence alone.");
    }

    private static void EvaluateBitLocker(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var volumes = components.Where(component => component.Type == InventoryComponentType.BitLockerVolume).ToArray();
        if (volumes.Length == 0)
        {
            AddFinding(
                findings,
                "BitLocker",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.53,
                "BitLocker protection status was not observed in this scan.",
                ["No BitLocker volume components were found."],
                "BitLocker inventory",
                DateTimeOffset.UtcNow,
                ["BitLocker"],
                "No evidence of BitLocker state is not proof of a missing or disabled protection feature.",
                "Verify BitLocker status for the system volumes in the Windows Security or Control Panel UI.");
            return;
        }

        var protectedCount = 0;
        var unprotectedCount = 0;
        var evidence = new List<string>();
        foreach (var volume in volumes)
        {
            var protectionStatus = GetDetail(volume, "protectionStatus") ?? GetDetail(volume, "status") ?? "Unknown";
            evidence.Add($"{volume.DisplayName}: {protectionStatus}");
            if (ContainsAny(protectionStatus, "protected", "on", "encrypted", "enabled"))
            {
                protectedCount++;
            }
            else if (ContainsAny(protectionStatus, "off", "not protected", "disabled", "paused"))
            {
                unprotectedCount++;
            }
        }

        var state = unprotectedCount > 0 ? SecurityPrivacyAssessmentState.Degraded : SecurityPrivacyAssessmentState.Healthy;
        AddFinding(
            findings,
            "BitLocker",
            state,
            state == SecurityPrivacyAssessmentState.Healthy ? "Healthy" : "Warning",
            state == SecurityPrivacyAssessmentState.Healthy ? 0.88 : 0.8,
            state == SecurityPrivacyAssessmentState.Healthy
                ? $"{protectedCount} BitLocker-protected volume(s) were observed. WinForge does not read recovery keys or key protectors."
                : $"{unprotectedCount} BitLocker volume(s) appear unprotected or disabled. Review the volume protection state before relying on encryption enforcement.",
            evidence,
            volumes[0].Source,
            volumes.Max(volume => volume.ObservedAt),
            volumes.Select(volume => volume.DisplayName).Take(5).ToArray(),
            "BitLocker status can change after a query. WinForge records the status only and does not alter the device or recovery configuration.",
            "Check the protection state of the system volume and confirm the configuration with the Windows Security panel.");
    }

    private static void EvaluateUacAndSecureBoot(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var system = components
            .Where(component => component.Type == InventoryComponentType.WindowsSystemInfo || component.Type == InventoryComponentType.OperatingSystem)
            .ToArray();

        if (system.Length == 0)
        {
            AddFinding(
                findings,
                "System security baseline",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.5,
                "No Windows system baseline metadata was observed for UAC or Secure Boot status.",
                ["The current inventory contains no Windows system metadata for security baseline checks."],
                "Windows system inventory",
                DateTimeOffset.UtcNow,
                ["UAC", "Secure Boot"],
                "Unavailable system metadata does not imply a security issue.",
                "Review the System Information and Windows Security pages to confirm the current UAC and Secure Boot state.");
            return;
        }

        var uacValues = system.SelectMany(component => ComponentValues(component, "uacEnabled", "UACEnabled", "uac", "UserAccountControl")).ToArray();
        var secureBootValues = system.SelectMany(component => ComponentValues(component, "secureBootEnabled", "SecureBootEnabled", "secureBootState", "SecureBootState")).ToArray();

        if (uacValues.Length > 0)
        {
            var uacOn = uacValues.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            var state = uacOn ? SecurityPrivacyAssessmentState.Healthy : SecurityPrivacyAssessmentState.Degraded;
            AddFinding(
                findings,
                "UAC",
                state,
                state == SecurityPrivacyAssessmentState.Healthy ? "Healthy" : "Warning",
                state == SecurityPrivacyAssessmentState.Healthy ? 0.89 : 0.8,
                state == SecurityPrivacyAssessmentState.Healthy
                    ? "UAC appears enabled in the current Windows system metadata."
                    : "The observed UAC state suggests it is disabled or not enforced.",
                uacValues.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(),
                "Windows system inventory",
                system.Max(component => component.ObservedAt),
                ["UAC"],
                "UAC and Secure Boot are observed as metadata and are not automatically modified or enforced by WinForge.",
                "Confirm the current UAC configuration in the User Account Control settings dialog.");
        }

        if (secureBootValues.Length > 0)
        {
            var secureBootOn = secureBootValues.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            var state = secureBootOn ? SecurityPrivacyAssessmentState.Healthy : SecurityPrivacyAssessmentState.Degraded;
            AddFinding(
                findings,
                "Secure Boot",
                state,
                state == SecurityPrivacyAssessmentState.Healthy ? "Healthy" : "Warning",
                state == SecurityPrivacyAssessmentState.Healthy ? 0.9 : 0.78,
                state == SecurityPrivacyAssessmentState.Healthy
                    ? "Secure Boot is observed as enabled."
                    : "The observed Secure Boot state indicates it is not enabled or is unavailable for enforcement.",
                secureBootValues.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(),
                "Windows system inventory",
                system.Max(component => component.ObservedAt),
                ["Secure Boot"],
                "Secure Boot status can vary by firmware and hardware configuration; WinForge does not alter firmware settings.",
                "Review firmware settings and the Windows Security page for the current boot-protection state.");
        }
    }

    private static void EvaluateStartupPersistence(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var startupEntries = components.Count(component => component.Type == InventoryComponentType.StartupEntry);
        var scheduledTasks = components.Count(component => component.Type == InventoryComponentType.ScheduledTask);
        if (startupEntries == 0 && scheduledTasks == 0)
        {
            AddFinding(
                findings,
                "Startup and persistence",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.5,
                "No startup or scheduled-task persistence records were observed in the current scan.",
                ["The current scan reported no startup or scheduled task entries."],
                "Startup and task inventory",
                DateTimeOffset.UtcNow,
                ["Startup entries", "Scheduled tasks"],
                "No observed entries does not prove there are none; it may reflect provider limitations or the selected scan scope.",
                "Open the Startup tab and Task Scheduler to review any software configured to persist at login or boot.");
            return;
        }

        AddFinding(
            findings,
            "Startup and persistence",
            SecurityPrivacyAssessmentState.Healthy,
            "Informational",
            0.7,
            $"{startupEntries:N0} startup entry(s) and {scheduledTasks:N0} scheduled task(s) were observed. WinForge reports them as persistence observations only and does not determine whether they are safe or malicious.",
            [
                $"Startup entries: {startupEntries:N0}",
                $"Scheduled tasks: {scheduledTasks:N0}"
            ],
            "Startup and task inventory",
            DateTimeOffset.UtcNow,
            ["Startup entries", "Scheduled tasks"],
            "Persistence metadata is evidence of configuration, not evidence of malicious intent.",
            "Review each startup or scheduled task for purpose, signer, and expected behavior before acting on it.");
    }

    private static void EvaluatePrivacyConfiguration(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var observedSettings = new List<(string Name, string Value)>();
        foreach (var component in components)
        {
            foreach (var pair in component.Details)
            {
                if (string.IsNullOrWhiteSpace(pair.Value) ||
                    !ContainsAny(pair.Key, "privacy", "advertising", "diagnostic", "location", "telemetry", "personalization"))
                {
                    continue;
                }

                observedSettings.Add(($"{component.DisplayName}: {pair.Key}", pair.Value));
            }
        }

        if (observedSettings.Count == 0 && OperatingSystem.IsWindows())
        {
            observedSettings.AddRange(ReadWindowsPrivacySettings());
        }

        if (observedSettings.Count == 0)
        {
            AddFinding(
                findings,
                "Privacy",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.52,
                "No privacy-setting observations were captured in the current scan.",
                ["The current inventory reported no privacy-related configuration values."],
                "Windows privacy registry and system metadata",
                DateTimeOffset.UtcNow,
                ["Privacy settings", "Windows privacy controls"],
                "Missing privacy metadata does not mean the setting is absent or unsafe; it only means the current scan did not observe it.",
                "Review the Windows Privacy & security pages for the user-configured privacy settings that are relevant to this device.");
            return;
        }

        var enabledValues = observedSettings
            .Where(setting => IsEnabledFlag(setting.Value))
            .Select(setting => setting.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var disabledValues = observedSettings
            .Where(setting => IsDisabledFlag(setting.Value))
            .Select(setting => setting.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var state = enabledValues.Length > 0 && disabledValues.Length == 0
            ? SecurityPrivacyAssessmentState.Degraded
            : disabledValues.Length > 0
                ? SecurityPrivacyAssessmentState.Healthy
                : SecurityPrivacyAssessmentState.Unknown;

        var severity = state == SecurityPrivacyAssessmentState.Degraded ? "Warning" : state == SecurityPrivacyAssessmentState.Healthy ? "Healthy" : "Unknown";
        var message = state switch
        {
            SecurityPrivacyAssessmentState.Degraded => "Privacy-affecting settings are enabled in the current observed configuration; review whether this matches user intent.",
            SecurityPrivacyAssessmentState.Healthy => "Observed privacy-related settings are disabled or restricted in the current visible configuration.",
            _ => "Privacy configuration is present but its current state could not be interpreted confidently from the available evidence."
        };

        AddFinding(
            findings,
            "Privacy",
            state,
            severity,
            state == SecurityPrivacyAssessmentState.Degraded ? 0.76 : state == SecurityPrivacyAssessmentState.Healthy ? 0.86 : 0.58,
            message,
            observedSettings.Take(6).Select(setting => $"{setting.Name}={setting.Value}").ToArray(),
            "Windows privacy registry and system metadata",
            observedSettings.Select(setting => setting.Value).Any() ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow,
            ["Privacy settings", "Data collection controls"],
            "These are direct observations of specific privacy flags and do not prove an unsafe configuration without user context.",
            "Compare the observed values with the Windows Privacy pages and confirm whether any enabled settings are intentional and expected for this device.");
    }

    private static void EvaluateSecurityProviderMetadata(IReadOnlyList<InventoryComponent> components, ICollection<SecurityPrivacyFinding> findings)
    {
        var systemComponents = components
            .Where(component => component.Type == InventoryComponentType.WindowsSystemInfo || component.Type == InventoryComponentType.OperatingSystem)
            .ToArray();
        var providerMetadata = systemComponents
            .SelectMany(component => component.Details)
            .Where(pair => pair.Key.Contains("security", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("provider", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (providerMetadata.Length == 0)
        {
            AddFinding(
                findings,
                "Security provider metadata",
                SecurityPrivacyAssessmentState.Unknown,
                "Unknown",
                0.48,
                "No explicit security provider metadata was retrieved from the current system inventory.",
                ["No provider-specific security metadata is currently available."],
                "Windows system inventory",
                DateTimeOffset.UtcNow,
                ["Security Provider", "Windows Security"],
                "Provider metadata absence is not proof of missing protection, only that the current provider did not expose it.",
                "Review the Windows Security and device security pages to compare the active security provider and baseline state.");
        }
    }

    private static IReadOnlyList<(string Name, string Value)> ReadWindowsPrivacySettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var results = new List<(string Name, string Value)>();

        try
        {
            using var advertisingInfo = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo");
            if (advertisingInfo is not null)
            {
                var enabled = advertisingInfo.GetValue("Enabled")?.ToString();
                if (!string.IsNullOrWhiteSpace(enabled))
                {
                    results.Add(("AdvertisingInfo: Enabled", enabled));
                }
            }

            using var personalization = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\InputPersonalization");
            if (personalization is not null)
            {
                foreach (var valueName in new[] { "RestrictImplicitTextCollection", "RestrictImplicitInkCollection" })
                {
                    var value = personalization.GetValue(valueName)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        results.Add(($"InputPersonalization: {valueName}", value));
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        return results;
    }

    private static bool IsEnabledFlag(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static bool IsDisabledFlag(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase));

    private static SecurityPrivacyAssessmentState DetermineOverallState(IReadOnlyCollection<SecurityPrivacyFinding> findings)
    {
        if (findings.Count == 0)
        {
            return SecurityPrivacyAssessmentState.Incomplete;
        }

        var required = findings
            .Where(finding => finding.Category is not "Windows Update" and not "Security provider metadata")
            .ToArray();

        if (required.Length == 0)
        {
            return findings.Any(finding => finding.State == SecurityPrivacyAssessmentState.Degraded)
                ? SecurityPrivacyAssessmentState.Degraded
                : findings.Any(finding => finding.State == SecurityPrivacyAssessmentState.Critical)
                    ? SecurityPrivacyAssessmentState.Critical
                    : findings.Any(finding => finding.State == SecurityPrivacyAssessmentState.Unknown)
                        ? SecurityPrivacyAssessmentState.Unknown
                        : SecurityPrivacyAssessmentState.Healthy;
        }

        if (required.Any(finding => finding.State == SecurityPrivacyAssessmentState.Critical))
        {
            return SecurityPrivacyAssessmentState.Critical;
        }

        if (required.Any(finding => finding.State == SecurityPrivacyAssessmentState.Degraded))
        {
            return SecurityPrivacyAssessmentState.Degraded;
        }

        if (required.Any(finding => finding.State == SecurityPrivacyAssessmentState.Unknown))
        {
            return SecurityPrivacyAssessmentState.Unknown;
        }

        if (required.Any(finding => finding.State == SecurityPrivacyAssessmentState.Unavailable))
        {
            return SecurityPrivacyAssessmentState.Unavailable;
        }

        if (required.All(finding => finding.State == SecurityPrivacyAssessmentState.Healthy))
        {
            return SecurityPrivacyAssessmentState.Healthy;
        }

        return SecurityPrivacyAssessmentState.Incomplete;
    }

    private static string BuildSummary(IReadOnlyCollection<SecurityPrivacyFinding> findings, SecurityPrivacyAssessmentState overallState)
    {
        var healthy = findings.Count(f => f.State == SecurityPrivacyAssessmentState.Healthy);
        var degraded = findings.Count(f => f.State == SecurityPrivacyAssessmentState.Degraded);
        var unknown = findings.Count(f => f.State == SecurityPrivacyAssessmentState.Unknown);

        var stateText = overallState switch
        {
            SecurityPrivacyAssessmentState.Critical => "Overall security posture is critical",
            SecurityPrivacyAssessmentState.Degraded => "Overall security posture is degraded",
            SecurityPrivacyAssessmentState.Healthy => "Overall security posture is healthy",
            SecurityPrivacyAssessmentState.Unknown => "Overall security posture is unknown",
            SecurityPrivacyAssessmentState.Unavailable => "Overall security posture is unavailable",
            _ => "Overall security posture is incomplete"
        };

        return $"{stateText}; {healthy} healthy categories, {degraded} warning(s), {unknown} unknown/unavailable category(s). No security settings are changed by WinForge.";
    }

    private static bool IsServiceName(InventoryComponent component, params string[] serviceNames) =>
        component.Details.TryGetValue("Name", out var name) &&
        !string.IsNullOrWhiteSpace(name) &&
        serviceNames.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase) ||
            (name.Contains(candidate, StringComparison.OrdinalIgnoreCase) && candidate.Length > 3));

    private static string GetServiceState(InventoryComponent component)
    {
        var candidate = GetDetail(component, "State") ?? GetDetail(component, "Status") ?? GetDetail(component, "StartMode");
        return string.IsNullOrWhiteSpace(candidate) ? "Unknown" : candidate;
    }

    private static SecurityPrivacyAssessmentState ToSecurityState(string state, bool isDefender)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return SecurityPrivacyAssessmentState.Unknown;
        }

        if (ContainsAny(state, "running", "started", "auto", "enabled", "active"))
        {
            return SecurityPrivacyAssessmentState.Healthy;
        }

        if (ContainsAny(state, "stopped", "disabled", "paused", "not available", "not running", "blocked"))
        {
            return isDefender ? SecurityPrivacyAssessmentState.Degraded : SecurityPrivacyAssessmentState.Degraded;
        }

        return SecurityPrivacyAssessmentState.Unknown;
    }

    private static void AddFinding(
        ICollection<SecurityPrivacyFinding> findings,
        string category,
        SecurityPrivacyAssessmentState state,
        string severity,
        double confidence,
        string message,
        IEnumerable<string> evidence,
        string source,
        DateTimeOffset observedAt,
        IReadOnlyList<string> relatedEntities,
        string limitations,
        string suggestedInvestigation)
    {
        var duplicate = findings.FirstOrDefault(existing =>
            existing.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
            existing.Message.Equals(message, StringComparison.Ordinal));
        if (duplicate is not null)
        {
            return;
        }

        findings.Add(new SecurityPrivacyFinding(
            category,
            state,
            severity,
            confidence,
            message,
            evidence.ToArray(),
            source,
            observedAt,
            relatedEntities,
            limitations,
            suggestedInvestigation));
    }

    private static IEnumerable<string> GatherEvidence(InventoryComponent component)
    {
        var details = component.Details
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .Take(6)
            .ToArray();

        if (details.Length > 0)
        {
            return details;
        }

        return [$"{component.Type}: {component.DisplayName}"];
    }

    private static IEnumerable<string> ComponentValues(InventoryComponent component, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (GetDetail(component, key) is { } value)
            {
                yield return value;
            }
        }
    }

    private static string? GetDetail(InventoryComponent component, string key) =>
        component.Details.TryGetValue(key, out var value) ? value : null;

    private static bool ContainsAny(string? value, params string[] tokens) =>
        !string.IsNullOrWhiteSpace(value) && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}
