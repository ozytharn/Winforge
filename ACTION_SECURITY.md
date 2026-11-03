# WinForge Action Security Blueprint

**Status:** Future privileged-broker design plus current simulation-only implementation security boundary. The current runtime is not an authenticated or privileged broker.

**Release readiness:** The current Action Broker is suitable only for simulation-only release with the limitations in [ACTION_SECURITY_AUDIT.md](./ACTION_SECURITY_AUDIT.md). It is not approved for real Windows-changing execution.

The V2 security proposal, independent review, and remediation are documented in [ACTION_BROKER_V2_SECURITY.md](./ACTION_BROKER_V2_SECURITY.md), [ACTION_BROKER_V2_INDEPENDENT_REVIEW.md](./ACTION_BROKER_V2_INDEPENDENT_REVIEW.md), and [ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md), with companion architecture/IPC/recovery documents. They are unimplemented design material, not proof that proposed controls work. ADR 0012 remains Proposed; no host, IPC, or consent architecture is accepted.

## 0. Current simulation implementation

The desktop application currently contains a simulation-only lifecycle. It validates typed, bounded parameters; binds request, approval, target, policy version, and preview; serializes one-time execution; and records local journal events. The simulator operates only on synthetic in-memory state. It does not resolve or modify Windows targets.

The WPF approval control records an explicit local user interaction. Its `current-user` approver value is not proof of OS identity. No security property in this document should be read as authorizing real execution.

Interrupted journal starts are marked interrupted on the next journal read. This cannot recover or prove prior in-memory simulation effects after forced termination. The local JSON journal is user-writable evidence, not tamper-proof audit storage. Simulation verification and rollback outcomes are not evidence of real Windows verification or rollback capability.

The WPF preview identifies the expected simulated effect and simulation-policy decision. Execution status labels verification and rollback as simulated, and the UI surfaces the identity, journal, and interrupted-state limitations.

The independent audit and remediation record is [ACTION_SECURITY_AUDIT.md](./ACTION_SECURITY_AUDIT.md). The current residual risks are same-process authority, same-user journal tampering, lack of authenticated IPC, and lack of an actual privilege boundary.

## 1. Security posture

WinForge is a local-first Windows intelligence platform. Its monitoring and inventory features remain read-only. Any future system-changing operation must be treated as a security-sensitive workflow and must pass through a separately approved authorization boundary. The exact host and transport remain undecided; ADR 0011 currently constrains privileged work to a separate service unless a later ADR explicitly narrows or supersedes it.

The fundamental rule is: deny by default, authorize explicitly, revalidate before execution, and write an execution journal that records available evidence and uncertainty. A journal is not automatically proof of what happened or rollback-resistant.

## 2. Trust boundaries

Any future broker design must separate the following trust domains:
- interactive desktop app,
- user-controlled UI and approval workflow,
- policy engine,
- broker service,
- execution handlers,
- verification and journal layer,
- Windows system APIs and protected components.

No boundary is trusted by default. Every request must be validated by the authoritative broker before any action. This requirement does not select or authorize a runtime architecture.

## 3. Authorization model

Authorization is request-bound and time-limited. The authorization token must contain:
- request id,
- operation id,
- target identity hash,
- parameter digest,
- preview digest,
- risk class,
- policy version,
- approval subject,
- approval expiry time,
- correlation id,
- single-use flag.

The exact target, parameters, and policy version must be identical to the values used at approval time. If any item changes, the authorization becomes invalid.

