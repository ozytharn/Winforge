using System.Collections.Immutable;
using WinForge.ActionBroker.V2.Binding;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.ActionBroker.V2.Lifecycle;

public enum LifecycleState
{
    Proposed,
    Validated,
    ApprovalPending,
    Approved,
    Accepted,
    Started,
    Completed,
    Verified,
    Rejected,
    Expired,
    Cancelled,
    Failed,
    Interrupted,
    RecoveryRequired,
    Reconciled
}

public enum LifecycleActor
{
    BrokerPolicy,
    ConsentComponent,
    BrokerDispatcher,
    OperationHandler,
    Verifier,
    RecoveryCoordinator
}

public enum VerificationEvidenceKind
{
    SameExecutorReturnValue,
    SeparateComponentSharedDependencies,
    IndependentEvidenceSource
}

public sealed record VerificationEvidence(
    string ExecutorComponentId,
    string VerifierComponentId,
    string EvidenceSourceId,
    VerificationEvidenceKind Kind,
    bool PostconditionsSatisfied,
    DateTimeOffset ObservedAtUtc)
{
    public bool MeetsModelIndependenceCriteria =>
        Kind == VerificationEvidenceKind.IndependentEvidenceSource &&
        !string.IsNullOrWhiteSpace(ExecutorComponentId) &&
        !string.IsNullOrWhiteSpace(VerifierComponentId) &&
        !string.IsNullOrWhiteSpace(EvidenceSourceId) &&
        !StringComparer.Ordinal.Equals(ExecutorComponentId, VerifierComponentId) &&
        !StringComparer.Ordinal.Equals(ExecutorComponentId, EvidenceSourceId) &&
        !StringComparer.Ordinal.Equals(VerifierComponentId, EvidenceSourceId) &&
        Enum.IsDefined(Kind) &&
        ObservedAtUtc != default;
}

public sealed record LifecycleTransitionEvidence
{
    public bool RequestBindingMatches { get; init; }
    public bool ApprovalCurrent { get; init; }
    public bool ApprovalConsumedDurably { get; init; }
    public bool PolicyAndTargetFresh { get; init; }
    public bool StartBarrierCommitted { get; init; }
    public bool DispatchInvariantCertain { get; init; }
    public bool KnownOutcome { get; init; }
    public bool CrashObserved { get; init; }
    public bool NoDispatchProven { get; init; }
    public bool LedgerIntegrityCertain { get; init; }
    public bool RecoveryDispositionVerified { get; init; }
    public VerificationEvidence? Verification { get; init; }
}

public sealed record LifecycleTransitionResult(
    bool Allowed,
    LifecycleState? NewState,
    string? RejectionReason)
{
    public static LifecycleTransitionResult Permit(LifecycleState state) => new(true, state, null);
    public static LifecycleTransitionResult Deny(string reason) => new(false, null, reason);
}

public static class AuthorizationLifecycle
{
    public static LifecycleTransitionResult Evaluate(
        LifecycleState current,
        LifecycleState requested,
        LifecycleActor actor,
        LifecycleTransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!IsTopologicallyAllowed(current, requested))
        {
            return LifecycleTransitionResult.Deny($"Transition {current} -> {requested} is not permitted.");
        }

