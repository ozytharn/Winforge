using System.Collections.Immutable;
using WinForge.ActionBroker.V2.Lifecycle;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.ActionBroker.V2.Contracts;

public enum ReversibilityClass
{
    Reversible,
    Compensable,
    Irreversible,
    Unknown
}

public enum RollbackEligibility
{
    Prohibited,
    AllowedOnlyWhenEffectIsKnown,
    ManualReviewOnly
}

public enum RollbackAttemptState
{
    NotStarted,
    Started,
    Completed,
    IndependentlyVerified,
    Partial,
    OutcomeUnknown,
    RecoveryRequired
}

public enum RollbackEvidenceKind
{
    KnownComplete,
    KnownPartial,
    OutcomeUnknown
}

public sealed record RollbackOutcomeEvidence(
    string EvidenceId,
    string SourceComponentId,
    string ContentDigest,
    RollbackEvidenceKind Kind,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(EvidenceId) &&
        !string.IsNullOrWhiteSpace(SourceComponentId) &&
        !string.IsNullOrWhiteSpace(ContentDigest) &&
        Enum.IsDefined(Kind) &&
        ObservedAtUtc != default;

    public static RollbackOutcomeEvidence Create(
        string evidenceId,
        string sourceComponentId,
        string contentDigest,
        RollbackEvidenceKind kind,
        DateTimeOffset observedAtUtc)
    {
        var id = CanonicalValue.NormalizeText(evidenceId, nameof(evidenceId), 256);
        var source = CanonicalValue.NormalizeText(sourceComponentId, nameof(sourceComponentId), 256);
        var digest = CanonicalValue.NormalizeText(contentDigest, nameof(contentDigest), 256);
        if (id.Length == 0 || source.Length == 0 || digest.Length == 0 || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Rollback evidence requires a valid identity, source, digest, and kind.");
        }

        return new RollbackOutcomeEvidence(id, source, digest, kind, observedAtUtc.ToUniversalTime());
    }
}

public sealed record RollbackContract
{
    private RollbackContract(
        string procedureId,
        int procedureVersion,
        RollbackEligibility eligibility,
        ImmutableArray<string> preconditions,
        ImmutableArray<string> expectedEffects)
    {
        ProcedureId = procedureId;
        ProcedureVersion = procedureVersion;
        Eligibility = eligibility;
        Preconditions = preconditions;
        ExpectedEffects = expectedEffects;
    }

    public string ProcedureId { get; }
    public int ProcedureVersion { get; }
    public RollbackEligibility Eligibility { get; }
    public ImmutableArray<string> Preconditions { get; }
    public ImmutableArray<string> ExpectedEffects { get; }

    public static RollbackContract Create(
        string procedureId,
        int procedureVersion,
        RollbackEligibility eligibility,
        IEnumerable<string> preconditions,
        IEnumerable<string> expectedEffects)
    {
        ArgumentNullException.ThrowIfNull(preconditions);
        ArgumentNullException.ThrowIfNull(expectedEffects);
        if (!Enum.IsDefined(eligibility))
        {
            throw new ArgumentOutOfRangeException(nameof(eligibility));
        }

        var before = Normalize(preconditions, nameof(preconditions));
        var after = Normalize(expectedEffects, nameof(expectedEffects));
        if (procedureVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(procedureVersion));
        }

        if (eligibility == RollbackEligibility.Prohibited)
        {
            if (!string.IsNullOrWhiteSpace(procedureId))
            {
                throw new ArgumentException("Prohibited rollback cannot name an executable procedure.", nameof(procedureId));
            }

            return new RollbackContract(string.Empty, procedureVersion, eligibility, before, after);
        }

        var normalizedProcedureId = CanonicalValue.NormalizeText(procedureId, nameof(procedureId), 256);
        if (normalizedProcedureId.Length == 0 || before.IsEmpty || after.IsEmpty)
        {
            throw new ArgumentException("An eligible rollback needs a specific procedure, preconditions, and expected effects.");
        }

