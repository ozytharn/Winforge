using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WinForge.App.Commands;
using WinForge.Application.Monitoring;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.App.ViewModels;

public sealed record GuardianSampleRow(
    string CollectedAt,
    string CpuUsage,
    string MemoryUsage,
    string StorageVolumes,
    string Uptime,
    string Network,
    string ProcessCount,
    string GpuMetrics);

public sealed record GuardianMetricSummaryRow(
    string Metric,
    string EntityIdentity,
    int SampleCount,
    int UnavailableCount,
    string Average,
    string Minimum,
    string Maximum,
    string Latest,
    int SamplingGapCount,
    string PreviousPeriodAverage,
    string Source);

public sealed record GuardianThresholdCrossingRow(
    string Category,
    int WarningSampleCount,
    int CriticalSampleCount,
    string MostRecentAt);

public sealed class GuardianViewModel : ViewModelBase
{
    private const int MaximumHistoryRows = 60;

    private readonly GuardianTelemetryService _telemetryService;
    private readonly GuardianAssessment _assessment;
    private readonly GuardianHistoryAnalyzer _historyAnalyzer;
    private readonly ILogger<GuardianViewModel> _logger;
    private GuardianAlertTracker _alertTracker = new();
    private CancellationTokenSource? _monitorCancellation;
    private bool _isMonitoring;
    private string _cpuWarningPercent = GuardianThresholds.Default.CpuWarningPercent.ToString("F0", CultureInfo.CurrentCulture);
    private string _memoryWarningPercent = GuardianThresholds.Default.MemoryWarningPercent.ToString("F0", CultureInfo.CurrentCulture);
    private string _minimumFreeStoragePercent = GuardianThresholds.Default.MinimumFreeStoragePercent.ToString("F0", CultureInfo.CurrentCulture);
    private string _cpuCriticalPercent = GuardianThresholds.Default.CpuCriticalPercent.ToString("F0", CultureInfo.CurrentCulture);
    private string _memoryCriticalPercent = GuardianThresholds.Default.MemoryCriticalPercent.ToString("F0", CultureInfo.CurrentCulture);
    private string _minimumFreeStorageCriticalPercent = GuardianThresholds.Default.MinimumFreeStorageCriticalPercent.ToString("F0", CultureInfo.CurrentCulture);
    private string _samplingIntervalSeconds = "2";
    private string _retentionDays = "30";
    private string _maximumStoredSamples = GuardianRetentionPolicy.Default.MaximumSamples.ToString(CultureInfo.CurrentCulture);
    private string _selectedTimeRange = "Last 24 hours";
    private string _statusText = "Monitoring has not been started.";
    private string _cpuText = "CPU: not sampled";
    private string _memoryText = "Memory: not sampled";
    private string _storageText = "Storage: not sampled";
    private string _uptimeText = "Uptime: not sampled";
    private string _networkText = "Network: not sampled";
    private string _processText = "Processes: not sampled";
    private string _gpuText = "GPU: adapter metrics unavailable";
    private string _historicalSummaryText = "No persisted telemetry history has been analyzed.";
    private string _lastSampleText = "No telemetry sample is available.";
    private string _errorMessage = string.Empty;

