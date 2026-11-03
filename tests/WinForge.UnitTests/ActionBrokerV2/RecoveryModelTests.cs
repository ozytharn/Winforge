using WinForge.ActionBroker.V2.Lifecycle;
using WinForge.ActionBroker.V2.Recovery;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.UnitTests.ActionBrokerV2;

public sealed class RecoveryModelTests
{
    [Fact]
    public void Recovery_plan_has_stable_identity_version_and_ordered_dependencies()
    {
        var first = Step(Guid.Parse("30000000-0000-0000-0000-000000000001"), 0);
        var second = Step(
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            1,
            [first.StepId]);
        var plan = RecoveryPlan.Create(
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            2,
            ActionBrokerV2TestData.RequestId,
            TargetIdentity.Create("synthetic-target", "plan-target"),
            [second, first]);

        Assert.Equal(2, plan.Version);
        Assert.Equal([first.StepId, second.StepId], plan.Steps.Select(static step => step.StepId));
        Assert.Throws<ArgumentException>(() => RecoveryPlan.Create(
            Guid.Parse("30000000-0000-0000-0000-000000000004"),
            1,
            ActionBrokerV2TestData.RequestId,
            TargetIdentity.Create("synthetic-target", "plan-target"),
            [
                Step(Guid.Parse("30000000-0000-0000-0000-000000000005"), 0,
                    [Guid.Parse("30000000-0000-0000-0000-000000000006")]),
                Step(Guid.Parse("30000000-0000-0000-0000-000000000006"), 1)
            ]));
    }