        return new RollbackContract(
            normalizedProcedureId,
            procedureVersion,
            eligibility,
            before,
            after);
    }

    private static ImmutableArray<string> Normalize(IEnumerable<string> values, string parameterName)
    {
        var items = ImmutableArray.CreateBuilder<string>();
        var scanned = 0;
        foreach (var value in values)
        {
            if (++scanned > 512)
            {
                throw new ArgumentException("Rollback descriptors must be bounded.", parameterName);
            }

            var normalized = CanonicalValue.NormalizeText(value, parameterName, 512);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("Rollback descriptors must be non-empty and bounded.", parameterName);
            }

            if (!items.Contains(normalized, StringComparer.Ordinal))
            {
                if (items.Count == 128)
                {
                    throw new ArgumentException("Rollback descriptors must be non-empty and bounded.", parameterName);
                }

                items.Add(normalized);
            }
        }

        return items.ToImmutable();
    }
}

public sealed record OperationContract
{
    private OperationContract(
        string actionType,
        string version,
        TargetIdentity target,
        ImmutableArray<string> preconditions,
        ImmutableArray<string> expectedPostconditions,
        ReversibilityClass reversibility,
        RollbackContract? rollback)
    {
        ActionType = actionType;
        Version = version;
        Target = target;
        Preconditions = preconditions;
        ExpectedPostconditions = expectedPostconditions;
        Reversibility = reversibility;
        Rollback = rollback;
    }

    public string ActionType { get; }
    public string Version { get; }
    public TargetIdentity Target { get; }
    public ImmutableArray<string> Preconditions { get; }
    public ImmutableArray<string> ExpectedPostconditions { get; }
    public ReversibilityClass Reversibility { get; }
    public RollbackContract? Rollback { get; }

    public static OperationContract Create(
        string actionType,
        string version,
        TargetIdentity target,
        IEnumerable<string> preconditions,
        IEnumerable<string> expectedPostconditions,
        ReversibilityClass reversibility,
        RollbackContract? rollback)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(preconditions);
        ArgumentNullException.ThrowIfNull(expectedPostconditions);
        if (!Enum.IsDefined(reversibility))
        {
            throw new ArgumentOutOfRangeException(nameof(reversibility));
        }

        var before = Normalize(preconditions, nameof(preconditions));
        var after = Normalize(expectedPostconditions, nameof(expectedPostconditions));
        if (before.IsEmpty || after.IsEmpty)
        {
            throw new ArgumentException("Operation contracts require explicit preconditions and expected postconditions.");
        }

        if ((reversibility is ReversibilityClass.Reversible or ReversibilityClass.Compensable) &&
            (rollback is null || rollback.Eligibility == RollbackEligibility.Prohibited))
        {
            throw new ArgumentException("Reversible or compensable operations require a specific recovery contract.", nameof(rollback));
        }

        if ((reversibility is ReversibilityClass.Irreversible or ReversibilityClass.Unknown) &&
            rollback is { Eligibility: RollbackEligibility.AllowedOnlyWhenEffectIsKnown })
        {
            throw new ArgumentException("Irreversible or unknown operations cannot claim generic rollback eligibility.", nameof(rollback));
        }

        var normalizedActionType = CanonicalValue.NormalizeText(actionType, nameof(actionType), 256);
        var normalizedVersion = CanonicalValue.NormalizeText(version, nameof(version), 128);
        if (normalizedActionType.Length == 0 || normalizedVersion.Length == 0)
        {
            throw new ArgumentException("Operation action type and version are required.");
        }

        return new OperationContract(
            normalizedActionType,
            normalizedVersion,
            target,
            before,
            after,
            reversibility,
            rollback);
    }

    private static ImmutableArray<string> Normalize(IEnumerable<string> values, string parameterName)
    {
        var items = ImmutableArray.CreateBuilder<string>();
        var scanned = 0;
        foreach (var value in values)
        {
            if (++scanned > 512)
            {
                throw new ArgumentException("Operation descriptors must be bounded.", parameterName);
            }

            var normalized = CanonicalValue.NormalizeText(value, parameterName, 512);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("Operation descriptors must be non-empty and bounded.", parameterName);
            }

            if (!items.Contains(normalized, StringComparer.Ordinal))
            {
                if (items.Count == 128)
                {
                    throw new ArgumentException("Operation descriptors must be non-empty and bounded.", parameterName);
                }

                items.Add(normalized);
            }
        }

        return items.ToImmutable();
    }
}

public enum RollbackRejection
{
    None,
    IrreversibleOrUnknown,
    ProhibitedByContract,
    ManualReviewRequired,
    PreconditionsNotSatisfied,
    EffectNotKnown,
    TargetMismatch,
    ProcedureMismatch,
    NotAuthorized,
    AlreadyAttempted,
    MissingProcedure
}

