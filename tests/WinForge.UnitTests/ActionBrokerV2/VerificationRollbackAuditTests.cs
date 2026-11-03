using WinForge.ActionBroker.V2.Audit;
using WinForge.ActionBroker.V2.Contracts;
using WinForge.ActionBroker.V2.Lifecycle;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.UnitTests.ActionBrokerV2;

public sealed class VerificationRollbackAuditTests
{
    [Fact]
    public void Operation_contract_requires_target_preconditions_and_expected_postconditions()
    {
        var target = TargetIdentity.Create("synthetic-target", "target-1", "revision-1");
        var exception = Assert.Throws<ArgumentException>(() => OperationContract.Create(
            "synthetic.operation",
            "1.0",
            target,
            [],
            ["postcondition:known"],
            ReversibilityClass.Unknown,
            rollback: null));

        Assert.Contains("preconditions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_rollback_is_denied_for_irreversible_or_unknown_operations()
    {
        var target = TargetIdentity.Create("synthetic-target", "target-1");
        var rollback = RollbackContract.Create(
            "specific-compensation-procedure",
            1,
            RollbackEligibility.AllowedOnlyWhenEffectIsKnown,
            ["precondition:target-current"],
            ["postcondition:compensation-complete"]);

        Assert.Throws<ArgumentException>(() => OperationContract.Create(
            "irreversible.synthetic",
            "1.0",
            target,
            ["precondition:valid"],
            ["postcondition:complete"],
            ReversibilityClass.Irreversible,
            rollback));
        Assert.Throws<ArgumentException>(() => OperationContract.Create(
            "unknown.synthetic",
            "1.0",
            target,
            ["precondition:valid"],
            ["postcondition:complete"],
            ReversibilityClass.Unknown,
            rollback));
    }

    [Fact]
    public void Rollback_requires_known_effect_matching_target_and_separate_authorization()
    {
        var operation = ReversibleOperation();
        var target = operation.Target;

        Assert.True(RollbackRules.CanAttempt(operation, target, true, true, true, false).Allowed);
        Assert.Equal(RollbackRejection.EffectNotKnown,
            RollbackRules.CanAttempt(operation, target, false, true, true, false).Rejection);
        Assert.Equal(RollbackRejection.NotAuthorized,
            RollbackRules.CanAttempt(operation, target, true, true, false, false).Rejection);
        Assert.Equal(RollbackRejection.TargetMismatch,
            RollbackRules.CanAttempt(
                operation,
                TargetIdentity.Create("synthetic-target", "other-target"),
                true,
                true,
                true,
                false).Rejection);
        Assert.Equal(RollbackRejection.AlreadyAttempted,
            RollbackRules.CanAttempt(operation, target, true, true, true, true).Rejection);
        Assert.Equal(RollbackRejection.PreconditionsNotSatisfied,
            RollbackRules.CanAttempt(operation, target, true, false, true, false).Rejection);

        var manualReviewRollback = RollbackContract.Create(
            "manual-recovery-procedure",
            1,
            RollbackEligibility.ManualReviewOnly,
            ["precondition:review-target"],
            ["postcondition:reviewed"]);
        var manualReviewOperation = OperationContract.Create(
            "manual-recovery.operation",
            "1.0",
            target,
            ["precondition:valid"],
            ["postcondition:complete"],
            ReversibilityClass.Compensable,
            manualReviewRollback);
        Assert.Equal(
            RollbackRejection.ManualReviewRequired,
            RollbackRules.CanAttempt(manualReviewOperation, target, true, true, true, false).Rejection);
    }

    [Fact]
    public void Rollback_procedure_and_version_must_match_the_operation_contract()
    {
        var operation = ReversibleOperation();
        var valid = RollbackAttempt.Create(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            "specific-compensation-procedure",
            1,
            operation.Target,
            RollbackAttemptState.NotStarted);
        var wrongVersion = valid with { ProcedureVersion = 2 };
        var wrongProcedure = valid with { ProcedureId = "different-procedure" };

        Assert.True(RollbackRules.ValidateAttemptContract(operation, valid).Allowed);
        Assert.Equal(RollbackRejection.ProcedureMismatch,
            RollbackRules.ValidateAttemptContract(operation, wrongVersion).Rejection);
        Assert.Equal(RollbackRejection.ProcedureMismatch,
            RollbackRules.ValidateAttemptContract(operation, wrongProcedure).Rejection);
        Assert.Throws<ArgumentException>(() => RollbackAttempt.Create(
            Guid.Parse("50000000-0000-0000-0000-000000000011"),
            "",
            1,
            operation.Target,
            RollbackAttemptState.NotStarted));
    }

    [Fact]
    public void Partial_and_unknown_rollback_outcomes_require_recovery_and_never_verify_as_success()
    {
        var operation = ReversibleOperation();
        var started = RollbackAttempt.Create(
            Guid.Parse("50000000-0000-0000-0000-000000000002"),
            "specific-compensation-procedure",
            1,
            operation.Target,
            RollbackAttemptState.Started);
        var partial = RollbackRules.EvaluateTransition(
            operation,
            started,
            RollbackAttemptState.Partial,
            RollbackActor.RollbackProcedure,
            new RollbackTransitionEvidence
            {
                Outcome = RollbackEvidence(RollbackEvidenceKind.KnownPartial, "partial")
            });
        var recovery = RollbackRules.EvaluateTransition(
            operation,
            partial.Attempt!,
            RollbackAttemptState.RecoveryRequired,
            RollbackActor.RecoveryCoordinator,
            new RollbackTransitionEvidence());
        var unknown = RollbackRules.EvaluateTransition(
            operation,
            started,
            RollbackAttemptState.OutcomeUnknown,
            RollbackActor.RollbackProcedure,
            new RollbackTransitionEvidence
            {
                Outcome = RollbackEvidence(RollbackEvidenceKind.OutcomeUnknown, "unknown")
            });
        var unknownVerify = RollbackRules.EvaluateTransition(
            operation,
            unknown.Attempt!,
            RollbackAttemptState.IndependentlyVerified,
            RollbackActor.Verifier,
            new RollbackTransitionEvidence { Verification = IndependentEvidence() });
        var completed = RollbackRules.EvaluateTransition(
            operation,
            started,
            RollbackAttemptState.Completed,
            RollbackActor.RollbackProcedure,
            new RollbackTransitionEvidence
            {
                Outcome = RollbackEvidence(RollbackEvidenceKind.KnownComplete, "complete")
            });
        var wrongProcedureOutcome = RollbackRules.EvaluateTransition(
            operation,
            started,
            RollbackAttemptState.Completed,
            RollbackActor.RollbackProcedure,
            new RollbackTransitionEvidence
            {
                Outcome = RollbackEvidence(RollbackEvidenceKind.KnownComplete, "wrong-procedure") with
                {
                    SourceComponentId = "different-procedure"
                }
            });
        var failedVerification = RollbackRules.EvaluateTransition(
            operation,
            completed.Attempt!,
            RollbackAttemptState.IndependentlyVerified,
            RollbackActor.Verifier,
            new RollbackTransitionEvidence
            {
                Verification = IndependentEvidence() with { PostconditionsSatisfied = false }
            });

        Assert.True(partial.Allowed);
        Assert.True(recovery.Allowed);
        Assert.True(unknown.Allowed);
        Assert.False(unknownVerify.Allowed);
        Assert.Equal(RollbackEvidenceKind.KnownPartial, partial.Attempt!.OutcomeEvidence!.Kind);
        Assert.False(wrongProcedureOutcome.Allowed);
        Assert.False(failedVerification.Allowed);
        Assert.False(RollbackRules.EvaluateTransition(
            operation,
            completed.Attempt!,
            RollbackAttemptState.IndependentlyVerified,
            RollbackActor.Verifier,
            new RollbackTransitionEvidence
            {
                Verification = IndependentEvidence() with { ExecutorComponentId = "different-procedure" }
            }).Allowed);
    }

    [Fact]
    public void Rollback_state_transitions_have_deterministic_before_and_after_commit_crash_windows()
    {
        foreach (var scenario in RollbackTransitionScenarios.All())
        {
            var before = new FakeRollbackStore(scenario.Operation, scenario.Initial);
            Assert.Throws<ModelCommitException>(() => before.Transition(
                scenario.To,
                scenario.Actor,
                scenario.Evidence,
                ModelCommitFault.BeforeCommit));
            Assert.Equal(scenario.Initial.State, before.Attempt.State);

            var after = new FakeRollbackStore(scenario.Operation, scenario.Initial);
            var exception = Assert.Throws<ModelCommitException>(() => after.Transition(
                scenario.To,
                scenario.Actor,
                scenario.Evidence,
                ModelCommitFault.AfterCommit));
            Assert.True(exception.CommitMayHaveSucceeded);
            Assert.Equal(scenario.To, after.Attempt.State);
        }
    }

    [Fact]
    public void Audit_events_require_contiguous_order_valid_lifecycle_and_shared_correlation()
    {
        var valid = new[]
        {
            Event(1, AuditEventKind.ProposalCreated, LifecycleState.Proposed),
            Event(2, AuditEventKind.ProposalValidated, LifecycleState.Validated),
            Event(3, AuditEventKind.ApprovalPending, LifecycleState.ApprovalPending),
            Event(4, AuditEventKind.ApprovalRecorded, LifecycleState.Approved),
            Event(5, AuditEventKind.ExecutionAccepted, LifecycleState.Accepted),
            Event(6, AuditEventKind.ExecutionStarted, LifecycleState.Started),
            Event(7, AuditEventKind.HandlerCompleted, LifecycleState.Completed),
            Event(8, AuditEventKind.IndependentlyVerified, LifecycleState.Verified)
        };
        var invalid = valid
            .Select((item, index) => item with { Sequence = index + 1 })
            .ToArray();
        invalid[4] = invalid[4] with { StateAfter = LifecycleState.Started };
        invalid[6] = invalid[6] with { CorrelationId = Guid.Parse("60000000-0000-0000-0000-000000000001") };

        Assert.True(AuditEventValidator.Validate(valid).IsValid);
        var result = AuditEventValidator.Validate(invalid);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Contains("must record state", StringComparison.Ordinal));
        Assert.Contains(result.Errors, static error => error.Contains("one request and correlation", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_rejects_gaps_and_non_lifecycle_events_without_step_correlation()
    {
        var events = new[]
        {
            Event(1, AuditEventKind.ProposalCreated, LifecycleState.Proposed),
            Event(3, AuditEventKind.RecoveryStepIntent, null) with { RecoveryStepId = null }
        };
        var result = AuditEventValidator.Validate(events);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Contains("contiguous", StringComparison.Ordinal));
        Assert.Contains(result.Errors, static error => error.Contains("recovery-step and attempt identities", StringComparison.Ordinal));

        var missingEvidence = new[]
        {
            Event(1, AuditEventKind.ProposalCreated, LifecycleState.Proposed),
            Event(2, AuditEventKind.RecoveryStepIntent, null) with { EvidenceDigest = null }
        };
        Assert.Contains(
            AuditEventValidator.Validate(missingEvidence).Errors,
            static error => error.Contains("evidence digest", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_requires_recovery_step_intent_completion_and_verification_order()
    {
        var events = new[]
        {
            Event(1, AuditEventKind.ProposalCreated, LifecycleState.Proposed),
            Event(2, AuditEventKind.ProposalValidated, LifecycleState.Validated),
            Event(3, AuditEventKind.ApprovalPending, LifecycleState.ApprovalPending),
            Event(4, AuditEventKind.ApprovalRecorded, LifecycleState.Approved),
            Event(5, AuditEventKind.ExecutionAccepted, LifecycleState.Accepted),
            Event(6, AuditEventKind.ExecutionStarted, LifecycleState.Started),
            Event(7, AuditEventKind.HandlerCompleted, LifecycleState.Completed),
            Event(8, AuditEventKind.RecoveryRequired, LifecycleState.RecoveryRequired),
            Event(9, AuditEventKind.RecoveryStepIntent, null),
            Event(10, AuditEventKind.RecoveryStepCompleted, null),
            Event(11, AuditEventKind.RecoveryStepVerified, null)
        };
        var invalid = events.ToArray();
        invalid[9] = invalid[9] with { Kind = AuditEventKind.RecoveryStepVerified };

        Assert.True(AuditEventValidator.Validate(events).IsValid);
        Assert.False(AuditEventValidator.Validate(invalid).IsValid);
    }

    private static OperationContract ReversibleOperation()
    {
        var target = TargetIdentity.Create("synthetic-target", "target-1", "revision-1");
        var rollback = RollbackContract.Create(
            "specific-compensation-procedure",
            1,
            RollbackEligibility.AllowedOnlyWhenEffectIsKnown,
            ["precondition:target-current"],
            ["postcondition:compensation-complete"]);
        return OperationContract.Create(
            "synthetic.operation",
            "1.0",
            target,
            ["precondition:target-current"],
            ["postcondition:expected-effect"],
            ReversibilityClass.Compensable,
            rollback);
    }

    private static VerificationEvidence IndependentEvidence() =>
        new(
            "specific-compensation-procedure",
            "independent-verifier",
            "independent-source",
            VerificationEvidenceKind.IndependentEvidenceSource,
            true,
            ActionBrokerV2TestData.Now);

    private static RollbackOutcomeEvidence RollbackEvidence(RollbackEvidenceKind kind, string evidenceId) =>
        RollbackOutcomeEvidence.Create(
            evidenceId,
            "specific-compensation-procedure",
            $"digest-{evidenceId}",
            kind,
            ActionBrokerV2TestData.Now);

    private static ActionAuditEvent Event(long sequence, AuditEventKind kind, LifecycleState? state) =>
        new(
            Guid.Parse($"70000000-0000-0000-0000-{sequence:000000000000}"),
            ActionBrokerV2TestData.RequestId,
            ActionBrokerV2TestData.CorrelationId,
            sequence,
            kind,
            state,
            ActionBrokerV2TestData.Now,
            EvidenceDigest: kind is AuditEventKind.RecoveryStepIntent or AuditEventKind.RecoveryStepCompleted or
                AuditEventKind.RecoveryStepVerified or AuditEventKind.RollbackStarted or
                AuditEventKind.RollbackCompleted or AuditEventKind.RollbackPartial or AuditEventKind.RollbackVerified or
                AuditEventKind.RollbackOutcomeUnknown
                ? "modeled-evidence-digest"
                : null,
            RecoveryStepId: kind is AuditEventKind.RecoveryStepIntent or AuditEventKind.RecoveryStepCompleted or
                AuditEventKind.RecoveryStepVerified or AuditEventKind.RollbackStarted or
                AuditEventKind.RollbackCompleted or AuditEventKind.RollbackPartial or AuditEventKind.RollbackVerified or
                AuditEventKind.RollbackOutcomeUnknown
                ? ActionBrokerV2TestData.StepId
                : null,
            AttemptId: kind is AuditEventKind.RecoveryStepIntent or AuditEventKind.RecoveryStepCompleted or
                AuditEventKind.RecoveryStepVerified or AuditEventKind.RollbackStarted or
                AuditEventKind.RollbackCompleted or AuditEventKind.RollbackPartial or AuditEventKind.RollbackVerified or
                AuditEventKind.RollbackOutcomeUnknown
                ? ActionBrokerV2TestData.AttemptId
                : null);
}

internal sealed class FakeRollbackStore(OperationContract operation, RollbackAttempt initial)
{
    public RollbackAttempt Attempt { get; private set; } = initial;

    public void Transition(
        RollbackAttemptState requested,
        RollbackActor actor,
        RollbackTransitionEvidence evidence,
        ModelCommitFault fault)
    {
        var result = RollbackRules.EvaluateTransition(operation, Attempt, requested, actor, evidence);
        if (!result.Allowed || result.Attempt is null)
        {
            throw new InvalidOperationException(result.Reason);
        }

        if (fault == ModelCommitFault.BeforeCommit)
        {
            throw new ModelCommitException(commitMayHaveSucceeded: false);
        }

        Attempt = result.Attempt;
        if (fault == ModelCommitFault.AfterCommit)
        {
            throw new ModelCommitException(commitMayHaveSucceeded: true);
        }
    }
}

internal sealed record RollbackTransitionScenario(
    OperationContract Operation,
    RollbackAttempt Initial,
    RollbackAttemptState To,
    RollbackActor Actor,
    RollbackTransitionEvidence Evidence);

internal static class RollbackTransitionScenarios
{
    public static IReadOnlyList<RollbackTransitionScenario> All()
    {
        var target = TargetIdentity.Create("synthetic-target", "rollback-target");
        var initial = RollbackAttempt.Create(
            Guid.Parse("50000000-0000-0000-0000-000000000010"),
            "synthetic-rollback",
            1,
            target,
            RollbackAttemptState.NotStarted);
        var rollbackContract = RollbackContract.Create(
            "synthetic-rollback",
            1,
            RollbackEligibility.AllowedOnlyWhenEffectIsKnown,
            ["precondition:target-current"],
            ["postcondition:compensation-complete"]);
        var operation = OperationContract.Create(
            "synthetic-operation",
            "1.0",
            target,
            ["precondition:target-current"],
            ["postcondition:operation-complete"],
            ReversibilityClass.Compensable,
            rollbackContract);
        var started = initial with { State = RollbackAttemptState.Started };
        var completed = started with
        {
            State = RollbackAttemptState.Completed,
            OutcomeEvidence = RollbackOutcomeEvidence.Create(
                "done", "synthetic-rollback", "digest-done",
                RollbackEvidenceKind.KnownComplete, ActionBrokerV2TestData.Now)
        };
        var partial = started with
        {
            State = RollbackAttemptState.Partial,
            OutcomeEvidence = RollbackOutcomeEvidence.Create(
                "partial", "synthetic-rollback", "digest-partial",
                RollbackEvidenceKind.KnownPartial, ActionBrokerV2TestData.Now)
        };
        var unknown = started with
        {
            State = RollbackAttemptState.OutcomeUnknown,
            OutcomeEvidence = RollbackOutcomeEvidence.Create(
                "unknown", "synthetic-rollback", "digest-unknown",
                RollbackEvidenceKind.OutcomeUnknown, ActionBrokerV2TestData.Now)
        };
        var independent = new VerificationEvidence(
            "synthetic-rollback", "independent-verifier", "independent-source",
            VerificationEvidenceKind.IndependentEvidenceSource, true, ActionBrokerV2TestData.Now);
        return
        [
            new(operation, initial, RollbackAttemptState.Started, RollbackActor.AuthorizationCoordinator,
                new RollbackTransitionEvidence
                {
                    SeparatelyAuthorized = true,
                    EffectKnownToHaveOccurred = true,
                    PreconditionsSatisfied = true
                }),
            new(operation, started, RollbackAttemptState.Completed, RollbackActor.RollbackProcedure,
                new RollbackTransitionEvidence
                {
                    Outcome = RollbackOutcomeEvidence.Create(
                        "done", "synthetic-rollback", "digest-done",
                        RollbackEvidenceKind.KnownComplete, ActionBrokerV2TestData.Now)
                }),
            new(operation, started, RollbackAttemptState.Partial, RollbackActor.RollbackProcedure,
                new RollbackTransitionEvidence
                {
                    Outcome = RollbackOutcomeEvidence.Create(
                        "partial", "synthetic-rollback", "digest-partial",
                        RollbackEvidenceKind.KnownPartial, ActionBrokerV2TestData.Now)
                }),
            new(operation, started, RollbackAttemptState.OutcomeUnknown, RollbackActor.RollbackProcedure,
                new RollbackTransitionEvidence
                {
                    Outcome = RollbackOutcomeEvidence.Create(
                        "unknown", "synthetic-rollback", "digest-unknown",
                        RollbackEvidenceKind.OutcomeUnknown, ActionBrokerV2TestData.Now)
                }),
            new(operation, partial, RollbackAttemptState.RecoveryRequired, RollbackActor.RecoveryCoordinator,
                new RollbackTransitionEvidence()),
            new(operation, unknown, RollbackAttemptState.RecoveryRequired, RollbackActor.RecoveryCoordinator,
                new RollbackTransitionEvidence()),
            new(operation, completed, RollbackAttemptState.IndependentlyVerified, RollbackActor.Verifier,
                new RollbackTransitionEvidence { Verification = independent }),
            new(operation, completed, RollbackAttemptState.RecoveryRequired, RollbackActor.RecoveryCoordinator,
                new RollbackTransitionEvidence())
        ];
    }
}
