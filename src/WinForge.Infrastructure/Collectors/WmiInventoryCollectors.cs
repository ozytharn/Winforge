using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WmiEnumerationOptions = System.Management.EnumerationOptions;

namespace WinForge.Infrastructure.Collectors;

public abstract class WmiInventoryCollector : IInventoryCollector
{
    private readonly ILogger _logger;
    private readonly InventoryScanGroup _scanGroup;

    protected WmiInventoryCollector(
        string collectorId,
        string query,
        ILoggerFactory loggerFactory,
        InventoryScanGroup scanGroup = InventoryScanGroup.Hardware)
    {
        CollectorId = collectorId;
        Query = query;
        _scanGroup = scanGroup;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    public string CollectorId { get; }

    public InventoryScanGroup ScanGroup => _scanGroup;

    protected string Query { get; }

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    protected abstract InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex);

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();

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
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery(Query),
                options);
            using var rows = searcher.Get();
            var rowIndex = 0;
            foreach (ManagementObject row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    var component = CreateComponent(row, rowIndex);
                    if (component is null)
                    {
                        issues.Add(new CollectorIssue(
                            CollectorId,
                            "missing_identity",
                            "A system record lacked the identity fields required to store it."));
                    }
                    else
                    {
                        components.Add(component);
                    }
                }

                rowIndex++;
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

    protected static string? ReadString(ManagementBaseObject row, string propertyName) =>
        Convert.ToString(row[propertyName], CultureInfo.InvariantCulture);

    protected static string? ReadNumber(ManagementBaseObject row, string propertyName) =>
        Convert.ToString(row[propertyName], CultureInfo.InvariantCulture);

    protected static InventoryComponent BuildComponent(
        InventoryComponentType type,
        string identityKey,
        string displayName,
        IReadOnlyDictionary<string, string?> details,
        string category = "General")
    {
        var availability = details.ToDictionary(
            detail => detail.Key,
            detail => string.IsNullOrWhiteSpace(detail.Value)
                ? InventoryFieldAvailability.Missing
                : InventoryFieldAvailability.Available,
            StringComparer.Ordinal);
        return new InventoryComponent(
            type,
            identityKey,
            displayName,
            "WMI",
            2,
            DateTimeOffset.UtcNow,
            details)
        {
            Category = category,
            FieldAvailability = availability
        };
    }
}

public sealed class OperatingSystemInventoryCollector : WmiInventoryCollector
{
    public OperatingSystemInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "operating-system",
            "SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var caption = ReadString(row, "Caption");
        if (string.IsNullOrWhiteSpace(caption))
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.OperatingSystem,
            "operating-system:current",
            caption.Trim(),
            new Dictionary<string, string?>
            {
                ["version"] = ReadString(row, "Version"),
                ["build"] = ReadString(row, "BuildNumber"),
                ["architecture"] = ReadString(row, "OSArchitecture")
            });
    }
}

public sealed class ProcessorInventoryCollector : WmiInventoryCollector
{
    public ProcessorInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "processors",
            "SELECT DeviceID, Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var deviceId = ReadString(row, "DeviceID");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var name = ReadString(row, "Name");
        return BuildComponent(
            InventoryComponentType.Processor,
            $"processor:{deviceId.Trim()}",
            string.IsNullOrWhiteSpace(name) ? "Processor name unavailable" : name.Trim(),
            new Dictionary<string, string?>
            {
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["cores"] = ReadNumber(row, "NumberOfCores"),
                ["logicalProcessors"] = ReadNumber(row, "NumberOfLogicalProcessors")
            });
    }
}

