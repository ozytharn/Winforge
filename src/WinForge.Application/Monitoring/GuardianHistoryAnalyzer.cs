using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.Application.Monitoring;

public sealed class GuardianHistoryAnalyzer
{
    public IReadOnlyList<GuardianThresholdCrossingSummary> AnalyzeThresholdCrossings(
        IReadOnlyList<GuardianTelemetrySnapshot> samples,
        GuardianThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(thresholds);
        thresholds.Validate();

        var counts = new Dictionary<string, CrossingCount>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            if (sample.CpuUsagePercent.Availability == InventoryFieldAvailability.Available &&
                sample.CpuUsagePercent.Value is { } cpu)
            {
                Add("CPU", cpu, thresholds.CpuWarningPercent, thresholds.CpuCriticalPercent, sample.CollectedAt);
            }

            if (sample.AvailableMemoryBytes.Availability == InventoryFieldAvailability.Available &&
                sample.TotalMemoryBytes.Availability == InventoryFieldAvailability.Available &&
                sample.AvailableMemoryBytes.Value is { } available &&
                sample.TotalMemoryBytes.Value is { } total &&
                total > 0 &&
                available <= total)
            {
                var usedPercent = (1d - (double)available / total) * 100;
                Add("Memory", usedPercent, thresholds.MemoryWarningPercent, thresholds.MemoryCriticalPercent, sample.CollectedAt);
            }

            foreach (var volume in sample.StorageVolumes)
            {
                if (volume.Availability != InventoryFieldAvailability.Available ||
                    volume.TotalBytes is not { } volumeTotal ||
                    volume.FreeBytes is not { } free ||
                    volumeTotal == 0 ||
                    free > volumeTotal)
                {
                    continue;
                }

                var freePercent = (double)free / volumeTotal * 100;
                var category = $"Storage {volume.Name}";
                if (freePercent < thresholds.MinimumFreeStorageCriticalPercent)
                {
                    Record(category, true, sample.CollectedAt);
                }
                else if (freePercent < thresholds.MinimumFreeStoragePercent)
                {
                    Record(category, false, sample.CollectedAt);
                }
            }
        }

        return counts
            .Select(pair => new GuardianThresholdCrossingSummary(
                pair.Key,
                pair.Value.WarningSamples,
                pair.Value.CriticalSamples,
                pair.Value.MostRecentAt))
            .OrderByDescending(summary => summary.CriticalSampleCount)
            .ThenByDescending(summary => summary.WarningSampleCount)
            .ThenBy(summary => summary.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        void Add(string category, double value, double warning, double critical, DateTimeOffset observedAt)
        {
            if (value >= critical)
            {
                Record(category, true, observedAt);
            }
            else if (value >= warning)
            {
                Record(category, false, observedAt);
            }
        }

        void Record(string category, bool critical, DateTimeOffset observedAt)
        {
            if (!counts.TryGetValue(category, out var count))
            {
                count = new CrossingCount();
                counts.Add(category, count);
            }

            if (critical)
            {
                count.CriticalSamples++;
            }
            else
            {
                count.WarningSamples++;
            }

            if (observedAt > count.MostRecentAt)
            {
                count.MostRecentAt = observedAt;
            }
        }
    }

    public IReadOnlyList<GuardianMetricSummary> Analyze(
        IReadOnlyList<GuardianTelemetrySnapshot> recent,
        IReadOnlyList<GuardianTelemetrySnapshot> previous)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(previous);
        var currentValues = Flatten(recent);
        var previousValues = Flatten(previous);
        var summaries = new List<GuardianMetricSummary>();
        foreach (var group in currentValues.GroupBy(value => (value.Metric, value.Identity, value.Source)))
        {
            var available = group.Where(value => value.Value is not null).ToArray();
            var ordered = group.OrderBy(value => value.At).ToArray();
            var declaredIntervals = ordered
                .Where(value => value.Interval is { } interval && interval > TimeSpan.Zero)
                .Select(value => value.Interval!.Value)
                .OrderBy(value => value)
                .ToArray();
            var observedIntervals = ordered
                .Zip(ordered.Skip(1), (first, second) => second.At - first.At)
                .Where(interval => interval > TimeSpan.Zero)
                .OrderBy(interval => interval)
                .ToArray();
            var intervals = declaredIntervals.Length > 0 ? declaredIntervals : observedIntervals;
            var expectedInterval = intervals.Length == 0 ? TimeSpan.Zero : intervals[(intervals.Length - 1) / 2];
            var gaps = 0;
            if (expectedInterval > TimeSpan.Zero)
            {
                for (var index = 1; index < ordered.Length; index++)
                {
                    if (ordered[index].At - ordered[index - 1].At > expectedInterval + expectedInterval)
                    {
                        gaps++;
                    }
                }
            }

            var prior = previousValues
                .Where(value => value.Metric == group.Key.Metric && value.Identity == group.Key.Identity)
                .Where(value => value.Source == group.Key.Source)
                .Where(value => value.Value is not null)
                .Select(value => value.Value!.Value)
                .ToArray();
            summaries.Add(new GuardianMetricSummary(
                group.Key.Metric,
                group.Key.Identity,
                group.Key.Source,
                available.Length,
                ordered.Length - available.Length,
                available.Length == 0 ? null : available.Average(value => value.Value!.Value),
                available.Length == 0 ? null : available.Min(value => value.Value!.Value),
                available.Length == 0 ? null : available.Max(value => value.Value!.Value),
                available.OrderBy(value => value.At).LastOrDefault()?.Value,
                ordered.LastOrDefault()?.At,
                gaps,
                prior.Length == 0 ? null : prior.Average()));
        }