public sealed record RollbackCheck(bool Allowed, RollbackRejection Rejection)
{
    public static RollbackCheck Permit() => new(true, RollbackRejection.None);
    public static RollbackCheck Deny(RollbackRejection rejection) => new(false, rejection);
}

public sealed record RollbackAttempt(
    Guid AttemptId,
    string ProcedureId,
    int ProcedureVersion,
    TargetIdentity Target,
    RollbackAttemptState State,
    RollbackOutcomeEvidence? OutcomeEvidence = null,
    VerificationEvidence? VerificationEvidence = null)
{
    public static RollbackAttempt Create(
        Guid attemptId,
        string procedureId,
        int procedureVersion,
        TargetIdentity target,
        RollbackAttemptState state)
    {
        if (attemptId == Guid.Empty ||
            procedureVersion <= 0 ||
            !Enum.IsDefined(state))
        {
            throw new ArgumentException("Rollback attempt needs an identifier, valid state, and positive procedure version.");
        }

        ArgumentNullException.ThrowIfNull(target);
        var normalizedProcedureId = CanonicalValue.NormalizeText(procedureId, nameof(procedureId), 256);
        if (normalizedProcedureId.Length == 0)
        {
            throw new ArgumentException("Rollback attempt procedure ID is required.", nameof(procedureId));
        }

        return new RollbackAttempt(
            attemptId,
            normalizedProcedureId,
            procedureVersion,
            target,
            state);
    }
}

public static class RollbackRules
{
    public static RollbackCheck ValidateAttemptContract(
        OperationContract operation,
        RollbackAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(attempt);
        if (operation.Rollback is null || operation.Rollback.Eligibility == RollbackEligibility.Prohibited)
        {
            return RollbackCheck.Deny(RollbackRejection.ProhibitedByContract);
        }

        if (attempt.AttemptId == Guid.Empty ||
            attempt.ProcedureVersion <= 0 ||
            !Enum.IsDefined(attempt.State))
        {
            return RollbackCheck.Deny(RollbackRejection.ProcedureMismatch);
        }

        if (!StringComparer.Ordinal.Equals(operation.Rollback.ProcedureId, attempt.ProcedureId) ||
            operation.Rollback.ProcedureVersion != attempt.ProcedureVersion)
        {
            return RollbackCheck.Deny(RollbackRejection.ProcedureMismatch);
        }

        if (operation.Target != attempt.Target)
        {
            return RollbackCheck.Deny(RollbackRejection.TargetMismatch);
        }

        return RollbackCheck.Permit();
    }

    public static RollbackCheck CanAttempt(
        OperationContract operation,
        TargetIdentity currentTarget,
        bool effectKnownToHaveOccurred,
        bool preconditionsSatisfied,
        bool separatelyAuthorized,
        bool attemptAlreadyStarted)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(currentTarget);

        if (operation.Reversibility is ReversibilityClass.Irreversible or ReversibilityClass.Unknown)
        {
            return RollbackCheck.Deny(RollbackRejection.IrreversibleOrUnknown);
        }

        if (operation.Rollback is null || operation.Rollback.Eligibility == RollbackEligibility.Prohibited)
        {
            return RollbackCheck.Deny(RollbackRejection.ProhibitedByContract);
        }

        if (operation.Rollback.Eligibility == RollbackEligibility.ManualReviewOnly)
        {
            return RollbackCheck.Deny(RollbackRejection.ManualReviewRequired);
        }

        if (!preconditionsSatisfied)
        {
            return RollbackCheck.Deny(RollbackRejection.PreconditionsNotSatisfied);
        }

        if (!effectKnownToHaveOccurred)
        {
            return RollbackCheck.Deny(RollbackRejection.EffectNotKnown);
        }

        if (operation.Target != currentTarget)
        {
            return RollbackCheck.Deny(RollbackRejection.TargetMismatch);
        }

        if (!separatelyAuthorized)
        {
            return RollbackCheck.Deny(RollbackRejection.NotAuthorized);
        }

        if (attemptAlreadyStarted)
        {
            return RollbackCheck.Deny(RollbackRejection.AlreadyAttempted);
        }

        if (string.IsNullOrWhiteSpace(operation.Rollback.ProcedureId) ||
            operation.Rollback.ProcedureVersion <= 0)
        {
            return RollbackCheck.Deny(RollbackRejection.MissingProcedure);
        }

