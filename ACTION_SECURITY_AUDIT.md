# Action Broker Independent Security Audit v1

**Status:** Remediated simulation-only audit; release-ready with documented limitations for simulation use only; no real Windows executor is present.  
**Scope:** Action Broker domain/application implementation, JSON journal persistence, WPF integration, and tests.  
**Design references:** Action Broker specifications and ADRs 0001–0011, especially ADR 0011.

## Executive result

The audit found lifecycle weaknesses in the first simulation implementation. All four findings below were remediated within the existing simulation-only architecture and covered by deterministic tests. The Release build and complete solution test suite pass.

This is not a security approval for real operations. The broker remains in the desktop process, uses a simulated in-memory state, and has no authenticated IPC, service identity, privilege boundary, or real Windows target resolver.

## Findings

### AB-SEC-001 — Approval/request binding was not enforced during execution

- **Severity:** Medium
- **Affected component:** `ActionBrokerService` request, approval, and execution lifecycle.
- **Reproduction:** Create and approve a request, change its target or parameters while retaining the request ID, recompute its public preview digest, and submit it with the original approval. Previously, the service checked only part of the approval tuple.
- **Actual impact:** The in-process simulation could apply a different simulated action than the one the original approval represented. No Windows state was reachable.
- **Fix applied:** The service now accepts approvals only for the exact request registered by the same broker instance, stores the authoritative request and approval, compares the full immutable records at execution, checks the action, target-bound digest, policy version, expiry, and one-time state, and rejects unsupported policy versions.
- **Proof:** `ExecuteAsync_rejects_preview_tampering_even_when_digest_is_recomputed`, `ExecuteAsync_rejects_modified_target_and_parameter_after_approval`, and `ExecuteAsync_rejects_modified_or_cross_request_approval`.
- **Remaining limitation:** Request and approval authority exists only for the lifetime of the app process. This intentionally rejects old request objects after restart; it is not a production token or identity system.

### AB-SEC-002 — Parameters and target identity were not consistently bound

- **Severity:** Medium
- **Affected component:** Parameter validation, policy evaluation, and simulated target revalidation.
- **Reproduction:** Provide a target identity that differs from a key in the parameter JSON, use an unexpected parameter type, or select an unsupported target type. The original handler validated target presence by request identity but applied mutations using parameter-supplied names.
- **Actual impact:** A request could update a different key in simulated state than the UI target indicated. An initial workspace fixture also contained escaped control characters, making the target evidence inaccurate.
- **Fix applied:** Added strict per-action parameter shapes and JSON bounds, canonical normalization, target/action type mapping, exact parameter-to-target checks, a fixed supported policy version, exact simulated target allowlists, and corrected the workspace fixture paths. The simulation coordinator is now internal and cannot be called through the public application API.
- **Proof:** `CreateRequest_rejects_target_parameter_mismatch_and_unavailable_targets`, `CreateRequest_rejects_unknown_action_policy_version_and_unexpected_parameter_types`, `ExecuteAsync_rejects_modified_target_and_parameter_after_approval`, and `Duplicate_execution_is_prevented`.
- **Remaining limitation:** The target resolver only knows synthetic simulation entries. It does not resolve or protect actual Windows objects.

### AB-SEC-003 — Replay protection was race-prone and did not recover incomplete starts

- **Severity:** Medium
- **Affected component:** Approval consumption, execution coordination, and restart recovery.
- **Reproduction:** Submit the same request/approval concurrently, or restart after a `started` journal event and attempt to reuse a previously issued request.
- **Actual impact:** The prior check-then-execute sequence could race; approval/execution state was memory-only and a started journal entry was not reconciled after restart. The impact was limited to simulated state.
- **Fix applied:** A lifecycle semaphore now serializes request registration, approval issuance, and execution. An approval is consumed once and a durable `started` event is written before the simulator is entered. Only requests registered in the current broker instance can be approved or executed. Journal loading records incomplete starts as `Interrupted`; old requests are rejected after restart.
- **Proof:** `Concurrent_execute_calls_consume_approval_once`, `ExecuteAsync_rejects_replay_after_service_restart`, and `ReadJournalAsync_marks_a_persisted_started_execution_interrupted_after_restart`.
- **Remaining limitation:** The journal is local user-writable JSON, not a tamper-proof cross-process security ledger. A real broker requires durable atomic authorization state and a reviewed concurrency/IPC design.

### AB-SEC-004 — Journal corruption and writes could misrepresent audit history

- **Severity:** Low
- **Affected component:** `JsonFileActionBrokerJournalStore` and execution snapshots.
- **Reproduction:** Corrupt the JSON file or issue concurrent appends through separate store instances. Previously, malformed JSON could be read as an empty journal and direct overwrites could lose entries. The execution path also captured a fresh default state instead of the actual simulation state.
- **Actual impact:** Prior simulation events could appear absent, concurrent events could be lost, and the saved state snapshot could be false.
- **Fix applied:** Journal and snapshot files are written through same-directory temporary files and replacement; per-path locks serialize reads and writes across store instances; corrupt or null journal content raises `InvalidDataException`; execution snapshots use the actual in-memory simulation state. Start-journal persistence is required before execution and failures surface to the UI.
- **Proof:** Integration tests `LoadJournalAsync_fails_closed_when_journal_is_corrupt`, `Concurrent_appends_are_serialized_without_losing_journal_entries`, and `State_snapshot_is_written_atomically_to_the_expected_local_file`; unit test `CreateRequest_approve_and_execute_simulation_succeeds_and_snapshots_actual_state`.
- **Remaining limitation:** Atomic replacement and fail-closed parsing do not protect against a malicious process running as the same user modifying or deleting the files. There is no cryptographic integrity/authentication claim.

