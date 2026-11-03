using WinForge.Application.Monitoring;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.UnitTests;

public sealed class GuardianAssessmentTests
{
    [Fact]
    public void Evaluate_reports_configured_cpu_memory_and_storage_warnings()
    {
        var snapshot = CreateSnapshot(
            Cpu(95),
            Memory(10),
            Memory(100),
            [new GuardianStorageVolume("C:\\", "System", 100, 5, InventoryFieldAvailability.Available, "DriveInfo")]);

        var findings = new GuardianAssessment().Evaluate(snapshot, new GuardianThresholds(90, 85, 10));

        Assert.Equal(GuardianFindingState.Warning, Find(findings, "CPU").State);
        Assert.Equal(GuardianFindingState.Warning, Find(findings, "Memory").State);
        Assert.Equal(GuardianFindingState.Warning, Find(findings, "Storage C:\\").State);
    }

    [Fact]
    public void Evaluate_keeps_unavailable_data_unknown_instead_of_reporting_a_gap()
    {
        var snapshot = CreateSnapshot(
            new TelemetryReading<double>(null, InventoryFieldAvailability.Inaccessible, "GetSystemTimes", "Access denied."),
            new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, "GlobalMemoryStatusEx"),
            new TelemetryReading<ulong>(null, InventoryFieldAvailability.Unsupported, "GlobalMemoryStatusEx"),
            []);

        var findings = new GuardianAssessment().Evaluate(snapshot, GuardianThresholds.Default);

        Assert.Equal(GuardianFindingState.Unknown, Find(findings, "CPU").State);
        Assert.Equal(GuardianFindingState.Unknown, Find(findings, "Memory").State);
        Assert.Equal(GuardianFindingState.Unknown, Find(findings, "Storage").State);
    }

    [Fact]
    public void Evaluate_rejects_out_of_range_thresholds()
    {
        var snapshot = CreateSnapshot(Cpu(0), Memory(50), Memory(100), []);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GuardianAssessment().Evaluate(snapshot, new GuardianThresholds(101, 90, 10)));
    }

    [Fact]
    public void Evaluate_applies_threshold_boundary_semantics_consistently()
    {
        var exactMinimumVolume = new GuardianStorageVolume(
            "D:\\",
            "Data",
            100,
            10,
            InventoryFieldAvailability.Available,
            "DriveInfo");
        var snapshot = CreateSnapshot(
            Cpu(90),
            Memory(10),
            Memory(100),
            [exactMinimumVolume]);

        var findings = new GuardianAssessment().Evaluate(snapshot, new GuardianThresholds(90, 90, 10));

        Assert.Equal(GuardianFindingState.Warning, Find(findings, "CPU").State);
        Assert.Equal(GuardianFindingState.Warning, Find(findings, "Memory").State);
        Assert.Equal(GuardianFindingState.Informational, Find(findings, "Storage D:\\").State);
    }

    [Fact]
    public void Evaluate_preserves_unsupported_sensor_state_as_unknown()
    {
        var snapshot = CreateSnapshot(
            new TelemetryReading<double>(
                null,
                InventoryFieldAvailability.Unsupported,
                "Optional sensor",
                "The platform does not expose this metric."),
            Memory(10),
            Memory(100),
            []);

        var findings = new GuardianAssessment().Evaluate(snapshot, GuardianThresholds.Default);

        Assert.Equal(GuardianFindingState.Unknown, Find(findings, "CPU").State);
        Assert.Contains("does not expose", Find(findings, "CPU").Message);
    }

    private static GuardianFinding Find(IEnumerable<GuardianFinding> findings, string category) =>
        Assert.Single(findings, finding => finding.Category == category);

    private static GuardianTelemetrySnapshot CreateSnapshot(
        TelemetryReading<double> cpu,
        TelemetryReading<ulong> availableMemory,
        TelemetryReading<ulong> totalMemory,
        IReadOnlyList<GuardianStorageVolume> volumes)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new GuardianTelemetrySnapshot(
            timestamp,
            cpu,
            availableMemory,
            totalMemory,
            new TelemetryReading<TimeSpan>(TimeSpan.FromHours(2), InventoryFieldAvailability.Available, "TickCount64"),
            new TelemetryReading<int>(
                volumes.Count,
                InventoryFieldAvailability.Available,
                "DriveInfo",
                volumes.Count == 0 ? "No fixed volumes." : null),
            volumes);
    }

    private static TelemetryReading<double> Cpu(double value) =>
        new(value, InventoryFieldAvailability.Available, "GetSystemTimes");

    private static TelemetryReading<ulong> Memory(ulong value) =>
        new(value, InventoryFieldAvailability.Available, "GlobalMemoryStatusEx");
}
