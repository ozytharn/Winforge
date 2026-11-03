using WinForge.ActionBroker.V2.Lifecycle;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.UnitTests.ActionBrokerV2;

public sealed class AuthorizationLifecycleTests
{
    [Fact]
    public void Approval_is_request_policy_identity_and_challenge_bound_and_one_time()
    {
        var request = ActionBrokerV2TestData.Request();
        var approval = ApprovalRecord.CreateModelRecord(ActionBrokerV2TestData.ApprovalId, request);

        Assert.True(AuthorizationRules.Evaluate(request, approval, ActionBrokerV2TestData.Now).Allowed);
        var recaptured = CallerIdentityEvidence.CreateUnverified(
            request.Identity.Evidence.Connection,
            request.Identity.Evidence.Process,
            request.Identity.Evidence.LogonSession,
            request.Identity.Evidence.CapturedAtUtc.AddSeconds(1));
        Assert.True(AuthorizationRules.Evaluate(
            request, approval, recaptured, ActionBrokerV2TestData.Now).Allowed);
        var reconnect = ActionBrokerV2TestData.Identity(
            connectionId: Guid.Parse("20000000-0000-0000-0000-000000000010")).Evidence;
        Assert.Equal(
            AuthorizationRejection.IdentityMismatch,
            AuthorizationRules.Evaluate(request, approval, reconnect, ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.BindingMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(parameters: ActionBrokerV2TestData.Parameters(value: CanonicalValue.FromString("light"))),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.BindingMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(target: TargetIdentity.Create("preference", "notifications", "v1")),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.PolicyMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(policyVersion: "policy-2"),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.ChallengeMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(challenge: Guid.Parse("20000000-0000-0000-0000-000000000001")),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.IdentityMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(identity: ActionBrokerV2TestData.Identity(processId: 4321)),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.RequestMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(requestId: Guid.Parse("20000000-0000-0000-0000-000000000003")),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.AuthorizationMismatch,
            AuthorizationRules.Evaluate(
                ActionBrokerV2TestData.Request(authorizationId: Guid.Parse("20000000-0000-0000-0000-000000000011")),
                approval,
                ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.ApprovalNotAvailable,
            AuthorizationRules.Evaluate(request, approval with { State = ApprovalUseState.Consumed }, ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.ExpiryMismatch,
            AuthorizationRules.Evaluate(
                request,
                approval with { ExpiresAtUtc = approval.ExpiresAtUtc.AddSeconds(1) },
                ActionBrokerV2TestData.Now).Rejection);
    }

    [Fact]
    public void Expired_and_not_yet_valid_authorizations_are_denied()
    {
        var expiredRequest = ActionBrokerV2TestData.Request(expiresAt: ActionBrokerV2TestData.Now);
        var expiredApproval = ApprovalRecord.CreateModelRecord(ActionBrokerV2TestData.ApprovalId, expiredRequest);
        var futureRequest = ActionBrokerV2TestData.Request(
            createdAt: ActionBrokerV2TestData.Now.AddMinutes(1),
            expiresAt: ActionBrokerV2TestData.Now.AddMinutes(2));
        var futureApproval = ApprovalRecord.CreateModelRecord(Guid.Parse("20000000-0000-0000-0000-000000000004"), futureRequest);

        Assert.Equal(
            AuthorizationRejection.RequestExpired,
            AuthorizationRules.Evaluate(expiredRequest, expiredApproval, ActionBrokerV2TestData.Now).Rejection);
        Assert.Equal(
            AuthorizationRejection.RequestNotYetValid,
            AuthorizationRules.Evaluate(futureRequest, futureApproval, ActionBrokerV2TestData.Now).Rejection);
    }

    [Fact]
    public async Task Approval_cannot_be_consumed_twice_concurrently()
    {
        var request = ActionBrokerV2TestData.Request();
        var approval = ApprovalRecord.CreateModelRecord(ActionBrokerV2TestData.ApprovalId, request);
        var ledger = new ModelApprovalLedger();

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => ledger.Consume(request, approval, ActionBrokerV2TestData.Now))));

        Assert.Single(results, static result => result.Allowed);
        Assert.Equal(31, results.Count(static result => !result.Allowed));
        Assert.True(ledger.IsConsumed);
    }

