# ADR 0011: Action Broker authorization and lifecycle

**Status:** Accepted as a design constraint

## Context

WinForge requires a clear boundary between read-only inspection and any future privileged operation. The project already documents a monitored, read-only architecture, but it does not contain a real broker implementation. A privileged operation cannot be treated as safe merely because a client application is allowed to reach the desired target.

## Decision

- Keep the desktop application and monitoring collectors read-only and non-elevated.
- Introduce a separate Action Broker service as the only future privileged execution boundary.
- Require request-bound, expiring authorization tokens whose validity depends on action type, target identity, parameter digest, preview digest, policy version, and approval expiry.
- Enforce deny-by-default and allowlist-based operation routing for every action.
- Revalidate the target and preconditions immediately before execution; a stale preview or changed target invalidates the approval.
- Reject replay, duplicate execution, and approval reuse with a one-time-use authorization state.
- Keep privilege separation between the UI, policy evaluation, broker execution layer, and verification layer.
- Record an execution journal for every action request, state transition, verification result, interruption, rollback attempt, and final outcome.
- Require explicit rollback contracts for any action capable of changing state; if recovery is not defined or verified, the action remains prohibited.
- Keep the initial action catalog deliberately narrow and strict, excluding system-critical, security-critical, and irreversible categories by default.

## Consequences

- The current repository remains read-only and does not execute privileged operations.
- Future system-changing workflows must be designed as typed broker operations, not generic commands.
- Authorization becomes a finite, request-scoped security object rather than a broad persistent capability.
- The initial design intentionally excludes system-critical operations until they pass a dedicated security and verification review.
- This ADR preserves the operating principle: no privileged action without a reviewed allowlist, fresh target validation, journal evidence, and explicit recovery semantics.
