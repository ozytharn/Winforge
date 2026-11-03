using System.Globalization;
using System.Text.RegularExpressions;
using WinForge.Domain.DigitalTwin;
using WinForge.Domain.Inventory;

namespace WinForge.Application.DigitalTwin;

public sealed class DigitalTwinBuilder
{
    private static readonly string[] MetadataFields =
    [
        "status", "state", "startupType", "version", "publisher", "manufacturer", "class",
        "processId", "parentProcessId", "processArchitecture", "startTimeUtc", "executablePath",
        "associatedApplicationIdentity", "associationEvidence", "serviceName", "dependencies",
        "instanceId", "deviceInstanceId", "associatedDeviceIds", "driverInfPath", "installLocation",
        "installScope", "taskPath", "actions", "commandTarget", "startupScope", "sourceKind",
        "freeBytes", "availableFreeBytes", "totalBytes", "recoveryStatus", "availability",
        "collectionTimestampUtc", "confidence", "warnings", "limitations"
    ];

    private static readonly Regex ExecutablePathPattern = new(
        @"(?<path>(?:[A-Za-z]:\\|\\\\)[^|,]*?\.exe)(?=,| \||$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public DigitalTwinSnapshot Build(PassportSnapshot inventory, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var entities = new Dictionary<string, DigitalTwinEntity>(StringComparer.OrdinalIgnoreCase);
        var components = inventory.Components;

        foreach (var component in components)
        {
            var type = MapEntityType(component.Type);
            if (type is null || string.IsNullOrWhiteSpace(component.IdentityKey))
            {
                continue;
            }

            var metadata = MetadataFields
                .Where(component.Details.ContainsKey)
                .ToDictionary(
                    key => key,
                    key => component.Details.GetValueOrDefault(key),
                    StringComparer.Ordinal);
            var availability = ResolveAvailability(component);
            var entity = new DigitalTwinEntity(
                type.Value,
                component.IdentityKey,
                component.DisplayName,
                component.Source,
                component.ObservedAt,
                availability,
                ConfidenceFor(component),
                metadata,
                component.Type);
            entities.TryAdd(entity.Id, entity);
        }

        var relationships = new Dictionary<string, DigitalTwinRelationship>(StringComparer.OrdinalIgnoreCase);
        var processByPid = BuildProcessIndex(components);
        var deviceByInstance = BuildDeviceIndex(components);
        var servicesByName = BuildServiceIndex(components);

        foreach (var component in components)
        {
            var sourceType = MapEntityType(component.Type);
            if (sourceType is null)
            {
                continue;
            }

            var sourceIdentity = component.IdentityKey;
            if (component.Type == InventoryComponentType.Process)
            {
                AddExecutableRelationship(
                    component,
                    DigitalTwinEntityType.Process,
                    DigitalTwinRelationshipType.Runs,
                    entities,
                    relationships);

                if (TryReadInteger(component, "parentProcessId", out var parentPid) &&
                    processByPid.TryGetValue(parentPid, out var parent))
                {
                    AddRelationship(
                        relationships,
                        new DigitalTwinRelationship(
                            DigitalTwinEntityType.Process,
                            sourceIdentity,
                            DigitalTwinEntityType.Process,
                            parent.IdentityKey,
                            DigitalTwinRelationshipType.DependsOn,
                            component.Source,
                            $"Reported parent process ID {parentPid} matched a process in the same inventory snapshot.",
                            0.88m,
                            true,
                            component.ObservedAt));
                }

                if (component.Details.GetValueOrDefault("associatedApplicationIdentity") is { Length: > 0 } appIdentity &&
                    entities.ContainsKey(DigitalTwinIdentity.CreateId(DigitalTwinEntityType.Application, appIdentity)))
                {
                    AddRelationship(
                        relationships,
                        new DigitalTwinRelationship(
                            DigitalTwinEntityType.Process,
                            sourceIdentity,
                            DigitalTwinEntityType.Application,
                            appIdentity,
                            DigitalTwinRelationshipType.AssociatedWith,
                            component.Details.GetValueOrDefault("associationEvidence") ?? component.Source,
                            "The inventory scan associated this process using its executable path beneath the registered application installation location.",
                            0.92m,
                            true,
                            component.ObservedAt));
                }
            }
            else if (component.Type is InventoryComponentType.Service or InventoryComponentType.RecoveryService)
            {
                AddExecutableRelationship(
                    component,
                    DigitalTwinEntityType.Service,
                    DigitalTwinRelationshipType.Runs,
                    entities,
                    relationships);
                AddApplicationServiceRelationships(
                    component,
                    components,
                    relationships);

                foreach (var dependencyName in SplitList(GetDetail(component, "Dependencies", "dependencies")))
                {
                    if (servicesByName.TryGetValue(dependencyName, out var dependency))
                    {
                        AddRelationship(
                            relationships,
                            new DigitalTwinRelationship(
                                DigitalTwinEntityType.Service,
                                sourceIdentity,
                                DigitalTwinEntityType.Service,
                                dependency.IdentityKey,
                                DigitalTwinRelationshipType.DependsOn,
                                component.Source,
                                $"Windows service metadata listed dependency '{dependencyName}'.",
                                0.95m,
                                true,
                                component.ObservedAt));
                    }
                }
            }
            else if (component.Type == InventoryComponentType.StartupEntry)
            {
                AddExecutableRelationship(
                    component,
                    DigitalTwinEntityType.StartupEntry,
                    DigitalTwinRelationshipType.Launches,
                    entities,
                    relationships);
            }
            else if (component.Type == InventoryComponentType.ScheduledTask)
            {
                foreach (var executablePath in ParseScheduledTaskExecutablePaths(
                             component.Details.GetValueOrDefault("actions")))
                {
                    AddExecutableRelationship(
                        component,
                        DigitalTwinEntityType.ScheduledTask,
                        DigitalTwinRelationshipType.Launches,
                        entities,
                        relationships,
                        executablePath);
                }
            }
            else if (component.Type == InventoryComponentType.Device)
            {
                AddDeviceDriverRelationships(component, components, deviceByInstance, relationships);
            }
            else if (IsHardwareComponent(component.Type))
            {
                var instance = GetDetail(component, "deviceInstanceId", "instanceId", "pnpDeviceId");
                if (!string.IsNullOrWhiteSpace(instance) &&
                    deviceByInstance.TryGetValue(instance, out var device))
                {
                    AddRelationship(
                        relationships,
                        new DigitalTwinRelationship(
                            sourceType.Value,
                            sourceIdentity,
                            DigitalTwinEntityType.Device,
                            device.IdentityKey,
                            DigitalTwinRelationshipType.RepresentsDevice,
                            component.Source,
                            "The hardware observation reported the matching Plug and Play device instance ID.",
                            0.97m,
                            true,
                            component.ObservedAt));
                }
            }
        }

        AddApplicationExecutableRelationships(components, entities, relationships);
        var snapshotEntities = entities.Values
            .OrderBy(entity => entity.Type)
            .ThenBy(entity => entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var snapshotRelationships = relationships.Values
            .OrderBy(edge => edge.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.Type)
            .ToArray();
        var timestamp = (createdAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return new DigitalTwinSnapshot(
            Guid.NewGuid(),
            timestamp,
            inventory.LatestScan?.ScanId,
            inventory.LatestScan?.Scope,
            snapshotEntities,
            snapshotRelationships,
            DigitalTwinSnapshotHasher.ComputeChecksum(snapshotEntities, snapshotRelationships));
    }

    private static DigitalTwinEntityType? MapEntityType(InventoryComponentType type) =>
        type switch
        {
            InventoryComponentType.Processor or InventoryComponentType.MemoryModule or
                InventoryComponentType.MemorySummary or InventoryComponentType.Motherboard or
                InventoryComponentType.Bios or InventoryComponentType.GraphicsAdapter or
                InventoryComponentType.StorageDevice or InventoryComponentType.NetworkAdapter or
                InventoryComponentType.Display or InventoryComponentType.AudioDevice or
                InventoryComponentType.AudioEndpoint or InventoryComponentType.Battery or
                InventoryComponentType.Firmware or InventoryComponentType.InputDevice or
                InventoryComponentType.Sensor or InventoryComponentType.StorageHealth =>
                DigitalTwinEntityType.Hardware,
            InventoryComponentType.Device => DigitalTwinEntityType.Device,
            InventoryComponentType.DriverPackage => DigitalTwinEntityType.Driver,
            InventoryComponentType.InstalledApplication or InventoryComponentType.StoreApplication =>
                DigitalTwinEntityType.Application,
            InventoryComponentType.Service or InventoryComponentType.RecoveryService =>
                DigitalTwinEntityType.Service,
            InventoryComponentType.Process => DigitalTwinEntityType.Process,
            InventoryComponentType.StartupEntry => DigitalTwinEntityType.StartupEntry,
            InventoryComponentType.ScheduledTask => DigitalTwinEntityType.ScheduledTask,
            InventoryComponentType.RecoveryVolume or InventoryComponentType.RecoveryPartition =>
                DigitalTwinEntityType.Volume,
            InventoryComponentType.RecoveryEnvironment or InventoryComponentType.RestorePoint or
                InventoryComponentType.BackupCatalogItem or InventoryComponentType.BitLockerVolume or
                InventoryComponentType.RecoveryEvent => DigitalTwinEntityType.RecoveryCapability,
            InventoryComponentType.OperatingSystem or InventoryComponentType.WindowsUpdate or
                InventoryComponentType.WindowsFeature or InventoryComponentType.EnvironmentVariable or
                InventoryComponentType.WindowsSystemInfo => DigitalTwinEntityType.Configuration,
            _ => null
        };

    private static bool IsHardwareComponent(InventoryComponentType type) =>
        MapEntityType(type) == DigitalTwinEntityType.Hardware;

    private static InventoryFieldAvailability ResolveAvailability(InventoryComponent component)
    {
        var explicitValue = GetDetail(component, "availability");
        if (Enum.TryParse<InventoryFieldAvailability>(explicitValue, out var parsed))
        {
            return parsed;
        }

        if (component.FieldAvailability.Values.Contains(InventoryFieldAvailability.Inaccessible))
        {
            return InventoryFieldAvailability.Inaccessible;
        }

        if (component.FieldAvailability.Values.Contains(InventoryFieldAvailability.Unavailable))
        {
            return InventoryFieldAvailability.Unavailable;
        }

        if (component.FieldAvailability.Values.Contains(InventoryFieldAvailability.Available))
        {
            return InventoryFieldAvailability.Available;
        }

        return InventoryFieldAvailability.Unknown;
    }

    private static decimal ConfidenceFor(InventoryComponent component) =>
        component.IdentityKey.StartsWith("device:", StringComparison.OrdinalIgnoreCase) ||
        component.Type == InventoryComponentType.Device
            ? 0.98m
            : component.FieldAvailability.Values.Any(value => value is InventoryFieldAvailability.Unknown or InventoryFieldAvailability.Unavailable)
                ? 0.72m
                : 0.90m;

    private static Dictionary<int, InventoryComponent> BuildProcessIndex(
        IEnumerable<InventoryComponent> components) =>
        components
            .Where(component => component.Type == InventoryComponentType.Process &&
                                TryReadInteger(component, "processId", out _))
            .GroupBy(component => ReadInteger(component, "processId"))
            .Where(group => group.Key.HasValue)
            .ToDictionary(group => group.Key!.Value, group => group.First());

    private static Dictionary<string, InventoryComponent> BuildDeviceIndex(
        IEnumerable<InventoryComponent> components) =>
        components
            .Where(component => component.Type == InventoryComponentType.Device)
            .SelectMany(component => SplitList(GetDetail(component, "deviceInstanceId", "instanceId", "associatedDeviceIds"))
                .Append(component.IdentityKey)
                .Select(instance => (Instance: instance, Component: component)))
            .GroupBy(item => item.Instance, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Component, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, InventoryComponent> BuildServiceIndex(
        IEnumerable<InventoryComponent> components) =>
        components
            .Where(component => component.Type is InventoryComponentType.Service or InventoryComponentType.RecoveryService)
            .SelectMany(component => new[]
            {
                GetDetail(component, "Name", "name", "serviceName"),
                GetDetail(component, "serviceName")
            }.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => (Name: value!, Component: component)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Component, StringComparer.OrdinalIgnoreCase);

    private static void AddExecutableRelationship(
        InventoryComponent component,
        DigitalTwinEntityType sourceType,
        DigitalTwinRelationshipType relationshipType,
        IDictionary<string, DigitalTwinEntity> entities,
        IDictionary<string, DigitalTwinRelationship> relationships,
        string? explicitPath = null)
    {
        var path = explicitPath ?? GetDetail(component, "executablePath", "commandTarget");
        if (!IsWindowsPath(path))
        {
            return;
        }

        var executable = new DigitalTwinEntity(
            DigitalTwinEntityType.Executable,
            path!,
            Path.GetFileName(path) ?? "Executable",
            component.Source,
            component.ObservedAt,
            InventoryFieldAvailability.Available,
            0.95m,
            new Dictionary<string, string?> { ["path"] = path },
            null);
        entities.TryAdd(executable.Id, executable);
        AddRelationship(
            relationships,
            new DigitalTwinRelationship(
                sourceType,
                component.IdentityKey,
                DigitalTwinEntityType.Executable,
                executable.IdentityKey,
                relationshipType,
                component.Source,
                $"The inventory source reported executable path '{path}'.",
                0.95m,
                true,
                component.ObservedAt));
    }

    private static void AddApplicationServiceRelationships(
        InventoryComponent service,
        IReadOnlyList<InventoryComponent> components,
        IDictionary<string, DigitalTwinRelationship> relationships)
    {
        var servicePath = GetDetail(service, "executablePath");
        if (!IsWindowsPath(servicePath))
        {
            return;
        }

        foreach (var application in components.Where(component =>
                     component.Type == InventoryComponentType.InstalledApplication))
        {
            var location = GetDetail(application, "installLocation");
            if (!IsWithinDirectory(servicePath!, location))
            {
                continue;
            }

            AddRelationship(
                relationships,
                new DigitalTwinRelationship(
                    DigitalTwinEntityType.Application,
                    application.IdentityKey,
                    DigitalTwinEntityType.Service,
                    service.IdentityKey,
                    DigitalTwinRelationshipType.AssociatedWith,
                    "Windows uninstall registry + Win32_Service",
                    $"Service executable path '{servicePath}' is beneath the registered installation location '{location}'.",
                    0.86m,
                    true,
                    service.ObservedAt));
        }
    }

    private static void AddApplicationExecutableRelationships(
        IReadOnlyList<InventoryComponent> components,
        IReadOnlyDictionary<string, DigitalTwinEntity> entities,
        IDictionary<string, DigitalTwinRelationship> relationships)
    {
        var applications = components
            .Where(component => component.Type is InventoryComponentType.InstalledApplication or InventoryComponentType.StoreApplication)
            .Select(component => (Component: component, Location: GetDetail(component, "installLocation")))
            .Where(application => IsWindowsPath(application.Location))
            .ToArray();

        foreach (var executable in entities.Values.Where(entity => entity.Type == DigitalTwinEntityType.Executable))
        {
            foreach (var application in applications)
            {
                if (!IsWithinDirectory(executable.IdentityKey, application.Location))
                {
                    continue;
                }

                AddRelationship(
                    relationships,
                    new DigitalTwinRelationship(
                        DigitalTwinEntityType.Application,
                        application.Component.IdentityKey,
                        DigitalTwinEntityType.Executable,
                        executable.IdentityKey,
                        DigitalTwinRelationshipType.AssociatedWith,
                        "Windows uninstall registry + executable-path inventory",
                        $"Executable path '{executable.IdentityKey}' is beneath registered application installation location '{application.Location}'.",
                        0.86m,
                        true,
                        executable.LastObservedAt));
            }
        }
    }

    private static void AddDeviceDriverRelationships(
        InventoryComponent device,
        IReadOnlyList<InventoryComponent> components,
        IReadOnlyDictionary<string, InventoryComponent> deviceByInstance,
        IDictionary<string, DigitalTwinRelationship> relationships)
    {
        var instanceId = GetDetail(device, "deviceInstanceId", "instanceId") ?? device.IdentityKey;
        if (!deviceByInstance.TryGetValue(instanceId, out var matchedDevice))
        {
            return;
        }

        foreach (var driver in components.Where(component => component.Type == InventoryComponentType.DriverPackage))
        {
            if (!SplitList(GetDetail(driver, "associatedDeviceIds")).Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            AddRelationship(
                relationships,
                new DigitalTwinRelationship(
                    DigitalTwinEntityType.Device,
                    matchedDevice.IdentityKey,
                    DigitalTwinEntityType.Driver,
                    driver.IdentityKey,
                    DigitalTwinRelationshipType.UsesPackage,
                    driver.Source,
                    $"Driver package metadata lists device instance ID '{instanceId}'.",
                    0.98m,
                    true,
                    driver.ObservedAt));
        }
    }

    private static IEnumerable<string> ParseScheduledTaskExecutablePaths(string? actions)
    {
        if (string.IsNullOrWhiteSpace(actions))
        {
            return [];
        }

        return ExecutablePathPattern.Matches(actions)
            .Select(match => match.Groups["path"].Value.Trim().Trim('"'))
            .Where(IsWindowsPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? GetDetail(InventoryComponent component, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (component.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            var matchingKey = component.Details.Keys.FirstOrDefault(
                candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            if (matchingKey is not null && !string.IsNullOrWhiteSpace(component.Details[matchingKey]))
            {
                return component.Details[matchingKey]!.Trim();
            }
        }

        return null;
    }

    private static bool TryReadInteger(InventoryComponent component, string key, out int value) =>
        int.TryParse(
            GetDetail(component, key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);

    private static int? ReadInteger(InventoryComponent component, string key) =>
        TryReadInteger(component, key, out var value) ? value : null;

    private static bool IsWindowsPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length > 2 &&
        (char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/') ||
         path.StartsWith(@"\\", StringComparison.Ordinal));

    private static bool IsWithinDirectory(string executablePath, string? directory)
    {
        if (!IsWindowsPath(directory) || !IsWindowsPath(executablePath))
        {
            return false;
        }

        var normalizedDirectory = directory!.TrimEnd('\\', '/');
        return executablePath.StartsWith(normalizedDirectory + "\\", StringComparison.OrdinalIgnoreCase) ||
               executablePath.StartsWith(normalizedDirectory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddRelationship(
        IDictionary<string, DigitalTwinRelationship> relationships,
        DigitalTwinRelationship relationship)
    {
        var id = $"{relationship.SourceId}|{relationship.Type}|{relationship.TargetId}";
        relationships.TryAdd(id, relationship);
    }
}