    [Fact]
    public void Approval_consumption_commit_failures_are_distinguishable_as_before_or_after_commit()
    {
        var request = ActionBrokerV2TestData.Request();
        var approval = ApprovalRecord.CreateModelRecord(ActionBrokerV2TestData.ApprovalId, request);
        var before = new ModelApprovalLedger();
        Assert.Throws<ModelCommitException>(() => before.Consume(
            request, approval, ActionBrokerV2TestData.Now, ModelCommitFault.BeforeCommit));
        Assert.False(before.IsConsumed);

        var after = new ModelApprovalLedger();
        var exception = Assert.Throws<ModelCommitException>(() => after.Consume(
            request, approval, ActionBrokerV2TestData.Now, ModelCommitFault.AfterCommit));
        Assert.True(exception.CommitMayHaveSucceeded);
        Assert.True(after.IsConsumed);
        Assert.Equal(
            AuthorizationRejection.ApprovalNotAvailable,
            after.Consume(request, approval, ActionBrokerV2TestData.Now).Rejection);
        Assert.True(before.Consume(request, approval, ActionBrokerV2TestData.Now).Allowed);
    }

    [Fact]
    public void Lifecycle_requires_the_approval_and_durable_start_barriers()
    {
        var missingApprovalConsumption = AuthorizationLifecycle.Evaluate(
            LifecycleState.Approved,
            LifecycleState.Accepted,
            LifecycleActor.BrokerPolicy,
            new LifecycleTransitionEvidence
            {
                RequestBindingMatches = true,
                ApprovalCurrent = true,
                PolicyAndTargetFresh = true
            });
        var missingStartCommit = AuthorizationLifecycle.Evaluate(
            LifecycleState.Accepted,
            LifecycleState.Started,
            LifecycleActor.BrokerDispatcher,
            new LifecycleTransitionEvidence { PolicyAndTargetFresh = true });
        var wrongActor = AuthorizationLifecycle.Evaluate(
            LifecycleState.Accepted,
            LifecycleState.Started,
            LifecycleActor.OperationHandler,
            new LifecycleTransitionEvidence { StartBarrierCommitted = true, PolicyAndTargetFresh = true });

        Assert.False(missingApprovalConsumption.Allowed);
        Assert.False(missingStartCommit.Allowed);
        Assert.False(wrongActor.Allowed);
        Assert.True(AuthorizationLifecycle.Evaluate(
            LifecycleState.Accepted,
            LifecycleState.Started,
            LifecycleActor.BrokerDispatcher,
            new LifecycleTransitionEvidence { StartBarrierCommitted = true, PolicyAndTargetFresh = true }).Allowed);
    }

    [Fact]
    public void Executor_success_and_shared_dependency_checks_are_not_independent_verification()
    {
        var sameExecutor = new VerificationEvidence(
            "executor",
            "executor",
            "executor",
            VerificationEvidenceKind.SameExecutorReturnValue,
            true,
            ActionBrokerV2TestData.Now);
        var sharedDependencies = sameExecutor with
        {
            VerifierComponentId = "separate-verifier",
            EvidenceSourceId = "shared-local-cache",
            Kind = VerificationEvidenceKind.SeparateComponentSharedDependencies
        };
        var independent = sameExecutor with
        {
            VerifierComponentId = "independent-verifier",
            EvidenceSourceId = "independent-state-source",
            Kind = VerificationEvidenceKind.IndependentEvidenceSource
        };

        Assert.False(VerifiedTransition(sameExecutor).Allowed);
        Assert.False(VerifiedTransition(sharedDependencies).Allowed);
        Assert.True(VerifiedTransition(independent).Allowed);
        Assert.False(VerifiedTransition(independent with { PostconditionsSatisfied = false }).Allowed);
    }

