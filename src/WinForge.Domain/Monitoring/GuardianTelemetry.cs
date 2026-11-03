using WinForge.Domain.Inventory;

namespace WinForge.Domain.Monitoring;

public sealed record TelemetryReading<T>(
    T? Value,
    InventoryFieldAvailability Availability,
    string Source,
    string? Limitation = null,
    TimeSpan? Interval = null)
    where T : struct;

public sealed record GuardianStorageVolume(
    string Name,
    string? Label,
    ulong? TotalBytes,
    ulong? FreeBytes,
    InventoryFieldAvailability Availability,
    string Source,
    string? Limitation = null);

public sealed record GuardianNetworkInterface(
    string Identity,
    string Name,
    string Status,
    TelemetryReading<double> SentBytesPerSecond,
    TelemetryReading<double> ReceivedBytesPerSecond);

public sealed record GuardianProcessObservation(
    string Identity,
    int ProcessId,
    string Name,
    DateTimeOffset? StartedAt,
    TelemetryReading<double> CpuPercent,
    TelemetryReading<ulong> WorkingSetBytes);

public sealed record GuardianGpuAdapter(
    string Identity,
    string Name,
    string Source,
    InventoryFieldAvailability Availability,
    TelemetryReading<double> UtilizationPercent,
    TelemetryReading<ulong> DedicatedMemoryBytes,
    TelemetryReading<double> TemperatureCelsius,
    string? Limitation = null);

public sealed record GuardianTelemetrySnapshot(
    DateTimeOffset CollectedAt,
    TelemetryReading<double> CpuUsagePercent,
    TelemetryReading<ulong> AvailableMemoryBytes,
    TelemetryReading<ulong> TotalMemoryBytes,
    TelemetryReading<TimeSpan> SystemUptime,
    TelemetryReading<int> StorageEnumeration,
    IReadOnlyList<GuardianStorageVolume> StorageVolumes)
{
    public IReadOnlyList<GuardianNetworkInterface> NetworkInterfaces { get; init; } = [];

    public IReadOnlyList<GuardianProcessObservation> Processes { get; init; } = [];

    public TelemetryReading<int> ProcessEnumeration { get; init; } =
        new(null, InventoryFieldAvailability.Unavailable, "System.Diagnostics.Process", "Process enumeration was not collected.");

    public IReadOnlyList<GuardianGpuAdapter> GpuAdapters { get; init; } = [];
}

public sealed record GuardianRetentionPolicy(TimeSpan MaximumAge, int MaximumSamples)
{
    public static GuardianRetentionPolicy Default { get; } = new(TimeSpan.FromDays(30), 25_000);

    public void Validate()
    {
        if (MaximumAge < TimeSpan.FromDays(1) || MaximumAge > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAge), "Telemetry retention must be from 1 through 365 days.");
        }

        if (MaximumSamples is < 100 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSamples), "Telemetry sample limit must be from 100 through 100,000.");
        }
    }
}

public sealed record GuardianTelemetryRange(
    DateTimeOffset From,
    DateTimeOffset To,
    int MaximumSamples = 10_000);

public sealed record GuardianMetricSummary(
    string Metric,
    string EntityIdentity,
    string Source,
    int SampleCount,
    int UnavailableCount,
    double? Average,
    double? Minimum,
    double? Maximum,
    double? Latest,
    DateTimeOffset? LatestAt,
    int SamplingGapCount,
    double? PreviousPeriodAverage);

public sealed record GuardianThresholdCrossingSummary(
    string Category,
    int WarningSampleCount,
    int CriticalSampleCount,
    DateTimeOffset MostRecentAt);
