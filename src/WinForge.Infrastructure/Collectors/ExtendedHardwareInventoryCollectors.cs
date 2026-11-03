using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WmiEnumerationOptions = System.Management.EnumerationOptions;

namespace WinForge.Infrastructure.Collectors;

public abstract class NamespacedWmiInventoryCollector : IInventoryCollector
{
    private readonly ILogger _logger;
    private readonly string _nameSpace;
    private readonly string _query;
    private readonly InventoryComponentType _type;
    private readonly string _category;
    private readonly string[] _identityFields;
    private readonly string[] _displayFields;
    private readonly string[] _detailFields;
    private readonly InventoryScanGroup _scanGroup;

    protected NamespacedWmiInventoryCollector(
        string collectorId,
        string nameSpace,
        string query,
        InventoryComponentType type,
        string category,
        string[] identityFields,
        string[] displayFields,
        string[] detailFields,
        ILoggerFactory loggerFactory,
        InventoryScanGroup scanGroup = InventoryScanGroup.Hardware)
    {
        CollectorId = collectorId;
        _nameSpace = nameSpace;
        _query = query;
        _type = type;
        _category = category;
        _identityFields = identityFields;
        _displayFields = displayFields;
        _detailFields = detailFields;
        _scanGroup = scanGroup;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    public string CollectorId { get; }

    public virtual InventoryScanGroup ScanGroup => _scanGroup;

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    protected virtual string? FormatValue(string propertyName, object value) =>
        value is Array array
            ? string.Join("; ", array.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)))
            : Convert.ToString(value, CultureInfo.InvariantCulture);

    protected virtual void AddAdditionalDetails(
        IDictionary<string, string?> details,
        IDictionary<string, InventoryFieldAvailability> availability)
    {
    }

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();

        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "unsupported_platform", "This inventory source is only available on Windows.")]);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new WmiEnumerationOptions
            {
                ReturnImmediately = false,
                Rewindable = false,
                Timeout = TimeSpan.FromSeconds(15)
            };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope($@"\\.\{_nameSpace}"),
                new ObjectQuery(_query),
                options);
            using var rows = searcher.Get();
            var rowIndex = 0;
            foreach (ManagementObject row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    var details = new Dictionary<string, string?>(StringComparer.Ordinal);
                    var availability = new Dictionary<string, InventoryFieldAvailability>(StringComparer.Ordinal);
                    foreach (var field in _detailFields)
                    {
                        var property = row.Properties[field];
                        if (property is null)
                        {
                            details[field] = null;
                            availability[field] = InventoryFieldAvailability.Unsupported;
                            continue;
                        }

                        var value = property.Value is null ? null : FormatValue(field, property.Value);
                        details[field] = string.IsNullOrWhiteSpace(value) ? null : value;
                        availability[field] = details[field] is null
                            ? InventoryFieldAvailability.Missing
                            : InventoryFieldAvailability.Available;
                    }

                    AddAdditionalDetails(details, availability);
                    var identity = FirstNonEmpty(details, _identityFields);
                    if (identity is null)
                    {
                        issues.Add(new CollectorIssue(
                            CollectorId,
                            "missing_identity",
                            $"Inventory record {rowIndex} did not expose a usable identifier."));
                    }
                    else
                    {
                        var displayName = FirstNonEmpty(details, _displayFields) ?? identity;
                        components.Add(new InventoryComponent(
                            _type,
                            identity.Trim().ToUpperInvariant(),
                            displayName,
                            "WMI",
                            2,
                            DateTimeOffset.UtcNow,
                            details)
                        {
                            Category = _category,
                            FieldAvailability = availability
                        });
                    }
                }

                rowIndex++;
            }

            if (components.Count == 0 && issues.Count == 0)
            {
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "no_instances_reported",
                    "The Windows provider reported no instances for this inventory category."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManagementException exception)
        {
            var (code, message) = exception.ErrorCode.ToString() switch
            {
                "InvalidClass" => ("wmi_class_unsupported", "The Windows provider does not support this inventory class."),
                "AccessDenied" => ("wmi_access_denied", "Windows denied access to this inventory class."),
                _ => ("wmi_query_failed", $"Windows inventory query failed ({exception.GetType().Name}).")
            };
            _logger.LogWarning(exception, "WMI collection failed for {CollectorId}.", CollectorId);
            issues.Add(new CollectorIssue(CollectorId, code, message));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "WMI collection failed for {CollectorId}.", CollectorId);
            issues.Add(new CollectorIssue(
                CollectorId,
                "wmi_query_failed",
                $"Windows inventory query failed ({exception.GetType().Name})."));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private static string? FirstNonEmpty(
        IReadOnlyDictionary<string, string?> details,
        IEnumerable<string> fields) =>
        fields.Select(field => details.TryGetValue(field, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed class StorageHealthInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "DeviceId", "UniqueId", "FriendlyName", "Model", "Manufacturer", "SerialNumber",
        "BusType", "MediaType", "HealthStatus", "OperationalStatus", "Temperature",
        "TemperatureMax", "Wear", "PowerOnHours", "FirmwareVersion", "Size"
    ];

    public StorageHealthInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "storage-health",
            @"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_PhysicalDisk",
            InventoryComponentType.StorageHealth,
            "Storage health",
            ["UniqueId", "DeviceId", "SerialNumber"],
            ["FriendlyName", "Model", "DeviceId"],
            Fields,
            loggerFactory)
    {
    }
}

