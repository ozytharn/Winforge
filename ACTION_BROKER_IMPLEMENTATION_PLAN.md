# WinForge Action Broker Implementation Plan (Design Only)

**Status:** Historical V1 design blueprint. The V1 simulation-only engine and WPF workflow have since been implemented; this document does not describe V2 implementation authorization. No privileged operations are implemented.

The proposed separate-host V2 plan is [ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md). It remains design-only and does not authorize implementation of a service, IPC endpoint, or real operation.

## 1. Objective

Prepare the architecture, contracts, policy, and testing frame for a future Action Broker that executes only typed, allowlisted operations under a strict authorization model. The goal is to keep WinForge read-only until the design is reviewed and validated.

## 2. Non-goals

This plan intentionally does not implement:
- privileged Windows service execution,
- real system-changing operations,
- registry mutation,
- service modification,
- driver installation or removal,
- boot or recovery configuration changes,
- backup modification or deletion,
- any live power or service-management workflows.

## 3. Phase 1 - Security boundary and policy

Deliverables:
- deny-by-default policy,
- action risk taxonomy,
- protected-component registry,
- approval and expiry model,
- auth token contract,
- request validation and size limits.

Outcome:
- no operation may execute without an explicit allowlist and policy decision.

## 4. Phase 2 - Broker service architecture

Deliverables:
- broker service identity and ACL,
- named-pipe protocol contract,
- typed operation registry,
- action lifecycle state machine,
- execution journal schema,
- replay and duplicate detection model.

Outcome:
- the broker owns execution trust, not the desktop app.

## 5. Phase 3 - Good target and stale validation

Deliverables:
- target identity model,
- preview digest comparison,
- precondition revalidation,
- protected-target enforcement,
- recheck on current service or config state.

Outcome:
- actions can only run against the exact approved target and valid precondition set.

## 6. Phase 4 - Execution, verification, and rollback

Deliverables:
- execution state tracking,
- verification step definitions,
- rollback contract rules,
- interrupted-operation handling,
- final journal record and completion state.

Outcome:
- the broker records evidence and never conflates started status with success.

## 7. Phase 5 - Security validation and design review

Deliverables:
- simulation-only security test suite,
- auditability checks,
- unresolved risk register,
- design review signoff before any privileged runtime implementation.

Outcome:
- the broker design is reviewed before any real action execution is enabled.

## 8. Decision gate

No production implementation should begin until:
- the action catalog is reviewed,
- the risk model is accepted,
- the broker security review is complete,
- the execution journal model is accepted,
- protected components and rollback rules are explicitly defined.

## 9. Implementation note

This plan is a design artifact only. It intentionally preserves WinForge’s current read-only architecture and avoids system-changing operations until the required authorization, protection, and auditing designs are complete.
