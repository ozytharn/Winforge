using WinForge.Application.Monitoring;

namespace WinForge.UnitTests;

public sealed class GuardianAlertTrackerTests
{
    [Fact]
    public void Observe_requires_two_consecutive_samples_and_suppresses_duplicate_alerts()
    {
        var tracker = new GuardianAlertTracker();
        var warning = Finding(GuardianFindingState.Warning, "High CPU");

        Assert.Empty(tracker.Observe([warning]));
        Assert.Empty(tracker.ActiveAlerts);
        Assert.Single(tracker.Observe([warning]));
        Assert.Single(tracker.ActiveAlerts);
        Assert.Empty(tracker.Observe([warning]));
    }

    [Fact]
    public void Observe_escalates_active_alert_and_resolves_when_condition_clears()
    {
        var tracker = new GuardianAlertTracker();
        tracker.Observe([Finding(GuardianFindingState.Warning, "High CPU")]);
        tracker.Observe([Finding(GuardianFindingState.Warning, "High CPU")]);

        Assert.Single(tracker.Observe([Finding(GuardianFindingState.Critical, "Critical CPU")]));
        Assert.Equal(GuardianFindingState.Critical, Assert.Single(tracker.ActiveAlerts).State);
        Assert.Empty(tracker.Observe([Finding(GuardianFindingState.Unknown, "CPU unavailable")]));
        Assert.Empty(tracker.ActiveAlerts);
    }

    private static GuardianFinding Finding(GuardianFindingState state, string message) =>
        new("CPU", state, message, "test", DateTimeOffset.UtcNow);
}