    public GuardianViewModel(
        GuardianTelemetryService telemetryService,
        GuardianAssessment assessment,
        GuardianHistoryAnalyzer historyAnalyzer,
        ILogger<GuardianViewModel> logger)
    {
        _telemetryService = telemetryService;
        _assessment = assessment;
        _historyAnalyzer = historyAnalyzer;
        _logger = logger;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsMonitoring, HandleUnexpectedError);
        StopCommand = new RelayCommand(Stop, () => IsMonitoring);
        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, () => !IsMonitoring, HandleUnexpectedError);
        TimeRangeOptions = ["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days"];
    }

    public ObservableCollection<GuardianSampleRow> History { get; } = [];

    public ObservableCollection<GuardianFinding> Findings { get; } = [];

    public ObservableCollection<GuardianFinding> ActiveAlerts { get; } = [];

    public ObservableCollection<GuardianMetricSummaryRow> HistoricalSummaries { get; } = [];

    public ObservableCollection<GuardianThresholdCrossingRow> HistoricalThresholdCrossings { get; } = [];

    public IReadOnlyList<string> TimeRangeOptions { get; }

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public AsyncRelayCommand LoadHistoryCommand { get; }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string CpuWarningPercent
    {
        get => _cpuWarningPercent;
        set => SetProperty(ref _cpuWarningPercent, value);
    }

    public string MemoryWarningPercent
    {
        get => _memoryWarningPercent;
        set => SetProperty(ref _memoryWarningPercent, value);
    }

    public string MinimumFreeStoragePercent
    {
        get => _minimumFreeStoragePercent;
        set => SetProperty(ref _minimumFreeStoragePercent, value);
    }

    public string CpuCriticalPercent { get => _cpuCriticalPercent; set => SetProperty(ref _cpuCriticalPercent, value); }

    public string MemoryCriticalPercent { get => _memoryCriticalPercent; set => SetProperty(ref _memoryCriticalPercent, value); }

    public string MinimumFreeStorageCriticalPercent
    {
        get => _minimumFreeStorageCriticalPercent;
        set => SetProperty(ref _minimumFreeStorageCriticalPercent, value);
    }

    public string SamplingIntervalSeconds
    {
        get => _samplingIntervalSeconds;
        set => SetProperty(ref _samplingIntervalSeconds, value);
    }

    public string RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, value);
    }

    public string MaximumStoredSamples
    {
        get => _maximumStoredSamples;
        set => SetProperty(ref _maximumStoredSamples, value);
    }

    public string SelectedTimeRange
    {
        get => _selectedTimeRange;
        set => SetProperty(ref _selectedTimeRange, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public string StorageText
    {
        get => _storageText;
        private set => SetProperty(ref _storageText, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => SetProperty(ref _uptimeText, value);
    }

    public string NetworkText { get => _networkText; private set => SetProperty(ref _networkText, value); }

    public string ProcessText { get => _processText; private set => SetProperty(ref _processText, value); }

    public string GpuText { get => _gpuText; private set => SetProperty(ref _gpuText, value); }

    public string HistoricalSummaryText
    {
        get => _historicalSummaryText;
        private set => SetProperty(ref _historicalSummaryText, value);
    }

    public string LastSampleText
    {
        get => _lastSampleText;
        private set => SetProperty(ref _lastSampleText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public void Stop() => _monitorCancellation?.Cancel();

    public async Task InitializeAsync()
    {
        try
        {
            var latest = await _telemetryService.GetLatestAsync(CancellationToken.None);
            if (latest is not null)
            {
                UpdateSample(latest, GuardianThresholds.Default, false);
                StatusText = $"Loaded persisted telemetry from {latest.CollectedAt.ToLocalTime():g}. Monitoring remains stopped until started.";
            }

            await LoadHistoryAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Persisted Guardian telemetry could not be loaded.");
            ErrorMessage = $"Historical telemetry could not be loaded: {exception.Message}";
        }
    }

    private async Task StartAsync()
    {
        var thresholds = ReadThresholds();
        if (thresholds is null ||
            !int.TryParse(SamplingIntervalSeconds, NumberStyles.Integer, CultureInfo.CurrentCulture, out var intervalSeconds) ||
            intervalSeconds is < 2 or > 60 ||
            !int.TryParse(RetentionDays, NumberStyles.Integer, CultureInfo.CurrentCulture, out var retentionDays) ||
            retentionDays is < 1 or > 365 ||
            !int.TryParse(MaximumStoredSamples, NumberStyles.Integer, CultureInfo.CurrentCulture, out var maximumSamples) ||
            maximumSamples is < 100 or > 100_000)
        {
            if (thresholds is not null)
            {
                ErrorMessage = "Sampling interval must be 2–60 seconds; retention must be 1–365 days; sample limit must be 100–100,000.";
            }

            return;
        }

        var retention = new GuardianRetentionPolicy(TimeSpan.FromDays(retentionDays), maximumSamples);
        _alertTracker = new GuardianAlertTracker();
        ActiveAlerts.Clear();
        var sampleInterval = TimeSpan.FromSeconds(intervalSeconds);
        _monitorCancellation = new CancellationTokenSource();
        var cancellationToken = _monitorCancellation.Token;
        ErrorMessage = string.Empty;
        IsMonitoring = true;
        StatusText = "Passive monitoring is active. No system settings will be changed.";

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleStartedAt = Stopwatch.GetTimestamp();
                var result = await _telemetryService.CollectAndPersistAsync(retention, cancellationToken);
                UpdateSample(result.Snapshot, thresholds, true);
                if (!result.Persisted)
                {
                    _logger.LogWarning("Guardian telemetry sample at {CollectedAt} was not persisted: {Failure}", result.Snapshot.CollectedAt, result.PersistenceFailure);
                    ErrorMessage = $"Telemetry is displayed but this sample could not be saved: {result.PersistenceFailure}";
                    StatusText = "Monitoring continues; historical persistence failed for this sample.";
                }

                var remainingInterval = sampleInterval - Stopwatch.GetElapsedTime(sampleStartedAt);
                if (remainingInterval > TimeSpan.Zero)
                {
                    await Task.Delay(remainingInterval, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Passive monitoring stopped.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Guardian passive monitoring failed.");
            ErrorMessage = $"Monitoring stopped because telemetry collection failed: {exception.Message}";
            StatusText = "Monitoring stopped after a collection failure.";
        }
        finally
        {
            _monitorCancellation.Dispose();
            _monitorCancellation = null;
            IsMonitoring = false;
        }
    }

    private GuardianThresholds? ReadThresholds()
    {
        if (!TryParseThreshold(CpuWarningPercent, out var cpu) ||
            !TryParseThreshold(MemoryWarningPercent, out var memory) ||
            !TryParseThreshold(MinimumFreeStoragePercent, out var storage) ||
            !TryParseThreshold(CpuCriticalPercent, out var cpuCritical) ||
            !TryParseThreshold(MemoryCriticalPercent, out var memoryCritical) ||
            !TryParseThreshold(MinimumFreeStorageCriticalPercent, out var storageCritical))
        {
            ErrorMessage = "Enter numeric percentage thresholds from 0 through 100.";
            return null;
        }

        var thresholds = new GuardianThresholds(cpu, memory, storage, cpuCritical, memoryCritical, storageCritical);
        try
        {
            thresholds.Validate();
            ErrorMessage = string.Empty;
            return thresholds;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            ErrorMessage = exception.Message;
            return null;
        }
    }

    private void UpdateSample(GuardianTelemetrySnapshot sample, GuardianThresholds thresholds, bool updateAlerts)
    {
        var findings = _assessment.Evaluate(sample, thresholds);
        Findings.Clear();
        foreach (var finding in findings)
        {
            Findings.Add(finding);
        }

        if (updateAlerts)
        {
            _alertTracker.Observe(findings);
            ActiveAlerts.Clear();
            foreach (var alert in _alertTracker.ActiveAlerts)
            {
                ActiveAlerts.Add(alert);
            }
        }

        var warningCount = findings.Count(finding => finding.State is GuardianFindingState.Warning or GuardianFindingState.Critical);
        StatusText = warningCount == 0
            ? "No configured thresholds are currently exceeded."
            : $"{warningCount} configured threshold warning(s) require attention.";

        CpuText = FormatPercentage("CPU", sample.CpuUsagePercent);
        MemoryText = sample.AvailableMemoryBytes.Value is { } available &&
                     sample.TotalMemoryBytes.Value is { } total &&
                     total > 0 &&
                     available <= total
            ? $"Memory: {FormatBytes(available)} available of {FormatBytes(total)} ({100d * (total - available) / total:F1}% used)"
            : $"Memory: {DescribeUnavailable(sample.AvailableMemoryBytes.Availability, sample.AvailableMemoryBytes.Limitation ?? sample.TotalMemoryBytes.Limitation)}";
        StorageText = sample.StorageVolumes.Count == 0
            ? $"Storage: {DescribeUnavailable(sample.StorageEnumeration.Availability, sample.StorageEnumeration.Limitation)}"
            : $"Storage: {sample.StorageVolumes.Count} fixed volume(s) observed; " +
              $"{sample.StorageVolumes.Count(volume => volume.Availability == InventoryFieldAvailability.Available)} readable";
        UptimeText = sample.SystemUptime.Value is { } uptime
            ? $"Uptime: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : $"Uptime: {DescribeUnavailable(sample.SystemUptime.Availability, sample.SystemUptime.Limitation)}";
        NetworkText = sample.NetworkInterfaces.Count == 0
            ? "Network: interface counters unavailable"
            : $"Network: {sample.NetworkInterfaces.Count} interface(s); " +
              $"{sample.NetworkInterfaces.Count(network => network.SentBytesPerSecond.Availability == InventoryFieldAvailability.Available)} throughput readings available";
        ProcessText = sample.ProcessEnumeration.Value is { } processCount
            ? $"Processes: {sample.Processes.Count} sampled of {processCount} examined (max 128 examined / 20 shown)"
            : $"Processes: {DescribeUnavailable(sample.ProcessEnumeration.Availability, sample.ProcessEnumeration.Limitation)}";
        var unsupportedGpuMetrics = sample.GpuAdapters.Count == 0
            ? 0
            : sample.GpuAdapters.Count(adapter =>
                adapter.UtilizationPercent.Availability == InventoryFieldAvailability.Unsupported ||
                adapter.DedicatedMemoryBytes.Availability == InventoryFieldAvailability.Unsupported ||
                adapter.TemperatureCelsius.Availability == InventoryFieldAvailability.Unsupported);
        GpuText = sample.GpuAdapters.Count == 0
            ? "GPU: no adapters reported or enumeration unavailable"
            : $"GPU: {sample.GpuAdapters.Count} adapter(s) identified; {unsupportedGpuMetrics} have unsupported live metric sets";
        LastSampleText = $"Last sample: {sample.CollectedAt.ToLocalTime():G} — sources are shown per finding.";

        History.Insert(0, ToHistoryRow(sample));
        while (History.Count > MaximumHistoryRows)
        {
            History.RemoveAt(History.Count - 1);
        }
    }

    private async Task LoadHistoryAsync()
    {
        var range = SelectedTimeRange switch
        {
            "Last hour" => TimeSpan.FromHours(1),
            "Last 7 days" => TimeSpan.FromDays(7),
            "Last 30 days" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromHours(24)
        };
        var now = DateTimeOffset.UtcNow;
        var samples = await _telemetryService.GetHistoryAsync(now - range, now, 10_000, CancellationToken.None);
        var midpoint = now - range;
        var previous = await _telemetryService.GetHistoryAsync(
            midpoint - range,
            midpoint.AddTicks(-1),
            10_000,
            CancellationToken.None);
        var summaries = await Task.Run(() => _historyAnalyzer.Analyze(samples, previous));
        var configuredThresholds = ReadThresholds();
        var usedDefaultThresholds = configuredThresholds is null;
        var thresholds = configuredThresholds ?? GuardianThresholds.Default;
        var crossings = await Task.Run(() => _historyAnalyzer.AnalyzeThresholdCrossings(samples, thresholds));
        HistoricalSummaries.Clear();
        foreach (var summary in summaries.Take(200))
        {
            HistoricalSummaries.Add(new GuardianMetricSummaryRow(
                FormatMetricName(summary.Metric),
                summary.EntityIdentity,
                summary.SampleCount,
                summary.UnavailableCount,
                FormatSummaryValue(summary.Metric, summary.Average),
                FormatSummaryValue(summary.Metric, summary.Minimum),
                FormatSummaryValue(summary.Metric, summary.Maximum),
                FormatSummaryValue(summary.Metric, summary.Latest),
                summary.SamplingGapCount,
                FormatSummaryValue(summary.Metric, summary.PreviousPeriodAverage),
                summary.Source));
        }

        HistoricalThresholdCrossings.Clear();
        foreach (var crossing in crossings)
        {
            HistoricalThresholdCrossings.Add(new GuardianThresholdCrossingRow(
                crossing.Category,
                crossing.WarningSampleCount,
                crossing.CriticalSampleCount,
                crossing.MostRecentAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)));
        }

        History.Clear();
        foreach (var sample in samples.OrderByDescending(item => item.CollectedAt).Take(MaximumHistoryRows))
        {
            History.Add(ToHistoryRow(sample));
        }

        var crossingSummary = crossings.Count == 0
            ? "No recorded samples crossed the configured thresholds."
            : "Threshold crossings: " + string.Join(
                "; ",
                crossings.Take(5).Select(crossing =>
                    $"{crossing.Category}: {crossing.WarningSampleCount} warning / {crossing.CriticalSampleCount} critical sample(s)"));
        if (usedDefaultThresholds)
        {
            crossingSummary += " Invalid configured thresholds; defaults were used for this summary.";
        }
        HistoricalSummaryText = samples.Count == 0
            ? $"No persisted samples are available for the {SelectedTimeRange.ToLowerInvariant()} range. {crossingSummary}"
            : $"Analyzed {samples.Count:N0} samples from {samples[0].CollectedAt.ToLocalTime():g} through {samples[^1].CollectedAt.ToLocalTime():g}. " +
              "Gaps are counted when observed intervals exceed twice the median sample interval; unavailable values are not treated as zero. " +
              crossingSummary;
    }

    private static GuardianSampleRow ToHistoryRow(GuardianTelemetrySnapshot sample)
    {
        var memory = sample.AvailableMemoryBytes.Value is { } available &&
                     sample.TotalMemoryBytes.Value is { } total &&
                     total > 0 &&
                     available <= total
            ? $"{FormatBytes(available)} available; {(100d * (total - available) / total):F1}% used"
            : DescribeUnavailable(sample.AvailableMemoryBytes.Availability, sample.AvailableMemoryBytes.Limitation);
        return new GuardianSampleRow(
            sample.CollectedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture),
            FormatPercentageValue(sample.CpuUsagePercent),
            memory,
            $"{sample.StorageVolumes.Count} volume(s); {sample.NetworkInterfaces.Count} network interface(s)",
            sample.SystemUptime.Value is { } uptime
                ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
                : DescribeUnavailable(sample.SystemUptime.Availability, sample.SystemUptime.Limitation),
            string.Join("; ", sample.NetworkInterfaces.Take(3).Select(network =>
                $"{network.Name}: tx {FormatRate(network.SentBytesPerSecond)}, rx {FormatRate(network.ReceivedBytesPerSecond)}")),
            $"{sample.Processes.Count} sampled",
            sample.GpuAdapters.Count == 0
                ? "No adapter data"
                : $"{sample.GpuAdapters.Count} adapter(s); live metrics unsupported where unavailable");
    }

    private static string FormatRate(TelemetryReading<double> reading) =>
        reading.Value is { } value && reading.Availability == InventoryFieldAvailability.Available
            ? $"{FormatByteRate(value)}/s"
            : $"{reading.Availability}";

    private static string FormatMetricName(string metric)
    {
        if (metric.StartsWith("storage.free.bytes:", StringComparison.Ordinal))
        {
            return "Volume free capacity";
        }

        if (metric.StartsWith("storage.total.bytes:", StringComparison.Ordinal))
        {
            return "Volume total capacity";
        }

        return metric switch
        {
            "cpu.percent" => "CPU utilization",
            "memory.available.bytes" => "Available physical memory",
            "memory.total.bytes" => "Total physical memory",
            "memory.used.percent" => "Physical memory used",
            "system.uptime.seconds" => "System uptime",
            "network.sent.bytes_per_second" => "Network sent throughput",
            "network.received.bytes_per_second" => "Network received throughput",
            "process.enumeration.count" => "Processes examined",
            "process.cpu.percent" => "Process CPU utilization",
            "process.working_set.bytes" => "Process working set",
            "gpu.utilization.percent" => "GPU utilization",
            "gpu.dedicated_memory.bytes" => "GPU dedicated memory",
            "gpu.temperature.celsius" => "GPU temperature",
            _ => metric
        };
    }

    private static string FormatSummaryValue(string metric, double? value)
    {
        if (value is not { } numeric || !double.IsFinite(numeric))
        {
            return "Unavailable";
        }

        if (metric.Contains("percent", StringComparison.Ordinal))
        {
            return $"{numeric:F1}%";
        }

        if (metric.Contains("bytes_per_second", StringComparison.Ordinal))
        {
            return $"{FormatByteRate(numeric)}/s";
        }

        if (metric.Contains("bytes", StringComparison.Ordinal))
        {
            return FormatByteRate(numeric);
        }

        if (metric == "system.uptime.seconds")
        {
            var uptime = TimeSpan.FromSeconds(Math.Max(0, numeric));
            return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
        }

        return metric == "gpu.temperature.celsius"
            ? $"{numeric:F1} C"
            : numeric.ToString("F2", CultureInfo.CurrentCulture);
    }

    private static string FormatByteRate(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            return "Unavailable";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "An unexpected Guardian monitoring error occurred.");
        ErrorMessage = $"Monitoring could not start: {exception.Message}";
    }

    private static bool TryParseThreshold(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static string FormatPercentage(string name, TelemetryReading<double> reading) =>
        reading.Value is { } value && reading.Availability == InventoryFieldAvailability.Available
            ? $"{name}: {value:F1}% over {reading.Interval?.TotalSeconds:F2}s"
            : $"{name}: {DescribeUnavailable(reading.Availability, reading.Limitation)}";

    private static string FormatPercentageValue(TelemetryReading<double> reading) =>
        reading.Value is { } value && reading.Availability == InventoryFieldAvailability.Available
            ? $"{value:F1}% / {reading.Interval?.TotalSeconds:F2}s"
            : DescribeUnavailable(reading.Availability, reading.Limitation);

    private static string DescribeUnavailable(InventoryFieldAvailability availability, string? limitation) =>
        $"{availability} — {limitation ?? "No reading was reported."}";

    private static string FormatBytes(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GiB";
}