    [Fact]
    public void Recovery_attempt_cannot_be_used_with_a_different_plan_version()
    {
        var contract = Step();
        var plan = Plan(contract);
        var differentVersion = RecoveryPlan.Create(
            plan.PlanId,
            plan.Version + 1,
            plan.RequestId,
            plan.Target,
            [contract]);
        var attempt = RecoveryStepAttempt.CreateInitial(ActionBrokerV2TestData.AttemptId, plan, contract);

        var result = RecoveryStepRules.Evaluate(
            differentVersion,
            contract,
            attempt,
            RecoveryStepState.IntentRecorded,
            RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence
            {
                DurableIntent = Evidence(RecoveryEvidenceKind.DurableIntent, "intent")
            });

        Assert.False(result.Allowed);
        Assert.Contains("plan and procedure versions", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_step_intent_requires_matching_target_preconditions_and_verified_dependencies()
    {
        var prerequisite = Step(Guid.Parse("30000000-0000-0000-0000-000000000030"), 0);
        var dependent = Step(
            Guid.Parse("30000000-0000-0000-0000-000000000031"),
            1,
            [prerequisite.StepId]);
        var plan = RecoveryPlan.Create(
            Guid.Parse("30000000-0000-0000-0000-000000000032"),
            1,
            ActionBrokerV2TestData.RequestId,
            TargetIdentity.Create("synthetic-target", "plan-target"),
            [prerequisite, dependent]);
        var initial = RecoveryStepAttempt.CreateInitial(
            Guid.Parse("30000000-0000-0000-0000-000000000033"),
            plan,
            dependent);
        var noDependencyEvidence = IntentEvidence(plan);
        var unverifiedDependencyEvidence = noDependencyEvidence with
        {
            DependencyStates = System.Collections.Immutable.ImmutableDictionary<Guid, RecoveryStepState>.Empty
                .Add(prerequisite.StepId, RecoveryStepState.Completed)
        };
        var verifiedDependencyEvidence = noDependencyEvidence with
        {
            DependencyStates = System.Collections.Immutable.ImmutableDictionary<Guid, RecoveryStepState>.Empty
                .Add(prerequisite.StepId, RecoveryStepState.IndependentlyVerified)
        };

        Assert.False(RecoveryStepRules.Evaluate(
            plan, dependent, initial, RecoveryStepState.IntentRecorded, RecoveryActor.Coordinator,
            noDependencyEvidence).Allowed);
        Assert.False(RecoveryStepRules.Evaluate(
            plan, dependent, initial, RecoveryStepState.IntentRecorded, RecoveryActor.Coordinator,
            unverifiedDependencyEvidence).Allowed);
        Assert.True(RecoveryStepRules.Evaluate(
            plan, dependent, initial, RecoveryStepState.IntentRecorded, RecoveryActor.Coordinator,
            verifiedDependencyEvidence).Allowed);
        Assert.False(RecoveryStepRules.Evaluate(
            plan,
            dependent,
            initial,
            RecoveryStepState.IntentRecorded,
            RecoveryActor.Coordinator,
            verifiedDependencyEvidence with { CurrentTarget = TargetIdentity.Create("synthetic-target", "changed-target") }).Allowed);
    }

    [Fact]
    public void Recovery_step_requires_durable_intent_before_completion()
    {
        var contract = Step();
        var plan = Plan(contract);
        var initial = RecoveryStepAttempt.CreateInitial(ActionBrokerV2TestData.AttemptId, plan, contract);
        var noIntent = RecoveryStepRules.Evaluate(
            plan,
            contract,
            initial,
            RecoveryStepState.Completed,
            RecoveryActor.StepProcedure,
            new RecoveryStepTransitionEvidence
            {
                Completion = Evidence(RecoveryEvidenceKind.CompletionReported, "completed")
            });
        var intent = RecoveryStepRules.Evaluate(
            plan,
            contract,
            initial,
            RecoveryStepState.IntentRecorded,
            RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence
            {
                PreconditionsSatisfied = true,
                CurrentTarget = plan.Target,
                DurableIntent = Evidence(RecoveryEvidenceKind.DurableIntent, "intent")
            });
        var completed = RecoveryStepRules.Evaluate(
            plan,
            contract,
            intent.Attempt!,
            RecoveryStepState.Completed,
            RecoveryActor.StepProcedure,
            new RecoveryStepTransitionEvidence
            {
                Completion = Evidence(RecoveryEvidenceKind.CompletionReported, "completion")
            });

        Assert.False(noIntent.Allowed);
        Assert.Equal(RecoveryStepState.IntentRecorded, intent.Attempt!.State);
        Assert.Equal(RecoveryStepState.Completed, completed.Attempt!.State);
    }

    [Fact]
    public void Recovery_step_verification_requires_an_independent_source_and_passed_postconditions()
    {
        var contract = Step();
        var plan = Plan(contract);
        var completed = RecoveryStepAttempt.CreateInitial(ActionBrokerV2TestData.AttemptId, plan, contract) with
        {
            State = RecoveryStepState.Completed,
            CompletionEvidence = Evidence(RecoveryEvidenceKind.CompletionReported, "completion")
        };
        var shared = new VerificationEvidence(
            "operation-specific-recovery-procedure",
            "separate-verifier",
            "shared-store",
            VerificationEvidenceKind.SeparateComponentSharedDependencies,
            true,
            ActionBrokerV2TestData.Now);
        var independent = shared with
        {
            EvidenceSourceId = "independent-state-source",
            Kind = VerificationEvidenceKind.IndependentEvidenceSource
        };

        Assert.False(RecoveryStepRules.Evaluate(
            plan,
            contract,
            completed,
            RecoveryStepState.IndependentlyVerified,
            RecoveryActor.Verifier,
            new RecoveryStepTransitionEvidence { Verification = shared }).Allowed);
        Assert.True(RecoveryStepRules.Evaluate(
            plan,
            contract,
            completed,
            RecoveryStepState.IndependentlyVerified,
            RecoveryActor.Verifier,
            new RecoveryStepTransitionEvidence { Verification = independent }).Allowed);
        Assert.False(RecoveryStepRules.Evaluate(
            plan,
            contract,
            completed,
            RecoveryStepState.IndependentlyVerified,
            RecoveryActor.Verifier,
            new RecoveryStepTransitionEvidence
            {
                Verification = independent with { ExecutorComponentId = "different-procedure" }
            }).Allowed);
        Assert.False(RecoveryStepRules.Evaluate(
            plan,
            contract,
            completed,
            RecoveryStepState.IndependentlyVerified,
            RecoveryActor.Verifier,
            new RecoveryStepTransitionEvidence { Verification = independent with { PostconditionsSatisfied = false } }).Allowed);
    }

    [Fact]
    public void Crash_classification_distinguishes_not_started_completed_unverified_and_verified()
    {
        Assert.Equal(RecoveryCrashDisposition.NotStarted,
            RecoveryStepRules.ClassifyCrash(RecoveryStepState.Planned));
        Assert.Equal(RecoveryCrashDisposition.ReconcileWithoutReplay,
            RecoveryStepRules.ClassifyCrash(RecoveryStepState.IntentRecorded));
        Assert.Equal(RecoveryCrashDisposition.ReconcileWithoutReplay,
            RecoveryStepRules.ClassifyCrash(RecoveryStepState.Completed));
        Assert.Equal(RecoveryCrashDisposition.AlreadyVerified,
            RecoveryStepRules.ClassifyCrash(RecoveryStepState.IndependentlyVerified));
        Assert.Equal(RecoveryCrashDisposition.KnownNoEffect,
            RecoveryStepRules.ClassifyCrash(RecoveryStepState.FailedKnownNoEffect));
    }

    [Fact]
    public void Uncertain_step_is_never_replayed_and_only_known_no_effect_retry_can_be_reauthorized()
    {
        var contract = Step(
            ActionBrokerV2TestData.StepId,
            0,
            retryPolicy: RecoveryRetryPolicy.RetryOnlyAfterKnownNoEffectAndFreshAuthorization);
        var plan = Plan(contract);
        var uncertain = RecoveryStepAttempt.CreateInitial(ActionBrokerV2TestData.AttemptId, plan, contract) with
        {
            State = RecoveryStepState.RecoveryRequired,
            FailureEvidence = Evidence(RecoveryEvidenceKind.AmbiguousOutcome, "ambiguous")
        };
        var knownNoEffect = uncertain with
        {
            State = RecoveryStepState.FailedKnownNoEffect,
            FailureEvidence = Evidence(RecoveryEvidenceKind.KnownNoEffect, "no-effect")
        };
        var attemptId = Guid.Parse("40000000-0000-0000-0000-000000000001");

        Assert.False(RecoveryStepRules.CanCreateRetry(plan, contract, uncertain, attemptId, freshAuthorization: true, out _));
        Assert.False(RecoveryStepRules.CanCreateRetry(plan, contract, knownNoEffect, attemptId, freshAuthorization: false, out _));
        Assert.True(RecoveryStepRules.CanCreateRetry(plan, contract, knownNoEffect, attemptId, freshAuthorization: true, out var retry));
        Assert.NotNull(retry);
        Assert.Equal(RecoveryStepState.Planned, retry!.State);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.False(RecoveryStepRules.CanCreateRetry(
            plan,
            contract,
            knownNoEffect with { AttemptNumber = int.MaxValue },
            Guid.Parse("40000000-0000-0000-0000-000000000002"),
            freshAuthorization: true,
            out _));
    }

    [Fact]
    public void Deterministic_fake_store_injects_crashes_before_and_after_each_recovery_step_transition()
    {
        foreach (var scenario in RecoveryTransitionScenarios.All())
        {
            var before = new FakeRecoveryStepStore(scenario.Plan, scenario.Contract, scenario.Initial);
            Assert.Throws<ModelCommitException>(() => before.Transition(
                scenario.To,
                scenario.Actor,
                scenario.Evidence,
                ModelCommitFault.BeforeCommit));
            Assert.Equal(scenario.Initial.State, before.Attempt.State);

            var after = new FakeRecoveryStepStore(scenario.Plan, scenario.Contract, scenario.Initial);
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
    public void Recovery_state_does_not_transition_back_to_intent_or_auto_replay()
    {
        var contract = Step();
        var plan = Plan(contract);
        var uncertain = RecoveryStepAttempt.CreateInitial(ActionBrokerV2TestData.AttemptId, plan, contract) with
        {
            State = RecoveryStepState.RecoveryRequired,
            FailureEvidence = Evidence(RecoveryEvidenceKind.AmbiguousOutcome, "unknown")
        };

        var retry = RecoveryStepRules.Evaluate(
            plan,
            contract,
            uncertain,
            RecoveryStepState.IntentRecorded,
            RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence
            {
                DurableIntent = Evidence(RecoveryEvidenceKind.DurableIntent, "second-intent")
            });

        Assert.False(retry.Allowed);
    }

    private static RecoveryStepContract Step(
        Guid? stepId = null,
        int ordinal = 0,
        IEnumerable<Guid>? dependencies = null,
        RecoveryRetryPolicy retryPolicy = RecoveryRetryPolicy.Never) =>
        RecoveryStepContract.Create(
            stepId ?? ActionBrokerV2TestData.StepId,
            ordinal,
            "operation-specific-recovery-procedure",
            procedureVersion: 1,
            dependencies ?? [],
            ["precondition:target-identity-current"],
            ["postcondition:expected-recovery-state"],
            retryPolicy);

    private static RecoveryPlan Plan(RecoveryStepContract step) =>
        RecoveryPlan.Create(
            Guid.Parse("30000000-0000-0000-0000-000000000020"),
            1,
            ActionBrokerV2TestData.RequestId,
            TargetIdentity.Create("synthetic-target", "plan-target"),
            [step]);

    private static RecoveryEvidence Evidence(RecoveryEvidenceKind kind, string evidenceId) =>
        RecoveryEvidence.Create(
            evidenceId,
            "operation-specific-recovery-procedure",
            $"digest-{evidenceId}",
            kind,
            ActionBrokerV2TestData.Now);

    private static RecoveryStepTransitionEvidence IntentEvidence(RecoveryPlan plan) =>
        new()
        {
            PreconditionsSatisfied = true,
            CurrentTarget = plan.Target,
            DurableIntent = Evidence(RecoveryEvidenceKind.DurableIntent, "dependency-intent")
        };
}

internal sealed class FakeRecoveryStepStore(
    RecoveryPlan plan,
    RecoveryStepContract contract,
    RecoveryStepAttempt initial)
{
    public RecoveryStepAttempt Attempt { get; private set; } = initial;

    public void Transition(
        RecoveryStepState requested,
        RecoveryActor actor,
        RecoveryStepTransitionEvidence evidence,
        ModelCommitFault fault)
    {
        var result = RecoveryStepRules.Evaluate(plan, contract, Attempt, requested, actor, evidence);
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

internal sealed record RecoveryTransitionScenario(
    RecoveryPlan Plan,
    RecoveryStepContract Contract,
    RecoveryStepAttempt Initial,
    RecoveryStepState To,
    RecoveryActor Actor,
    RecoveryStepTransitionEvidence Evidence);

internal static class RecoveryTransitionScenarios
{
    public static IReadOnlyList<RecoveryTransitionScenario> All()
    {
        var result = new List<RecoveryTransitionScenario>();
        var contract = RecoveryStepContract.Create(
            ActionBrokerV2TestData.StepId,
            0,
            "recovery-procedure",
            1,
            [],
            ["precondition:target-current"],
            ["postcondition:recovered"],
            RecoveryRetryPolicy.Never);
        var plan = RecoveryPlan.Create(
            Guid.Parse("30000000-0000-0000-0000-000000000021"),
            1,
            ActionBrokerV2TestData.RequestId,
            TargetIdentity.Create("synthetic-target", "plan-target"),
            [contract]);
        var initial = RecoveryStepAttempt.CreateInitial(ActionBrokerV2TestData.AttemptId, plan, contract);
        var intent = RecoveryEvidence.Create(
            "durable-intent", "coordinator", "digest-intent",
            RecoveryEvidenceKind.DurableIntent, ActionBrokerV2TestData.Now);
        var completion = RecoveryEvidence.Create(
            "completion", "recovery-procedure", "digest-completion",
            RecoveryEvidenceKind.CompletionReported, ActionBrokerV2TestData.Now);
        var noEffect = RecoveryEvidence.Create(
            "known-no-effect", "recovery-procedure", "digest-no-effect",
            RecoveryEvidenceKind.KnownNoEffect, ActionBrokerV2TestData.Now);
        var ambiguity = RecoveryEvidence.Create(
            "ambiguous", "coordinator", "digest-ambiguous",
            RecoveryEvidenceKind.AmbiguousOutcome, ActionBrokerV2TestData.Now);
        var independent = new VerificationEvidence(
            "recovery-procedure", "independent-verifier", "independent-source",
            VerificationEvidenceKind.IndependentEvidenceSource, true, ActionBrokerV2TestData.Now);

        result.Add(new(plan, contract, initial, RecoveryStepState.IntentRecorded, RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence
            {
                PreconditionsSatisfied = true,
                CurrentTarget = plan.Target,
                DurableIntent = intent
            }));
        var intentState = initial with { State = RecoveryStepState.IntentRecorded, IntentEvidence = intent };
        result.Add(new(plan, contract, intentState, RecoveryStepState.Completed, RecoveryActor.StepProcedure,
            new RecoveryStepTransitionEvidence { Completion = completion }));
        result.Add(new(plan, contract, intentState, RecoveryStepState.FailedKnownNoEffect, RecoveryActor.StepProcedure,
            new RecoveryStepTransitionEvidence { KnownNoEffect = noEffect }));
        result.Add(new(plan, contract, intentState, RecoveryStepState.OutcomeUnknown, RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence { Ambiguity = ambiguity }));
        result.Add(new(plan, contract, intentState, RecoveryStepState.RecoveryRequired, RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence { Ambiguity = ambiguity }));
        result.Add(new(plan, contract, intentState with { State = RecoveryStepState.OutcomeUnknown }, RecoveryStepState.RecoveryRequired,
            RecoveryActor.Coordinator, new RecoveryStepTransitionEvidence { Ambiguity = ambiguity }));
        var completed = intentState with { State = RecoveryStepState.Completed, CompletionEvidence = completion };
        result.Add(new(plan, contract, completed, RecoveryStepState.IndependentlyVerified, RecoveryActor.Verifier,
            new RecoveryStepTransitionEvidence { Verification = independent }));
        result.Add(new(plan, contract, completed, RecoveryStepState.RecoveryRequired, RecoveryActor.Coordinator,
            new RecoveryStepTransitionEvidence()));
        return result;
    }
}
