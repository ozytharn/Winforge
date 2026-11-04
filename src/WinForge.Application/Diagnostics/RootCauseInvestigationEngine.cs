using WinForge.Domain.Configuration;
using WinForge.Domain.Diagnostics;
using WinForge.Domain.DigitalTwin;
using WinForge.Domain.Inventory;
using WinForge.Domain.Monitoring;

namespace WinForge.Application.Diagnostics;

public sealed class RootCauseInvestigationEngine
{
    public RootCauseAssessment Investigate(
        GuardianTelemetrySnapshot? telemetry,
        PassportSnapshot? inventory = null,
        DigitalTwinSnapshot? digitalTwin = null,
        IReadOnlyList<ConfigurationDriftChange>? driftChanges = null)
    {
        var hypotheses = new List<RootCauseHypothesis>();
        var warnings = new List<string>();

        var cpuReading = telemetry?.CpuUsagePercent;
        var memoryReading = telemetry?.AvailableMemoryBytes;
        var inventoryComponents = inventory?.Components ?? [];
        var driftList = driftChanges ?? [];

        if (cpuReading is { Availability: InventoryFieldAvailability.Available, Value: >= 80d })
        {
            var problemMatches = inventoryComponents
                .Where(HasProblemEvidence)
                .Take(5)
                .Select(component => new RootCauseEvidence(
                    component.Source,
                    "inventory",
                    $"{component.DisplayName} shows evidence of a problem state: {DescribeProblem(component)}.",
                    component.ObservedAt))
                .ToArray();

            if (problemMatches.Length > 0)
            {
                hypotheses.Add(new RootCauseHypothesis(
                    HypothesisId: $"cpu-and-problem-{Guid.NewGuid():N}",
                    Summary: "High CPU usage correlates with inventory-reported device or service problems.",
                    Category: "Performance",
                    AffectedEntityIdentity: problemMatches[0].Source,
                    Confidence: RootCauseConfidence.Medium,
                    SupportingEvidence: problemMatches,
                    ContradictingEvidence: [],
                    Limitation: "This is a correlation-based hypothesis, not proof of causation.",
                    IsCorrelationOnly: true));
            }
        }

        if (driftList.Count >= 3)
        {
            var driftEvidence = driftList
                .Take(4)
                .Select(change => new RootCauseEvidence(
                    change.Source,
                    "configuration-drift",
                    change.Summary,
                    change.ObservedAtUtc))
                .ToArray();

            if (driftEvidence.Length > 0)
            {
                hypotheses.Add(new RootCauseHypothesis(
                    HypothesisId: $"drift-signal-{Guid.NewGuid():N}",
                    Summary: "Recent drift across multiple components aligns with a broader configuration change.",
                    Category: "Configuration",
                    AffectedEntityIdentity: null,
                    Confidence: RootCauseConfidence.Medium,
                    SupportingEvidence: driftEvidence,
                    ContradictingEvidence: [],
                    Limitation: "Configuration drift is not proof that the drift caused a reliability event.",
                    IsCorrelationOnly: true));
            }
        }

        if (digitalTwin is not null && digitalTwin.Relationships.Count > 0 && telemetry is not null)
        {
            var edgeCount = digitalTwin.Relationships.Count;
            var strongSignal = (cpuReading is { Availability: InventoryFieldAvailability.Available, Value: >= 85d }) ||
                (memoryReading is { Availability: InventoryFieldAvailability.Available, Value: { } memory } && memory > 0);
            if (strongSignal && edgeCount > 0)
            {
                hypotheses.Add(new RootCauseHypothesis(
                    HypothesisId: $"service-graph-{Guid.NewGuid():N}",
                    Summary: "The current system state has a dense dependency graph alongside severe resource pressure.",
                    Category: "Dependency",
                    AffectedEntityIdentity: digitalTwin.Relationships.First().SourceIdentity,
                    Confidence: RootCauseConfidence.Low,
                    SupportingEvidence: new[]
                    {
                        new RootCauseEvidence("DigitalTwin", "relationship-graph", $"{edgeCount} relationships were observed in the current topology.", digitalTwin.CreatedAt),
                        new RootCauseEvidence(telemetry.CpuUsagePercent.Source, "telemetry", $"CPU usage at {telemetry.CpuUsagePercent.Value:F1}%.", telemetry.CollectedAt)
                    },
                    ContradictingEvidence: [],
                    Limitation: "A dense dependency graph can contribute to suspicion but is not causal evidence.",
                    IsCorrelationOnly: true));
            }
        }

        if (hypotheses.Count == 0)
        {
            warnings.Add("No supported root-cause candidates were generated from the available evidence. Additional telemetry or inventory context is required.");
        }

        warnings.Add("All investigations remain correlation-based and must not be presented as proof of causation.");

        return new RootCauseAssessment(
            hypotheses
                .OrderByDescending(h => h.Confidence)
                .ThenBy(h => h.Category, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            hypotheses.Any(h => h.IsCorrelationOnly));
    }

    private static bool HasProblemEvidence(InventoryComponent component)
    {
        if (component.Details is null)
        {
            return false;
        }

        return component.Details.Any(pair =>
            pair.Key.Contains("problem", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Contains("status", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Contains("failure", StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeProblem(InventoryComponent component)
    {
        var detail = component.Details
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(pair =>
                pair.Key.Contains("problem", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("status", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("failure", StringComparison.OrdinalIgnoreCase));

        return detail.Value is { Length: > 0 }
            ? detail.Value
            : component.DisplayName;
    }
}
