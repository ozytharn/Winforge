using System.Collections.Immutable;
using WinForge.ActionBroker.V2.Lifecycle;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.ActionBroker.V2.Recovery;

public enum RecoveryRetryPolicy
{
    Never,
    RetryOnlyAfterKnownNoEffectAndFreshAuthorization,
    ManualReconciliationOnly
}

public enum RecoveryStepState
{
    Planned,
    IntentRecorded,
    Completed,
    IndependentlyVerified,
    FailedKnownNoEffect,
    OutcomeUnknown,
    RecoveryRequired
}

public enum RecoveryEvidenceKind
{
    DurableIntent,
    CompletionReported,
    KnownNoEffect,
    IndependentVerification,
    AmbiguousOutcome
}

public sealed record RecoveryEvidence(
    string EvidenceId,
    string SourceComponentId,
    string ContentDigest,
    RecoveryEvidenceKind Kind,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(EvidenceId) &&
        !string.IsNullOrWhiteSpace(SourceComponentId) &&
        !string.IsNullOrWhiteSpace(ContentDigest) &&
        Enum.IsDefined(Kind) &&
        ObservedAtUtc != default;

    public static RecoveryEvidence Create(
        string evidenceId,
        string sourceComponentId,
        string contentDigest,
        RecoveryEvidenceKind kind,
        DateTimeOffset observedAtUtc)
    {
        var id = CanonicalValue.NormalizeText(evidenceId, nameof(evidenceId), 256);
        var source = CanonicalValue.NormalizeText(sourceComponentId, nameof(sourceComponentId), 256);
        var digest = CanonicalValue.NormalizeText(contentDigest, nameof(contentDigest), 256);
        if (id.Length == 0 || source.Length == 0 || digest.Length == 0 || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Recovery evidence requires a valid identity, source, digest, and kind.");
        }

        return new RecoveryEvidence(
            id,
            source,
            digest,
            kind,
            observedAtUtc.ToUniversalTime());
    }
}

public sealed record RecoveryStepContract
{
    private RecoveryStepContract(
        Guid stepId,
        int ordinal,
        string procedureId,
        int procedureVersion,
        ImmutableArray<Guid> dependencies,
        ImmutableArray<string> preconditions,
        ImmutableArray<string> expectedEffects,
        RecoveryRetryPolicy retryPolicy)
    {
        StepId = stepId;
        Ordinal = ordinal;
        ProcedureId = procedureId;
        ProcedureVersion = procedureVersion;
        Dependencies = dependencies;
        Preconditions = preconditions;
        ExpectedEffects = expectedEffects;
        RetryPolicy = retryPolicy;
    }

    public Guid StepId { get; }
    public int Ordinal { get; }
    public string ProcedureId { get; }
    public int ProcedureVersion { get; }
    public ImmutableArray<Guid> Dependencies { get; }
    public ImmutableArray<string> Preconditions { get; }
    public ImmutableArray<string> ExpectedEffects { get; }
    public RecoveryRetryPolicy RetryPolicy { get; }

    public bool IsEquivalentTo(RecoveryStepContract other) =>
        StepId == other.StepId &&
        Ordinal == other.Ordinal &&
        StringComparer.Ordinal.Equals(ProcedureId, other.ProcedureId) &&
        ProcedureVersion == other.ProcedureVersion &&
        RetryPolicy == other.RetryPolicy &&
        Dependencies.SequenceEqual(other.Dependencies) &&
        Preconditions.SequenceEqual(other.Preconditions, StringComparer.Ordinal) &&
        ExpectedEffects.SequenceEqual(other.ExpectedEffects, StringComparer.Ordinal);