        var allowed = (current, requested, actor) switch
        {
            (LifecycleState.Proposed, LifecycleState.Validated, LifecycleActor.BrokerPolicy) =>
                evidence.RequestBindingMatches && evidence.PolicyAndTargetFresh,
            (LifecycleState.Proposed, LifecycleState.Rejected, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Proposed, LifecycleState.Expired, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Proposed, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Validated, LifecycleState.ApprovalPending, LifecycleActor.BrokerPolicy) =>
                evidence.RequestBindingMatches && evidence.PolicyAndTargetFresh,
            (LifecycleState.Validated, LifecycleState.Rejected, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Validated, LifecycleState.Expired, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Validated, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.ApprovalPending, LifecycleState.Approved, LifecycleActor.ConsentComponent) =>
                evidence.RequestBindingMatches && evidence.ApprovalCurrent,
            (LifecycleState.ApprovalPending, LifecycleState.Rejected, LifecycleActor.ConsentComponent) => true,
            (LifecycleState.ApprovalPending, LifecycleState.Rejected, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.ApprovalPending, LifecycleState.Expired, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.ApprovalPending, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Approved, LifecycleState.Accepted, LifecycleActor.BrokerPolicy) =>
                evidence.RequestBindingMatches &&
                evidence.ApprovalCurrent &&
                evidence.ApprovalConsumedDurably &&
                evidence.PolicyAndTargetFresh,
            (LifecycleState.Approved, LifecycleState.Rejected, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Approved, LifecycleState.Expired, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Approved, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy) => true,
            (LifecycleState.Accepted, LifecycleState.Started, LifecycleActor.BrokerDispatcher) =>
                evidence.StartBarrierCommitted && evidence.PolicyAndTargetFresh,
            (LifecycleState.Accepted, LifecycleState.Cancelled, LifecycleActor.RecoveryCoordinator) =>
                evidence.NoDispatchProven,
            (LifecycleState.Accepted, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator) =>
                !evidence.LedgerIntegrityCertain || !evidence.DispatchInvariantCertain,
            (LifecycleState.Started, LifecycleState.Completed, LifecycleActor.OperationHandler) => evidence.KnownOutcome,
            (LifecycleState.Started, LifecycleState.Failed, LifecycleActor.OperationHandler) => evidence.KnownOutcome,
            (LifecycleState.Started, LifecycleState.Interrupted, LifecycleActor.RecoveryCoordinator) => evidence.CrashObserved,
            (LifecycleState.Started, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator) =>
                !evidence.LedgerIntegrityCertain,
            (LifecycleState.Completed, LifecycleState.Verified, LifecycleActor.Verifier) =>
                evidence.Verification is { PostconditionsSatisfied: true, MeetsModelIndependenceCriteria: true },
            (LifecycleState.Completed, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator) => true,
            (LifecycleState.Interrupted, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator) => true,
            (LifecycleState.RecoveryRequired, LifecycleState.Reconciled, LifecycleActor.RecoveryCoordinator) =>
                evidence.RecoveryDispositionVerified,
            _ => false
        };

        return allowed
            ? LifecycleTransitionResult.Permit(requested)
            : LifecycleTransitionResult.Deny($"Required transition evidence is missing or actor {actor} is not authoritative.");
    }

    public static bool IsTopologicallyAllowed(LifecycleState current, LifecycleState requested) =>
        (current, requested) switch
        {
            (LifecycleState.Proposed, LifecycleState.Validated or LifecycleState.Rejected or LifecycleState.Expired or LifecycleState.Cancelled) => true,
            (LifecycleState.Validated, LifecycleState.ApprovalPending or LifecycleState.Rejected or LifecycleState.Expired or LifecycleState.Cancelled) => true,
            (LifecycleState.ApprovalPending, LifecycleState.Approved or LifecycleState.Rejected or LifecycleState.Expired or LifecycleState.Cancelled) => true,
            (LifecycleState.Approved, LifecycleState.Accepted or LifecycleState.Rejected or LifecycleState.Expired or LifecycleState.Cancelled) => true,
            (LifecycleState.Accepted, LifecycleState.Started or LifecycleState.Cancelled or LifecycleState.RecoveryRequired) => true,
            (LifecycleState.Started, LifecycleState.Completed or LifecycleState.Failed or LifecycleState.Interrupted or LifecycleState.RecoveryRequired) => true,
            (LifecycleState.Completed, LifecycleState.Verified or LifecycleState.RecoveryRequired) => true,
            (LifecycleState.Interrupted, LifecycleState.RecoveryRequired) => true,
            (LifecycleState.RecoveryRequired, LifecycleState.Reconciled) => true,
            _ => false
        };
}

public enum ApprovalUseState
{
    Available,
    Consumed,
    Revoked,
    Expired
}

public enum ApprovalEvidenceKind
{
    ModeledSessionUserConfirmation
}