Request binding proves equality of broker-held data, not human intent. The current V2 design does not establish that a human saw the exact preview against same-user malware. See [the architecture remediation](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md#2-consent-model-and-limits); consequential operations remain prohibited until the consent boundary is independently accepted.

## 4. Required privilege boundaries before any real execution

- The desktop app must continue to run without special privilege for standard usage.
- If an operation is approved to require a Windows service, that service must run under a reviewed minimum-privilege identity. No current V2 operation establishes that requirement.
- Any execution handler must be allowlisted and scoped to one operation type.
- No generic administrative command runner is allowed.
- No arbitrary `PowerShell`, script execution, command string processing, or dynamic invocation is allowed through the broker.
- No raw registry mutation, direct driver installation, or service change is accepted unless a specific, reviewed handler exists.

## 5. Deny-by-default policy

The broker must reject any request that:
- is outside the allowlist,
- lacks a defined operation handler,
- lacks a valid request or correlation id,
- misses required authorization fields,
- targets a protected component,
- is expired or revoked,
- contains a changed target or parameter digest,
- is duplicated or replayed,
- fails verification or revalidation,
- lacks a valid rollback contract when one is required.

Unknown, ambiguous, or partially valid requests never execute.

## 6. Stale preview and target revalidation

A preview shown in the UI is not execution authorization. Before any action, the broker must perform a fresh target lookup and compare the result against the approved target identity and preview digest.

The broker must reject the action when:
- the target moved,
- the target changed identity,
- the target no longer matches the approved parameters,
- the target is protected, missing, or ambiguous,
- policy or risk classification changed after approval,
- the precondition is no longer valid.

## 7. Replay and duplicate protection

The broker must implement a dedupe and replay protection strategy:
- request id uniqueness,
- correlation-id tracking,
- idempotency token persistence,
- one-time authorization consumption,
- journal state checks prior to handler launch,
- conflict detection for in-flight duplicates.

This prevents duplicate execution, replay, or multiple approval reuse after an interrupted transaction.

## 8. Execution verification and risk handling

A successful start is not a successful operation.

The broker must require post-execution verification for all state-changing actions. Verification includes:
- target state reached expected values,
- side effects are within approved scope,
- protected components remain intact,
- no unintended mutation occurred,
- final journal entry reflects the verified result.

If verification fails, the broker must record a failed or partial result and evaluate rollback or repair where available.

## 9. Execution journal and interrupted operations

Every action attempt must generate a journal record. A journal entry should include:
- request id,
- operation instance id,
- target identity,
- action type,
- actor id,
- timestamp,
- policy state,
- approval state,
- execution status,
- verification result,
- rollback state,
- reason code.

The broker must explicitly handle interrupted operations, partial failures, and timeouts. No partial success can be treated as a completed action without a valid verification state.

## 10. Rollback contracts

Rollback is not a universal capability. The action contract must define whether rollback is possible, supported, and verifiable.

A rollback contract must specify:
- exact recovery method,
- scope of affected items,
- verification conditions,
- rollback availability window,
- limit of uncertainty,
- recovery journal record.

If rollback cannot be guaranteed, the action cannot be treated as safe.

## 11. Protected components and exclusions

Protected categories include:
- boot and startup-critical components,
- security software,
- firewall and endpoint security,
- recovery infrastructure,
- device drivers and kernel support,
- filesystems, storage services, and boot state,
- key system services.

The broker must reject changes to protected components unless a separate, explicitly reviewed policy exists.

## 12. Auditability and operational security

The broker must write security-relevant events for:
- request creation,
- validation success or failure,
- approval state,
- target revalidation,
- execution start,
- verification status,
- completion, rollback, or failure,
- cancellation and interruption.

Audit events should not record secrets or large raw payloads. They should record the minimum necessary evidence for investigation and remediation.

## 13. Unresolved design risks

Before any real execution, the following must be explicitly reviewed:
- service identity and Windows ACL requirements,
- update and repair of the broker service itself,
- operation-specific rollback guarantees,
- multi-session and per-user target validation,
- policy version roll-forward and downgrade handling,
- durable journal integrity and tamper resistance,
- handling of transient target state during the execution window.

## 14. Design review conclusion

The Action Broker is a security boundary, not a convenience feature. The present simulation does not establish a production security boundary. Until a separately hosted broker, authenticated IPC, operation-specific target validation, production-grade journal, and reviewed rollback/verification contracts are implemented and independently assessed, WinForge must remain read-only and avoid privileged system-changing actions.
