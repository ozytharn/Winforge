using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WmiEnumerationOptions = System.Management.EnumerationOptions;

namespace WinForge.Infrastructure.Collectors;

public sealed class WindowsServicesInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "Name", "DisplayName", "Description", "State", "Status", "StartMode", "StartName",
        "PathName", "Dependencies", "ServiceType", "ProcessId", "Started", "DelayedAutoStart"
    ];

    public WindowsServicesInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "windows-services",
            "root\\cimv2",
            "SELECT * FROM Win32_Service",
            InventoryComponentType.Service,
            "Services",
            ["Name"],
            ["DisplayName", "Name"],
            Fields,
            loggerFactory,
            InventoryScanGroup.Software)
    {
    }

    protected override string? FormatValue(string propertyName, object value) =>
        string.Equals(propertyName, "PathName", StringComparison.Ordinal)
            ? ExtractExecutablePath(Convert.ToString(value, CultureInfo.InvariantCulture))
            : base.FormatValue(propertyName, value);

    private static string? ExtractExecutablePath(string? serviceCommand)
    {
        if (string.IsNullOrWhiteSpace(serviceCommand))
        {
            return null;
        }

        var command = serviceCommand.Trim();
        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : command.Trim('"');
        }

        var executableEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableEnd >= 0)
        {
            return command[..(executableEnd + 4)];
        }

        return command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
    }
}

public sealed class WindowsUpdatesInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "HotFixID", "Description", "InstalledOn", "ServicePackInEffect", "CSName"
    ];

    public WindowsUpdatesInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "windows-updates",
            "root\\cimv2",
            "SELECT * FROM Win32_QuickFixEngineering",
            InventoryComponentType.WindowsUpdate,
            "Windows updates",
            ["HotFixID"],
            ["HotFixID", "Description"],
            Fields,
            loggerFactory,
            InventoryScanGroup.System)
    {
    }
}

public sealed class WindowsFeaturesInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "Name", "Caption", "Description", "InstallState", "Status", "InstallDate"
    ];

    public WindowsFeaturesInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "windows-optional-features",
            "root\\cimv2",
            "SELECT * FROM Win32_OptionalFeature",
            InventoryComponentType.WindowsFeature,
            "Windows optional features",
            ["Name"],
            ["Caption", "Name"],
            Fields,
            loggerFactory,
            InventoryScanGroup.System)
    {
    }
}

public sealed class WindowsBootConfigurationInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "Name", "BootDirectory", "ConfigurationPath", "LastDrive", "SettingID"
    ];

    public WindowsBootConfigurationInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "windows-boot-configuration",
            "root\\cimv2",
            "SELECT * FROM Win32_BootConfiguration",
            InventoryComponentType.WindowsSystemInfo,
            "Windows configuration",
            ["SettingID", "Name"],
            ["Name"],
            Fields,
            loggerFactory,
            InventoryScanGroup.System)
    {
    }
}

public sealed class WindowsOperatingSystemDetailsInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "Caption", "Version", "BuildNumber", "OSArchitecture", "OperatingSystemSKU",
        "ProductType", "InstallDate", "LastBootUpTime", "WindowsDirectory", "SystemDirectory"
    ];

    public WindowsOperatingSystemDetailsInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "windows-system-details",
            "root\\cimv2",
            "SELECT * FROM Win32_OperatingSystem",
            InventoryComponentType.WindowsSystemInfo,
            "Windows system information",
            ["CSName"],
            ["Caption"],
            ["CSName", .. Fields],
            loggerFactory,
            InventoryScanGroup.System)
    {
    }
}

