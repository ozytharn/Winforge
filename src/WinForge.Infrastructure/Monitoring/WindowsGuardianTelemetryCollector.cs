using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Management;
using WinForge.Application.Monitoring;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;
using WmiEnumerationOptions = System.Management.EnumerationOptions;

namespace WinForge.Infrastructure.Monitoring;

public sealed class WindowsGuardianTelemetryCollector : IGuardianTelemetryCollector
{
    private const int MaximumProcessEntries = 128;
    private const int MaximumProcessObservations = 20;
    private readonly object _cpuSync = new();
    private readonly object _stateSync = new();
    private (ulong Idle, ulong Kernel, ulong User, long Timestamp)? _previousCpuTimes;
    private readonly Dictionary<string, (long Sent, long Received)> _previousNetwork = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TimeSpan CpuTime, long Timestamp)> _previousProcesses = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GuardianGpuAdapter>? _gpuAdapters;
    private static readonly TimeSpan MinimumCpuSampleInterval = TimeSpan.FromMilliseconds(100);

    public Task<GuardianTelemetrySnapshot> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private GuardianTelemetrySnapshot Collect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collectedAt = DateTimeOffset.UtcNow;
        var cpu = ReadCpuUsage();
        cancellationToken.ThrowIfCancellationRequested();
        var memory = ReadMemory();
        cancellationToken.ThrowIfCancellationRequested();
        var storage = ReadStorage();
        cancellationToken.ThrowIfCancellationRequested();
        var network = ReadNetwork(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var processes = ReadProcesses(collectedAt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var gpus = _gpuAdapters ??= ReadGpuAdapters(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var uptime = new TelemetryReading<TimeSpan>(
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            InventoryFieldAvailability.Available,
            "Environment.TickCount64");

        return new GuardianTelemetrySnapshot(
            collectedAt,
            cpu,
            memory.Available,
            memory.Total,
            uptime,
            storage.Enumeration,
            storage.Volumes)
        {
            NetworkInterfaces = network,
            Processes = processes.Observations,
            ProcessEnumeration = processes.Enumeration,
            GpuAdapters = gpus
        };
    }

    private TelemetryReading<double> ReadCpuUsage()
    {
        try
        {
            var sampledAt = Stopwatch.GetTimestamp();
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var current = (idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64(), sampledAt);
            lock (_cpuSync)
            {
                var previous = _previousCpuTimes;
                _previousCpuTimes = current;

                if (previous is null)
                {
                    return new TelemetryReading<double>(
                        null,
                        InventoryFieldAvailability.Unavailable,
                        "GetSystemTimes",
                        "The first sample establishes a CPU baseline; usage is reported on the next sample.");
                }

                var interval = Stopwatch.GetElapsedTime(previous.Value.Timestamp, current.Item4);
                if (interval < MinimumCpuSampleInterval)
                {
                    return new TelemetryReading<double>(
                        null,
                        InventoryFieldAvailability.Unavailable,
                        "GetSystemTimes",
                        $"The CPU sample interval was shorter than {MinimumCpuSampleInterval.TotalMilliseconds:F0} ms.",
                        interval);
                }

                var totalPrevious = previous.Value.Kernel + previous.Value.User;
                var totalCurrent = current.Item2 + current.Item3;
                if (current.Item1 < previous.Value.Idle ||
                    totalCurrent <= totalPrevious ||
                    current.Item1 - previous.Value.Idle > totalCurrent - totalPrevious)
                {
                    return new TelemetryReading<double>(
                        null,
                        InventoryFieldAvailability.Unavailable,
                        "GetSystemTimes",
                        "CPU counters changed unexpectedly; a later sample may be usable.",
                        interval);
                }

                var totalDelta = totalCurrent - totalPrevious;
                var idleDelta = current.Item1 - previous.Value.Idle;
                var usage = (double)(totalDelta - idleDelta) / totalDelta * 100;
                return new TelemetryReading<double>(
                    Math.Clamp(usage, 0, 100),
                    InventoryFieldAvailability.Available,
                    "GetSystemTimes",
                    Interval: interval);
            }
        }
        catch (Win32Exception exception)
        {
            return new TelemetryReading<double>(
                null,
                InventoryFieldAvailability.Inaccessible,
                "GetSystemTimes",
                exception.Message);
        }
        catch (DllNotFoundException exception)
        {
            return new TelemetryReading<double>(
                null,
                InventoryFieldAvailability.Unsupported,
                "GetSystemTimes",
                exception.Message);
        }
        catch (EntryPointNotFoundException exception)
        {
            return new TelemetryReading<double>(
                null,
                InventoryFieldAvailability.Unsupported,
                "GetSystemTimes",
                exception.Message);
        }
    }

    private static (TelemetryReading<ulong> Available, TelemetryReading<ulong> Total) ReadMemory()
    {
        var source = "GlobalMemoryStatusEx";
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return (
                new TelemetryReading<ulong>(status.AvailablePhysical, InventoryFieldAvailability.Available, source),
                new TelemetryReading<ulong>(status.TotalPhysical, InventoryFieldAvailability.Available, source));
        }
        catch (Win32Exception exception)
        {
            return (
                new TelemetryReading<ulong>(null, InventoryFieldAvailability.Inaccessible, source, exception.Message),
                new TelemetryReading<ulong>(null, InventoryFieldAvailability.Inaccessible, source, exception.Message));
        }
        catch (DllNotFoundException exception)
        {
            return (
                new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, source, exception.Message),
                new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, source, exception.Message));
        }
        catch (EntryPointNotFoundException exception)
        {
            return (
                new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, source, exception.Message),
                new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, source, exception.Message));
        }
    }

    private static (TelemetryReading<int> Enumeration, IReadOnlyList<GuardianStorageVolume> Volumes) ReadStorage()
    {
        const string source = "System.IO.DriveInfo";
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).ToArray();
        }
        catch (IOException exception)
        {
            return (
                new TelemetryReading<int>(null, InventoryFieldAvailability.Inaccessible, source, exception.Message),
                []);
        }
        catch (UnauthorizedAccessException exception)
        {
            return (
                new TelemetryReading<int>(null, InventoryFieldAvailability.Inaccessible, source, exception.Message),
                []);
        }

        var volumes = new List<GuardianStorageVolume>(drives.Length);
        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady)
                {
                    volumes.Add(new GuardianStorageVolume(
                        drive.Name,
                        null,
                        null,
                        null,
                        InventoryFieldAvailability.Inaccessible,
                        source,
                        "The fixed volume is not ready or cannot be queried."));
                    continue;
                }

                volumes.Add(new GuardianStorageVolume(
                    drive.Name,
                    drive.VolumeLabel,
                    (ulong)drive.TotalSize,
                    (ulong)drive.AvailableFreeSpace,
                    InventoryFieldAvailability.Available,
                    source));
            }
            catch (IOException exception)
            {
                volumes.Add(InaccessibleVolume(drive.Name, source, exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                volumes.Add(InaccessibleVolume(drive.Name, source, exception.Message));
            }
            catch (Win32Exception exception)
            {
                volumes.Add(InaccessibleVolume(drive.Name, source, exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                volumes.Add(InaccessibleVolume(drive.Name, source, exception.Message));
            }
        }

        return (
            new TelemetryReading<int>(
                drives.Length,
                InventoryFieldAvailability.Available,
                source,
                drives.Length == 0 ? "No fixed volumes were reported." : null),
            volumes);
    }

    private static GuardianStorageVolume InaccessibleVolume(string name, string source, string limitation) =>
        new(name, null, null, null, InventoryFieldAvailability.Inaccessible, source, limitation);

    private IReadOnlyList<GuardianNetworkInterface> ReadNetwork(CancellationToken cancellationToken)
    {
        const string source = "NetworkInterface.GetIPv4Statistics delta";
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException exception)
        {
            return
            [
                new GuardianNetworkInterface(
                    "network:enumeration",
                    "Network interface enumeration",
                    "Unavailable",
                    Unavailable<double>(InventoryFieldAvailability.Inaccessible, source, exception.Message),
                    Unavailable<double>(InventoryFieldAvailability.Inaccessible, source, exception.Message))
            ];
        }

        var now = Stopwatch.GetTimestamp();
        var result = new List<GuardianNetworkInterface>(Math.Min(interfaces.Length, 64));
        lock (_stateSync)
        {
            var activeIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in interfaces.Take(64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = $"network:{adapter.Id}";
                activeIdentities.Add(identity);
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    result.Add(new GuardianNetworkInterface(
                        identity,
                        adapter.Name,
                        adapter.OperationalStatus.ToString(),
                        Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "The interface is not connected."),
                        Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "The interface is not connected.")));
                    _previousNetwork.Remove(identity);
                    continue;
                }

                try
                {
                    var statistics = adapter.GetIPv4Statistics();
                    var sent = statistics.BytesSent;
                    var received = statistics.BytesReceived;
                    TelemetryReading<double> sentRate;
                    TelemetryReading<double> receivedRate;
                    if (_previousNetwork.TryGetValue(identity, out var previous) &&
                        _previousNetwork.TryGetValue($"{identity}:timestamp", out var previousTimestamp))
                    {
                        var interval = Stopwatch.GetElapsedTime(previousTimestamp.Sent, now);
                        sentRate = CounterRate(sent, previous.Sent, interval, source);
                        receivedRate = CounterRate(received, previous.Received, interval, source);
                    }
                    else
                    {
                        sentRate = Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "A counter baseline is required before throughput can be calculated.");
                        receivedRate = Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "A counter baseline is required before throughput can be calculated.");
                    }

                    _previousNetwork[identity] = (sent, received);
                    _previousNetwork[$"{identity}:timestamp"] = (now, now);
                    result.Add(new GuardianNetworkInterface(identity, adapter.Name, adapter.OperationalStatus.ToString(), sentRate, receivedRate));
                }
                catch (NetworkInformationException exception)
                {
                    result.Add(new GuardianNetworkInterface(
                        identity,
                        adapter.Name,
                        "Unavailable",
                        Unavailable<double>(InventoryFieldAvailability.Inaccessible, source, exception.Message),
                        Unavailable<double>(InventoryFieldAvailability.Inaccessible, source, exception.Message)));
                }
            }

            foreach (var stale in _previousNetwork.Keys
                         .Where(key => !key.EndsWith(":timestamp", StringComparison.Ordinal) &&
                                       !activeIdentities.Contains(key))
                         .ToArray())
            {
                _previousNetwork.Remove(stale);
                _previousNetwork.Remove($"{stale}:timestamp");
            }
        }

        return result;
    }

    private (TelemetryReading<int> Enumeration, IReadOnlyList<GuardianProcessObservation> Observations) ReadProcesses(
        DateTimeOffset collectedAt,
        CancellationToken cancellationToken)
    {
        const string source = "System.Diagnostics.Process interval sample";
        var now = Stopwatch.GetTimestamp();
        var candidates = new List<(GuardianProcessObservation Observation, TimeSpan? CpuTime)>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Win32Exception exception)
        {
            return (Unavailable<int>(InventoryFieldAvailability.Inaccessible, source, exception.Message), []);
        }
        catch (InvalidOperationException exception)
        {
            return (Unavailable<int>(InventoryFieldAvailability.Unavailable, source, exception.Message), []);
        }

        var observedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in processes.Take(MaximumProcessEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pid = process.Id;
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    name = $"Process {pid}";
                }

                DateTimeOffset? startedAt = null;
                TimeSpan? cpuTime = null;
                ulong? workingSet = null;
                InventoryFieldAvailability workingSetAvailability;
                string? limitation = null;
                try
                {
                    startedAt = process.StartTime.ToUniversalTime();
                    cpuTime = process.TotalProcessorTime;
                    workingSet = checked((ulong)process.WorkingSet64);
                    workingSetAvailability = InventoryFieldAvailability.Available;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception or NotSupportedException or OverflowException)
                {
                    workingSetAvailability = exception is Win32Exception
                        ? InventoryFieldAvailability.Inaccessible
                        : InventoryFieldAvailability.Unavailable;
                    limitation = exception.Message;
                    try
                    {
                        workingSet = checked((ulong)process.WorkingSet64);
                        workingSetAvailability = InventoryFieldAvailability.Available;
                    }
                    catch (Exception nested) when (
                        nested is InvalidOperationException or Win32Exception or NotSupportedException or OverflowException)
                    {
                        limitation = nested.Message;
                    }
                }

                var identity = startedAt is { } start
                    ? $"process:{pid}:{start.UtcTicks}"
                    : $"process:{pid}:unidentified:{collectedAt.UtcTicks}";
                observedIdentities.Add(identity);
                var cpu = startedAt is not null && cpuTime is { } currentCpu
                    ? GetProcessCpu(identity, currentCpu, now, source)
                    : Unavailable<double>(
                        InventoryFieldAvailability.Inaccessible,
                        source,
                        "Process start time or CPU time was inaccessible; this observation is not associated across samples.");
                var memory = workingSet is { } bytes
                    ? new TelemetryReading<ulong>(bytes, workingSetAvailability, source, limitation)
                    : new TelemetryReading<ulong>(null, workingSetAvailability, source, limitation ?? "Working set was unavailable.");
                candidates.Add((
                    new GuardianProcessObservation(identity, pid, name, startedAt, cpu, memory),
                    cpuTime));
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        lock (_stateSync)
        {
            foreach (var stale in _previousProcesses.Keys.Where(key => !observedIdentities.Contains(key)).ToArray())
            {
                _previousProcesses.Remove(stale);
            }
        }

        var observations = candidates
            .OrderByDescending(candidate => candidate.Observation.WorkingSetBytes.Value ?? 0)
            .Take(MaximumProcessObservations)
            .Select(candidate => candidate.Observation)
            .ToArray();
        return (
            new TelemetryReading<int>(
                candidates.Count,
                InventoryFieldAvailability.Available,
                source,
                $"Examined at most {MaximumProcessEntries} processes; retained at most {MaximumProcessObservations} observations."),
            observations);
    }

    private TelemetryReading<double> GetProcessCpu(
        string identity,
        TimeSpan currentCpu,
        long sampledAt,
        string source)
    {
        lock (_stateSync)
        {
            var prior = _previousProcesses.GetValueOrDefault(identity);
            _previousProcesses[identity] = (currentCpu, sampledAt);
            if (prior.Timestamp == 0 || currentCpu < prior.CpuTime)
            {
                return Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "A per-process CPU baseline is not yet available.");
            }

            var interval = Stopwatch.GetElapsedTime(prior.Timestamp, sampledAt);
            if (interval < MinimumCpuSampleInterval)
            {
                return Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "The process CPU sampling interval was too short.");
            }

            var percent = (currentCpu - prior.CpuTime).TotalSeconds /
                          (interval.TotalSeconds * Environment.ProcessorCount) * 100;
            return new TelemetryReading<double>(Math.Clamp(percent, 0, 100), InventoryFieldAvailability.Available, source, Interval: interval);
        }
    }

    private static IReadOnlyList<GuardianGpuAdapter> ReadGpuAdapters(CancellationToken cancellationToken)
    {
        const string source = "Win32_VideoController";
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
                new ObjectQuery("SELECT Name, PNPDeviceID FROM Win32_VideoController"),
                options);
            using var results = searcher.Get();
            var adapters = new List<GuardianGpuAdapter>();
            foreach (ManagementObject item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (item)
                {
                    var name = item["Name"] as string;
                    var pnpId = item["PNPDeviceID"] as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var hasPnpIdentity = !string.IsNullOrWhiteSpace(pnpId);
                    var identity = hasPnpIdentity ? $"gpu:{pnpId}" : $"gpu-name:{name}:{adapters.Count}";
                    const string limitation = "Win32_VideoController identifies adapters but does not provide a reliable portable live utilization, temperature, or dedicated-memory measurement.";
                    adapters.Add(new GuardianGpuAdapter(
                        identity,
                        name,
                        source,
                        InventoryFieldAvailability.Available,
                        Unavailable<double>(InventoryFieldAvailability.Unsupported, source, limitation),
                        new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, source, limitation),
                        Unavailable<double>(InventoryFieldAvailability.Unsupported, source, limitation),
                        hasPnpIdentity
                            ? limitation
                            : $"{limitation} PnP identity was not exposed; the name and enumeration position may not remain stable."));
                }
            }

            return adapters;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return
            [
                new GuardianGpuAdapter(
                    "gpu:enumeration",
                    "GPU adapter enumeration",
                    source,
                    exception is UnauthorizedAccessException ? InventoryFieldAvailability.Inaccessible : InventoryFieldAvailability.Unavailable,
                    Unavailable<double>(InventoryFieldAvailability.Unsupported, source, exception.Message),
                    new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, source, exception.Message),
                    Unavailable<double>(InventoryFieldAvailability.Unsupported, source, exception.Message),
                    exception.Message)
            ];
        }
    }

    private static TelemetryReading<double> CounterRate(long current, long previous, TimeSpan interval, string source)
    {
        if (interval <= TimeSpan.Zero || current < previous)
        {
            return Unavailable<double>(InventoryFieldAvailability.Unavailable, source, "Counter reset or invalid interval; no throughput rate was inferred.");
        }

        return new TelemetryReading<double>(
            (current - previous) / interval.TotalSeconds,
            InventoryFieldAvailability.Available,
            source,
            Interval: interval);
    }

    private static TelemetryReading<T> Unavailable<T>(
        InventoryFieldAvailability availability,
        string source,
        string limitation)
        where T : struct =>
        new(null, availability, source, limitation);

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
