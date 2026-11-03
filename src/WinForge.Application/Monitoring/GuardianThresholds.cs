namespace WinForge.Application.Monitoring;

public sealed record GuardianThresholds(
    double CpuWarningPercent,
    double MemoryWarningPercent,
    double MinimumFreeStoragePercent,
    double CpuCriticalPercent = 98,
    double MemoryCriticalPercent = 98,
    double MinimumFreeStorageCriticalPercent = 2)
{
    public static GuardianThresholds Default { get; } = new(90, 90, 10);

    public void Validate()
    {
        ValidatePercent(nameof(CpuWarningPercent), CpuWarningPercent);
        ValidatePercent(nameof(MemoryWarningPercent), MemoryWarningPercent);
        ValidatePercent(nameof(MinimumFreeStoragePercent), MinimumFreeStoragePercent);
        ValidatePercent(nameof(CpuCriticalPercent), CpuCriticalPercent);
        ValidatePercent(nameof(MemoryCriticalPercent), MemoryCriticalPercent);
        ValidatePercent(nameof(MinimumFreeStorageCriticalPercent), MinimumFreeStorageCriticalPercent);
        if (CpuCriticalPercent < CpuWarningPercent ||
            MemoryCriticalPercent < MemoryWarningPercent ||
            MinimumFreeStorageCriticalPercent > MinimumFreeStoragePercent)
        {
            throw new ArgumentOutOfRangeException(nameof(CpuCriticalPercent), "Critical thresholds must be at least as severe as their warning thresholds.");
        }
    }

    private static void ValidatePercent(string name, double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(name, value, "Thresholds must be between 0 and 100 percent.");
        }
    }
}
