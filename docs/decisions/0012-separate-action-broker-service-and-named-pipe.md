# ADR 0012: Separate Action Broker service and local named-pipe proposal

**Status:** Proposed; design only  
**Date:** 2026-11-03

## Context

Action Broker V1 is an in-process WPF application component that performs typed operations only against synthetic in-memory state. It has no authenticated Windows caller identity, privilege boundary, durable cross-process authorization state, or real Windows target resolver. It must remain simulation-only.

A future operation architecture needs to isolate the non-elevated desktop client from any broker-owned authority, authenticate Windows callers, coordinate sessions, and recover safely when the process terminates around a side effect. Existing ADR 0011 establishes the design constraints for a separate broker, request-bound authorization, deny-by-default policy, target revalidation, replay resistance, journal, verification, and rollback. This ADR proposes a more specific hosting and IPC direction for review; it does not authorize implementation or execution.

## Proposed decision (not accepted)

The original candidate was a separately installed, demand-start Windows service using a dedicated virtual service account and restricted service SID, exposed through authenticated local named-pipe IPC. The independent architecture review found that no selected V2 operation currently proves this machine-wide service is necessary. Accordingly, the current recommendation is to start with hostless contracts and deterministic tests; compare a per-user process, service, narrowly scoped UAC helper, and transport choices only after a concrete use case and rights matrix exist. The service/named-pipe approach remains a candidate, not the current recommendation or an accepted decision.

If a service is later justified, it must not default to `LocalSystem`, an administrator identity, elevation, or a generic command runner. If an operation cannot be performed under a reviewed minimum-privilege model, it remains denied. The current design does not establish trustworthy human intent against same-user malware. A challenge or signature binds request data, not what a human saw; consequential operations remain prohibited until an independent consent boundary is accepted.

V1 remains a distinct simulation-only workflow. There is no automatic routing or fallback between V1 and a future V2 service. No real operation is approved by this proposal.

## Alternatives considered

1. **Machine-wide Windows service and local named pipe — candidate, not selected.** Central policy, durable global deduplication, cross-session coordination, and OS service identity; highest deployment/privilege and lifetime attack-surface cost. No current operation establishes that this boundary is necessary.
2. **Per-request elevated helper via UAC — not selected as general broker.** Short-lived and visibly consented, but UAC alone does not bind displayed operation details, and durable global recovery/concurrency is harder.
3. **Per-user unelevated broker — viable only for user-token-scoped work.** Strong session separation and less privilege, but cannot provide machine-wide authority and needs conflict coordination.

Local RPC and COM/RPC remain alternatives if named-pipe identity, access control, and protocol review do not meet requirements. TCP/HTTP and remote pipe access are rejected for the initial local-only design.

## Consequences

- The architecture, IPC, threat model, durable recovery, and implementation gates are specified in `ACTION_BROKER_V2_ARCHITECTURE.md`, `ACTION_BROKER_V2_IPC.md`, `ACTION_BROKER_V2_SECURITY.md`, `ACTION_BROKER_V2_RECOVERY.md`, and `ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md`.
- The recommendation is not accepted until explicit architecture/security review resolves service identity, ACLs, caller/session token verification, consent authenticity, durable ledger/key protection, recovery/anti-rollback, multi-session behavior, and installer/update servicing.
- The independent review and remediation are documented in `ACTION_BROKER_V2_INDEPENDENT_REVIEW.md` and `ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md`. They recommend keeping this ADR Proposed and identify no currently justified machine-wide service requirement.
- ADR 0011 remains accepted and unchanged; it currently requires a separate service for privileged operations. Any different executable architecture must be approved by a new ADR that explicitly narrows or supersedes that constraint. This ADR does not amend ADR 0011.
- Hash chaining alone does not make local storage tamper-proof. Missing terminal evidence does not prove a side effect did not occur; ambiguous outcomes must be reconciled and may require `RecoveryRequired`.
- No service, IPC endpoint, elevation mechanism, real executor, database migration, or Windows-changing operation is introduced by this ADR.
- ADRs 0001–0011 remain unchanged. V1 behavior remains simulation-only.
