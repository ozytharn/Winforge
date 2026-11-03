using Microsoft.Extensions.Logging;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Inventory;

public sealed class PassportScanService : IPassportScanService
{
    private static readonly IReadOnlyDictionary<InventoryScanGroup, string> ScanScopes =
        new Dictionary<InventoryScanGroup, string>
        {
            [InventoryScanGroup.All] = "All PC Passport inventory",
            [InventoryScanGroup.Hardware] = "Hardware and driver inventory",
            [InventoryScanGroup.Software] = "Software, services, processes, startup, and scheduled-task inventory",
            [InventoryScanGroup.System] = "Windows system information, updates, features, and configuration",
            [InventoryScanGroup.Recovery] = "Recovery discovery"
        };
    private readonly IInventoryCollector[] _collectors;
    private readonly IInventoryRepository _repository;
    private readonly ILogger<PassportScanService> _logger;

    public PassportScanService(
        IEnumerable<IInventoryCollector> collectors,
        IInventoryRepository repository,
        ILogger<PassportScanService> logger)
    {
        _collectors = collectors.OrderBy(collector => collector.CollectorId, StringComparer.Ordinal).ToArray();
        _repository = repository;
        _logger = logger;

        if (_collectors.Length == 0)
        {
            throw new ArgumentException("At least one inventory collector is required.", nameof(collectors));
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _repository.InitializeAsync(cancellationToken);

    public Task<PassportSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        _repository.GetSnapshotAsync(cancellationToken);

    public async Task<ScanRecord> RunScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) =>
        await RunScanAsync(InventoryScanGroup.All, progress, cancellationToken).ConfigureAwait(false);

    public async Task<ScanRecord> RunScanAsync(
        InventoryScanGroup group,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ScanScopes.TryGetValue(group, out var scanScope))
        {
            throw new ArgumentOutOfRangeException(nameof(group), group, "The inventory scan group is not supported.");
        }

        var selectedCollectors = _collectors
            .Where(collector => group == InventoryScanGroup.All || collector.ScanGroup == group)
            .ToArray();
        if (selectedCollectors.Length == 0)
        {
            throw new InvalidOperationException($"No inventory collectors are registered for the {group} scan.");
        }

        var scanId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();
        var attemptedCollectors = 0;
        var cancelled = false;

        await _repository.StartScanAsync(
            scanId,
            startedAt,
            scanScope,
            selectedCollectors.Length,
            cancellationToken);

        for (var index = 0; index < selectedCollectors.Length; index++)
        {
            var collector = selectedCollectors[index];
            progress?.Report(new ScanProgress(
                $"Scanning {collector.CollectorId}…",
                attemptedCollectors,
                selectedCollectors.Length));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptedCollectors++;
                var result = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
                components.AddRange(result.Components);
                issues.AddRange(result.Issues);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Inventory collector {CollectorId} failed.", collector.CollectorId);
                issues.Add(new CollectorIssue(
                    collector.CollectorId,
                    "collector_failed",
                    $"Collection failed ({exception.GetType().Name}). See the application log for details."));
            }

            progress?.Report(new ScanProgress(
                $"Scanned {collector.CollectorId}",
                attemptedCollectors,
                selectedCollectors.Length));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        if (cancelled)
        {
            issues.Add(new CollectorIssue(
                "scan",
                "cancelled",
                "The scan was cancelled. Results from completed collectors were retained."));
        }

        var status = cancelled
            ? InventoryScanStatus.Cancelled
            : issues.Count == 0
                ? InventoryScanStatus.Completed
                : components.Count == 0
                    ? InventoryScanStatus.Failed
                    : InventoryScanStatus.Partial;

        var reconciledComponents = ReconcileComponents(components);
        var scan = new ScanRecord(
            scanId,
            startedAt,
            DateTimeOffset.UtcNow,
            scanScope,
            status,
            attemptedCollectors,
            selectedCollectors.Length,
            reconciledComponents,
            issues);

        await _repository.CompleteScanAsync(scan, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation(
            "Passport scan {ScanId} finished with status {Status}, {ComponentCount} components, and {IssueCount} issues.",
            scanId,
            status,
            reconciledComponents.Count,
            issues.Count);

        return scan;
    }

    private static IReadOnlyList<InventoryComponent> ReconcileComponents(
        IReadOnlyList<InventoryComponent> components)
    {
        var reconciled = new Dictionary<(InventoryComponentType Type, string Identity), InventoryComponent>();
        foreach (var component in AssociateProcessesWithRegisteredApplications(components))
        {
            var identity = component.IdentityKey.Trim().ToUpperInvariant();
            var key = (component.Type, identity);
            if (!reconciled.TryGetValue(key, out var existing))
            {
                reconciled.Add(key, component);
                continue;
            }

            var existingIsAuthoritative = IsAuthoritative(existing.Source);
            var incomingIsAuthoritative = IsAuthoritative(component.Source);
            var preferred = incomingIsAuthoritative && !existingIsAuthoritative ? component : existing;
            var fallback = ReferenceEquals(preferred, component) ? existing : component;
            var details = fallback.Details.ToDictionary(
                detail => detail.Key,
                detail => detail.Value,
                StringComparer.Ordinal);
            foreach (var detail in preferred.Details)
            {
                if (detail.Value is not null ||
                    !details.TryGetValue(detail.Key, out var supplementalValue) ||
                    supplementalValue is null)
                {
                    details[detail.Key] = detail.Value;
                }
            }

            var availability = fallback.FieldAvailability.ToDictionary(
                field => field.Key,
                field => field.Value,
                StringComparer.Ordinal);
            foreach (var field in preferred.FieldAvailability)
            {
                if (!preferred.Details.TryGetValue(field.Key, out var authoritativeValue) ||
                    authoritativeValue is not null ||
                    !fallback.Details.TryGetValue(field.Key, out var supplementalValue) ||
                    supplementalValue is null)
                {
                    availability[field.Key] = field.Value;
                }
            }
            foreach (var detail in details)
            {
                if (!availability.ContainsKey(detail.Key))
                {
                    availability[detail.Key] = detail.Value is null
                        ? InventoryFieldAvailability.Unknown
                        : InventoryFieldAvailability.Available;
                }
            }

            if (component.Type == InventoryComponentType.DriverPackage)
            {
                var associatedDeviceIds = new[] { existing, component }
                    .SelectMany(item => item.Details.TryGetValue("associatedDeviceIds", out var value)
                        ? (value ?? string.Empty).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                        : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                details["associatedDeviceIds"] = associatedDeviceIds.Length == 0
                    ? null
                    : string.Join(Environment.NewLine, associatedDeviceIds);
                availability["associatedDeviceIds"] = associatedDeviceIds.Length == 0
                    ? InventoryFieldAvailability.Missing
                    : InventoryFieldAvailability.Available;
            }

            reconciled[key] = preferred with
            {
                DisplayName = IsUsableName(preferred.DisplayName) ? preferred.DisplayName : fallback.DisplayName,
                Source = string.Join(
                    ", ",
                    new[] { existing.Source, component.Source }
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(source => source, StringComparer.Ordinal)),
                Details = details,
                FieldAvailability = availability,
                ObservedAt = existing.ObservedAt >= component.ObservedAt
                    ? existing.ObservedAt
                    : component.ObservedAt
            };
        }

        return reconciled.Values
            .OrderBy(component => component.Type)
            .ThenBy(component => component.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<InventoryComponent> AssociateProcessesWithRegisteredApplications(
        IReadOnlyList<InventoryComponent> components)
    {
        var applicationInstallations = components
            .Where(component => component.Type == InventoryComponentType.InstalledApplication)
            .Select(component =>
            {
                var location = component.Details.GetValueOrDefault("installLocation");
                return (Component: component, Location: NormalizeInstallLocation(location));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Location))
            .OrderByDescending(item => item.Location!.Length)
            .ToArray();

        if (applicationInstallations.Length == 0)
        {
            return components;
        }

        return components.Select(component =>
        {
            if (component.Type != InventoryComponentType.Process ||
                !component.Details.TryGetValue("executablePath", out var executablePath) ||
                string.IsNullOrWhiteSpace(executablePath))
            {
                return component;
            }

            var application = applicationInstallations.FirstOrDefault(item =>
                IsWithinInstallLocation(executablePath, item.Location!));
            if (application.Component is null)
            {
                return component;
            }

            var details = component.Details.ToDictionary(
                detail => detail.Key,
                detail => detail.Value,
                StringComparer.Ordinal);
            details["associatedApplicationIdentity"] = application.Component.IdentityKey;
            details["associatedApplicationName"] = application.Component.DisplayName;
            details["associationEvidence"] = "Executable path is under the registered installation location.";
            var availability = component.FieldAvailability.ToDictionary(
                field => field.Key,
                field => field.Value,
                StringComparer.Ordinal);
            availability["associatedApplicationIdentity"] = InventoryFieldAvailability.Available;
            availability["associatedApplicationName"] = InventoryFieldAvailability.Available;
            availability["associationEvidence"] = InventoryFieldAvailability.Available;
            return component with
            {
                Details = details,
                FieldAvailability = availability
            };
        }).ToArray();
    }

    private static string? NormalizeInstallLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(location.Trim()).TrimEnd('\\', '/');
        return expanded.Length == 0 ? null : expanded;
    }

    private static bool IsWithinInstallLocation(string executablePath, string installLocation) =>
        executablePath.Equals(installLocation, StringComparison.OrdinalIgnoreCase) ||
        executablePath.StartsWith(installLocation + "\\", StringComparison.OrdinalIgnoreCase) ||
        executablePath.StartsWith(installLocation + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthoritative(string source) =>
        source.Contains("SetupAPI", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Configuration Manager", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsableName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.EndsWith(" unavailable", StringComparison.OrdinalIgnoreCase);
}
