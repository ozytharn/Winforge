using System.Buffers.Binary;
using System.Globalization;
using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;
using WmiEnumerationOptions = System.Management.EnumerationOptions;

namespace WinForge.Infrastructure.Collectors;

public sealed class NvmeHealthInventoryCollector : IInventoryCollector
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint ProtocolTypeNvme = 3;
    private const uint NvmeDataTypeLogPage = 2;
    private const uint NvmeHealthInfoLogPage = 2;
    private const int ProtocolDataOffset = 40;
    private const int ProtocolSpecificHeaderOffset = 8;
    private const int NvmeHealthLogLength = 512;
    private const int OutputBufferLength = ProtocolSpecificHeaderOffset + ProtocolDataOffset + NvmeHealthLogLength;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;

    private readonly ILogger<NvmeHealthInventoryCollector> _logger;

    public NvmeHealthInventoryCollector(ILogger<NvmeHealthInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "nvme-health";

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
                [new CollectorIssue(CollectorId, "unsupported_platform", "NVMe health queries are only available on Windows.")]);
        }

        try
        {
            var options = new WmiEnumerationOptions
            {
                ReturnImmediately = false,
                Rewindable = false,
                Timeout = TimeSpan.FromSeconds(15)
            };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT DeviceID, Model, SerialNumber, PNPDeviceID FROM Win32_DiskDrive"),
                options);
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (row)
                {
                    var deviceId = Convert.ToString(row["DeviceID"], CultureInfo.InvariantCulture);
                    var model = Convert.ToString(row["Model"], CultureInfo.InvariantCulture);
                    var serialNumber = Convert.ToString(row["SerialNumber"], CultureInfo.InvariantCulture);
                    var pnpDeviceId = Convert.ToString(row["PNPDeviceID"], CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        issues.Add(new CollectorIssue(
                            CollectorId,
                            "storage_identity_unavailable",
                            "A physical disk did not expose a Windows device path."));
                        continue;
                    }

                    var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["deviceId"] = deviceId,
                        ["model"] = model,
                        ["serialNumber"] = serialNumber,
                        ["pnpDeviceId"] = pnpDeviceId,
                        ["protocol"] = null,
                        ["criticalWarning"] = null,
                        ["compositeTemperatureCelsius"] = null,
                        ["availableSparePercent"] = null,
                        ["percentageUsed"] = null,
                        ["dataUnitsRead"] = null,
                        ["dataUnitsWritten"] = null,
                        ["powerOnHours"] = null
                    };
                    var availability = new Dictionary<string, InventoryFieldAvailability>(StringComparer.Ordinal)
                    {
                        ["deviceId"] = InventoryFieldAvailability.Available,
                        ["model"] = string.IsNullOrWhiteSpace(model) ? InventoryFieldAvailability.Missing : InventoryFieldAvailability.Available,
                        ["serialNumber"] = string.IsNullOrWhiteSpace(serialNumber) ? InventoryFieldAvailability.Missing : InventoryFieldAvailability.Available,
                        ["pnpDeviceId"] = string.IsNullOrWhiteSpace(pnpDeviceId) ? InventoryFieldAvailability.Missing : InventoryFieldAvailability.Available,
                        ["protocol"] = InventoryFieldAvailability.Unknown
                    };

                    var queryResult = QueryHealth(deviceId, cancellationToken);
                    details["protocol"] = queryResult.Availability == InventoryFieldAvailability.Available
                        ? "NVMe"
                        : null;
                    foreach (var field in HealthFields)
                    {
                        details[field] = queryResult.Values.TryGetValue(field, out var value) ? value : null;
                        availability[field] = queryResult.Availability == InventoryFieldAvailability.Available
                            ? details[field] is null
                                ? InventoryFieldAvailability.Missing
                                : InventoryFieldAvailability.Available
                            : queryResult.Availability;
                    }
                    availability["protocol"] = queryResult.Availability == InventoryFieldAvailability.Available
                        ? InventoryFieldAvailability.Available
                        : queryResult.Availability;
                    if (queryResult.ErrorCode is { } errorCode)
                    {
                        issues.Add(new CollectorIssue(
                            CollectorId,
                            queryResult.Availability == InventoryFieldAvailability.Inaccessible
                                ? "nvme_access_denied"
                                : "nvme_health_unavailable",
                            $"NVMe health data for {deviceId} was not available (Win32 error {errorCode})."));
                    }

                    components.Add(new InventoryComponent(
                        InventoryComponentType.StorageHealth,
                        $"nvme:{(string.IsNullOrWhiteSpace(pnpDeviceId) ? deviceId : pnpDeviceId).Trim().ToUpperInvariant()}",
                        string.IsNullOrWhiteSpace(model) ? deviceId : model,
                        "Storage protocol-specific IOCTL",
                        2,
                        DateTimeOffset.UtcNow,
                        details)
                    {
                        Category = "Storage health",
                        FieldAvailability = availability
                    });
                }
            }

            if (components.Count == 0 && issues.Count == 0)
            {
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "no_storage_devices_reported",
                    "Windows reported no physical disk instances."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManagementException exception)
        {
            _logger.LogWarning(exception, "Physical disk enumeration failed.");
            var code = exception.ErrorCode.ToString() == "AccessDenied"
                ? "storage_access_denied"
                : exception.ErrorCode.ToString() == "InvalidClass"
                    ? "storage_class_unsupported"
                    : "storage_enumeration_failed";
            issues.Add(new CollectorIssue(
                CollectorId,
                code,
                $"Windows physical disk enumeration failed ({exception.GetType().Name})."));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Physical disk enumeration failed.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "storage_enumeration_failed",
                $"Windows physical disk enumeration failed ({exception.GetType().Name})."));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private static QueryResult QueryHealth(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = NativeMethods.CreateFileW(
            deviceId,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (device.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return new QueryResult(
                new Dictionary<string, string?>(),
                MapAvailability(error),
                error);
        }

        var queryBuffer = new byte[OutputBufferLength];
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(0, 4), StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(8, 4), ProtocolTypeNvme);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(12, 4), NvmeDataTypeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(16, 4), NvmeHealthInfoLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(20, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(24, 4), ProtocolDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(queryBuffer.AsSpan(28, 4), NvmeHealthLogLength);

        cancellationToken.ThrowIfCancellationRequested();
        var succeeded = NativeMethods.DeviceIoControl(
            device,
            IoctlStorageQueryProperty,
            queryBuffer,
            8 + ProtocolDataOffset,
            queryBuffer,
            (uint)queryBuffer.Length,
            out var bytesReturned,
            IntPtr.Zero);
        if (!succeeded)
        {
            var error = Marshal.GetLastWin32Error();
            return new QueryResult(new Dictionary<string, string?>(), MapAvailability(error), error);
        }

        var returnedDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(queryBuffer.AsSpan(24, 4));
        if (returnedDataOffset < ProtocolDataOffset ||
            returnedDataOffset > queryBuffer.Length - ProtocolSpecificHeaderOffset - NvmeHealthLogLength)
        {
            return new QueryResult(
                new Dictionary<string, string?>(),
                InventoryFieldAvailability.Unavailable,
                null);
        }

        var dataStart = checked(ProtocolSpecificHeaderOffset + (int)returnedDataOffset);
        if (bytesReturned < dataStart + NvmeHealthLogLength)
        {
            return new QueryResult(
                new Dictionary<string, string?>(),
                InventoryFieldAvailability.Unavailable,
                null);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(queryBuffer.AsSpan(0, 4)) < dataStart ||
            BinaryPrimitives.ReadUInt32LittleEndian(queryBuffer.AsSpan(4, 4)) < dataStart ||
            BinaryPrimitives.ReadUInt32LittleEndian(queryBuffer.AsSpan(8, 4)) != ProtocolTypeNvme ||
            BinaryPrimitives.ReadUInt32LittleEndian(queryBuffer.AsSpan(12, 4)) != NvmeDataTypeLogPage ||
            BinaryPrimitives.ReadUInt32LittleEndian(queryBuffer.AsSpan(28, 4)) < NvmeHealthLogLength)
        {
            return new QueryResult(
                new Dictionary<string, string?>(),
                InventoryFieldAvailability.Unavailable,
                null);
        }

        var log = queryBuffer.AsSpan(dataStart, NvmeHealthLogLength);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["criticalWarning"] = $"0x{log[0]:X2}",
            ["compositeTemperatureCelsius"] = BinaryPrimitives.ReadUInt16LittleEndian(log.Slice(1, 2)) is var kelvin &&
                kelvin > 0 &&
                kelvin < 2000 &&
                kelvin != ushort.MaxValue
                ? (kelvin - 273.15).ToString("F1", CultureInfo.InvariantCulture)
                : null,
            ["availableSparePercent"] = log[3].ToString(CultureInfo.InvariantCulture),
            ["percentageUsed"] = log[5].ToString(CultureInfo.InvariantCulture),
            ["dataUnitsRead"] = ReadUnsigned128(log.Slice(32, 16)),
            ["dataUnitsWritten"] = ReadUnsigned128(log.Slice(48, 16)),
            ["powerOnHours"] = ReadUnsigned128(log.Slice(128, 16))
        };
        return new QueryResult(values, InventoryFieldAvailability.Available, null);
    }

    private static string ReadUnsigned128(ReadOnlySpan<byte> littleEndianValue) =>
        new BigInteger(littleEndianValue, isUnsigned: true, isBigEndian: false)
            .ToString(CultureInfo.InvariantCulture);

    private static InventoryFieldAvailability MapAvailability(int errorCode) =>
        errorCode switch
        {
            ErrorAccessDenied => InventoryFieldAvailability.Inaccessible,
            ErrorInvalidFunction or ErrorNotSupported => InventoryFieldAvailability.Unsupported,
            _ => InventoryFieldAvailability.Unavailable
        };

    private static readonly string[] HealthFields =
    [
        "criticalWarning", "compositeTemperatureCelsius", "availableSparePercent", "percentageUsed",
        "dataUnitsRead", "dataUnitsWritten", "powerOnHours"
    ];

    private sealed record QueryResult(
        IReadOnlyDictionary<string, string?> Values,
        InventoryFieldAvailability Availability,
        int? ErrorCode);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[] inputBuffer,
            uint inputBufferSize,
            byte[] outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
