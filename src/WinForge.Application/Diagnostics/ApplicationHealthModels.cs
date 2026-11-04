namespace WinForge.Application.Diagnostics;

public enum ApplicationHealthState
{
    Healthy,
    Degraded,
    Failed,
    Unsupported,
    Unavailable,
    Unknown
}

public sealed record ApplicationDiagnosticCheck(
    string Name,
    ApplicationHealthState State,
    string Message,
    DateTimeOffset ObservedAtUtc,
    string? Details = null,
    string? RecommendedAction = null);

public sealed record ApplicationHealthReport(
    ApplicationHealthState OverallState,
    string Summary,
    IReadOnlyList<ApplicationDiagnosticCheck> Checks,
    DateTimeOffset AssessedAtUtc,
    string RuntimeVersion,
    string AppVersion,
    string DataDirectory,
    string DatabasePath);