    public static RecoveryStepContract Create(
        Guid stepId,
        int ordinal,
        string procedureId,
        int procedureVersion,
        IEnumerable<Guid> dependencies,
        IEnumerable<string> preconditions,
        IEnumerable<string> expectedEffects,
        RecoveryRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(preconditions);
        ArgumentNullException.ThrowIfNull(expectedEffects);
        var dependencyItems = dependencies.Take(129).ToImmutableArray();
        var preconditionItems = NormalizeDescriptors(preconditions, nameof(preconditions));
        var effectItems = NormalizeDescriptors(expectedEffects, nameof(expectedEffects));
        if (stepId == Guid.Empty || ordinal < 0 || procedureVersion <= 0 ||
            dependencyItems.Length > 128 ||
            dependencyItems.Any(static id => id == Guid.Empty) ||
            dependencyItems.Distinct().Count() != dependencyItems.Length ||
            preconditionItems.IsEmpty ||
            effectItems.IsEmpty ||
            !Enum.IsDefined(retryPolicy))
        {
            throw new ArgumentException("Recovery step requires valid identity/version/order, unique dependencies, preconditions, and expected effects.");
        }

        var normalizedProcedureId = CanonicalValue.NormalizeText(procedureId, nameof(procedureId), 256);
        if (normalizedProcedureId.Length == 0)
        {
            throw new ArgumentException("Recovery step procedure ID is required.", nameof(procedureId));
        }

        return new RecoveryStepContract(
            stepId,
            ordinal,
            normalizedProcedureId,
            procedureVersion,
            dependencyItems,
            preconditionItems,
            effectItems,
            retryPolicy);
    }

    private static ImmutableArray<string> NormalizeDescriptors(IEnumerable<string> values, string parameterName)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var scanned = 0;
        foreach (var value in values)
        {
            if (++scanned > 512)
            {
                throw new ArgumentException("Recovery descriptors must be bounded.", parameterName);
            }

            var normalized = CanonicalValue.NormalizeText(value, parameterName, 512);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("Recovery descriptors must be non-empty and bounded.", parameterName);
            }

            if (!builder.Contains(normalized, StringComparer.Ordinal))
            {
                if (builder.Count == 128)
                {
                    throw new ArgumentException("Recovery descriptors must be non-empty and bounded.", parameterName);
                }

                builder.Add(normalized);
            }
        }

        return builder.ToImmutable();
    }
}

public sealed record RecoveryPlan
{
    private RecoveryPlan(
        Guid planId,
        int version,
        Guid requestId,
        TargetIdentity target,
        ImmutableArray<RecoveryStepContract> steps)
    {
        PlanId = planId;
        Version = version;
        RequestId = requestId;
        Target = target;
        Steps = steps;
    }

    public Guid PlanId { get; }
    public int Version { get; }
    public Guid RequestId { get; }
    public TargetIdentity Target { get; }
    public ImmutableArray<RecoveryStepContract> Steps { get; }

    public static RecoveryPlan Create(
        Guid planId,
        int version,
        Guid requestId,
        TargetIdentity target,
        IEnumerable<RecoveryStepContract> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(target);
        var supplied = steps.Take(513).ToImmutableArray();
        if (supplied.Length > 512 || supplied.Any(static step => step is null))
        {
            throw new ArgumentException("Recovery plan requires at most 512 non-null steps.", nameof(steps));
        }

        var items = supplied.OrderBy(static step => step.Ordinal).ToImmutableArray();
        if (planId == Guid.Empty || requestId == Guid.Empty || version <= 0 ||
            items.IsEmpty ||
            items.Any(static step => step is null) ||
            items.Select(static step => step.StepId).Distinct().Count() != items.Length ||
            items.Select(static step => step.Ordinal).Distinct().Count() != items.Length)
        {
            throw new ArgumentException("Recovery plan requires unique step IDs and order, a request, and a positive version.");
        }

        var byId = items.ToDictionary(static step => step.StepId);
        foreach (var step in items)
        {
            foreach (var dependencyId in step.Dependencies)
            {
                if (!byId.TryGetValue(dependencyId, out var dependency) ||
                    dependency.Ordinal >= step.Ordinal)
                {
                    throw new ArgumentException("Every recovery dependency must exist and precede its dependent step.", nameof(steps));
                }
            }
        }

        return new RecoveryPlan(planId, version, requestId, target, items);
    }
}

public sealed record RecoveryStepAttempt(
    Guid AttemptId,
    Guid PlanId,
    int PlanVersion,
    Guid StepId,
    string ProcedureId,
    int ProcedureVersion,
    int AttemptNumber,
    RecoveryStepState State,
    RecoveryEvidence? IntentEvidence = null,
    RecoveryEvidence? CompletionEvidence = null,
    VerificationEvidence? VerificationEvidence = null,
    RecoveryEvidence? FailureEvidence = null)
{
    public static RecoveryStepAttempt CreateInitial(
        Guid attemptId,
        RecoveryPlan plan,
        RecoveryStepContract step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        if (attemptId == Guid.Empty ||
            !plan.Steps.Any(candidate => candidate.IsEquivalentTo(step)))
        {
            throw new ArgumentException("Attempt must reference a step contained in the supplied recovery plan.");
        }

        return new RecoveryStepAttempt(
            attemptId,
            plan.PlanId,
            plan.Version,
            step.StepId,
            step.ProcedureId,
            step.ProcedureVersion,
            1,
            RecoveryStepState.Planned);
    }
}