public sealed class MemoryModuleInventoryCollector : WmiInventoryCollector
{
    public MemoryModuleInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "memory-modules",
            "SELECT Tag, DeviceLocator, Manufacturer, Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var tag = ReadString(row, "Tag");
        var locator = ReadString(row, "DeviceLocator");
        var identity = !string.IsNullOrWhiteSpace(tag) ? tag : locator;
        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.MemoryModule,
            $"memory:{identity.Trim()}",
            string.IsNullOrWhiteSpace(locator) ? identity.Trim() : locator.Trim(),
            new Dictionary<string, string?>
            {
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["capacityBytes"] = ReadNumber(row, "Capacity"),
                ["speedMHz"] = ReadNumber(row, "Speed"),
                ["smbiosMemoryType"] = ReadNumber(row, "SMBIOSMemoryType")
            });
    }
}

public sealed class PnpDeviceInventoryCollector : WmiInventoryCollector
{
    public PnpDeviceInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "pnp-devices",
            "SELECT PNPDeviceID, Name, Manufacturer, PNPClass, Status, ConfigManagerErrorCode FROM Win32_PnPEntity",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var deviceId = ReadString(row, "PNPDeviceID");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var name = ReadString(row, "Name");
        return BuildComponent(
            InventoryComponentType.Device,
            deviceId.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(name) ? "Device name unavailable" : name.Trim(),
            new Dictionary<string, string?>
            {
                ["instanceId"] = deviceId.Trim(),
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["class"] = ReadString(row, "PNPClass"),
                ["status"] = ReadString(row, "Status"),
                ["configurationManagerErrorCode"] = ReadNumber(row, "ConfigManagerErrorCode")
            },
            ReadString(row, "PNPClass") ?? "Other devices");
    }
}

public sealed class WmiHardwareInventoryCollector : IInventoryCollector
{
    private readonly ILogger<WmiHardwareInventoryCollector> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public WmiHardwareInventoryCollector(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WmiHardwareInventoryCollector>();
    }

    public string CollectorId => "hardware-details";

    public async Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var collectors = new WmiInventoryCollector[]
        {
            new MotherboardInventoryCollector(_loggerFactory),
            new BiosInventoryCollector(_loggerFactory),
            new GraphicsAdapterInventoryCollector(_loggerFactory),
            new StorageDeviceInventoryCollector(_loggerFactory),
            new NetworkAdapterInventoryCollector(_loggerFactory),
            new DisplayInventoryCollector(_loggerFactory),
            new AudioDeviceInventoryCollector(_loggerFactory),
            new BatteryInventoryCollector(_loggerFactory),
            new MemorySummaryInventoryCollector(_loggerFactory)
        };
        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();

        foreach (var collector in collectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
                components.AddRange(result.Components);
                issues.AddRange(result.Issues);
                if (result.Components.Count == 0 && result.Issues.Count == 0)
                {
                    issues.Add(new CollectorIssue(
                        collector.CollectorId,
                        "no_instances_reported",
                        "Windows reported no instances for this category; hardware absence is not confirmed."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Hardware detail collector {CollectorId} failed.", collector.CollectorId);
                issues.Add(new CollectorIssue(
                    collector.CollectorId,
                    "collector_failed",
                    $"Hardware details could not be collected ({exception.GetType().Name})."));
            }
        }

        return new CollectorResult(CollectorId, components, issues);
    }
}

public sealed class MotherboardInventoryCollector : WmiInventoryCollector
{
    public MotherboardInventoryCollector(ILoggerFactory loggerFactory)
        : base("motherboard", "SELECT Tag, Product, Manufacturer, SerialNumber, Version, Model FROM Win32_BaseBoard", loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var tag = ReadString(row, "Tag");
        var serial = ReadString(row, "SerialNumber");
        var product = ReadString(row, "Product");
        var manufacturer = ReadString(row, "Manufacturer");
        var identity = WmiInventoryValues.FirstValue(tag, serial, $"{manufacturer}|{product}");
        if (identity is null || identity == "|")
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.Motherboard,
            $"motherboard:{identity}",
            WmiInventoryValues.FirstValue(product, ReadString(row, "Model")) ?? "Motherboard identity available",
            new Dictionary<string, string?>
            {
                ["product"] = product,
                ["manufacturer"] = manufacturer,
                ["serialNumber"] = serial,
                ["version"] = ReadString(row, "Version"),
                ["model"] = ReadString(row, "Model")
            },
            "Motherboard");
    }
}

