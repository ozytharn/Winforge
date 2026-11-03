# WinForge Action Broker V1 Design Blueprint

**Status:** Design blueprint plus a simulation-only implementation. Release readiness is **READY WITH DOCUMENTED LIMITATIONS for simulation-only use**. The current app includes a typed, allowlisted Action Broker simulation engine and WPF workflow that never mutates Windows state. It is not approved for real Windows-changing execution.
**Scope:** Secure, deny-by-default broker for typed, audited system operations.

The V2 architecture and its independent review/remediation remain design-only for hosting and execution: [architecture](./ACTION_BROKER_V2_ARCHITECTURE.md), [security](./ACTION_BROKER_V2_SECURITY.md), [IPC](./ACTION_BROKER_V2_IPC.md), [recovery](./ACTION_BROKER_V2_RECOVERY.md), [implementation plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md), [independent review](./ACTION_BROKER_V2_INDEPENDENT_REVIEW.md), and [architecture remediation](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md). A hostless model library and deterministic tests are implemented, but no V2 host, transport, authenticated identity, trustworthy consent mechanism, durable store, or real operation is selected or implemented. ADR 0012 remains Proposed. This does not change V1 or authorize real operations.

## Implementation status

V1 remains in the application-domain simulation layer only. Its implementation validates action requests, binds exact broker-issued previews and approvals, serializes one-time execution, persists the execution journal, and exposes the workflow through the WinForge WPF shell. The separate hostless V2 library models typed requests, canonical binding, authorization/lifecycle, recovery, verification/rollback, and audit contracts; tests use synthetic state and fault injection only. It does not implement a real Windows executor, privileged helper, authenticated user identity, durable V2 persistence, IPC, or any system-changing operation. See [ACTION_SECURITY_AUDIT.md](./ACTION_SECURITY_AUDIT.md) for V1 audited limits.

## 1. Purpose

WinForge must keep its desktop application and monitoring services read-only by default. Any future state-changing operation requires a separately approved authorization and execution boundary that independently validates the request, target, policy, and approval. The host and IPC model are not yet selected. The accepted ADR 0011 currently requires a separate service for privileged operations; any different executable architecture requires a new ADR that narrows or supersedes that constraint. No operation is currently approved.

The privileged broker remains a design boundary, not a runtime implementation. The current runtime only executes typed operations against synthetic in-memory simulation state.

## Current supported simulation workflow and release boundary

The WPF application supports catalog selection, typed/bounded request validation, canonical preview generation with the expected simulated effect and policy status, explicit one-time approval bound to the exact request, deny-by-default policy checks, isolated simulation execution, simulated postcondition verification, and local journal review. Execution results distinguish simulated verification and rollback from real-system outcomes. The initial catalog contains only the registered synthetic user-preference, startup-entry, allowlisted-setting, transient-workspace, and read-only inspection simulations. No unregistered action is executable.

The execution journal is atomically replaced and rejects corrupt content instead of presenting it as empty. A persisted `started` record without a terminal record is marked `Interrupted` at the next journal read. This recovery communicates an incomplete simulation only: after forced termination, in-memory state may be lost and the exact simulation effects cannot be proven. A failed journal write before `started` prevents simulation execution; an inability to refresh the journal after execution is reported separately from the simulation result.

Three limitations are release boundaries, not implementation details to assume away: `current-user` is a UI label rather than authenticated identity; the local JSON journal is not tamper-proof; and forced termination can leave an incomplete simulation whose in-memory effects cannot be reconstructed. The broker is in-process and **not approved for real Windows-changing execution**. Simulated verification and rollback do not establish that real verification or rollback would work.

Release-readiness verification on 2026-11-03 passed: Release build (0 warnings/errors), 97 full-suite tests (46 unit and 51 Windows integration; 0 failed/skipped), and a WPF UI Automation smoke covering the complete workflow and graceful shutdown. Detailed audit and regression evidence is in [ACTION_SECURITY_AUDIT.md](./ACTION_SECURITY_AUDIT.md).

## 2. Current architecture status

- The interactive desktop app remains non-elevated and read-only.
- The V2 hostless model/test milestone is implemented; no V2 runtime host is selected. Revisit a per-user or machine-wide host only after a concrete operation establishes its rights and coordination requirements. ADR 0011 remains the service constraint for privileged operations, while ADR 0012 remains Proposed.
- The broker validates all requests before execution, regardless of where the request originated.
- The broker only accepts typed operation definitions and a strict allowlist, never arbitrary shell commands, script blocks, or general-purpose command runners.
- The broker never trusts client-supplied risk classification, target identity, or approval tokens. It recalculates them using its own policy engine.
- The broker records an execution journal for every operation attempt, outcome, and verification result.

The service/named-pipe proposal is not an accepted architectural decision. The current review also concludes that exact-preview human intent against same-user malware is not established; consequential operations remain prohibited until an independent consent boundary is accepted. See [the architecture remediation](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md) for the full host comparison, identity model, anti-rollback limits, canonical lifecycle, verifier criteria, and rollback rules.

