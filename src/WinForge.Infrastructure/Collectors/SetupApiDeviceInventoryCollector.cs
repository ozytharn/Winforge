using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.Infrastructure.Collectors;

public sealed class SetupApiDeviceInventoryCollector : IInventoryCollector
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorInvalidData = 13;
    private const uint ErrorNotFound = 1168;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorNotSupported = 50;
    private const uint MaxPropertyBytes = 1024 * 1024;
    private const uint MaxInstanceIdCharacters = 32768;
    private const uint DeviceNodeHasProblem = 0x00000400;
    private const uint DeviceNodeStarted = 0x00000008;
    private const uint ProblemDisabled = 22;
    private const uint ProblemFailedInstall = 28;
    private const uint ConfigRetSuccess = 0;
    private const uint DevicePropertyTypeFileTime = 0x10;
    private const uint DevicePropertyTypeString = 0x12;
    private static readonly Guid DeviceDriverPropertyFormatId = new("A8B865DD-2E3D-4094-AD97-E593A70C75D6");

    private readonly ILogger<SetupApiDeviceInventoryCollector> _logger;

    public SetupApiDeviceInventoryCollector(ILogger<SetupApiDeviceInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "pnp-setupapi";

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private CollectorResult Collect(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CollectorResult(
                CollectorId,
                [],
                [new CollectorIssue(CollectorId, "unsupported_platform", "SetupAPI device enumeration is only available on Windows.")]);
        }

        var components = new List<InventoryComponent>();
        var issues = new List<CollectorIssue>();
        cancellationToken.ThrowIfCancellationRequested();

        using var deviceSet = NativeMethods.SetupDiGetClassDevsW(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfAllClasses | DigcfPresent);
        if (deviceSet.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("SetupAPI could not create the present-device information set. Win32 error {ErrorCode}.", error);
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "enumeration_failed", $"Windows device enumeration failed (Win32 error {error}).")]);
        }

        for (uint index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deviceData = new SpDeviceInfoData
            {
                Size = (uint)Marshal.SizeOf<SpDeviceInfoData>()
            };

            if (!NativeMethods.SetupDiEnumDeviceInfo(deviceSet, index, ref deviceData))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                {
                    break;
                }

                _logger.LogWarning("SetupAPI device enumeration stopped at index {Index}. Win32 error {ErrorCode}.", index, error);
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "enumeration_incomplete",
                    $"Device enumeration stopped before all devices were read (Win32 error {error})."));
                break;
            }

            var instanceId = ReadInstanceId(deviceSet, ref deviceData, cancellationToken);
            if (instanceId.Value is null)
            {
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "device_identity_unavailable",
                    $"A present device at enumeration index {index} did not expose a usable device instance ID ({instanceId.Availability})."));
                continue;
            }

            components.AddRange(ReadDevice(deviceSet, ref deviceData, instanceId.Value, cancellationToken));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private IReadOnlyList<InventoryComponent> ReadDevice(
        SafeDeviceInfoSetHandle deviceSet,
        ref SpDeviceInfoData deviceData,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["instanceId"] = instanceId,
            ["classGuid"] = deviceData.ClassGuid == Guid.Empty ? null : deviceData.ClassGuid.ToString("B")
        };
        var availability = new Dictionary<string, InventoryFieldAvailability>(StringComparer.Ordinal)
        {
            ["instanceId"] = InventoryFieldAvailability.Available,
            ["classGuid"] = deviceData.ClassGuid == Guid.Empty
                ? InventoryFieldAvailability.Missing
                : InventoryFieldAvailability.Available
        };

        ReadRegistryProperty(deviceSet, ref deviceData, 0, cancellationToken, details, availability, "deviceDescription");
        ReadRegistryProperty(deviceSet, ref deviceData, 1, cancellationToken, details, availability, "hardwareIds");
        ReadRegistryProperty(deviceSet, ref deviceData, 2, cancellationToken, details, availability, "compatibleIds");
        ReadRegistryProperty(deviceSet, ref deviceData, 4, cancellationToken, details, availability, "serviceName");
        ReadRegistryProperty(deviceSet, ref deviceData, 7, cancellationToken, details, availability, "class");
        ReadRegistryProperty(deviceSet, ref deviceData, 8, cancellationToken, details, availability, "classGuidProperty");
        ReadRegistryProperty(deviceSet, ref deviceData, 9, cancellationToken, details, availability, "driverKey");
        ReadRegistryProperty(deviceSet, ref deviceData, 11, cancellationToken, details, availability, "manufacturer");
        ReadRegistryProperty(deviceSet, ref deviceData, 12, cancellationToken, details, availability, "friendlyName");
        ReadRegistryProperty(deviceSet, ref deviceData, 22, cancellationToken, details, availability, "enumerator");

        ReadDeviceProperty(deviceSet, ref deviceData, 2, "driverDate", DevicePropertyTypeFileTime, cancellationToken, details, availability);
        ReadDeviceProperty(deviceSet, ref deviceData, 3, "driverVersion", DevicePropertyTypeString, cancellationToken, details, availability);
        ReadDeviceProperty(deviceSet, ref deviceData, 4, "driverDescription", DevicePropertyTypeString, cancellationToken, details, availability);
        ReadDeviceProperty(deviceSet, ref deviceData, 5, "driverInfPath", DevicePropertyTypeString, cancellationToken, details, availability);
        ReadDeviceProperty(deviceSet, ref deviceData, 9, "driverProvider", DevicePropertyTypeString, cancellationToken, details, availability);

        var status = ReadDeviceStatus(instanceId, cancellationToken);
        details["deviceNodeStatusFlags"] = status.StatusFlags?.ToString();
        details["deviceProblemCode"] = status.ProblemCode?.ToString();
        details["deviceStatus"] = status.DisplayStatus;
        details["driverStatus"] = status.ProblemCode switch
        {
            ProblemFailedInstall => "Missing driver or failed driver installation",
            ProblemDisabled => "Disabled",
            > 0 => "Device error",
            _ when status.StatusFlags is { } flags && (flags & DeviceNodeStarted) != 0 => "Started",
            _ when status.StatusFlags.HasValue => "Not started",
            _ => null
        };
        availability["deviceNodeStatusFlags"] = status.Availability;
        availability["deviceProblemCode"] = status.Availability;
        availability["deviceStatus"] = status.Availability;
        availability["driverStatus"] = status.Availability;

        var className = details["class"];
        var friendlyName = details["friendlyName"];
        var description = details["deviceDescription"];
        var displayName = FirstNonEmpty(friendlyName, description, instanceId);
        var deviceComponent = new InventoryComponent(
            InventoryComponentType.Device,
            instanceId.Trim().ToUpperInvariant(),
            displayName,
            "SetupAPI",
            2,
            DateTimeOffset.UtcNow,
            details)
        {
            Category = FirstNonEmpty(className, "Other devices"),
            FieldAvailability = availability
        };

        var driverInfPath = details["driverInfPath"];
        if (string.IsNullOrWhiteSpace(driverInfPath))
        {
            return [deviceComponent];
        }

        var driverDetails = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["infName"] = driverInfPath,
            ["provider"] = details["driverProvider"],
            ["version"] = details["driverVersion"],
            ["dateUtc"] = details["driverDate"],
            ["description"] = details["driverDescription"],
            ["deviceInstanceId"] = instanceId,
            ["associatedDeviceIds"] = instanceId,
            ["driverKey"] = details["driverKey"],
            ["signer"] = null,
            ["isSigned"] = null,
            ["signatureStatus"] = null
        };
        var driverAvailability = new Dictionary<string, InventoryFieldAvailability>(StringComparer.Ordinal)
        {
            ["infName"] = InventoryFieldAvailability.Available,
            ["provider"] = availability["driverProvider"],
            ["version"] = availability["driverVersion"],
            ["dateUtc"] = availability["driverDate"],
            ["description"] = availability["driverDescription"],
            ["deviceInstanceId"] = InventoryFieldAvailability.Available,
            ["associatedDeviceIds"] = InventoryFieldAvailability.Available,
            ["driverKey"] = availability["driverKey"],
            ["signer"] = InventoryFieldAvailability.Unsupported,
            ["isSigned"] = InventoryFieldAvailability.Unsupported,
            ["signatureStatus"] = InventoryFieldAvailability.Unsupported
        };
        var driverComponent = new InventoryComponent(
            InventoryComponentType.DriverPackage,
            $"driver-inf:{driverInfPath.Trim().ToUpperInvariant()}",
            FirstNonEmpty(details["driverDescription"], driverInfPath),
            "SetupAPI",
            2,
            DateTimeOffset.UtcNow,
            driverDetails)
        {
            Category = "Drivers",
            FieldAvailability = driverAvailability
        };
        return [deviceComponent, driverComponent];
    }

    private static void ReadDeviceProperty(
        SafeDeviceInfoSetHandle deviceSet,
        ref SpDeviceInfoData deviceData,
        uint propertyId,
        string name,
        uint expectedType,
        CancellationToken cancellationToken,
        IDictionary<string, string?> details,
        IDictionary<string, InventoryFieldAvailability> availability)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = new DevPropKey(DeviceDriverPropertyFormatId, propertyId);
        var succeeded = NativeMethods.SetupDiGetDevicePropertyW(
            deviceSet,
            ref deviceData,
            ref key,
            out var propertyType,
            IntPtr.Zero,
            0,
            out var requiredBytes,
            0);
        var error = succeeded ? 0u : (uint)Marshal.GetLastWin32Error();
        if (!succeeded && error != ErrorInsufficientBuffer)
        {
            details[name] = null;
            availability[name] = MapError(error);
            return;
        }

        if (requiredBytes == 0)
        {
            details[name] = null;
            availability[name] = InventoryFieldAvailability.Missing;
            return;
        }

        if (requiredBytes > MaxPropertyBytes)
        {
            details[name] = null;
            availability[name] = InventoryFieldAvailability.Unavailable;
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)requiredBytes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.SetupDiGetDevicePropertyW(
                    deviceSet,
                    ref deviceData,
                    ref key,
                    out propertyType,
                    buffer,
                    requiredBytes,
                    out requiredBytes,
                    0))
            {
                details[name] = null;
                availability[name] = MapError((uint)Marshal.GetLastWin32Error());
                return;
            }

            if (propertyType != expectedType)
            {
                details[name] = null;
                availability[name] = InventoryFieldAvailability.Unavailable;
                return;
            }

            if (expectedType == DevicePropertyTypeString)
            {
                if (requiredBytes % sizeof(char) != 0)
                {
                    details[name] = null;
                    availability[name] = InventoryFieldAvailability.Unavailable;
                    return;
                }

                var bytes = new byte[(int)requiredBytes];
                Marshal.Copy(buffer, bytes, 0, (int)requiredBytes);
                var value = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                details[name] = string.IsNullOrWhiteSpace(value) ? null : value;
                availability[name] = details[name] is null
                    ? InventoryFieldAvailability.Missing
                    : InventoryFieldAvailability.Available;
                return;
            }

            if (requiredBytes != sizeof(long))
            {
                details[name] = null;
                availability[name] = InventoryFieldAvailability.Unavailable;
                return;
            }

            var fileTimeBuffer = new byte[sizeof(long)];
            Marshal.Copy(buffer, fileTimeBuffer, 0, fileTimeBuffer.Length);
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(fileTimeBuffer);
            try
            {
                details[name] = DateTime.FromFileTimeUtc(fileTime).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                availability[name] = InventoryFieldAvailability.Available;
            }
            catch (ArgumentOutOfRangeException)
            {
                details[name] = null;
                availability[name] = InventoryFieldAvailability.Unavailable;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PropertyValue ReadInstanceId(
        SafeDeviceInfoSetHandle deviceSet,
        ref SpDeviceInfoData deviceData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var succeeded = NativeMethods.SetupDiGetDeviceInstanceIdW(
            deviceSet,
            ref deviceData,
            null,
            0,
            out var requiredCharacters);
        var error = succeeded ? 0u : (uint)Marshal.GetLastWin32Error();
        if (!succeeded && error != ErrorInsufficientBuffer)
        {
            return new PropertyValue(null, MapError(error));
        }

        if (requiredCharacters == 0)
        {
            return new PropertyValue(null, InventoryFieldAvailability.Missing);
        }

        if (requiredCharacters > MaxInstanceIdCharacters)
        {
            return new PropertyValue(null, InventoryFieldAvailability.Unavailable);
        }

        var buffer = new char[requiredCharacters];
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.SetupDiGetDeviceInstanceIdW(
                deviceSet,
                ref deviceData,
                buffer,
                requiredCharacters,
                out _))
        {
            return new PropertyValue(null, MapError((uint)Marshal.GetLastWin32Error()));
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return new PropertyValue(
            new string(buffer, 0, terminator < 0 ? buffer.Length : terminator),
            InventoryFieldAvailability.Available);
    }

    private static void ReadRegistryProperty(
        SafeDeviceInfoSetHandle deviceSet,
        ref SpDeviceInfoData deviceData,
        uint property,
        CancellationToken cancellationToken,
        IDictionary<string, string?> details,
        IDictionary<string, InventoryFieldAvailability> availability,
        string name)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var succeeded = NativeMethods.SetupDiGetDeviceRegistryPropertyW(
            deviceSet,
            ref deviceData,
            property,
            out var registryType,
            IntPtr.Zero,
            0,
            out var requiredBytes);
        var error = succeeded ? 0u : (uint)Marshal.GetLastWin32Error();
        if (!succeeded && error != ErrorInsufficientBuffer)
        {
            details[name] = null;
            availability[name] = MapError(error);
            return;
        }

        if (requiredBytes == 0)
        {
            details[name] = null;
            availability[name] = InventoryFieldAvailability.Missing;
            return;
        }

        if (requiredBytes > MaxPropertyBytes || requiredBytes > int.MaxValue)
        {
            details[name] = null;
            availability[name] = InventoryFieldAvailability.Unavailable;
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)requiredBytes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(
                    deviceSet,
                    ref deviceData,
                    property,
                    out registryType,
                    buffer,
                    requiredBytes,
                    out requiredBytes))
            {
                details[name] = null;
                availability[name] = MapError((uint)Marshal.GetLastWin32Error());
                return;
            }

            var bytes = new byte[requiredBytes];
            Marshal.Copy(buffer, bytes, 0, (int)requiredBytes);
            if (registryType is not 1 and not 7 || bytes.Length % 2 != 0)
            {
                details[name] = null;
                availability[name] = InventoryFieldAvailability.Unavailable;
                return;
            }

            var decoded = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            details[name] = registryType == 7
                ? string.Join(Environment.NewLine, decoded.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                : decoded;
            availability[name] = string.IsNullOrWhiteSpace(details[name])
                ? InventoryFieldAvailability.Missing
                : InventoryFieldAvailability.Available;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DeviceStatus ReadDeviceStatus(string instanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var locateResult = NativeMethods.CM_Locate_DevNodeW(out var deviceInstance, instanceId, 0);
        if (locateResult != ConfigRetSuccess)
        {
            return new DeviceStatus(null, null, MapConfigRet(locateResult), null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var statusResult = NativeMethods.CM_Get_DevNode_Status(
            out var statusFlags,
            out var problemCode,
            deviceInstance,
            0);
        if (statusResult != ConfigRetSuccess)
        {
            return new DeviceStatus(null, null, MapConfigRet(statusResult), null);
        }

        var hasProblem = (statusFlags & DeviceNodeHasProblem) != 0;
        var normalizedProblem = hasProblem ? problemCode : 0u;
        var statusText = normalizedProblem switch
        {
            ProblemDisabled => "Disabled",
            ProblemFailedInstall => "Driver installation failed (problem code 28)",
            > 0 => $"Device reports problem code {normalizedProblem}",
            _ when (statusFlags & DeviceNodeStarted) != 0 => "Started",
            _ => "Present; not started"
        };
        return new DeviceStatus(
            statusFlags,
            normalizedProblem,
            InventoryFieldAvailability.Available,
            statusText);
    }

    private static InventoryFieldAvailability MapConfigRet(uint configRet) =>
        NativeMethods.CM_MapCrToWin32Err(configRet, 0) switch
        {
            ErrorAccessDenied => InventoryFieldAvailability.Inaccessible,
            ErrorNotSupported or 120 => InventoryFieldAvailability.Unsupported,
            _ => InventoryFieldAvailability.Unavailable
        };

    private static InventoryFieldAvailability MapError(uint error) =>
        error switch
        {
            ErrorAccessDenied => InventoryFieldAvailability.Inaccessible,
            ErrorInvalidData or ErrorNotFound => InventoryFieldAvailability.Missing,
            ErrorNotSupported or 120 => InventoryFieldAvailability.Unsupported,
            _ => InventoryFieldAvailability.Unavailable
        };

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "Device name unavailable";

    private sealed record PropertyValue(string? Value, InventoryFieldAvailability Availability);

    private sealed record DeviceStatus(
        uint? StatusFlags,
        uint? ProblemCode,
        InventoryFieldAvailability Availability,
        string? DisplayStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    private sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSetHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() =>
            NativeMethods.SetupDiDestroyDeviceInfoList(handle);
    }

    private static class NativeMethods
    {
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeDeviceInfoSetHandle SetupDiGetClassDevsW(
            IntPtr classGuid,
            string? enumerator,
            IntPtr parentWindow,
            uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(
            SafeDeviceInfoSetHandle deviceInfoSet,
            uint memberIndex,
            ref SpDeviceInfoData deviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(
            SafeDeviceInfoSetHandle deviceInfoSet,
            ref SpDeviceInfoData deviceInfoData,
            [Out] char[]? deviceInstanceId,
            uint deviceInstanceIdSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryPropertyW(
            SafeDeviceInfoSetHandle deviceInfoSet,
            ref SpDeviceInfoData deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            IntPtr propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDevicePropertyW(
            SafeDeviceInfoSetHandle deviceInfoSet,
            ref SpDeviceInfoData deviceInfoData,
            ref DevPropKey propertyKey,
            out uint propertyType,
            IntPtr propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize,
            uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint CM_Locate_DevNodeW(
            out uint deviceInstance,
            string deviceId,
            uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status", ExactSpelling = true)]
        internal static extern uint CM_Get_DevNode_Status(
            out uint status,
            out uint problem,
            uint deviceInstance,
            uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_MapCrToWin32Err", ExactSpelling = true)]
        internal static extern uint CM_MapCrToWin32Err(uint configRet, uint defaultError);
    }
}