public sealed class BiosInventoryCollector : WmiInventoryCollector
{
    public BiosInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "bios",
            "SELECT Manufacturer, Name, SMBIOSBIOSVersion, Version, ReleaseDate, SerialNumber FROM Win32_BIOS",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var manufacturer = ReadString(row, "Manufacturer");
        var version = WmiInventoryValues.FirstValue(ReadString(row, "SMBIOSBIOSVersion"), ReadString(row, "Version"));
        var serialNumber = ReadString(row, "SerialNumber");
        var vendorVersion = string.IsNullOrWhiteSpace(manufacturer) && string.IsNullOrWhiteSpace(version)
            ? null
            : $"{manufacturer}|{version}";
        var identity = WmiInventoryValues.FirstValue(
            serialNumber,
            vendorVersion) ?? "system";
        return BuildComponent(
            InventoryComponentType.Bios,
            $"bios:{identity}",
            WmiInventoryValues.FirstValue(ReadString(row, "Name"), "System BIOS")!,
            new Dictionary<string, string?>
            {
                ["manufacturer"] = manufacturer,
                ["version"] = version,
                ["releaseDate"] = ReadString(row, "ReleaseDate"),
                ["serialNumber"] = serialNumber
            },
            "BIOS/UEFI");
    }
}

public sealed class GraphicsAdapterInventoryCollector : WmiInventoryCollector
{
    public GraphicsAdapterInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "graphics-adapters",
            "SELECT PNPDeviceID, DeviceID, Name, AdapterCompatibility, VideoProcessor, AdapterRAM, DriverVersion, DriverDate, VideoModeDescription FROM Win32_VideoController",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var identity = WmiInventoryValues.FirstValue(ReadString(row, "PNPDeviceID"), ReadString(row, "DeviceID"));
        if (identity is null)
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.GraphicsAdapter,
            $"graphics:{identity.Trim().ToUpperInvariant()}",
            WmiInventoryValues.FirstValue(ReadString(row, "Name"), "Graphics adapter identity available")!,
            new Dictionary<string, string?>
            {
                ["instanceId"] = ReadString(row, "PNPDeviceID"),
                ["manufacturer"] = ReadString(row, "AdapterCompatibility"),
                ["videoProcessor"] = ReadString(row, "VideoProcessor"),
                ["adapterRamBytes"] = ReadNumber(row, "AdapterRAM"),
                ["driverVersion"] = ReadString(row, "DriverVersion"),
                ["driverDate"] = ReadString(row, "DriverDate"),
                ["videoMode"] = ReadString(row, "VideoModeDescription")
            },
            "Graphics");
    }
}

public sealed class StorageDeviceInventoryCollector : WmiInventoryCollector
{
    public StorageDeviceInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "storage-devices",
            "SELECT PNPDeviceID, DeviceID, Index, Model, Manufacturer, SerialNumber, InterfaceType, MediaType, Size, Status FROM Win32_DiskDrive",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var identity = WmiInventoryValues.FirstValue(ReadString(row, "PNPDeviceID"), ReadString(row, "DeviceID"), ReadString(row, "Index"));
        if (identity is null)
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.StorageDevice,
            $"storage:{identity.Trim().ToUpperInvariant()}",
            WmiInventoryValues.FirstValue(ReadString(row, "Model"), "Storage device identity available")!,
            new Dictionary<string, string?>
            {
                ["instanceId"] = ReadString(row, "PNPDeviceID"),
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["serialNumber"] = ReadString(row, "SerialNumber"),
                ["interfaceType"] = ReadString(row, "InterfaceType"),
                ["mediaType"] = ReadString(row, "MediaType"),
                ["sizeBytes"] = ReadNumber(row, "Size"),
                ["status"] = ReadString(row, "Status")
            },
            "Storage");
    }
}