## 3. Core components

### 3.1 UI / orchestration layer

For V1 simulation, responsible for:
- presenting a preview of the proposed action,
- displaying the specific target and expected impact,
- showing the approval requirement and expiry,
- recording a local explicit approval bound to the exact simulation request. This is not authenticated identity or proof of human intent for V2.

Any future client must never execute privileged changes directly. For V2, a request-bound confirmation event does not prove that the user saw or understood the exact preview.

### 3.2 Policy engine

The policy engine evaluates:
- action class,
- action risk,
- caller identity and scope,
- target identity and protected status,
- policy version,
- approval validity,
- stale preview or changed target,
- conflicting or duplicate requests,
- supported rollback path and verification requirements.

The policy engine is authoritative; the client cannot override it.

### 3.3 Future broker authority (host undecided)

The future broker authority must own:
- the allowlist of supported actions,
- selected IPC authentication and protocol validation,
- request replay detection,
- target revalidation,
- execution orchestration,
- execution verification,
- journal writes,
- rollback or compensation coordination where supported.

### 3.4 Operation registry

Every operation is declared by name, typed parameters, risk classification, and required privilege boundary. Supported operation definitions include:
- operation id,
- operation kind,
- target type,
- required approval class,
- min policy version,
- maximum execution time,
- rollback contract type,
- verification requirement,
- protected component exclusions,
- supported execution context.

### 3.5 Execution verifier

The verifier runs after execution and checks:
- the target reached the expected state,
- desired properties changed as expected,
- no unsafe side effects occurred,
- rollback conditions or recovery notes are recorded,
- the journal contains a valid completion or failed state.

## 4. Action lifecycle and contracts

The broker uses a strict request lifecycle:

1. Proposal
   - User or trusted workflow proposes a typed action.
   - Request includes the exact target identity, parameters, and policy context.
   - The app generates a preview hash for the proposed action and target.

2. Authorization bind
   - A request-bound authorization token is created.
   - The token includes action type, target identity, normalized parameters, policy version, preview hash, expiry, request id, and one-time-use state.
   - Any parameter or target change invalidates the token.

3. Broker validation
   - Broker verifies the caller.
   - Broker verifies the schema, size, version, and expiry.
   - Broker checks risk classification, allowlist, permission scope, and protected-component policy.
   - Broker rejects unknown or unsupported operations.

4. Target revalidation
   - Broker resolves the target again immediately before execution.
   - Broker compares the revalidated target identity with the approved target identity and preview hash.
   - If the target is stale, missing, moved, protected, or changed, the broker aborts.

5. Execution and tracking
   - Broker starts execution with a unique operation instance id.
   - Broker updates the execution state in the journal before and after critical phases.
   - Broker captures an interrupted-operation checkpoint when supported.

6. Post-execution verification
   - Broker performs the required verification checks.
   - If verification fails, the broker records a failed or partial result and enters rollback or repair mode if supported.

7. Finalization
   - Broker marks the request completed, failed, cancelled, or rolled back.
   - The journal records the final state, verification status, and any unresolved risk.

## 5. Request-bound, expiring authorization

Authorization is not a permanent capability. It is a request-scoped token with a defined lifetime.

Required fields:
- `request_id`
- `operation_id`
- `target_identity`
- `parameter_digest`
- `preview_digest`
- `policy_version`
- `approver_id`
- `expires_at_utc`
- `correlation_id`
- `one_time_consumed`
- `risk_class`
- `verification_requirements`

Rules:
- The token is valid only for the exact action request and exact target identity.
- The token expires automatically.
- Any target, parameter, policy, or precondition change invalidates it.
- The broker must reject duplicate consumption and replay attempts.
- A stale approval is never reissued as a replacement without a fresh review.

## 6. Deny-by-default security model

The broker adopts a default-deny policy:
- unknown action types are rejected,
- unrecognized targets are rejected,
- ambiguous or missing identity data is rejected,
- protected components are denied unless explicitly in a reviewed allowlist,
- request expiry or bad policy state is rejected,
- incomplete verification requirements are rejected.

The broker does not interpret action request content as a general shell command. It accepts only structured, typed operations from a known registry.

## 7. Replay and duplicate execution prevention

The broker must detect:
- the same request arriving more than once,
- the same authorization being consumed more than once,
- a repeated request with a different correlation id but identical action semantics,
- duplicate execution after an interrupted transaction,
- partially completed operations without matching journal entries.

Mitigations:
- request id and correlation id required on every request,
- idempotency key persisted at the broker,
- one-time-use authorization state,
- dedupe checks prior to execution,
- final journal validation before completion.

## 8. Stale preview and target revalidation

The broker must revalidate the target immediately before execution. This prevents stale UI preview data from authorizing a different or moved target.

