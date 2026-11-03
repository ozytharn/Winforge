# WinForge Action Broker Simulation Security Testing

**Status:** Simulation-only security test plan with verified implementation coverage. No privileged system-changing execution is performed.

The current implementation validates the request/approval lifecycle and execution journal in the application layer, then exposes the workflow through the WPF shell. Actual automated coverage includes exact request/approval binding, recomputed-preview tampering, expiry with a controlled clock, parameter/target mismatch, unknown actions and targets, concurrent duplicate execution, restart replay, interrupted journal recovery, cancellation, pre-execution journal failures, journal corruption/concurrent persistence, and ViewModel preview invalidation plus the complete simulation workflow.

The full independent audit, finding identifiers, reproduction paths, fixes, and residual risks are recorded in [ACTION_SECURITY_AUDIT.md](./ACTION_SECURITY_AUDIT.md).

## Release-readiness verification

Latest verification on 2026-11-03:

- Release solution build: 0 warnings and 0 errors.
- Full solution tests: 46 unit and 51 Windows integration tests passed; 0 failures and 0 skipped tests.
- Focused Action Broker tests: 16 core unit tests and 7 integration tests passed; 0 failures and 0 skipped tests.
- Release WPF smoke test: startup, catalog navigation, preview generation, explicit approval, simulation execution, journal inspection, and graceful shutdown passed. Persisted lifecycle: `proposed → approved → started → completed`.
- Deterministic regressions cover retry after a failed durable start write and accurate presentation when journal refresh fails after a returned execution result.
- UI content identifies the expected simulated effect and simulation-policy result, and labels verification/rollback as simulation-only while exposing the known identity and journal limitations.

This evidence validates only the in-process simulation. It does not approve real Windows-changing execution, authenticate the UI's `current-user` label, make the local JSON journal tamper-proof, or prove exact in-memory effects after forced termination.

## 1. Scope

This document defines the testing strategy for the Action Broker security design before any privileged handler is deployed. The goal is to validate the broker contract and permission boundaries through simulation, not by changing Windows or user system configuration.

## 2. Test principles

- Every test must be simulation-only.
- No live service stop, driver install, startup mutation, registry change, or filesystem modification occurs.
- Tests validate authorization logic, policy enforcement, journaling, and stale-revalidation behavior.
- Failures must be recorded as design rejections, not as runtime exceptions that imply system safety.

## 3. Required test areas

The bullets below are the ongoing security test plan. They are not all implemented: real IPC identity, Windows target replacement/reparse-point checks, production broker restart recovery, and privileged boundary tests cannot be validated until such components exist. Current tests exercise only synthetic simulation state.

### 3.1 Request validation

- reject unknown operation ids,
- reject unsupported schema versions,
- reject messages over the configured size limit,
- reject requests missing correlation id or request id,
- reject requests with invalid target identity,
- reject malformed parameter payloads.

### 3.2 Authorization and policy

- reject expired approval tokens,
- reject used or replayed approval tokens,
- reject cross-target approval reuse,
- reject changed parameter digests,
- reject disallowed risk classes,
- reject policy version mismatches,
- reject deny-by-default requests.

### 3.3 Stale target protection

- approve target A, then change target to A2 before execution,
- approve a path, then replace it with a symlink or reparse point,
- approve a service name that is later renamed or no longer present,
- approve a startup item whose entry is changed after preview,
- detect changed protected-component status before execution.

### 3.4 Replay and duplicate detection

- resend the same request id,
- replay the same authorization token,
- submit same operation with different correlation ids but same target and parameters,
- simulate in-flight duplicate broker execution,
- verify one-time consumption state is enforced.

### 3.5 Privilege separation

- verify the UI cannot invoke a privileged handler directly,
- verify the broker rejects a message that is not schema-compliant,
- verify there is no generic command execution route,
- verify that a raw command string cannot bypass the operation registry.

### 3.6 Execution journal integrity

- simulate a failed start, ensure the journal records the failed state,
- simulate an interrupted action, ensure the state remains marked interrupted,
- simulate rollback failure, ensure the final state shows unresolved risk,
- verify journal ordering and correlation across event types.

### 3.7 Rollback contracts

- test an operation with an explicit rollback path,
- test an operation without a rollback contract,
- validate that high-risk actions with unknown rollback are rejected,
- validate that a failed rollback is recorded as a limitation, not as success.

### 3.8 Protected components

- reject operations targeted at drivers, security software, recovery infrastructure, or boot-critical components unless separately allowed,
- ensure the policy engine blocks protected-component changes even when the operation type is otherwise recognized,
- validate that protected-component information is updated and checked before execution.

## 4. Simulation harness requirements

The test harness should:
- build synthetic request payloads,
- create synthetic approval tokens,
- emulate target resolution changes,
- verify policy outputs for allow/deny decisions,
- assert exact journal states,
- assert the broker rejects invalid or mismatched operations,
- verify one-time-use and expiry behavior.

The harness must not call live Windows management APIs that could mutate the system.

## 5. Simulation test criteria

The design passes security validation when:
- all deny-by-default tests pass,
- stale-target and replay tests fail closed,
- approval reuse and parameter changes are rejected,
- the execution journal retains a complete state trail,
- protected components remain outside the validation path unless explicitly allowed,
- there is no generic execution route,
- no operation proceeds without verified policy and target state.

Passing these tests does not establish that a future privileged broker is secure or that the local journal is tamper-proof.

## 6. Known limitations

This plan validates only the current simulation contracts. It cannot prove a production broker is safe. Any future privileged executor remains subject to ADR 0011's separate-service constraint and requires independently reviewed identity, consent, IPC/host, threat, recovery, and code-security controls. ADR 0012's named-pipe proposal is not selected, and the current V2 milestone has no host or real executor.