## Threat scenario coverage

| Scenario | Result |
|---|---|
| Approval reuse, modification, cross-request reuse, and expiry | Rejected; controlled clock and exact stored-record comparison |
| Modified preview, target, or parameters | Rejected, including a recomputed public preview digest |
| Duplicate and concurrent execution | One execution at most; duplicate rejected |
| Unknown action, unavailable target, unexpected parameter type | Denied before simulation mutation |
| Policy version change | Unsupported version rejected; changed request cannot reuse stored approval |
| Preview digest inconsistency | Validator recomputes canonical preview and digest; execution also requires exact registered request |
| Replay after restart | Old request/approval rejected; incomplete `started` records are marked interrupted |
| Cancellation after durable start | Approval remains consumed; cancellation event recorded; no simulated state transition occurs in the deterministic test |
| App shutdown during an execution | WPF close cancels the broker operation and waits for the lifecycle gate before closing |
| Journal write failure before start | Execution does not enter simulator; error is surfaced |
| Journal corruption and concurrent writes | Corruption fails closed; append concurrency tested across store instances |
| UI input changed after preview/approval | ViewModel invalidates request and approval and disables execute |
| Direct lower-level simulator invocation | Coordinator is internal; no public Windows executor exists |
| Arbitrary commands, elevation, privileged service, Windows mutation | None found or introduced; confirmed by source review and simulation tests |

## Unresolved risks and limitations

- The application has no authenticated principal boundary. The UI's `current-user` approver label represents an explicit local click, not cryptographic or OS-backed identity proof.
- The broker runs in-process and does not provide isolation against other code executing inside the WinForge process.
- The local JSON journal is not signed, encrypted, or protected against same-user tampering or deletion.
- A forced process kill or power loss can leave `started` without a terminal record; the next journal load marks it `Interrupted`, but cannot prove which in-memory simulation statements ran before termination.
- UI Automation exercised tab navigation and the complete button-driven simulation lifecycle in the current desktop session. DPI, accessibility, and alternate display-scale behavior were not part of this security audit.
- No timeout-based executor exists because simulation handlers are synchronous and local. No claim is made for interruption safety of future asynchronous or real handlers.
- ADR 0011's separate privileged service, IPC authentication, OS target revalidation, privilege separation, and production-grade journal remain future design requirements. No migration or ADR was added or altered in this audit.

## Validation results

- Release solution build: succeeded; 0 warnings, 0 errors.
- Unit tests: 46 passed, 0 failed, 0 skipped.
- Windows integration tests: 50 passed, 0 failed, 0 skipped.
- Total automated tests: 96 passed, 0 failed, 0 skipped.
- WPF Release smoke test: launched successfully, navigated to Action Broker, invoked preview, approval, and execution through UI Automation, observed a successful simulation result, and closed cleanly.
- Persisted UI workflow journal: `proposed → approved → started → completed`.
- Database migrations: unchanged; no migration added.
- Security boundary: source review and tests confirm no real Windows-changing executor, elevation, service, or arbitrary command path was introduced.

The first UI smoke run exposed an interruption-recovery classification bug: a completed request was also marked interrupted. The recovery predicate was corrected and regression-tested; the one false marker created by that smoke run was superseded in the local journal with an explicit correction record. The final Release smoke run produced no false interruption event.

## Release-readiness follow-up — 2026-11-03

**Verdict:** READY WITH DOCUMENTED LIMITATIONS for simulation-only release. This is not approval for real Windows-changing execution.

### Focused findings and remediation

- **RR-001 — Medium — Approval consumed before durable start.** If the `started` journal append failed, execution was correctly prevented, but the in-memory approval had already been consumed and the request could not be retried despite no simulation having started. Approval consumption now follows a successful durable `started` append. A deterministic test injects one start-write failure, verifies no state transition, retries the same approval, and verifies the normal terminal lifecycle.
- **RR-002 — Low — Journal refresh failure obscured the execution result.** After the core returned a terminal simulation result, a subsequent journal-read failure was caught as if execution itself had failed. The UI now retains the returned execution state and reports journal-refresh failure separately. A deterministic ViewModel test verifies that successful simulation results remain visible.
- **RR-003 — Low — UI wording could blur simulated and real outcomes.** The preview omitted a plain-language expected simulated effect and the execution summary labeled an applied rollback as “supported rollback.” The UI now states the expected simulated effect and policy status, labels verification/rollback as simulation-only, and warns that simulated rollback does not prove real rollback capability.

### Current validation

- Release solution build: succeeded; 0 warnings, 0 errors.
- Full Release solution test suite: 97 passed (46 unit, 51 Windows integration), 0 failed, 0 skipped.
- Focused Action Broker core tests: 16 passed, 0 failed, 0 skipped.
- Focused Action Broker integration tests: 7 passed, 0 failed, 0 skipped.
- Release WPF startup, complete UI Automation workflow, and graceful shutdown: passed. The UI showed the simulation-only banner, required explicit approval, and persisted `proposed → approved → started → completed`.
- Journal recovery and cancellation/shutdown regression tests passed as part of the full suite and the focused Action Broker test runs.
- No migration or ADR was added or changed.

### Release boundary

Supported release scope remains synthetic in-memory simulation only. `current-user` is not authenticated identity; the local JSON journal is not tamper-proof; and forced termination can leave an incomplete simulation whose in-memory effects cannot be proven. Simulated verification or rollback does not establish real-system verification or rollback capability. A separate privileged broker, authenticated IPC, and independent review remain prerequisites before any real Windows-changing execution can be considered.