        return summaries
            .OrderBy(summary => summary.Metric, StringComparer.Ordinal)
            .ThenBy(summary => summary.EntityIdentity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<FlatValue> Flatten(IEnumerable<GuardianTelemetrySnapshot> samples)
    {
        var values = new List<FlatValue>();
        foreach (var sample in samples)
        {
            Add("cpu.percent", "system", sample.CpuUsagePercent);
            Add("memory.available.bytes", "system", sample.AvailableMemoryBytes);
            Add("memory.total.bytes", "system", sample.TotalMemoryBytes);
            Add(
                "memory.used.percent",
                "system",
                sample.AvailableMemoryBytes.Value is { } available &&
                sample.TotalMemoryBytes.Value is { } total &&
                total > 0 &&
                available <= total
                    ? new TelemetryReading<double>(
                        (1 - (double)available / total) * 100,
                        InventoryFieldAvailability.Available,
                        "GlobalMemoryStatusEx")
                    : new TelemetryReading<double>(
                        null,
                        InventoryFieldAvailability.Unavailable,
                        "GlobalMemoryStatusEx",
                        "Available and total memory were not both valid."));
            Add("system.uptime.seconds", "system", sample.SystemUptime.Value is { } uptime
                ? new TelemetryReading<double>(uptime.TotalSeconds, sample.SystemUptime.Availability, sample.SystemUptime.Source, sample.SystemUptime.Limitation)
                : new TelemetryReading<double>(null, sample.SystemUptime.Availability, sample.SystemUptime.Source, sample.SystemUptime.Limitation));
            Add("process.enumeration.count", "system",
                ToReading(sample.ProcessEnumeration.Value is { } processCount ? (ulong)processCount : null,
                    sample.ProcessEnumeration.Availability,
                    sample.ProcessEnumeration.Source,
                    sample.ProcessEnumeration.Limitation));
            foreach (var volume in sample.StorageVolumes)
            {
                Add($"storage.free.bytes:{volume.Name}", volume.Name,
                    ToReading(volume.FreeBytes, volume.Availability, volume.Source, volume.Limitation));
                Add($"storage.total.bytes:{volume.Name}", volume.Name,
                    ToReading(volume.TotalBytes, volume.Availability, volume.Source, volume.Limitation));
            }

            foreach (var network in sample.NetworkInterfaces)
            {
                Add("network.sent.bytes_per_second", network.Identity, network.SentBytesPerSecond);
                Add("network.received.bytes_per_second", network.Identity, network.ReceivedBytesPerSecond);
            }

            foreach (var process in sample.Processes)
            {
                Add("process.cpu.percent", process.Identity, process.CpuPercent);
                Add("process.working_set.bytes", process.Identity,
                    ToReading(process.WorkingSetBytes.Value, process.WorkingSetBytes.Availability,
                        process.WorkingSetBytes.Source, process.WorkingSetBytes.Limitation));
            }

            foreach (var gpu in sample.GpuAdapters)
            {
                Add("gpu.utilization.percent", gpu.Identity, gpu.UtilizationPercent);
                Add("gpu.dedicated_memory.bytes", gpu.Identity,
                    ToReading(gpu.DedicatedMemoryBytes.Value, gpu.DedicatedMemoryBytes.Availability,
                        gpu.DedicatedMemoryBytes.Source, gpu.DedicatedMemoryBytes.Limitation));
                Add("gpu.temperature.celsius", gpu.Identity, gpu.TemperatureCelsius);
            }

            void Add<T>(string metric, string identity, TelemetryReading<T> reading)
                where T : struct
            {
                object? rawValue = reading.Value;
                double? numericValue = rawValue switch
                {
                    double number => number,
                    ulong number => number,
                    int number => number,
                    TimeSpan span => span.TotalSeconds,
                    _ => (double?)null
                };
                values.Add(new FlatValue(
                    metric,
                    identity,
                    reading.Source,
                    sample.CollectedAt,
                    reading.Availability == InventoryFieldAvailability.Available ? numericValue : null,
                    reading.Interval));
            }
        }

        return values;
    }

    private static TelemetryReading<double> ToReading(
        ulong? value,
        InventoryFieldAvailability availability,
        string source,
        string? limitation) =>
        value is { } numeric
            ? new TelemetryReading<double>((double)numeric, availability, source, limitation)
            : new TelemetryReading<double>(null, availability, source, limitation);

    private sealed record FlatValue(
        string Metric,
        string Identity,
        string Source,
        DateTimeOffset At,
        double? Value,
        TimeSpan? Interval);

    private sealed class CrossingCount
    {
        public int WarningSamples { get; set; }

        public int CriticalSamples { get; set; }

        public DateTimeOffset MostRecentAt { get; set; }
    }
}