public enum RecoveryActor
{
    Coordinator,
    StepProcedure,
    Verifier
}

public sealed record RecoveryStepTransitionEvidence
{
    public bool PreconditionsSatisfied { get; init; }
    public TargetIdentity? CurrentTarget { get; init; }
    public ImmutableDictionary<Guid, RecoveryStepState> DependencyStates { get; init; } =
        ImmutableDictionary<Guid, RecoveryStepState>.Empty;
    public RecoveryEvidence? DurableIntent { get; init; }
    public RecoveryEvidence? Completion { get; init; }
    public RecoveryEvidence? KnownNoEffect { get; init; }
    public RecoveryEvidence? Ambiguity { get; init; }
    public VerificationEvidence? Verification { get; init; }
}

public sealed record RecoveryStepTransitionResult(
    bool Allowed,
    RecoveryStepAttempt? Attempt,
    string? Reason);

public enum RecoveryCrashDisposition
{
    NotStarted,
    ReconcileWithoutReplay,
    AlreadyVerified,
    KnownNoEffect
}

public static class RecoveryStepRules
{
    public static RecoveryStepTransitionResult Evaluate(
        RecoveryPlan plan,
        RecoveryStepContract contract,
        RecoveryStepAttempt attempt,
        RecoveryStepState requested,
        RecoveryActor actor,
        RecoveryStepTransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.DependencyStates);
        if (attempt.AttemptId == Guid.Empty ||
            attempt.AttemptNumber <= 0 ||
            !plan.Steps.Any(step => step.IsEquivalentTo(contract)) ||
            attempt.PlanId != plan.PlanId ||
            attempt.PlanVersion != plan.Version ||
            attempt.StepId != contract.StepId ||
            !StringComparer.Ordinal.Equals(attempt.ProcedureId, contract.ProcedureId) ||
            attempt.ProcedureVersion != contract.ProcedureVersion)
        {
            return new(false, null, "Recovery attempt does not match the immutable plan and procedure versions.");
        }

        var permitted = (attempt.State, requested, actor) switch
        {
            (RecoveryStepState.Planned, RecoveryStepState.IntentRecorded, RecoveryActor.Coordinator) =>
                evidence.PreconditionsSatisfied &&
                plan.Target == evidence.CurrentTarget &&
                contract.Dependencies.All(dependency =>
                    evidence.DependencyStates.TryGetValue(dependency, out var state) &&
                    state == RecoveryStepState.IndependentlyVerified) &&
                evidence.DurableIntent is { Kind: RecoveryEvidenceKind.DurableIntent, IsWellFormed: true },
            (RecoveryStepState.IntentRecorded, RecoveryStepState.Completed, RecoveryActor.StepProcedure) =>
                evidence.Completion is
                {
                    Kind: RecoveryEvidenceKind.CompletionReported,
                    IsWellFormed: true
                } completion &&
                StringComparer.Ordinal.Equals(completion.SourceComponentId, contract.ProcedureId),
            (RecoveryStepState.IntentRecorded, RecoveryStepState.FailedKnownNoEffect, RecoveryActor.StepProcedure) =>
                evidence.KnownNoEffect is
                {
                    Kind: RecoveryEvidenceKind.KnownNoEffect,
                    IsWellFormed: true
                } noEffect &&
                StringComparer.Ordinal.Equals(noEffect.SourceComponentId, contract.ProcedureId),
            (RecoveryStepState.IntentRecorded, RecoveryStepState.OutcomeUnknown, RecoveryActor.Coordinator) =>
                evidence.Ambiguity is { Kind: RecoveryEvidenceKind.AmbiguousOutcome, IsWellFormed: true },
            (RecoveryStepState.IntentRecorded, RecoveryStepState.RecoveryRequired, RecoveryActor.Coordinator) =>
                evidence.Ambiguity is { Kind: RecoveryEvidenceKind.AmbiguousOutcome, IsWellFormed: true },
            (RecoveryStepState.OutcomeUnknown, RecoveryStepState.RecoveryRequired, RecoveryActor.Coordinator) =>
                evidence.Ambiguity is { Kind: RecoveryEvidenceKind.AmbiguousOutcome, IsWellFormed: true },
            (RecoveryStepState.Completed, RecoveryStepState.IndependentlyVerified, RecoveryActor.Verifier) =>
                evidence.Verification is
                {
                    PostconditionsSatisfied: true,
                    MeetsModelIndependenceCriteria: true
                } verification &&
                StringComparer.Ordinal.Equals(verification.ExecutorComponentId, contract.ProcedureId),
            (RecoveryStepState.Completed, RecoveryStepState.RecoveryRequired, RecoveryActor.Coordinator) => true,
            _ => false
        };

