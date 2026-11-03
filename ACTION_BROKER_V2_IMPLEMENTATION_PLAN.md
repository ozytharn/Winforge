# WinForge Action Broker V2 — Implementation Plan

**Status:** Hostless contracts and deterministic tests are implemented in a V2-only library. No V2 host, transport, identity authenticator, consent mechanism, durable store, or real action is selected or authorized. ADR 0012 remains Proposed.

## Objective

Prepare a future, independently reviewed Action Broker without removing, changing, or automatically routing V1 behavior. Begin with hostless contracts and tests. Do not assume a service is necessary until an operation-to-rights analysis proves it.

## Approved hostless milestone traceability

This table was prepared before implementation. Paths identify planned V2-only code and tests; no host, transport schema, identity provider, consent provider, durable-storage contract, or operation handler is selected. Hostless test evidence proves only the deterministic model behavior described here.

| Approved requirement | Planned code location | Planned test location | Acceptance evidence / limit |
|---|---|---|---|
| Typed request, target, normalized parameters, caller claims versus unverified host evidence, session/logon context, authorization context, preview binding, expiry/nonce, policy version | `src/WinForge.ActionBroker.V2/Requests/` | `tests/WinForge.UnitTests/ActionBrokerV2/CanonicalBindingTests.cs`, `AuthorizationLifecycleTests.cs` | Invalid/missing values rejected; caller claims are never promoted to authenticated evidence. No OS identity is established. |
| Stable, unambiguous canonical request binding | `src/WinForge.ActionBroker.V2/Binding/` | `tests/WinForge.UnitTests/ActionBrokerV2/CanonicalBindingTests.cs` | Deterministic encoding/digest; ordering and equivalent normalized values bind identically; changed target/parameters/policy/preview/challenge bind differently. Digest is not a signature or consent proof. |
| Authorization and lifecycle transitions, expiry, replay, concurrency, changed request and policy mismatch | `src/WinForge.ActionBroker.V2/Lifecycle/` | `tests/WinForge.UnitTests/ActionBrokerV2/AuthorizationLifecycleTests.cs` | Legal transitions are explicit; invalid, duplicate, expired, changed, or replayed authorization is denied. Fake-state behavior is not durable production enforcement. |
| Recovery plan/step identity, ordering/dependencies, preconditions, intent, completion, verification, retry and ambiguous outcomes | `src/WinForge.ActionBroker.V2/Recovery/` | `tests/WinForge.UnitTests/ActionBrokerV2/RecoveryModelTests.cs` | Every modeled commit boundary has before/after failure cases; uncertain steps remain fenced/RecoveryRequired and are never automatically replayed. A fake cannot prove power-loss durability. |
| Operation-specific verification/rollback constraints and typed audit relationships | `src/WinForge.ActionBroker.V2/Contracts/` | `tests/WinForge.UnitTests/ActionBrokerV2/VerificationRollbackAuditTests.cs` | Handler success cannot imply independent verification; unknown/partial rollback is represented explicitly; audit order/correlation invariants tested. No real source independence or tamper resistance is proved. |
| Strict V1 separation and no Windows effects | Separate `net10.0` V2 library with no project/package references; tests reference this library only for V2 cases | V2 tests and project-reference/dependency inspection | V1 source/contracts/tests remain untouched; no Windows APIs, IPC, elevation, service, executor, database, or migrations in the V2 project. |
| No premature host/framework dependencies | V2 library project metadata and assembly references | `tests/WinForge.UnitTests/ActionBrokerV2/ActionBrokerV2ArchitectureTests.cs` | No package/project references, Windows target, IPC/service/elevation API, or WinForge dependency in the model assembly. |

### Implemented contract boundary

The implementation is `src/WinForge.ActionBroker.V2`, a dependency-free `net10.0` model library. It contains immutable canonical values and request/identity/authorization models, a deterministic length-prefixed SHA-256 request binder, authorization checks and canonical lifecycle transition rules, recovery plan/step/attempt rules, operation-specific verification and rollback contracts, and correlated audit event validation. Its caller identity values are structurally typed but permanently labeled unverified; its modeled session-user confirmation does not prove a human saw the preview.

Canonical binding uses a `WinForge.ActionBinding.V2` domain prefix and ordered tagged fields with 32-bit big-endian byte lengths. GUIDs use fixed-width `N`-format ASCII; text is Unicode NFC and strict UTF-8; object keys use ordinal order; arrays preserve order; null has a distinct type tag; integers and decimals have distinct type tags; decimal values use invariant `G29`; timestamps use UTC ticks. The digest is `sha256:` plus uppercase hex. Caller-supplied identity claims are intentionally excluded; unverified host-provided context is included. This digest binds modeled data only.