public sealed class MonitorEdidInventoryCollector : NamespacedWmiInventoryCollector
{
    public MonitorEdidInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "monitor-edid",
            @"root\wmi",
            "SELECT * FROM WmiMonitorID",
            InventoryComponentType.Display,
            "Displays",
            ["InstanceName"],
            ["UserFriendlyName", "ProductCodeID", "InstanceName"],
            ["InstanceName", "Active", "ManufacturerName", "ProductCodeID", "SerialNumberID",
                "UserFriendlyName", "WeekOfManufacture", "YearOfManufacture"],
            loggerFactory)
    {
    }

    protected override string? FormatValue(string propertyName, object value)
    {
        if (value is ushort[] characters)
        {
            var decoded = new string(characters
                .TakeWhile(character => character != 0)
                .Select(character => (char)character)
                .ToArray());
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }

        return base.FormatValue(propertyName, value);
    }
}

public sealed class MonitorDisplayParametersCollector : NamespacedWmiInventoryCollector
{
    public MonitorDisplayParametersCollector(ILoggerFactory loggerFactory)
        : base(
            "monitor-display-parameters",
            @"root\wmi",
            "SELECT * FROM WmiMonitorBasicDisplayParams",
            InventoryComponentType.Display,
            "Displays",
            ["InstanceName"],
            ["InstanceName"],
            ["InstanceName", "Active", "MaxHorizontalImageSize", "MaxVerticalImageSize", "SupportedDisplayFeatures"],
            loggerFactory)
    {
    }
}

public sealed class KeyboardInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "DeviceID", "Name", "Description", "PNPDeviceID", "Status", "Layout", "NumberOfFunctionKeys"
    ];

    public KeyboardInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "keyboards",
            "root\\cimv2",
            "SELECT * FROM Win32_Keyboard",
            InventoryComponentType.InputDevice,
            "Input devices",
            ["PNPDeviceID", "DeviceID"],
            ["Name", "Description"],
            Fields,
            loggerFactory)
    {
    }
}

public sealed class PointingDeviceInventoryCollector : NamespacedWmiInventoryCollector
{
    private static readonly string[] Fields =
    [
        "DeviceID", "Name", "Description", "PNPDeviceID", "Status", "PointingType",
        "NumberOfButtons", "Handedness", "Resolution"
    ];

    public PointingDeviceInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "pointing-devices",
            "root\\cimv2",
            "SELECT * FROM Win32_PointingDevice",
            InventoryComponentType.InputDevice,
            "Input devices",
            ["PNPDeviceID", "DeviceID"],
            ["Name", "Description"],
            Fields,
            loggerFactory)
    {
    }
}

public sealed class ThermalSensorInventoryCollector : NamespacedWmiInventoryCollector
{
    public ThermalSensorInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "thermal-sensors",
            @"root\wmi",
            "SELECT * FROM MSAcpi_ThermalZoneTemperature",
            InventoryComponentType.Sensor,
            "Sensors",
            ["InstanceName"],
            ["InstanceName"],
            ["InstanceName", "CurrentTemperature"],
            loggerFactory)
    {
    }

    protected override string? FormatValue(string propertyName, object value)
    {
        if (propertyName == "CurrentTemperature")
        {
            return uint.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var tenthsKelvin) &&
                tenthsKelvin is > 0 and < 2000
                ? (tenthsKelvin / 10d - 273.15d).ToString("F1", CultureInfo.InvariantCulture)
                : null;
        }

        return base.FormatValue(propertyName, value);
    }

    protected override void AddAdditionalDetails(
        IDictionary<string, string?> details,
        IDictionary<string, InventoryFieldAvailability> availability)
    {
        details["temperatureUnit"] = "Celsius";
        availability["temperatureUnit"] = InventoryFieldAvailability.Available;
    }
}

public sealed class FirmwareInventoryCollector : IInventoryCollector
{
    private const uint FirmwareTypeBios = 1;
    private const uint FirmwareTypeUefi = 2;
    private readonly ILogger<FirmwareInventoryCollector> _logger;

    public FirmwareInventoryCollector(ILogger<FirmwareInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "firmware-mode";

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                [],
                [new CollectorIssue(CollectorId, "unsupported_platform", "Firmware type detection is only available on Windows.")]);
        }

        if (!NativeMethods.GetFirmwareType(out var firmwareType))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("Windows could not report the firmware boot type. Win32 error {ErrorCode}.", error);
            return new CollectorResult(
                CollectorId,
                [],
                [new CollectorIssue(CollectorId, "firmware_type_unavailable", $"Windows could not report the firmware boot type (Win32 error {error}).")]);
        }

        var value = firmwareType switch
        {
            FirmwareTypeUefi => "UEFI",
            FirmwareTypeBios => "BIOS",
            _ => null
        };
        var availability = value is null
            ? InventoryFieldAvailability.Unsupported
            : InventoryFieldAvailability.Available;
        var component = new InventoryComponent(
            InventoryComponentType.Firmware,
            "windows-firmware-boot-type",
            value is null ? "Firmware boot mode unavailable" : $"{value} boot mode",
            "GetFirmwareType",
            2,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["bootMode"] = value })
        {
            Category = "Firmware",
            FieldAvailability = new Dictionary<string, InventoryFieldAvailability>
            {
                ["bootMode"] = availability
            }
        };
        return new CollectorResult(CollectorId, [component], []);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFirmwareType(out uint firmwareType);
    }
}