    [Fact]
    public void Recovery_reconciliation_requires_verified_disposition()
    {
        Assert.False(AuthorizationLifecycle.Evaluate(
            LifecycleState.RecoveryRequired,
            LifecycleState.Reconciled,
            LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence()).Allowed);
        Assert.True(AuthorizationLifecycle.Evaluate(
            LifecycleState.RecoveryRequired,
            LifecycleState.Reconciled,
            LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence { RecoveryDispositionVerified = true }).Allowed);
    }

    [Fact]
    public void Deterministic_fake_store_exercises_before_and_after_failure_for_every_lifecycle_transition()
    {
        foreach (var scenario in TransitionScenarios.All())
        {
            var before = new FakeDurableStateStore(scenario.From);
            Assert.Throws<ModelCommitException>(() => before.Transition(
                scenario.To, scenario.Actor, scenario.Evidence, ModelCommitFault.BeforeCommit));
            Assert.Equal(scenario.From, before.State);

            var after = new FakeDurableStateStore(scenario.From);
            var exception = Assert.Throws<ModelCommitException>(() => after.Transition(
                scenario.To, scenario.Actor, scenario.Evidence, ModelCommitFault.AfterCommit));
            Assert.True(exception.CommitMayHaveSucceeded);
            Assert.Equal(scenario.To, after.State);
        }
    }

    private static LifecycleTransitionResult VerifiedTransition(VerificationEvidence verification) =>
        AuthorizationLifecycle.Evaluate(
            LifecycleState.Completed,
            LifecycleState.Verified,
            LifecycleActor.Verifier,
            new LifecycleTransitionEvidence { Verification = verification });
}

internal enum ModelCommitFault
{
    None,
    BeforeCommit,
    AfterCommit
}

internal sealed class ModelCommitException(bool commitMayHaveSucceeded)
    : Exception(commitMayHaveSucceeded ? "Injected failure after model commit." : "Injected failure before model commit.")
{
    public bool CommitMayHaveSucceeded { get; } = commitMayHaveSucceeded;
}

internal sealed class ModelApprovalLedger
{
    private readonly object _gate = new();
    private bool _consumed;

    public bool IsConsumed
    {
        get
        {
            lock (_gate)
            {
                return _consumed;
            }
        }
    }

    public AuthorizationCheck Consume(
        ActionRequest request,
        ApprovalRecord approval,
        DateTimeOffset nowUtc,
        ModelCommitFault fault = ModelCommitFault.None)
    {
        lock (_gate)
        {
            if (_consumed)
            {
                return AuthorizationCheck.Deny(AuthorizationRejection.ApprovalNotAvailable);
            }

            var check = AuthorizationRules.Evaluate(request, approval, nowUtc);
            if (!check.Allowed)
            {
                return check;
            }

            if (fault == ModelCommitFault.BeforeCommit)
            {
                throw new ModelCommitException(commitMayHaveSucceeded: false);
            }

            _consumed = true;
            if (fault == ModelCommitFault.AfterCommit)
            {
                throw new ModelCommitException(commitMayHaveSucceeded: true);
            }

            return check;
        }
    }
}

internal sealed class FakeDurableStateStore(LifecycleState initialState)
{
    public LifecycleState State { get; private set; } = initialState;

    public void Transition(
        LifecycleState requested,
        LifecycleActor actor,
        LifecycleTransitionEvidence evidence,
        ModelCommitFault fault)
    {
        var result = AuthorizationLifecycle.Evaluate(State, requested, actor, evidence);
        if (!result.Allowed || result.NewState is null)
        {
            throw new InvalidOperationException(result.RejectionReason);
        }

        if (fault == ModelCommitFault.BeforeCommit)
        {
            throw new ModelCommitException(commitMayHaveSucceeded: false);
        }

        State = result.NewState.Value;
        if (fault == ModelCommitFault.AfterCommit)
        {
            throw new ModelCommitException(commitMayHaveSucceeded: true);
        }
    }
}