The deterministic fake commit stores and commit-failure injection exist only under `tests/WinForge.UnitTests/ActionBrokerV2`. They exercise before/after model commit windows, concurrency, replay, and transition ordering. Recovery/rollback evidence source identifiers must match the declared procedure in the model; verifier evidence must bind to that procedure and use a distinct modeled evidence source. Non-lifecycle audit events require step/attempt IDs and an evidence-digest reference. These are structural checks over synthetic inputs, not component authentication or tamper resistance. The fakes are not production persistence interfaces and do not prove disk durability, atomicity with external effects, power-loss behavior, anti-rollback, OS token binding, consent authenticity, or real verification/rollback independence. No transport DTO or parser was introduced; the companion IPC handshake and framing are stale candidate design, not an implemented protocol.

**Still prohibited:** service/installer, IPC, impersonation, elevation, privileged helper, executor/operation handler, real Windows-changing operation, production persistence, database changes/migrations, action catalog expansion, or changes to V1. ADR 0012 remains Proposed; ADR 0011 is unchanged.

The earlier authenticated named-pipe and service tasks in [project ARCHITECTURE.md §8](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md#8-communication-architecture) and [project PLAN.md Phase 7](./WinForge_Project_Documents/WinForge/PLAN.md#11-phase-7--security-foundation) are marked deferred/superseded for this hostless milestone. They are not host or transport selections.

## Hostless milestone validation record

Validation completed against the current workspace:

| Check | Command / evidence | Result |
|---|---|---|
| Release solution build | `dotnet build WinForge.sln --configuration Release --no-restore` | Passed; 0 warnings, 0 errors. |
| Focused V2 tests | `dotnet test tests\WinForge.UnitTests\WinForge.UnitTests.csproj --configuration Release --filter FullyQualifiedName~ActionBrokerV2 --no-restore` | Passed; 38 passed, 0 failed, 0 skipped. Includes canonical binding, authorization, recovery crash windows, rollback, audit, and architecture-dependency tests. |
| Focused commit-fault tests | `dotnet test tests\WinForge.UnitTests\WinForge.UnitTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~Deterministic_fake_store|FullyQualifiedName~Rollback_state_transitions_have_deterministic_before_and_after_commit_crash_windows"` | Passed; 3 passed, 0 failed, 0 skipped. |
| Full unit suite | `dotnet test tests\WinForge.UnitTests\WinForge.UnitTests.csproj --configuration Release --no-build` | Passed; 84 passed, 0 failed, 0 skipped. |
| Existing integration suite | `dotnet test tests\WinForge.IntegrationTests\WinForge.IntegrationTests.csproj --configuration Release --no-build` | Passed; 51 passed, 0 failed, 0 skipped. These tests did not invoke privileged Action Broker execution. |
| Host/dependency boundary | `ActionBrokerV2ArchitectureTests` in the full unit suite | Passed; the V2 assembly has no project/package or WinForge/Windows-host references, and the V2 source has no service, IPC, elevation, or real-executor APIs. |
| ADR and documentation state | Direct inspection of ADR 0011/0012 and the linked V2/project documents | ADR 0011 remains Accepted and unchanged; ADR 0012 remains Proposed. Service/named-pipe planning is explicitly deferred, not selected. |
| Local documentation links | Local Markdown-link check across nine directly related V2/action/project documents | Passed; no missing local targets. |

The initial focused run exposed an outdated assertion on the wording of a non-lifecycle audit validation error. The message and assertion were reconciled; the final focused and full suites pass. No V1 implementation, contract, catalog, or database/migration file was edited. The workspace has no Git metadata, so a repository diff and comparison against previously recorded V1 hash values could not be independently reproduced in this validation pass; this is a limitation, not a claim of hash-based verification. Existing V1 unit and integration coverage passed as part of the full suites.

These results validate only pure deterministic contracts and existing non-privileged tests. They do not prove OS identity authentication, human-consent authenticity, production persistence or crash durability, anti-rollback, actual verifier independence, or any Windows operation's safety.

## Phase 0 — Architecture and threat-model approval

Deliver:
- Review [architecture](./ACTION_BROKER_V2_ARCHITECTURE.md), [security](./ACTION_BROKER_V2_SECURITY.md), [IPC](./ACTION_BROKER_V2_IPC.md), [recovery](./ACTION_BROKER_V2_RECOVERY.md), this plan, the [independent review](./ACTION_BROKER_V2_INDEPENDENT_REVIEW.md), the [remediation](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md), and proposed ADR 0012.
- Select an initial use case or explicitly keep V2 execution empty. Build an operation-to-rights/scope matrix before choosing a host.
- Resolve ADR 0011 governance without editing its history. No alternative host architecture may be implemented for privileged work until a new explicit ADR narrows or supersedes its service constraint.
- Define the accepted same-user malware threat model and whether any user confirmation can meet operation requirements. Current design does not establish trustworthy human intent.

Gate: no runtime host, endpoint, or handler until the use case, host/transport decision, identity model, consent boundary, and anti-rollback assumptions are independently reviewed.

## Phase 1 — Contracts and deterministic harness (no host, no Windows effects)

Deliver:
- Hostless typed model values and canonicalization test vectors in an isolated V2 library. They are not transport DTOs or a frozen wire schema.
- Pure state-machine rules and deterministic test-only fake state with fault injection and controlled time. No production persistence interface or durability claim.
- Request-binding, authorization, recovery-step, crash-window, verification, rollback, and audit model tests. Protocol-parser fuzzing is deferred until a transport is selected.
- Prove V1 tests still pass and verify that no V1 source, contract, behavior, or database files changed.

Gate: canonical encoding/binding vectors and lifecycle invariants independently reviewed. Test fakes may alter synthetic state only. Passing this phase does not justify a service or freeze a protocol.

## Phase 2 — Host and transport decision (no implementation by default)

Deliver:
- Compare per-user hosting, a machine-wide service, narrowly scoped UAC helper, named pipes, Local RPC, and COM/RPC against the selected use case and its required rights.
- If no operation needs a runtime host, remain hostless. If a service is justified, define its identity, SCM permissions, install/update chain, endpoint ACL, session policy, endpoint verification, and failure handling.
- If no IPC host is selected, do not create an empty privileged service merely to validate platform mechanics.

Gate: signed architecture decision; independent review confirms least privilege and necessity. Any eventual endpoint starts with an empty executable catalog and fails closed.

## Phase 3 — Durable authorization and recovery model

Deliver:
- Broker-owned durable proposal, approval, accepted request, event journal, resource locks, and recovery states.
- Unique constraints, atomic canonical transitions, key protection, anti-rollback trust model, backups/restoration, and corruption recovery.
- Crash injection at every transaction/handler boundary; no automatic handler replay.

Gate: choose and document whether an independently controlled witness or separately validated hardware monotonic state is needed. Local timestamps, ACLs, protected keys, or a hash chain alone do not provide strong rollback resistance. Any mismatch fails closed.

## Phase 4 — Consent feasibility and session lifecycle

Deliver:
- Evaluate WPF approval, separate broker-controlled session UI, and OS-mediated consent against exact-preview binding and same-user malware.
- If prototyped, label the event as session-user confirmation unless human intent is independently established; bind exact request/session/expiry and test substitution/replay.
- Define logoff, process replacement, Fast User Switching, RDP/disconnected-session and accessibility behavior.

Gate: independent approval security decision. If exact-preview user intent cannot meet the threat model, no operation requiring such consent may be implemented. Signatures/challenges alone do not close this gate.

## Phase 5 — Operation-specific proposal (not authorized)

Only after host, identity, consent, and ledger gates, consider a specific narrowly scoped operation. Define privacy, target scope, rights, verification assurance, reversibility, and recovery individually. Read-only status does not automatically mean safe; a data-exfiltration surface also requires review.

Gate: per-operation approval and privacy/security review; no blanket catalog approval.

## Phase 6 — Operation-specific real execution (future, separate authorization)

This phase is deliberately not approved by the V2 architecture milestone. Each candidate operation requires a separate proposal, threat model, privilege matrix, stable target resolver, preconditions, exact preview, a consent-evidence model with explicitly accepted assurance and limits, durable idempotency, interruption reconciliation, independent verification, rollback/recovery contract, Windows tests in disposable environments, and independent security/code review.

No generic handler, arbitrary command, shell, script, command-line string, or dynamically selected executable is permitted. If an operation lacks an independently verifiable postcondition or safe ambiguity/recovery strategy, it remains prohibited.

## Phase 7 — Release and operations

Before any V2 production release: signed installer and binaries, secure update and downgrade protection, host-specific ACL verification if a host is selected, audit retention and privacy controls, incident response, key rotation/recovery procedures, compatibility matrix, support/repair/uninstall flows, telemetry policy, accessibility/consent UX, and a final release review.

## V1 coexistence and migration

- V1 remains in-process, simulation-only, and available independent of V2.
- Any eventual V2 runtime receives a distinct authorization namespace, request IDs, and state store; the host and database remain undecided.
- Never migrate V1 approval into V2 or infer approval from V1 history.
- UI clearly labels V1 as simulation and V2 as a separate broker. No automatic fallback or hidden mode switch.
- V2 absence, authentication failure, unsupported protocol, or recovery state must fail closed; it must not run a real operation via V1.
- Keep database migrations 1–5 unchanged unless a separately approved implementation proposal demonstrates a necessary V2 migration. V2 ledger technology and migration ownership remain open.

## Required implementation reviews and test evidence

- Threat modeling and independent architecture/security review before Phase 2.
- Hostile client, identity/token, session, ACL, protocol fuzz, and pipe-squatting tests.
- Duplicate/replay/concurrency/property tests and canonicalization cross-version vectors.
- Crash/power-loss injection before and after each durable transition and each side effect.
- Journal edit/truncate/reorder/key-loss/backup-rollback/recovery tests.
- Partial-effect, cancellation, timeout, verification-failure, rollback-failure, and recovery-required tests.
- If a service host is selected: installation, upgrade, downgrade, restart, stop, logoff, multi-user, and uninstall tests; otherwise, equivalent host-specific lifecycle tests.
- Static evidence of no generic command route and operation-handler allowlist.
- Per-operation independent reviewer signoff; no blanket approval for the catalog.