public sealed class NetworkAdapterInventoryCollector : WmiInventoryCollector
{
    public NetworkAdapterInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "network-adapters",
            "SELECT PNPDeviceID, GUID, DeviceID, Name, Manufacturer, MACAddress, NetEnabled, PhysicalAdapter, AdapterType, Speed, ServiceName FROM Win32_NetworkAdapter",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var identity = WmiInventoryValues.FirstValue(ReadString(row, "PNPDeviceID"), ReadString(row, "GUID"), ReadString(row, "DeviceID"));
        if (identity is null)
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.NetworkAdapter,
            $"network:{identity.Trim().ToUpperInvariant()}",
            WmiInventoryValues.FirstValue(ReadString(row, "Name"), "Network adapter identity available")!,
            new Dictionary<string, string?>
            {
                ["instanceId"] = ReadString(row, "PNPDeviceID"),
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["macAddress"] = ReadString(row, "MACAddress"),
                ["enabled"] = ReadString(row, "NetEnabled"),
                ["physicalAdapter"] = ReadString(row, "PhysicalAdapter"),
                ["adapterType"] = ReadString(row, "AdapterType"),
                ["speedBitsPerSecond"] = ReadNumber(row, "Speed"),
                ["serviceName"] = ReadString(row, "ServiceName")
            },
            "Network");
    }
}

public sealed class DisplayInventoryCollector : WmiInventoryCollector
{
    public DisplayInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "displays",
            "SELECT PNPDeviceID, DeviceID, Name, MonitorManufacturer, MonitorType, ScreenWidth, ScreenHeight, PixelsPerXLogicalInch, PixelsPerYLogicalInch, Status FROM Win32_DesktopMonitor",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var identity = WmiInventoryValues.FirstValue(ReadString(row, "PNPDeviceID"), ReadString(row, "DeviceID"));
        if (identity is null)
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.Display,
            $"display:{identity.Trim().ToUpperInvariant()}",
            WmiInventoryValues.FirstValue(ReadString(row, "Name"), "Display identity available")!,
            new Dictionary<string, string?>
            {
                ["instanceId"] = ReadString(row, "PNPDeviceID"),
                ["manufacturer"] = ReadString(row, "MonitorManufacturer"),
                ["monitorType"] = ReadString(row, "MonitorType"),
                ["screenWidthPixels"] = ReadNumber(row, "ScreenWidth"),
                ["screenHeightPixels"] = ReadNumber(row, "ScreenHeight"),
                ["pixelsPerXLogicalInch"] = ReadNumber(row, "PixelsPerXLogicalInch"),
                ["pixelsPerYLogicalInch"] = ReadNumber(row, "PixelsPerYLogicalInch"),
                ["status"] = ReadString(row, "Status")
            },
            "Displays");
    }
}

public sealed class AudioDeviceInventoryCollector : WmiInventoryCollector
{
    public AudioDeviceInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "audio-devices",
            "SELECT PNPDeviceID, DeviceID, Name, Manufacturer, ProductName, Status, StatusInfo FROM Win32_SoundDevice",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var identity = WmiInventoryValues.FirstValue(ReadString(row, "PNPDeviceID"), ReadString(row, "DeviceID"));
        if (identity is null)
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.AudioDevice,
            $"audio:{identity.Trim().ToUpperInvariant()}",
            WmiInventoryValues.FirstValue(ReadString(row, "Name"), ReadString(row, "ProductName"), "Audio device identity available")!,
            new Dictionary<string, string?>
            {
                ["instanceId"] = ReadString(row, "PNPDeviceID"),
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["productName"] = ReadString(row, "ProductName"),
                ["status"] = ReadString(row, "Status"),
                ["statusInfo"] = ReadNumber(row, "StatusInfo")
            },
            "Audio");
    }
}

