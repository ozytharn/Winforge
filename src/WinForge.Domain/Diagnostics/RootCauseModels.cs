namespace WinForge.Domain.Diagnostics;

public enum RootCauseConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed record RootCauseEvidence(
    string Source,
    string EvidenceType,
    string Detail,
    DateTimeOffset ObservedAtUtc);

public sealed record RootCauseHypothesis(
    string HypothesisId,
    string Summary,
    string Category,
    string? AffectedEntityIdentity,
    RootCauseConfidence Confidence,
    IReadOnlyList<RootCauseEvidence> SupportingEvidence,
    IReadOnlyList<RootCauseEvidence> ContradictingEvidence,
    string? Limitation,
    bool IsCorrelationOnly);

public sealed record RootCauseAssessment(
    IReadOnlyList<RootCauseHypothesis> Hypotheses,
    IReadOnlyList<string> Warnings,
    bool HasUnsupportedInference);
