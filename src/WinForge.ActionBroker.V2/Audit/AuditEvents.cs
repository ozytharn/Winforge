using System.Collections.Immutable;
using WinForge.ActionBroker.V2.Lifecycle;

namespace WinForge.ActionBroker.V2.Audit;

public enum AuditEventKind
{
    ProposalCreated,
    ProposalValidated,
    ApprovalPending,
    ApprovalRecorded,
    ProposalRejected,
    ProposalExpired,
    RequestCancelled,
    ExecutionAccepted,
    ExecutionStarted,
    HandlerCompleted,
    IndependentlyVerified,
    ExecutionFailed,
    ExecutionInterrupted,
    RecoveryRequired,
    RequestReconciled,
    RecoveryStepIntent,
    RecoveryStepCompleted,
    RecoveryStepVerified,
    RollbackStarted,
    RollbackCompleted,
    RollbackPartial,
    RollbackVerified,
    RollbackOutcomeUnknown
}

public sealed record ActionAuditEvent(
    Guid EventId,
    Guid RequestId,
    Guid CorrelationId,
    long Sequence,
    AuditEventKind Kind,
    LifecycleState? StateAfter,
    DateTimeOffset OccurredAtUtc,
    string? EvidenceDigest = null,
    Guid? RecoveryStepId = null,
    Guid? AttemptId = null);

public sealed record AuditValidationResult(ImmutableArray<string> Errors)
{
    public bool IsValid => Errors.IsEmpty;
}

public static class AuditEventValidator
{
    public static AuditValidationResult Validate(IEnumerable<ActionAuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var items = events.ToImmutableArray();
        var errors = ImmutableArray.CreateBuilder<string>();
        if (items.IsEmpty)
        {
            return new AuditValidationResult(errors.ToImmutable());
        }

        var first = items[0];
        if (first.RequestId == Guid.Empty || first.CorrelationId == Guid.Empty)
        {
            errors.Add("Audit request and correlation identifiers must be non-empty.");
        }

        if (items.Select(static item => item.EventId).Distinct().Count() != items.Length)
        {
            errors.Add("Audit event identifiers must be unique.");
        }

        LifecycleState? priorState = null;
        var stepProgress = new Dictionary<(Guid StepId, Guid AttemptId), AuditEventKind>();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (item.Sequence != index + 1)
            {
                errors.Add($"Audit sequence must be contiguous at index {index}.");
            }

            if (item.RequestId != first.RequestId || item.CorrelationId != first.CorrelationId)
            {
                errors.Add("Audit events must retain one request and correlation identity.");
            }

            if (item.EventId == Guid.Empty || item.OccurredAtUtc == default)
            {
                errors.Add("Audit events require a non-empty ID and timestamp.");
            }

            if (TryExpectedState(item.Kind, out var expectedState))
            {
                if (item.StateAfter != expectedState)
                {
                    errors.Add($"Audit event {item.Kind} must record state {expectedState}.");
                }

                if (priorState is null)
                {
                    if (expectedState != LifecycleState.Proposed)
                    {
                        errors.Add("The first lifecycle audit event must create a Proposed request.");
                    }
                }
                else if (expectedState != priorState &&
                    !AuthorizationLifecycle.IsTopologicallyAllowed(priorState.Value, expectedState))
                {
                    errors.Add($"Audit lifecycle order {priorState} -> {expectedState} is invalid.");
                }

                priorState = expectedState;
            }
            else if (item.StateAfter is not null ||
                item.RecoveryStepId is null || item.RecoveryStepId == Guid.Empty ||
                item.AttemptId is null || item.AttemptId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.EvidenceDigest))
            {
                errors.Add($"Non-lifecycle event {item.Kind} requires recovery-step and attempt identities, an evidence digest, and no lifecycle state.");
            }
            else
            {
                var key = (item.RecoveryStepId.Value, item.AttemptId.Value);
                var hasPriorStepEvent = stepProgress.TryGetValue(key, out var previousStepEvent);
                var stepAllowed = priorState == LifecycleState.RecoveryRequired &&
                    (item.Kind switch
                    {
                        AuditEventKind.RecoveryStepIntent => !hasPriorStepEvent,
                        AuditEventKind.RecoveryStepCompleted => previousStepEvent == AuditEventKind.RecoveryStepIntent,
                        AuditEventKind.RecoveryStepVerified => previousStepEvent == AuditEventKind.RecoveryStepCompleted,
                        AuditEventKind.RollbackStarted => !hasPriorStepEvent,
                        AuditEventKind.RollbackCompleted or AuditEventKind.RollbackPartial =>
                            previousStepEvent == AuditEventKind.RollbackStarted,
                        AuditEventKind.RollbackVerified => previousStepEvent == AuditEventKind.RollbackCompleted,
                        AuditEventKind.RollbackOutcomeUnknown => previousStepEvent == AuditEventKind.RollbackStarted,
                        _ => false
                    });
                if (!stepAllowed)
                {
                    errors.Add($"Audit step event {item.Kind} is out of order or lacks a RecoveryRequired lifecycle state.");
                }

                stepProgress[key] = item.Kind;
            }
        }

        return new AuditValidationResult(errors.ToImmutable());
    }

    private static bool TryExpectedState(AuditEventKind kind, out LifecycleState state)
    {
        switch (kind)
        {
            case AuditEventKind.ProposalCreated: state = LifecycleState.Proposed; return true;
            case AuditEventKind.ProposalValidated: state = LifecycleState.Validated; return true;
            case AuditEventKind.ApprovalPending: state = LifecycleState.ApprovalPending; return true;
            case AuditEventKind.ApprovalRecorded: state = LifecycleState.Approved; return true;
            case AuditEventKind.ProposalRejected: state = LifecycleState.Rejected; return true;
            case AuditEventKind.ProposalExpired: state = LifecycleState.Expired; return true;
            case AuditEventKind.RequestCancelled: state = LifecycleState.Cancelled; return true;
            case AuditEventKind.ExecutionAccepted: state = LifecycleState.Accepted; return true;
            case AuditEventKind.ExecutionStarted: state = LifecycleState.Started; return true;
            case AuditEventKind.HandlerCompleted: state = LifecycleState.Completed; return true;
            case AuditEventKind.IndependentlyVerified: state = LifecycleState.Verified; return true;
            case AuditEventKind.ExecutionFailed: state = LifecycleState.Failed; return true;
            case AuditEventKind.ExecutionInterrupted: state = LifecycleState.Interrupted; return true;
            case AuditEventKind.RecoveryRequired: state = LifecycleState.RecoveryRequired; return true;
            case AuditEventKind.RequestReconciled: state = LifecycleState.Reconciled; return true;
            default:
                state = default;
                return false;
        }
    }
}