public sealed class BatteryInventoryCollector : WmiInventoryCollector
{
    public BatteryInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "batteries",
            "SELECT DeviceID, Name, Chemistry, DesignCapacity, FullChargeCapacity, EstimatedChargeRemaining, BatteryStatus, Status FROM Win32_Battery",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var identity = ReadString(row, "DeviceID");
        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        return BuildComponent(
            InventoryComponentType.Battery,
            $"battery:{identity.Trim()}",
            WmiInventoryValues.FirstValue(ReadString(row, "Name"), identity.Trim())!,
            new Dictionary<string, string?>
            {
                ["chemistry"] = ReadString(row, "Chemistry"),
                ["designCapacity"] = ReadNumber(row, "DesignCapacity"),
                ["fullChargeCapacity"] = ReadNumber(row, "FullChargeCapacity"),
                ["estimatedChargePercent"] = ReadNumber(row, "EstimatedChargeRemaining"),
                ["batteryStatus"] = ReadNumber(row, "BatteryStatus"),
                ["status"] = ReadString(row, "Status")
            },
            "Battery");
    }
}

public sealed class DriverPackageInventoryCollector : WmiInventoryCollector
{
    public DriverPackageInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "driver-packages-wmi",
            "SELECT DeviceID, DeviceName, DriverName, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned, Signer FROM Win32_PnPSignedDriver",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var infName = ReadString(row, "InfName");
        var deviceId = ReadString(row, "DeviceID");
        var identity = !string.IsNullOrWhiteSpace(infName)
            ? $"driver-inf:{infName.Trim().ToUpperInvariant()}"
            : !string.IsNullOrWhiteSpace(deviceId)
                ? $"driver-device:{deviceId.Trim().ToUpperInvariant()}"
                : null;
        if (identity is null)
        {
            return null;
        }

        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["infName"] = infName,
            ["provider"] = ReadString(row, "DriverProviderName"),
            ["version"] = ReadString(row, "DriverVersion"),
            ["dateUtc"] = ReadString(row, "DriverDate"),
            ["description"] = ReadString(row, "DriverName") ?? ReadString(row, "DeviceName"),
            ["deviceInstanceId"] = deviceId,
            ["associatedDeviceIds"] = deviceId,
            ["signer"] = ReadString(row, "Signer"),
            ["isSigned"] = ReadString(row, "IsSigned")
        };
        var availability = details.ToDictionary(
            detail => detail.Key,
            detail => string.IsNullOrWhiteSpace(detail.Value)
                ? InventoryFieldAvailability.Missing
                : InventoryFieldAvailability.Available,
            StringComparer.Ordinal);

        return new InventoryComponent(
            InventoryComponentType.DriverPackage,
            identity,
            details["description"] ?? infName ?? deviceId ?? "Driver package",
            "WMI",
            2,
            DateTimeOffset.UtcNow,
            details)
        {
            Category = "Drivers",
            FieldAvailability = availability
        };
    }
}

public sealed class MemorySummaryInventoryCollector : WmiInventoryCollector
{
    public MemorySummaryInventoryCollector(ILoggerFactory loggerFactory)
        : base(
            "memory-summary",
            "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem",
            loggerFactory)
    {
    }

    protected override InventoryComponent? CreateComponent(ManagementBaseObject row, int rowIndex)
    {
        var totalBytes = ReadNumber(row, "TotalPhysicalMemory");
        return BuildComponent(
            InventoryComponentType.MemorySummary,
            "memory-summary:system",
            "Installed physical memory total",
            new Dictionary<string, string?>
            {
                ["manufacturer"] = ReadString(row, "Manufacturer"),
                ["model"] = ReadString(row, "Model"),
                ["totalBytes"] = totalBytes
            },
            "Memory");
    }
}

internal static class WmiInventoryValues
{
    public static string? FirstValue(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