internal sealed record TransitionScenario(
    LifecycleState From,
    LifecycleState To,
    LifecycleActor Actor,
    LifecycleTransitionEvidence Evidence);

internal static class TransitionScenarios
{
    public static IReadOnlyList<TransitionScenario> All()
    {
        var scenarios = new List<TransitionScenario>();
        void Add(LifecycleState from, LifecycleState to, LifecycleActor actor, LifecycleTransitionEvidence? evidence = null) =>
            scenarios.Add(new TransitionScenario(from, to, actor, evidence ?? new LifecycleTransitionEvidence()));

        var fresh = new LifecycleTransitionEvidence { RequestBindingMatches = true, PolicyAndTargetFresh = true };
        Add(LifecycleState.Proposed, LifecycleState.Validated, LifecycleActor.BrokerPolicy, fresh);
        Add(LifecycleState.Proposed, LifecycleState.Rejected, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Proposed, LifecycleState.Expired, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Proposed, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Validated, LifecycleState.ApprovalPending, LifecycleActor.BrokerPolicy, fresh);
        Add(LifecycleState.Validated, LifecycleState.Rejected, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Validated, LifecycleState.Expired, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Validated, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.ApprovalPending, LifecycleState.Approved, LifecycleActor.ConsentComponent,
            new LifecycleTransitionEvidence { RequestBindingMatches = true, ApprovalCurrent = true });
        Add(LifecycleState.ApprovalPending, LifecycleState.Rejected, LifecycleActor.ConsentComponent);
        Add(LifecycleState.ApprovalPending, LifecycleState.Expired, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.ApprovalPending, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Approved, LifecycleState.Accepted, LifecycleActor.BrokerPolicy,
            new LifecycleTransitionEvidence
            {
                RequestBindingMatches = true,
                ApprovalCurrent = true,
                ApprovalConsumedDurably = true,
                PolicyAndTargetFresh = true
            });
        Add(LifecycleState.Approved, LifecycleState.Rejected, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Approved, LifecycleState.Expired, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Approved, LifecycleState.Cancelled, LifecycleActor.BrokerPolicy);
        Add(LifecycleState.Accepted, LifecycleState.Started, LifecycleActor.BrokerDispatcher,
            new LifecycleTransitionEvidence { StartBarrierCommitted = true, PolicyAndTargetFresh = true });
        Add(LifecycleState.Accepted, LifecycleState.Cancelled, LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence { NoDispatchProven = true });
        Add(LifecycleState.Accepted, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence { LedgerIntegrityCertain = false });
        Add(LifecycleState.Started, LifecycleState.Completed, LifecycleActor.OperationHandler,
            new LifecycleTransitionEvidence { KnownOutcome = true });
        Add(LifecycleState.Started, LifecycleState.Failed, LifecycleActor.OperationHandler,
            new LifecycleTransitionEvidence { KnownOutcome = true });
        Add(LifecycleState.Started, LifecycleState.Interrupted, LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence { CrashObserved = true });
        Add(LifecycleState.Started, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence { LedgerIntegrityCertain = false });
        Add(LifecycleState.Completed, LifecycleState.Verified, LifecycleActor.Verifier,
            new LifecycleTransitionEvidence
            {
                Verification = new VerificationEvidence(
                    "executor", "independent-verifier", "independent-source",
                    VerificationEvidenceKind.IndependentEvidenceSource, true, ActionBrokerV2TestData.Now)
            });
        Add(LifecycleState.Completed, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator);
        Add(LifecycleState.Interrupted, LifecycleState.RecoveryRequired, LifecycleActor.RecoveryCoordinator);
        Add(LifecycleState.RecoveryRequired, LifecycleState.Reconciled, LifecycleActor.RecoveryCoordinator,
            new LifecycleTransitionEvidence { RecoveryDispositionVerified = true });
        return scenarios;
    }
}
