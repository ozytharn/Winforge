using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.Infrastructure.Collectors;

public sealed class AudioEndpointInventoryCollector : IInventoryCollector
{
    private const uint EndpointStateAll = 0x0F;
    private const uint EndpointStateActive = 0x01;
    private const uint EndpointStateDisabled = 0x02;
    private const uint EndpointStateNotPresent = 0x04;
    private const uint EndpointStateUnplugged = 0x08;
    private const ushort VariantTypeBstr = 8;
    private const ushort VariantTypeLpwstr = 31;
    private const int HResultNotFound = unchecked((int)0x80070490);
    private static readonly PropertyKey FriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    private readonly ILogger<AudioEndpointInventoryCollector> _logger;

    public AudioEndpointInventoryCollector(ILogger<AudioEndpointInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "audio-endpoints";

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
                [new CollectorIssue(CollectorId, "unsupported_platform", "Core Audio endpoint enumeration is only available on Windows.")]);
        }

        IMMDeviceEnumerator? enumerator = null;
        var stage = "activating Core Audio";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            stage = "querying default endpoints";
            var defaultRender = GetDefaultId(enumerator, DataFlow.Render, issues);
            var defaultCapture = GetDefaultId(enumerator, DataFlow.Capture, issues);
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage = $"enumerating {flow.ToString().ToLowerInvariant()} endpoints";
                IMMDeviceCollection? collection = null;
                try
                {
                    ThrowOnFailure(enumerator.EnumAudioEndpoints(flow, EndpointStateAll, out collection));
                    ThrowOnFailure(collection.GetCount(out var count));
                    for (uint index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stage = $"reading {flow.ToString().ToLowerInvariant()} endpoint {index}";
                        IMMDevice? device = null;
                        IPropertyStore? properties = null;
                        try
                        {
                            ThrowOnFailure(collection.Item(index, out device));
                            var idResult = device.GetId(out var idPointer);
                            string id;
                            try
                            {
                                ThrowOnFailure(idResult);
                                id = Marshal.PtrToStringUni(idPointer) ?? string.Empty;
                            }
                            finally
                            {
                                if (idPointer != IntPtr.Zero)
                                {
                                    Marshal.FreeCoTaskMem(idPointer);
                                }
                            }

                            ThrowOnFailure(device.GetState(out var state));
                            string? friendlyName = null;
                            try
                            {
                                ThrowOnFailure(device.OpenPropertyStore(0, out properties));
                                friendlyName = ReadFriendlyName(properties);
                            }
                            catch (COMException exception)
                            {
                                issues.Add(new CollectorIssue(
                                    CollectorId,
                                    "endpoint_name_unavailable",
                                    $"Windows could not read an audio endpoint name (HRESULT 0x{exception.HResult:X8})."));
                            }

                            if (string.IsNullOrWhiteSpace(id))
                            {
                                issues.Add(new CollectorIssue(
                                    CollectorId,
                                    "endpoint_identity_unavailable",
                                    "A Core Audio endpoint did not expose a usable endpoint ID."));
                                continue;
                            }

                            var details = new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                ["endpointId"] = id,
                                ["dataFlow"] = flow == DataFlow.Render ? "Render" : "Capture",
                                ["state"] = FormatState(state),
                                ["friendlyName"] = friendlyName,
                                ["isDefaultRender"] = string.Equals(id, defaultRender, StringComparison.OrdinalIgnoreCase).ToString(),
                                ["isDefaultCapture"] = string.Equals(id, defaultCapture, StringComparison.OrdinalIgnoreCase).ToString()
                            };
                            components.Add(new InventoryComponent(
                                InventoryComponentType.AudioEndpoint,
                                id.Trim().ToUpperInvariant(),
                                friendlyName ?? id,
                                "Core Audio MMDevice API",
                                2,
                                DateTimeOffset.UtcNow,
                                details)
                            {
                                Category = "Audio endpoints",
                                FieldAvailability = new Dictionary<string, InventoryFieldAvailability>(StringComparer.Ordinal)
                                {
                                    ["endpointId"] = InventoryFieldAvailability.Available,
                                    ["dataFlow"] = InventoryFieldAvailability.Available,
                                    ["state"] = InventoryFieldAvailability.Available,
                                    ["friendlyName"] = string.IsNullOrWhiteSpace(friendlyName)
                                        ? InventoryFieldAvailability.Missing
                                        : InventoryFieldAvailability.Available,
                                    ["isDefaultRender"] = defaultRender is null
                                        ? InventoryFieldAvailability.Unavailable
                                        : InventoryFieldAvailability.Available,
                                    ["isDefaultCapture"] = defaultCapture is null
                                        ? InventoryFieldAvailability.Unavailable
                                        : InventoryFieldAvailability.Available
                                }
                            });
                        }
                        catch (COMException exception)
                        {
                            issues.Add(new CollectorIssue(
                                CollectorId,
                                "endpoint_metadata_unavailable",
                                $"Windows could not read an audio endpoint's details (HRESULT 0x{exception.HResult:X8})."));
                        }
                        finally
                        {
                            if (properties is not null)
                            {
                                Marshal.FinalReleaseComObject(properties);
                            }

                            if (device is not null)
                            {
                                Marshal.FinalReleaseComObject(device);
                            }
                        }
                    }
                }
                finally
                {
                    if (collection is not null)
                    {
                        Marshal.FinalReleaseComObject(collection);
                    }
                }
            }

            if (components.Count == 0)
            {
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "no_endpoints_reported",
                    "Windows reported no audio endpoints in the enumerated endpoint states."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException exception)
        {
            _logger.LogWarning(exception, "Core Audio endpoint enumeration failed.");
            issues.Add(new CollectorIssue(
                CollectorId,
                "audio_endpoint_query_failed",
                $"Windows audio endpoint enumeration failed while {stage} (HRESULT 0x{exception.HResult:X8})."));
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private string? GetDefaultId(
        IMMDeviceEnumerator enumerator,
        DataFlow flow,
        ICollection<CollectorIssue> issues)
    {
        IMMDevice? device = null;
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia, out device);
            if (result == HResultNotFound)
            {
                return null;
            }

            if (result < 0)
            {
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "default_audio_endpoint_unavailable",
                    $"Windows could not identify the default {flow.ToString().ToLowerInvariant()} endpoint (HRESULT 0x{result:X8})."));
                return null;
            }

            var idResult = device.GetId(out var idPointer);
            try
            {
                if (idResult < 0)
                {
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "default_audio_endpoint_unavailable",
                        $"Windows could not read the default {flow.ToString().ToLowerInvariant()} endpoint ID (HRESULT 0x{idResult:X8})."));
                    return null;
                }

                return Marshal.PtrToStringUni(idPointer);
            }
            finally
            {
                if (idPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(idPointer);
                }
            }
        }
        finally
        {
            if (device is not null)
            {
                Marshal.FinalReleaseComObject(device);
            }
        }
    }

    private static string? ReadFriendlyName(IPropertyStore properties)
    {
        var key = FriendlyNameKey;
        var result = properties.GetValue(ref key, out var value);
        try
        {
            if (result == HResultNotFound)
            {
                return null;
            }

            ThrowOnFailure(result);
            return value.Type is VariantTypeLpwstr or VariantTypeBstr
                ? Marshal.PtrToStringUni(value.StringPointer)
                : null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    private static string FormatState(uint state)
    {
        var values = new List<string>();
        if ((state & EndpointStateActive) != 0) values.Add("Active");
        if ((state & EndpointStateDisabled) != 0) values.Add("Disabled");
        if ((state & EndpointStateNotPresent) != 0) values.Add("Not present");
        if ((state & EndpointStateUnplugged) != 0) values.Add("Unplugged");
        return values.Count == 0 ? $"Unknown ({state})" : string.Join(", ", values);
    }

    private static void ThrowOnFailure(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private enum DataFlow
    {
        Render,
        Capture
    }

    private enum Role
    {
        Console,
        Multimedia,
        Communications
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public IntPtr StringPointer;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern int PropVariantClear(ref PropVariant variant);
    }
}
