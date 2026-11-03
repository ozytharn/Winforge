using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.Infrastructure.Collectors;

public sealed class InstalledApplicationInventoryCollector : IInventoryCollector
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] ValueNames =
    [
        "DisplayName", "Publisher", "DisplayVersion", "InstallDate", "InstallLocation",
        "InstallSource", "ReleaseType", "URLInfoAbout", "URLUpdateInfo", "EstimatedSize",
        "WindowsInstaller", "SystemComponent", "ParentKeyName"
    ];
    private readonly ILogger<InstalledApplicationInventoryCollector> _logger;

    public InstalledApplicationInventoryCollector(ILogger<InstalledApplicationInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "installed-applications";

    public InventoryScanGroup ScanGroup => InventoryScanGroup.Software;

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();
        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "unsupported_platform", "Registry software inventory is only available on Windows.")]);
        }

        var views = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : [RegistryView.Registry32];
        foreach (var (hive, scope) in new[]
                 {
                     (RegistryHive.LocalMachine, "System"),
                     (RegistryHive.CurrentUser, "CurrentUser")
                 })
        {
            foreach (var view in views)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(UninstallPath, writable: false);
                    if (uninstallKey is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var entryKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                            if (entryKey is null)
                            {
                                continue;
                            }

                            var values = ValueNames.ToDictionary(
                                valueName => valueName,
                                valueName => ReadString(entryKey, valueName),
                                StringComparer.Ordinal);
                            if (string.IsNullOrWhiteSpace(values["DisplayName"]) ||
                                string.Equals(values["SystemComponent"], "1", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var identitySource = string.Join(
                                "\n",
                                scope,
                                Normalize(values["Publisher"]),
                                Normalize(values["DisplayName"]),
                                Normalize(values["InstallLocation"]),
                                subKeyName.Trim().ToUpperInvariant());
                            var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)));
                            var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                ["applicationName"] = values["DisplayName"],
                                ["publisher"] = values["Publisher"],
                                ["version"] = values["DisplayVersion"],
                                ["installDate"] = NormalizeInstallDate(values["InstallDate"]),
                                ["installLocation"] = values["InstallLocation"],
                                ["installScope"] = scope,
                                ["registrationKind"] = "Registered",
                                ["registrationSource"] = "Uninstall registry",
                                ["registryHive"] = hive.ToString(),
                                ["registryView"] = view.ToString(),
                                ["registryKeyPath"] = $@"{hive}\{UninstallPath}\{subKeyName}",
                                ["uninstallKeyName"] = subKeyName,
                                ["installSource"] = values["InstallSource"],
                                ["releaseType"] = values["ReleaseType"],
                                ["urlInfoAbout"] = values["URLInfoAbout"],
                                ["urlUpdateInfo"] = values["URLUpdateInfo"],
                                ["estimatedSizeKb"] = values["EstimatedSize"],
                                ["windowsInstaller"] = values["WindowsInstaller"],
                                ["parentKeyName"] = values["ParentKeyName"],
                                ["registryViews"] = view.ToString(),
                                ["uninstallCommand"] = null
                            };
                            var availability = details.ToDictionary(
                                detail => detail.Key,
                                detail => detail.Key switch
                                {
                                    "registrationKind" or "registrationSource" or "installScope" or "registryHive" or
                                        "registryView" or "registryKeyPath" or "uninstallKeyName" =>
                                        InventoryFieldAvailability.Available,
                                    "uninstallCommand" => InventoryFieldAvailability.Unsupported,
                                    _ => string.IsNullOrWhiteSpace(detail.Value)
                                        ? InventoryFieldAvailability.Missing
                                        : InventoryFieldAvailability.Available
                                },
                                StringComparer.Ordinal);
                            components.Add(new InventoryComponent(
                                InventoryComponentType.InstalledApplication,
                                $"registered-app:{identityHash}",
                                values["DisplayName"]!,
                                "Windows uninstall registry",
                                2,
                                DateTimeOffset.UtcNow,
                                details)
                            {
                                Category = "Installed applications",
                                FieldAvailability = availability
                            });
                        }
                        catch (UnauthorizedAccessException exception)
                        {
                            _logger.LogDebug(exception, "Access denied reading uninstall registration {SubKeyName}.", subKeyName);
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "application_registration_inaccessible",
                                "Windows denied access to one registered application entry."));
                        }
                        catch (IOException exception)
                        {
                            _logger.LogDebug(exception, "Uninstall registration {SubKeyName} changed during enumeration.", subKeyName);
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "application_registration_changed",
                                "A registered application entry changed while it was being read."));
                        }
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogWarning(exception, "Access denied reading {Hive} uninstall registrations in {View}.", hive, view);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "application_registry_inaccessible",
                        $"Windows denied access to the {scope.ToLowerInvariant()} uninstall inventory."));
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(exception, "Could not read {Hive} uninstall registrations in {View}.", hive, view);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "application_registry_unavailable",
                        $"The {scope.ToLowerInvariant()} uninstall inventory could not be read."));
                }
            }
        }

        if (components.Count == 0 && issues.Count == 0)
        {
            issues.Add(new CollectorIssue(
                CollectorId,
                "no_registered_applications",
                "Windows reported no visible uninstall registrations for the current user or system."));
        }

        return new CollectorResult(CollectorId, MergeRegistryViewDuplicates(components), issues);
    }

    private static IReadOnlyList<InventoryComponent> MergeRegistryViewDuplicates(
        IReadOnlyList<InventoryComponent> components) =>
        components
            .GroupBy(component => component.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var records = group.ToArray();
                if (records.Length == 1)
                {
                    return records[0];
                }

                var first = records[0];
                var details = first.Details.ToDictionary(
                    detail => detail.Key,
                    detail => detail.Value,
                    StringComparer.Ordinal);
                foreach (var record in records.Skip(1))
                {
                    foreach (var detail in record.Details)
                    {
                        if (detail.Key == "registryView" || detail.Key == "registryViews")
                        {
                            details[detail.Key] = string.Join(
                                "; ",
                                records.Select(candidate => candidate.Details.GetValueOrDefault(detail.Key))
                                    .Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Distinct(StringComparer.OrdinalIgnoreCase));
                        }
                        else if (string.IsNullOrWhiteSpace(details.GetValueOrDefault(detail.Key)) &&
                                 !string.IsNullOrWhiteSpace(detail.Value))
                        {
                            details[detail.Key] = detail.Value;
                        }
                    }
                }

                var availability = first.FieldAvailability.ToDictionary(
                    field => field.Key,
                    field => field.Value,
                    StringComparer.Ordinal);
                foreach (var record in records.Skip(1))
                {
                    foreach (var field in record.FieldAvailability)
                    {
                        if (!availability.TryGetValue(field.Key, out var current) ||
                            current is InventoryFieldAvailability.Missing or InventoryFieldAvailability.Unknown)
                        {
                            availability[field.Key] = field.Value;
                        }
                    }
                }

                return first with
                {
                    Details = details,
                    FieldAvailability = availability,
                    ObservedAt = records.Max(record => record.ObservedAt)
                };
            })
            .ToArray();

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string? NormalizeInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(
            value,
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var date)
            ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : value;
    }
}