public sealed record ApprovalRecord(
    Guid ApprovalId,
    Guid RequestId,
    Guid AuthorizationId,
    string BoundRequestDigest,
    string BoundIdentityDigest,
    string PolicyVersion,
    Guid Challenge,
    DateTimeOffset ExpiresAtUtc,
    ApprovalEvidenceKind EvidenceKind,
    ApprovalUseState State)
{
    public static ApprovalRecord CreateModelRecord(Guid approvalId, ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (approvalId == Guid.Empty)
        {
            throw new ArgumentException("Approval identifier must not be empty.", nameof(approvalId));
        }

        return new ApprovalRecord(
            approvalId,
            request.RequestId,
            request.Authorization.AuthorizationId,
            CanonicalRequestBinder.ComputeDigest(request),
            CanonicalRequestBinder.ComputeIdentityDigest(request.Identity.Evidence),
            request.Authorization.PolicyVersion,
            request.Authorization.Challenge,
            request.Authorization.ExpiresAtUtc,
            ApprovalEvidenceKind.ModeledSessionUserConfirmation,
            ApprovalUseState.Available);
    }
}

public enum AuthorizationRejection
{
    None,
    RequestExpired,
    RequestNotYetValid,
    ApprovalNotAvailable,
    RequestMismatch,
    AuthorizationMismatch,
    BindingMismatch,
    IdentityMismatch,
    PolicyMismatch,
    ChallengeMismatch,
    ExpiryMismatch,
    ApprovalExpired
}

public sealed record AuthorizationCheck(bool Allowed, AuthorizationRejection Rejection)
{
    public static AuthorizationCheck Permit() => new(true, AuthorizationRejection.None);
    public static AuthorizationCheck Deny(AuthorizationRejection rejection) => new(false, rejection);
}

public static class AuthorizationRules
{
    public static AuthorizationCheck Evaluate(
        ActionRequest request,
        ApprovalRecord approval,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Evaluate(request, approval, request.Identity.Evidence, nowUtc);
    }

    public static AuthorizationCheck Evaluate(
        ActionRequest request,
        ApprovalRecord approval,
        CallerIdentityEvidence? currentIdentity,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approval);
        if (currentIdentity is null)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.IdentityMismatch);
        }

        if (request.RequestId != approval.RequestId)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.RequestMismatch);
        }

        if (request.Authorization.AuthorizationId != approval.AuthorizationId)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.AuthorizationMismatch);
        }

        if (approval.ExpiresAtUtc.ToUniversalTime() != request.Authorization.ExpiresAtUtc)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.ExpiryMismatch);
        }

        var now = nowUtc.ToUniversalTime();
        if (now < request.Authorization.CreatedAtUtc)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.RequestNotYetValid);
        }

        if (now >= request.Authorization.ExpiresAtUtc)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.RequestExpired);
        }

        if (now >= approval.ExpiresAtUtc)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.ApprovalExpired);
        }

        if (approval.State == ApprovalUseState.Expired)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.ApprovalExpired);
        }

        if (approval.State != ApprovalUseState.Available)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.ApprovalNotAvailable);
        }

        if (!StringComparer.Ordinal.Equals(request.Authorization.PolicyVersion, approval.PolicyVersion))
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.PolicyMismatch);
        }

        if (request.Authorization.Challenge != approval.Challenge)
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.ChallengeMismatch);
        }

        if (!CanonicalRequestBinder.IsSameConnection(request.Identity.Evidence, currentIdentity) ||
            !StringComparer.Ordinal.Equals(
                CanonicalRequestBinder.ComputeIdentityDigest(currentIdentity),
                approval.BoundIdentityDigest))
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.IdentityMismatch);
        }

        if (!StringComparer.Ordinal.Equals(
                CanonicalRequestBinder.ComputeDigest(request),
                approval.BoundRequestDigest))
        {
            return AuthorizationCheck.Deny(AuthorizationRejection.BindingMismatch);
        }

        return AuthorizationCheck.Permit();
    }
}
