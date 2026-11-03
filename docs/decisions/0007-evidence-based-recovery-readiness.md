# ADR 0007: Evidence-based Recovery readiness findings

**Status:** Accepted

## Context

Recovery discovery combines providers with different coverage, permissions, and semantics. A restore point or backup directory does not prove that a machine or backup can be restored.

## Decision

- Derive readiness findings from existing Recovery observations without adding a parallel collector or persistence format.
- Report `Available`, `Gap`, `Attention`, or `Unknown` for individually assessed areas.
- Treat inaccessible or unsupported evidence as `Unknown`, not as a missing capability.
- Do not calculate a single readiness score or make a recoverability claim.
- Keep backup folders as unverified candidates; backup integrity verification and recovery actions require separate implementation and security review.

## Consequences

- Users can see current evidence and limitations without a misleading aggregate status.
- Findings can be recomputed from persisted Recovery observations; no new SQLite migration is required.
- Assessment quality is limited by the discovery providers, and it does not demonstrate that a restore operation would succeed.