Revalidation includes:
- stable object identity check,
- class/type validation,
- current path or instance state,
- protected-component verification,
- parameter digest match,
- dependency or prerequisite health check,
- change in risk classification or policy version.

If any value differs from the earlier approved state, the broker aborts and writes a rejection reason.

## 9. Execution journal and interrupted operations

Write-ahead journal entries must include:
- operation instance id,
- request id,
- target identity,
- event type,
- timestamp,
- actor and policy reference,
- state transition,
- verification status,
- rollback state,
- error code or reason.

Required lifecycle events:
- proposed,
- queued,
- approved,
- rejected,
- validation_failed,
- started,
- paused,
- interrupted,
- completed,
- failed,
- rolled_back,
- verification_failed,
- finalised.

Interrupted operations must be clearly marked. A partial result must not be treated as a successful operation without a complete verification record.

## 10. Rollback contracts and verification

Rollback is optional and must be explicit. The broker must never promise rollback unless the operation contract defines it.

Rollback contract requirements:
- actual supported recovery mechanism,
- exact restore target and scope,
- precheck on whether recovery state is available,
- verification steps after rollback,
- limits or degradation risk described to the user,
- journal entry for the rollback attempt itself.

Not all operations are rollback-safe. High-risk operations without a valid rollback contract must be rejected or remain in a simulation-only state.

## 11. Privilege separation

Privilege separation is mandatory:
- the app and UI layer do not own execution trust,
- the policy evaluation logic is separate from the execution layer,
- the broker executes only typed handlers from a restricted registry,
- the execution verifier is independent from the executor,
- the database writes audit and operational state but does not directly authorize execution.

This ensures that a compromised UI client cannot directly trigger privileged actions using raw system APIs.

## 12. Initial action catalog and exclusions

### 12.1 Included in the initial catalog (design only)

Low-risk, explicit, reversible actions that can be implemented later under review:
- user preference persistence,
- local cache cleanup in an allowlisted folder,
- log retention cleanup under a user-managed directory,
- temporary research artifact cleanup within a known project workspace,
- optional feature state inspection without changing configuration,
- service state inspection in a read-only model.

Moderate-risk actions that require explicit review and approval:
- enabling or disabling a non-critical startup entry for the current user only,
- creating a user-scoped scheduled task with a restricted, reviewable target,
- toggling a non-protected convenience setting under a documented control plane.

High-risk actions that require a dedicated authorization workflow and robust rollback:
- installing or removing a device driver,
- enabling or disabling a Windows service,
- changing firewall policy,
- changing boot order or startup configuration,
- modifying BitLocker state,
- changing startup or recovery configuration,
- deleting or overwriting files outside an allowlisted path.

### 12.2 Exclusions from the initial design

These actions are excluded from the initial broker design and remain prohibited until a separate authorization review is completed:
- arbitrary process termination,
- arbitrary service modification,
- arbitrary registry writes,
- driver installation or updates,
- startup configuration changes outside a narrow, approved user-scoped model,
- partition management,
- BitLocker operations,
- system restore or restore-point creation or deletion,
- WinRE modification,
- backup creation, deletion, or overwrite,
- arbitrary PowerShell or CMD execution,
- any action that runs under an unrestricted administrator context.

## 13. Risk classification

Risk classes are not a numeric score alone. A hard-deny rule applies when the action:
- is outside the allowlist,
- targets a protected component,
- uses an unsupported privilege context,
- lacks a verification plan,
- has no rollback contract,
- is unsafe or irreversible without explicit approval,
- depends on stale or missing evidence.

Risk classes for the initial design:
- Informational: read-only inspection only.
- Low: scoped, reversible, user-data-only operation.
- Moderate: user-scoped configuration change with limited effect and a clear recovery path.
- High: system-wide or difficult-to-reverse change.
- Prohibited: disallowed via policy or unsupported in this design.

## 14. Security testing approach

No real privileged action execution is allowed in this phase. Security testing is simulation-only and must validate:
- policy denial on unknown or unsupported handlers,
- stale-target rejection,
- replay and duplicate execution prevention,
- authorization expiry and one-time-use enforcement,
- protected-component denial,
- injection attempts against IPC payloads,
- approval cross-target tampering,
- journal integrity after failed or interrupted action attempts,
- rollback and verification failure paths.

This design explicitly avoids live Windows system modifications during validation.

## 15. Unresolved risks and design review findings

Open risks that require explicit design review before any real broker deployment:
- service identity and minimum-privilege configuration on Windows,
- cross-user or multi-session target identity resolution,
- safe handling of protected and vendor-signed drivers,
- rollback guarantees for configuration changes,
- audit log integrity and tamper resistance,
- edge cases where a target is renamed or replaced between preview and execution,
- handling of transient system state while a requested action is in-flight,
- whether an operation must be blocked by group policy or local security policy.

The design remains a restricted blueprint and must be reviewed again before any real execution path is enabled.