public sealed class StartupInventoryCollector : IInventoryCollector
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private static readonly Regex QuotedTarget = new("^\\s*\"(?<path>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ExecutableTarget = new(
        "^\\s*(?<path>.+?\\.exe)(?:\\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ILogger<StartupInventoryCollector> _logger;

    public StartupInventoryCollector(ILogger<StartupInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "startup-inventory";

    public InventoryScanGroup ScanGroup => InventoryScanGroup.Software;

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();
        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "unsupported_platform", "Startup inventory is only available on Windows.")]);
        }

        foreach (var (hive, scope) in new[]
                 {
                     (RegistryHive.CurrentUser, "CurrentUser"),
                     (RegistryHive.LocalMachine, "System")
                 })
        {
            foreach (var subPath in new[] { RunKey, RunOnceKey })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(subPath, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    foreach (var valueName in key.GetValueNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var rawValue = key.GetValue(
                                valueName,
                                null,
                                RegistryValueOptions.DoNotExpandEnvironmentNames);
                            var command = rawValue is null
                                ? null
                                : Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture);
                            var targetMatch = command is null
                                ? Match.Empty
                                : QuotedTarget.Match(command) is { Success: true } quoted
                                    ? quoted
                                    : ExecutableTarget.Match(command);
                            var target = targetMatch.Success ? targetMatch.Groups["path"].Value : null;
                            var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                ["entryName"] = valueName,
                                ["commandTarget"] = target,
                                ["sourceKind"] = "Registry",
                                ["sourcePath"] = $@"{hive}\{subPath}",
                                ["startupScope"] = scope,
                                ["arguments"] = null
                            };
                            var availability = details.ToDictionary(
                                detail => detail.Key,
                                detail => detail.Key switch
                                {
                                    "entryName" or "sourceKind" or "sourcePath" or "startupScope" =>
                                        InventoryFieldAvailability.Available,
                                    "arguments" => InventoryFieldAvailability.Unsupported,
                                    _ => string.IsNullOrWhiteSpace(detail.Value)
                                        ? InventoryFieldAvailability.Missing
                                        : InventoryFieldAvailability.Available
                                },
                                StringComparer.Ordinal);
                            components.Add(CreateComponent(
                                $"{scope}:{subPath}:{valueName}",
                                valueName,
                                "Windows Run registry",
                                details,
                                availability));
                        }
                        catch (UnauthorizedAccessException exception)
                        {
                            _logger.LogDebug(exception, "Access denied reading startup registration {ValueName}.", valueName);
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "startup_entry_inaccessible",
                                "Windows denied access to one startup registry entry."));
                        }
                        catch (IOException exception)
                        {
                            _logger.LogDebug(exception, "Startup registration {ValueName} changed during enumeration.", valueName);
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "startup_entry_changed",
                                "A startup registry entry changed while it was being read."));
                        }
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogWarning(exception, "Access denied reading startup registry key {Hive}\\{Key}.", hive, subPath);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "startup_registry_inaccessible",
                        $"Windows denied access to startup entries under {scope}."));
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(exception, "Startup registry key {Hive}\\{Key} could not be read.", hive, subPath);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "startup_registry_unavailable",
                        $"Startup registry entries under {scope} could not be read."));
                }
            }
        }

        AddStartupFolder(Environment.SpecialFolder.Startup, "CurrentUser", components, issues, cancellationToken);
        AddStartupFolder(Environment.SpecialFolder.CommonStartup, "System", components, issues, cancellationToken);
        if (components.Count == 0 && issues.Count == 0)
        {
            issues.Add(new CollectorIssue(
                CollectorId,
                "no_startup_entries_reported",
                "No startup entries were reported in the supported registry locations or startup folders."));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private void AddStartupFolder(
        Environment.SpecialFolder folder,
        string scope,
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        var folderPath = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(folderPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entryPath);
                var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["entryName"] = name,
                    ["entryPath"] = entryPath,
                    ["sourceKind"] = "Startup folder",
                    ["sourcePath"] = folderPath,
                    ["startupScope"] = scope,
                    ["commandTarget"] = Path.GetExtension(entryPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : entryPath,
                    ["arguments"] = null
                };
                var availability = details.ToDictionary(
                    detail => detail.Key,
                    detail => detail.Key switch
                    {
                        "arguments" => InventoryFieldAvailability.Unsupported,
                        "commandTarget" when Path.GetExtension(entryPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) =>
                            InventoryFieldAvailability.Unsupported,
                        _ => string.IsNullOrWhiteSpace(detail.Value)
                            ? InventoryFieldAvailability.Missing
                            : InventoryFieldAvailability.Available
                    },
                    StringComparer.Ordinal);
                components.Add(CreateComponent(
                    $"{scope}:folder:{entryPath}",
                    name,
                    "Windows startup folder",
                    details,
                    availability));
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Access denied reading startup folder {StartupFolder}.", folderPath);
            issues.Add(new CollectorIssue(
                CollectorId,
                "startup_folder_inaccessible",
                $"Windows denied access to the {scope.ToLowerInvariant()} startup folder."));
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Startup folder {StartupFolder} could not be read.", folderPath);
            issues.Add(new CollectorIssue(
                CollectorId,
                "startup_folder_unavailable",
                $"The {scope.ToLowerInvariant()} startup folder could not be read."));
        }
    }

    private static InventoryComponent CreateComponent(
        string identity,
        string displayName,
        string source,
        IReadOnlyDictionary<string, string?> details,
        IReadOnlyDictionary<string, InventoryFieldAvailability> availability) =>
        new(
            InventoryComponentType.StartupEntry,
            $"startup:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))}",
            displayName,
            source,
            2,
            DateTimeOffset.UtcNow,
            details)
        {
            Category = "Startup",
            FieldAvailability = availability
        };
}