public sealed class WindowsActivationInventoryCollector : WmiInventoryCollector
{
    private const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    public WindowsActivationInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "windows-activation",
            $"SELECT ID, Name, Description, LicenseStatus, ApplicationID FROM SoftwareLicensingProduct WHERE ApplicationID = '{WindowsApplicationId}'",
            loggerFactory,
            InventoryScanGroup.System)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var applicationId = ReadString(row, "ApplicationID");
        if (!string.Equals(applicationId, WindowsApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var licenseStatus = ReadString(row, "LicenseStatus");
        var status = licenseStatus switch
        {
            "0" => "Unlicensed",
            "1" => "Licensed",
            "2" => "Out-of-box grace",
            "3" => "Out-of-tolerance grace",
            "4" => "Non-genuine grace",
            "5" => "Notification",
            "6" => "Extended grace",
            _ => null
        };
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["productId"] = ReadString(row, "ID"),
            ["editionName"] = ReadString(row, "Name"),
            ["description"] = ReadString(row, "Description"),
            ["licenseStatusCode"] = licenseStatus,
            ["activationStatus"] = status,
            ["partialProductKey"] = null,
            ["applicationId"] = WindowsApplicationId
        };
        var availability = details.ToDictionary(
            detail => detail.Key,
            detail => detail.Key == "partialProductKey"
                ? InventoryFieldAvailability.Unsupported
                : string.IsNullOrWhiteSpace(detail.Value)
                    ? InventoryFieldAvailability.Missing
                    : InventoryFieldAvailability.Available,
            StringComparer.Ordinal);

        return BuildComponent(
            InventoryComponentType.WindowsSystemInfo,
            $"windows-activation:{details["productId"] ?? "unknown"}",
            "Windows activation",
            details,
            "Windows system information") with
        {
            Source = "Windows Software Licensing WMI",
            FieldAvailability = availability
        };
    }
}

public sealed class EnvironmentVariablesInventoryCollector : IInventoryCollector
{
    private static readonly string[] AllowedNames =
    [
        "ComSpec", "OS", "SystemRoot", "windir", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "ProgramData", "PROCESSOR_ARCHITECTURE"
    ];
    private readonly ILogger<EnvironmentVariablesInventoryCollector> _logger;

    public EnvironmentVariablesInventoryCollector(ILogger<EnvironmentVariablesInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "environment-variables";

    public InventoryScanGroup ScanGroup => InventoryScanGroup.System;

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
                [new CollectorIssue(CollectorId, "unsupported_platform", "Environment inventory is only available on Windows.")]);
        }

        ReadEnvironmentHive(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
            "System",
            components,
            issues,
            cancellationToken);
        ReadEnvironmentHive(
            RegistryHive.CurrentUser,
            "Environment",
            "CurrentUser",
            components,
            issues,
            cancellationToken);

        if (components.Count == 0 && issues.Count == 0)
        {
            issues.Add(new CollectorIssue(
                CollectorId,
                "no_environment_variables_reported",
                "None of the allowlisted system environment variables were reported."));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private void ReadEnvironmentHive(
        RegistryHive hive,
        string subKeyPath,
        string scope,
        ICollection<InventoryComponent> components,
        ICollection<CollectorIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                return;
            }

            var names = key.GetValueNames();
            foreach (var allowedName in AllowedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = names.FirstOrDefault(value =>
                    string.Equals(value, allowedName, StringComparison.OrdinalIgnoreCase));
                if (name is null)
                {
                    continue;
                }

                try
                {
                    var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var displayValue = value is null
                        ? null
                        : Convert.ToString(value, CultureInfo.InvariantCulture);
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["name"] = name,
                        ["value"] = displayValue,
                        ["scope"] = scope,
                        ["sourcePath"] = $@"{hive}\{subKeyPath}"
                    };
                    components.Add(new InventoryComponent(
                        InventoryComponentType.EnvironmentVariable,
                        $"{scope}:{name}".ToUpperInvariant(),
                        name,
                        "Windows environment registry",
                        2,
                        DateTimeOffset.UtcNow,
                        details)
                    {
                        Category = "Environment variables",
                        FieldAvailability = details.ToDictionary(
                            detail => detail.Key,
                            detail => string.IsNullOrWhiteSpace(detail.Value)
                                ? InventoryFieldAvailability.Missing
                                : InventoryFieldAvailability.Available,
                            StringComparer.Ordinal)
                    });
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogDebug(exception, "Access denied reading allowlisted environment variable {Name}.", name);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "environment_variable_inaccessible",
                        $"Windows denied access to the {scope.ToLowerInvariant()} environment values."));
                }
                catch (IOException exception)
                {
                    _logger.LogDebug(exception, "Environment variable {Name} could not be read.", name);
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "environment_variable_unavailable",
                        $"An allowlisted {scope.ToLowerInvariant()} environment value could not be read."));
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Access denied reading the {Scope} environment registry key.", scope);
            issues.Add(new CollectorIssue(
                CollectorId,
                "environment_registry_inaccessible",
                $"Windows denied access to {scope.ToLowerInvariant()} environment variables."));
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Could not read the {Scope} environment registry key.", scope);
            issues.Add(new CollectorIssue(
                CollectorId,
                "environment_registry_unavailable",
                $"The {scope.ToLowerInvariant()} environment variables could not be read."));
        }
    }
}