        if (!permitted)
        {
            return new(false, null, $"Recovery step transition {attempt.State} -> {requested} lacks required evidence or actor.");
        }

        var updated = requested switch
        {
            RecoveryStepState.IntentRecorded => attempt with { State = requested, IntentEvidence = evidence.DurableIntent },
            RecoveryStepState.Completed => attempt with { State = requested, CompletionEvidence = evidence.Completion },
            RecoveryStepState.FailedKnownNoEffect => attempt with { State = requested, FailureEvidence = evidence.KnownNoEffect },
            RecoveryStepState.OutcomeUnknown or RecoveryStepState.RecoveryRequired =>
                attempt with { State = requested, FailureEvidence = evidence.Ambiguity },
            RecoveryStepState.IndependentlyVerified =>
                attempt with { State = requested, VerificationEvidence = evidence.Verification },
            _ => throw new InvalidOperationException($"Unsupported recovery step state {requested}.")
        };
        return new(true, updated, null);
    }

    public static RecoveryCrashDisposition ClassifyCrash(RecoveryStepState state) =>
        state switch
        {
            RecoveryStepState.Planned => RecoveryCrashDisposition.NotStarted,
            RecoveryStepState.IntentRecorded or RecoveryStepState.OutcomeUnknown or RecoveryStepState.RecoveryRequired =>
                RecoveryCrashDisposition.ReconcileWithoutReplay,
            RecoveryStepState.Completed => RecoveryCrashDisposition.ReconcileWithoutReplay,
            RecoveryStepState.IndependentlyVerified => RecoveryCrashDisposition.AlreadyVerified,
            RecoveryStepState.FailedKnownNoEffect => RecoveryCrashDisposition.KnownNoEffect,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

    public static bool CanCreateRetry(
        RecoveryPlan plan,
        RecoveryStepContract contract,
        RecoveryStepAttempt previousAttempt,
        Guid retryAttemptId,
        bool freshAuthorization,
        out RecoveryStepAttempt? retry)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(previousAttempt);
        retry = null;
        if (retryAttemptId == Guid.Empty ||
            retryAttemptId == previousAttempt.AttemptId ||
            previousAttempt.AttemptId == Guid.Empty ||
            previousAttempt.AttemptNumber <= 0 ||
            previousAttempt.AttemptNumber == int.MaxValue ||
            !plan.Steps.Any(step => step.IsEquivalentTo(contract)) ||
            previousAttempt.PlanId != plan.PlanId ||
            previousAttempt.PlanVersion != plan.Version ||
            !StringComparer.Ordinal.Equals(previousAttempt.ProcedureId, contract.ProcedureId) ||
            previousAttempt.ProcedureVersion != contract.ProcedureVersion ||
            previousAttempt.StepId != contract.StepId ||
            previousAttempt.State != RecoveryStepState.FailedKnownNoEffect ||
            previousAttempt.FailureEvidence is not
            {
                Kind: RecoveryEvidenceKind.KnownNoEffect,
                IsWellFormed: true
            } noEffect ||
            !StringComparer.Ordinal.Equals(noEffect.SourceComponentId, contract.ProcedureId) ||
            contract.RetryPolicy != RecoveryRetryPolicy.RetryOnlyAfterKnownNoEffectAndFreshAuthorization ||
            !freshAuthorization)
        {
            return false;
        }

        retry = new RecoveryStepAttempt(
            retryAttemptId,
            plan.PlanId,
            plan.Version,
            contract.StepId,
            contract.ProcedureId,
            contract.ProcedureVersion,
            checked(previousAttempt.AttemptNumber + 1),
            RecoveryStepState.Planned);
        return true;
    }
}