        return RollbackCheck.Permit();
    }

    public static RollbackTransitionResult EvaluateTransition(
        OperationContract operation,
        RollbackAttempt attempt,
        RollbackAttemptState requested,
        RollbackActor actor,
        RollbackTransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(evidence);
        var contractCheck = ValidateAttemptContract(operation, attempt);
        if (!contractCheck.Allowed)
        {
            return new(false, null, $"Rollback attempt does not match the operation contract: {contractCheck.Rejection}.");
        }

        var rollbackContract = operation.Rollback;
        if (rollbackContract is null)
        {
            return new(false, null, "Rollback operation contract is missing.");
        }

        if (attempt.State == RollbackAttemptState.NotStarted &&
            !CanAttempt(
                operation,
                attempt.Target,
                evidence.EffectKnownToHaveOccurred,
                evidence.PreconditionsSatisfied,
                evidence.SeparatelyAuthorized,
                attemptAlreadyStarted: false).Allowed)
        {
            return new(false, null, "Rollback contract preconditions, known effect, target, or authorization are not satisfied.");
        }


        var allowed = (attempt.State, requested, actor) switch
        {
            (RollbackAttemptState.NotStarted, RollbackAttemptState.Started, RollbackActor.AuthorizationCoordinator) =>
                evidence.SeparatelyAuthorized,
            (RollbackAttemptState.Started, RollbackAttemptState.Completed, RollbackActor.RollbackProcedure) =>
                evidence.Outcome is
                {
                    Kind: RollbackEvidenceKind.KnownComplete,
                    IsWellFormed: true
                } complete &&
                StringComparer.Ordinal.Equals(complete.SourceComponentId, rollbackContract.ProcedureId),
            (RollbackAttemptState.Started, RollbackAttemptState.Partial, RollbackActor.RollbackProcedure) =>
                evidence.Outcome is
                {
                    Kind: RollbackEvidenceKind.KnownPartial,
                    IsWellFormed: true
                } partial &&
                StringComparer.Ordinal.Equals(partial.SourceComponentId, rollbackContract.ProcedureId),
            (RollbackAttemptState.Started, RollbackAttemptState.OutcomeUnknown, RollbackActor.RollbackProcedure) =>
                evidence.Outcome is
                {
                    Kind: RollbackEvidenceKind.OutcomeUnknown,
                    IsWellFormed: true
                } unknown &&
                StringComparer.Ordinal.Equals(unknown.SourceComponentId, rollbackContract.ProcedureId),
            (RollbackAttemptState.Partial or RollbackAttemptState.OutcomeUnknown,
                RollbackAttemptState.RecoveryRequired,
                RollbackActor.RecoveryCoordinator) => true,
            (RollbackAttemptState.Completed, RollbackAttemptState.IndependentlyVerified, RollbackActor.Verifier) =>
                evidence.Verification is
                {
                    PostconditionsSatisfied: true,
                    MeetsModelIndependenceCriteria: true
                } verification &&
                StringComparer.Ordinal.Equals(
                    verification.ExecutorComponentId,
                    rollbackContract.ProcedureId),
            (RollbackAttemptState.Completed, RollbackAttemptState.RecoveryRequired, RollbackActor.RecoveryCoordinator) => true,
            _ => false
        };
        if (!allowed)
        {
            return new(false, null, "Rollback transition lacks required evidence or actor.");
        }

        var updated = requested switch
        {
            RollbackAttemptState.Completed or RollbackAttemptState.Partial or RollbackAttemptState.OutcomeUnknown =>
                attempt with { State = requested, OutcomeEvidence = evidence.Outcome },
            RollbackAttemptState.IndependentlyVerified =>
                attempt with { State = requested, VerificationEvidence = evidence.Verification },
            _ => attempt with { State = requested }
        };
        return new(true, updated, null);
    }
}

public enum RollbackActor
{
    AuthorizationCoordinator,
    RollbackProcedure,
    Verifier,
    RecoveryCoordinator
}

public sealed record RollbackTransitionEvidence
{
    public bool SeparatelyAuthorized { get; init; }
    public bool EffectKnownToHaveOccurred { get; init; }
    public bool PreconditionsSatisfied { get; init; }
    public RollbackOutcomeEvidence? Outcome { get; init; }
    public VerificationEvidence? Verification { get; init; }
}

public sealed record RollbackTransitionResult(bool Allowed, RollbackAttempt? Attempt, string? Reason);
