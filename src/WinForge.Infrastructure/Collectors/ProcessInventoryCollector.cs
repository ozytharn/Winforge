using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WinForge.Application.Inventory;
using WinForge.Domain.Inventory;

namespace WinForge.Infrastructure.Collectors;

public sealed class ProcessInventoryCollector : IInventoryCollector
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int ErrorNoMoreFiles = 18;
    private const ushort MachineUnknown = 0;
    private const ushort MachineI386 = 0x014c;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;
    private readonly ILogger<ProcessInventoryCollector> _logger;

    public ProcessInventoryCollector(ILogger<ProcessInventoryCollector> logger)
    {
        _logger = logger;
    }

    public string CollectorId => "process-inventory";

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
                [new CollectorIssue(CollectorId, "unsupported_platform", "Process inventory is only available on Windows.")]);
        }

        var parentProcessIds = ReadParentProcessIds(cancellationToken, issues);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Windows process enumeration failed.");
            return new CollectorResult(
                CollectorId,
                components,
                [new CollectorIssue(CollectorId, "process_enumeration_failed", "Windows process enumeration failed.")]);
        }

        using (var processDisposer = new ProcessDisposer(processes))
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    components.Add(ReadProcess(process, parentProcessIds));
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    _logger.LogDebug(
                        exception,
                        "Process {ProcessId} exited or denied metadata during inventory.",
                        SafeProcessId(process));
                    issues.Add(new CollectorIssue(
                        CollectorId,
                        "process_changed_or_inaccessible",
                        "A process exited or denied access while its metadata was being collected."));
                }
            }
        }

        if (components.Count == 0 && issues.Count == 0)
        {
            issues.Add(new CollectorIssue(
                CollectorId,
                "no_processes_reported",
                "Windows reported no readable process records."));
        }

        return new CollectorResult(CollectorId, components, issues);
    }

    private static InventoryComponent ReadProcess(
        Process process,
        IReadOnlyDictionary<int, int> parentProcessIds)
    {
        var processId = process.Id;
        var processName = process.ProcessName;
        DateTime? startTime = null;
        long? cpuTime = null;
        long? workingSet = null;
        long? privateMemory = null;
        string? executablePath = null;
        var pathAvailability = InventoryFieldAvailability.Inaccessible;
        var startAvailability = InventoryFieldAvailability.Inaccessible;
        var cpuAvailability = InventoryFieldAvailability.Inaccessible;
        var memoryAvailability = InventoryFieldAvailability.Inaccessible;
        var privateMemoryAvailability = InventoryFieldAvailability.Inaccessible;
        try
        {
            startTime = process.StartTime.ToUniversalTime();
            startAvailability = InventoryFieldAvailability.Available;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            cpuTime = (long)process.TotalProcessorTime.TotalMilliseconds;
            cpuAvailability = InventoryFieldAvailability.Available;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            workingSet = process.WorkingSet64;
            memoryAvailability = InventoryFieldAvailability.Available;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            privateMemory = process.PrivateMemorySize64;
            privateMemoryAvailability = InventoryFieldAvailability.Available;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            executablePath = process.MainModule?.FileName;
            pathAvailability = string.IsNullOrWhiteSpace(executablePath)
                ? InventoryFieldAvailability.Missing
                : InventoryFieldAvailability.Available;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }

        var parentProcessId = parentProcessIds.TryGetValue(processId, out var parentId)
            ? parentId
            : (int?)null;
        var architecture = ReadArchitecture(process, out var architectureAvailability);
        var elapsedMilliseconds = startTime is { } started
            ? Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds)
            : 0;
        var averageCpuSinceStart = cpuTime is { } cpu && elapsedMilliseconds > 0
            ? (cpu / elapsedMilliseconds / Math.Max(1, Environment.ProcessorCount) * 100)
                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["processName"] = processName,
            ["processId"] = processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["executablePath"] = executablePath,
            ["parentProcessId"] = parentProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["workingSetBytes"] = workingSet?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["privateMemoryBytes"] = privateMemory?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cpuTimeMilliseconds"] = cpuTime?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["averageCpuPercentSinceStart"] = averageCpuSinceStart,
            ["processArchitecture"] = architecture,
            ["startTimeUtc"] = startTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["associatedApplicationIdentity"] = null,
            ["commandLine"] = null
        };
        var availability = new Dictionary<string, InventoryFieldAvailability>(StringComparer.Ordinal)
        {
            ["processName"] = InventoryFieldAvailability.Available,
            ["processId"] = InventoryFieldAvailability.Available,
            ["executablePath"] = pathAvailability,
            ["parentProcessId"] = parentProcessId.HasValue
                ? InventoryFieldAvailability.Available
                : InventoryFieldAvailability.Missing,
            ["workingSetBytes"] = memoryAvailability,
            ["privateMemoryBytes"] = privateMemoryAvailability,
            ["cpuTimeMilliseconds"] = cpuAvailability,
            ["averageCpuPercentSinceStart"] = cpuAvailability == InventoryFieldAvailability.Available && averageCpuSinceStart is not null
                ? InventoryFieldAvailability.Available
                : InventoryFieldAvailability.Unavailable,
            ["processArchitecture"] = architectureAvailability,
            ["startTimeUtc"] = startAvailability,
            ["associatedApplicationIdentity"] = InventoryFieldAvailability.Unknown,
            ["commandLine"] = InventoryFieldAvailability.Unsupported
        };

        return new InventoryComponent(
            InventoryComponentType.Process,
            $"process:{processId}:{(startTime?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}",
            processName,
            "Windows Process API",
            2,
            DateTimeOffset.UtcNow,
            details)
        {
            Category = "Processes",
            FieldAvailability = availability
        };
    }

    private static string? ReadArchitecture(Process process, out InventoryFieldAvailability availability)
    {
        try
        {
            if (!NativeMethods.IsWow64Process2(
                    process.SafeHandle,
                    out var processMachine,
                    out var nativeMachine))
            {
                availability = MapWin32Availability(Marshal.GetLastWin32Error());
                return null;
            }

            var machine = processMachine == MachineUnknown ? nativeMachine : processMachine;
            availability = InventoryFieldAvailability.Available;
            return machine switch
            {
                MachineI386 => "x86",
                MachineAmd64 => "x64",
                MachineArm64 => "ARM64",
                _ => $"Unknown (0x{machine:X4})"
            };
        }
        catch (EntryPointNotFoundException)
        {
            availability = InventoryFieldAvailability.Unsupported;
            return null;
        }
        catch (Win32Exception exception)
        {
            availability = MapWin32Availability(exception.NativeErrorCode);
            return null;
        }
        catch (InvalidOperationException)
        {
            availability = InventoryFieldAvailability.Unavailable;
            return null;
        }
    }

    private static InventoryFieldAvailability MapWin32Availability(int errorCode) =>
        errorCode switch
        {
            5 or 1314 => InventoryFieldAvailability.Inaccessible,
            50 or 120 => InventoryFieldAvailability.Unsupported,
            _ => InventoryFieldAvailability.Unavailable
        };

    private Dictionary<int, int> ReadParentProcessIds(
        CancellationToken cancellationToken,
        ICollection<CollectorIssue> issues)
    {
        var parentProcessIds = new Dictionary<int, int>();
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("Toolhelp process snapshot failed with Win32 error {ErrorCode}.", error);
            issues.Add(new CollectorIssue(
                CollectorId,
                "parent_process_ids_unavailable",
                $"Parent process IDs are unavailable (Win32 error {error})."));
            return parentProcessIds;
        }

        var entry = new ProcessEntry32
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry32>()
        };
        if (!NativeMethods.Process32FirstW(snapshot, ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNoMoreFiles)
            {
                _logger.LogWarning("Toolhelp process snapshot could not be read (Win32 error {ErrorCode}).", error);
                issues.Add(new CollectorIssue(
                    CollectorId,
                    "parent_process_ids_unavailable",
                    $"Parent process IDs are unavailable (Win32 error {error})."));
            }

            return parentProcessIds;
        }

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            parentProcessIds[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (NativeMethods.Process32NextW(snapshot, ref entry));

        var finalError = Marshal.GetLastWin32Error();
        if (finalError != ErrorNoMoreFiles)
        {
            issues.Add(new CollectorIssue(
                CollectorId,
                "parent_process_ids_incomplete",
                $"The parent-process snapshot ended before all process IDs were read (Win32 error {finalError})."));
        }

        return parentProcessIds;
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private sealed class ProcessDisposer(Process[] processes) : IDisposable
    {
        public void Dispose()
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeSnapshotHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", ExactSpelling = true, SetLastError = true)]
        internal static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process2(
            SafeHandle process,
            out ushort processMachine,
            out ushort nativeMachine);
    }
}
