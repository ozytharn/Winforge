# WinForge Action Broker V2 — Second Independent Remediation Review

**Review date:** 2026-11-03  
**Scope:** Design-only review of the original review, remediation, companion V2 documents, ADRs 0001–0012, and related Action Broker, testing, architecture, security, threat-model, recovery, and roadmap documents.  
**Verdict:** **READY FOR HOSTLESS CONTRACTS AND TESTS.** This is a narrow readiness decision for a pure hostless model/test phase only. It is not approval for an IPC endpoint, Windows service, UAC helper, privileged operation, or real executor.

## 1. Executive verdict

The remediation materially improves the design. It now:

- explicitly says that identity, process identification, preview integrity, user input, and broker authorization are distinct;
- does not claim that a challenge, signature, digest, or same-session UI proves human intent;
- defers host selection because no V2 operation or machine-wide rights need is established;
- identifies the limits of local transactions, ACLs, protected keys, and hash chains against rollback;
- establishes one normative lifecycle table, prohibits automatic replay of uncertain work, and separates handler completion from verification;
- prohibits generic rollback and denies irreversible/unknown operations without an approved recovery disposition; and
- preserves ADR 0011 and leaves ADR 0012 Proposed.

These are design statements, not implemented controls. The current evidence supports only a **hostless, synthetic contract/model and deterministic test phase**. A hostless test suite can verify canonicalization, policy decisions, one-time state transitions in a model, and crash-window handling in a fake store. It cannot prove the OS binds a pipe message token to a process identity, that a UI is trustworthy, that a local ledger resists rollback, or that a real target/verifier behaves as specified.

The implementation plan currently calls for “transport DTOs” and a “fake transactional store.” These must be kept explicitly provisional and model-only: do not freeze a wire protocol, identity-provider interface, consent-provider interface, durable storage contract, or operation-handler interface before a real use case and host are selected. No fake should be described as validating Windows identity, crash durability, or anti-rollback.

**No Critical findings are assigned for the hostless-only phase.** High-severity issues remain blockers to any consent-dependent or state-changing operation. The hostless phase is safe to begin only within the boundaries in Section 3 and the prerequisites in Section 11.

## 2. Scope and evidence

Inspected the original [independent review](./ACTION_BROKER_V2_INDEPENDENT_REVIEW.md), remediation [architecture](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md), and all companion V2 [architecture](./ACTION_BROKER_V2_ARCHITECTURE.md), [security](./ACTION_BROKER_V2_SECURITY.md), [IPC](./ACTION_BROKER_V2_IPC.md), [recovery](./ACTION_BROKER_V2_RECOVERY.md), and [implementation plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md) documents.

Inspected ADRs 0001–0012, including [ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) and [ADR 0012](./docs/decisions/0012-separate-action-broker-service-and-named-pipe.md), and the requested related sources: [ACTION_BROKER.md](./ACTION_BROKER.md), [ACTION_SECURITY.md](./ACTION_SECURITY.md), [ACTION_SECURITY_AUDIT.md](./ACTION_SECURITY_AUDIT.md), [ACTION_TESTING.md](./ACTION_TESTING.md), [ARCHITECTURE.md](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md), [THREAT_MODEL.md](./WinForge_Additional_Documents/WinForge/THREAT_MODEL.md), [SECURITY.md](./WinForge_Engineering_Specifications/WinForge/SECURITY.md), [RECOVERY.md](./WinForge_Engineering_Specifications/WinForge/RECOVERY.md), and [PLAN.md](./WinForge_Project_Documents/WinForge/PLAN.md).

ADRs 0001–0010 establish prior read-only, least-privilege, and evidence-preservation decisions; none selects the V2 host. ADR 0011 is the relevant accepted privileged-host constraint. The V1 audit and testing documents report passing simulation tests, which are evidence only for V1's in-process synthetic behavior. They are not evidence for V2, Windows identity, service/IPC security, real-system recovery, or rollback.

## 3. Hostless-first assessment

### What the hostless phase can validly prove

Provided it remains pure and deterministic, a model/test phase can demonstrate:

- canonicalization and equality behavior over typed, bounded in-memory values;
- that client-controlled claims are not treated as authoritative by the model;
- authorization decisions for declared model inputs;
- legal/illegal lifecycle transitions and duplicate/replay behavior within a simulated ledger;
- that the modeled dispatcher is unreachable until the modeled `Started` barrier is committed;
- conservative classification of modeled crash windows; and
- that handler-reported success alone cannot produce the model's `Verified` result.

These tests are useful falsification tools. They can expose design mistakes before host code exists, but do not validate production enforcement or operating-system semantics.

### What must remain host- or operation-specific

- **Identity provider:** SID, logon AuthenticationId/LUID, session, token type/claims, process binding, PID reuse, impersonation, and handle lifetime are OS/host concerns. A fake identity source can test how policy consumes a typed identity result; it cannot prove the result corresponds to the connected client.
- **Consent provider:** the model may represent a broker-bound session-user confirmation event. No provider abstraction can establish that a real user saw the genuine preview or intended the operation. Tests can cover binding, expiry, replay, and substitution in the protocol, not same-user malware resistance or human comprehension.
- **Durable state:** an in-memory/fake transactional store can exercise state ordering and injected failures. It does not prove a database transaction is durable after power loss, that the external effect is atomic with a record, or that snapshots cannot roll back approval consumption.
- **Transport:** “transport DTOs” are premature while the host and IPC are undecided. Begin with internal, versioned domain/model values and test canonical bytes separately. Do not commit to a wire schema, frame format, JSON rules, or public DTOs as an architecture decision until the actual host, threat model, and supported Windows versions justify them.
- **Operation handlers and targets:** do not define executable handler interfaces, real target resolvers, Windows API wrappers, or generic executor abstractions in this phase. There is no approved operation from which to derive them.
- **Verification and rollback:** model contracts may represent evidence classes and dispositions, but tests with synthetic values do not prove source independence, target attribution, safe compensation, or real-world reversibility.

### Avoid false confidence and unnecessary abstraction

The Phase 1 language in [the implementation plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md) should be interpreted narrowly: a “fake transactional store” is a state-machine test double, not a durable-store prototype; “protocol parser fuzz tests” should not freeze an unselected transport; “request-binding tests” test modeled rules only. Do not create pluggable identity, consent, storage, transport, or handler frameworks just to anticipate competing architectures. Keep the hostless phase to executable pure logic, contract examples, canonicalization vectors, and deterministic model tests.

**Conclusion:** Hostless-first is justified and the correct next step. It is not a miniature substitute for later Windows integration, independent consent review, durable storage validation, or per-operation security review.

## 4. Original finding traceability

Statuses below distinguish design response from implementation proof.

| Original ID | Severity | Remediation evidence and technical assessment | Acceptance criteria / remaining condition | Review status |
|---|---|---|---|---|
| H-01 — Human consent is not authenticated by the proposed protocol | High | Remediation §2 distinguishes OS identity, process instance, rendered preview, affirmative input, and broker authorization. It compares WPF, a separate per-session UI, and OS-mediated consent, and explicitly says none currently establishes human intent against same-user malware. Consequential actions are prohibited. This is an honest limit, not a technical fix to consent authenticity. | Before any operation that depends on consent: name the assurance claim, threat boundary, supported session/UI model, substitution/replay tests, accessibility/session behavior, and obtain independent acceptance. No test can prove subjective comprehension or defeat same-user malware absent a justified trusted OS boundary. | **Unresolved for operations; risk correctly bounded for hostless work.** |
| H-02 — The service architecture is not justified by the first V2 use case | High | Remediation §3 and the implementation plan state no real operation is selected and begin without a host. The alternatives matrix explains relative privilege and complexity. ADR 0011 nevertheless remains an accepted service constraint for privileged operations; ADR 0012 is only a proposal. | Before any executable host decision, select a real use case, enumerate exact rights/scope/coordination needs, compare least-privileged alternatives, and record one current decision. A service is not justified by the existence of a broker abstraction or a desire to test Windows APIs. | **Resolved for hostless phase; unresolved for executable scope.** |
| H-03 — OS identity is not yet bound unambiguously to the connection and request | High | Remediation §4 now defines connection, caller, process, and authorization contexts; one request per connection; broker-derived identity; process handle plus creation time; reconnect/logoff/replacement behavior; impersonation failure handling; and no client liveness requirement after durable acceptance. This is coherent as a candidate protocol. It does not define or test the exact supported Windows mechanism proving the token captured for a request corresponds to the connection-associated PID/process instance, nor settle effective-token/group semantics for authorization. | Before an endpoint: select the OS APIs and supported versions; specify connection/token/process association and token claim evaluation; prove PID reuse, thread impersonation, process exit, reconnect, session switch/logoff, and impersonation/revert failure behavior in Windows integration tests. A fake provider is not evidence. | **Partially resolved by design; not implementation-resolved.** |
| H-04 — Authorization-state rollback and audit anchoring are unresolved | High | Remediation §5 separates accidental restore, ordinary-user tampering, administrator tampering, broker compromise, and key compromise. It rejects local-only strong rollback claims and prohibits operations dependent on local-only one-time-consumption evidence. No external witness or validated TPM counter is selected. | Before any operation whose safety depends on no replay after rollback: select an independently administered anti-rollback anchor or explicitly narrow the supported threat model and deny the operation otherwise. Specify outage, stale-anchor, restore, key-loss, and rekey behavior; test real restore and snapshot scenarios. | **Unresolved for effectful/replay-sensitive operations; residual risk accurately stated.** |
| M-01 — Lifecycle transition definitions are internally inconsistent | Medium | Remediation §6 is declared normative; recovery diagrams now defer to it. `Accepted` without `Started` is safe only under the explicit no-dispatch-before-committed-Started invariant. `Started` with missing outcome proceeds through `Interrupted` to `RecoveryRequired`, with no replay. The model is materially clearer. However, the table is not yet a full operation-level compensation state machine, and the reported crash/fault matrix does not enumerate every pre/post-commit window. | Before any executable handler or compensation: turn each durable event and each side effect into before/after crash cases; model/property tests must reject illegal transitions; prove all dispatch paths obey the barrier; define recovery-step start/result/verification semantics and fence behavior. | **Partially resolved; sufficient basis for hostless modeling only.** |
| M-02 — “Independent verification” has no operational independence criterion | Medium | Remediation §7 defines four assurance classes and their shared-dependency limits. It requires per-operation source, freshness, provenance, and contradiction handling, and says handler return alone is not verification. This is technically coherent and appropriately avoids equating process separation with independence. | For each future operation, identify the postcondition source, shared executor/verifier dependencies, freshness/attribution limits, and fail-closed behavior; tests must demonstrate handler-only success cannot produce `Verified`. | **Resolved at design-criteria level; no implementation evidence.** |
| M-03 — Pipe ACL and multi-session policy are not specified enough | Medium | The remediation and IPC document deliberately defer transport selection and require explicit ACLs, endpoint verification, supported-OS review, and cross-session tests. Deferral is correct; no concrete pipe ACL exists. | Before IPC: select host, caller population, Windows versions, endpoint owner, ACL principals/rights, remote policy, session semantics, server authentication, race/squatting behavior, and tests. Do not present deferred requirements as implemented safeguards. | **Resolved for hostless phase; unresolved for any endpoint.** |
| M-04 — Rollback requirements conflict across accepted and proposed documents | Medium | Remediation §8 classifies reversible/compensable/irreversible/unknown; generic inverse operations are prohibited; irreversible/unknown or no-safe-disposition state changes are denied; compensation is not mislabeled as rollback. This aligns with ADR 0011's rule that undefined/unverified recovery means prohibited. | Every future operation must have an explicit recovery disposition. Tests must deny missing/unknown dispositions, and any allowed recovery step must be separately authorized and independently verified. | **Resolved by design; implementation proof remains future.** |
| M-05 — Normative documentation prematurely states the service design as settled | Medium | Most root V2 documents and ADR 0012 now say no host is selected. However, [ARCHITECTURE.md §8](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md) still says “The V2 design recommends” named-pipe IPC, and [PLAN.md §12](./WinForge_Project_Documents/WinForge/PLAN.md) retains an earlier P0 task list that calls for authenticated named pipes and an Action Broker service. The current section 7.3.2 and V2 plan supersede those statements but do not remove the ambiguity. [ACTION_TESTING.md](./ACTION_TESTING.md) also says a real executor requires a separate Windows service; that statement agrees with ADR 0011 for privileged operations but should distinguish that accepted constraint from the unselected V2 host and non-privileged scope. | Before broad implementation planning, update or explicitly mark these older passages as superseded, preserving ADR history. A repository-wide document review should find no statement that named pipes are currently recommended/selected. | **Partially resolved; normative prose remains inconsistent.** |
| L-01 — Initial IPC numeric limits are examples, not justified operating limits | Low | IPC §3 labels sizes/deadlines provisional and requires DTO-derived sizing and hostile-load validation before protocol freeze. Appropriate for a deferred endpoint. No operational justification or tests exist yet. | Before protocol freeze: document a valid-payload rationale and boundary/slow-client/aggregate-pressure results for every limit. | **Resolved as a design constraint; not validated or frozen.** |

## 5. Consent and identity assessment

### Consent

The remediation correctly distinguishes:

1. **Authenticated identity:** OS token facts about a request context.
2. **Authenticated process instance:** PID plus held process handle and creation time, as supplementary evidence.
3. **User-visible preview:** content generated by the broker and rendered by some UI.
4. **Explicit approval event:** an affirmative input received over a proposed channel.
5. **Broker authorization:** policy and durable state accept a request.

The first four do not automatically prove the fifth, and none proves subjective intent. A separate signed same-user UI process is not inherently isolated from same-user malware. UAC authenticates an elevation decision, not a fully specified operation preview. Challenge/digest binding can make substitution/replay harder, but cannot prove display/input corresponded to user intent.

**Testable in a model:** digest binding, challenge expiry, one-time consumption, replay rejection, changed preview rejection, session/process binding rules, and denial on missing identity evidence.

**Not established by those tests:** genuine human presence, comprehension, freedom from UI spoofing/automation, isolation from same-user malware, or the real OS behavior of a proposed consent mechanism. Those require a concrete host/UI design and independent platform review; some claims may remain unprovable and therefore unsuitable for the intended action.

### Identity

The request tuple and lifecycle are a significant improvement, particularly the clarification that broker-owned work can continue after durable acceptance without requiring a reconnect. Remaining ambiguity is at the OS boundary: “capture the token associated with the exact request message” and “connection-associated PID” are requirements, not proof that the selected API gives those values the claimed association under all supported Windows versions. Effective group/deny-only claims, token elevation/restriction state, process exit during each authorization boundary, response loss, and cross-session cases still need a platform-specific contract and tests.

After durable `Accepted`, client disconnect or process exit is not cancellation; pre-dispatch checks are broker-owned. This is coherent if acceptance is durably committed only after same-principal/session and process-instance checks, and if a later logoff/session-end policy is explicitly operation-specific. The current documents state the latter at a high level but do not define any actual operation, so no execution behavior can be inferred or approved.

## 6. Anti-rollback assessment

The threat model is appropriately candid:

| Threat | Design conclusion |
|---|---|
| Accidental older-backup restore | Local validation may detect some mismatches if independent current-generation evidence survives; coordinated restore of ledger and checkpoint can evade local checks. |
| Ordinary standard-user tampering | Correct ACLs and protected-key access may reduce risk, but concrete file/WAL/SHM/backup/key ACLs are not selected or tested. |
| Local administrator tampering | Not prevented by local-only controls; administrator can modify service, files, ACLs, and local checkpoints. |
| Broker compromise | Not prevented by local MAC/hash chains when the broker can update the ledger or request key operations. |
| Key compromise | Allows forging integrity evidence; rotation cannot restore trust in the compromised history. |
| Full machine snapshot rollback | Not prevented by local files, timestamps, ACLs, or a hash chain. It needs a separately controlled witness or an independently validated monotonic hardware design with explicit failure semantics. |

The design does not claim strong anti-rollback. That is correct. The consequence is also correct: do not implement an operation whose safety relies on a local record proving an approval was consumed exactly once across rollback. A fake store can test a modeled sequence number, but cannot establish an external anchor, hardware monotonicity, atomic coordination, or outage behavior.

## 7. Lifecycle and recovery

The remediation's transition table is materially more consistent than the original. It distinguishes `Accepted` from `Started`, makes `Started` a dispatch barrier, treats handler completion separately from verification, routes uncertain outcomes to `RecoveryRequired`, and forbids automatic handler replay. The proof that `Accepted` without `Started` means no dispatch remains conditional on all code paths obeying the barrier and on trustworthy durable state.

### Termination windows

“Before event” means the transaction did not commit; “after event” means the event/state projection is durably committed. External side effects are called out separately. This table is the review's explicit window analysis; it is not implementation proof.

| Transition / boundary | Crash before durable event | Crash after durable event, before next step | Required safe recovery |
|---|---|---|---|
| `None → Proposed` | No proposal exists; a retry may create a new broker ID. | Proposal exists, but is not approved or executable. | Return/query existing proposal by broker-owned idempotency state; do not infer execution. |
| `Proposed → Validated` | Remain `Proposed`; validate again from current broker state. | `Validated` is historical evidence only. | Revalidate target, policy, and preconditions before approval/acceptance. |
| `Proposed/Validated → Rejected` or `Expired` | Prior nonterminal state remains; retry the transition decision, not an operation. | Terminal proposal state is preserved. | New material request gets a new ID and preview. |
| `Validated → ApprovalPending` | No challenge exists; issuing one can be retried under current conditions. | Outstanding challenge exists. | Invalidate outstanding challenge after restart as designed; require fresh presentation/confirmation. |
| `ApprovalPending → Approved` | No approval is established; duplicate input cannot be treated as approved. | Approval event and exact binding are recorded. | Revalidate expiry, owner/session, process instance, policy, and target before `Accepted`; restart-survival rule for an approved-but-unaccepted record must be explicit in implementation. |
| `ApprovalPending → Rejected/Expired/Cancelled` | Challenge remains pending until expiry/revocation is durably recorded; it must not be used meanwhile if the decision is already known. | Challenge is terminal and cannot be reused. | Fail closed on ambiguous commit; query ledger and revoke if outcome cannot be established. |
| `Approved → Expired/Cancelled/Rejected` | Approval may appear valid until the transition is committed. | Approval is invalidated and must not be consumed. | Serialize revocation with acceptance; one atomic winner only. |
| `Approved → Accepted` | Approval remains unconsumed; no dispatch is allowed. | Execution ID, consumed approval, and target fence are durable. | Retry/query the same execution ID; never create a second accepted execution or reuse approval. |
| `Accepted → Started` | Under the dispatch barrier, no handler has been dispatched; approval remains consumed. | Handler has not necessarily run yet, but may be dispatched only after the committed `Started` is observed. | Before event: safely close as `Cancelled/RestartBeforeDispatch` only with valid ledger/barrier evidence. After event: any crash is uncertain; do not replay automatically. |
| `Started → external effect → Completed` | If no handler call occurred, a durable `Started` still cannot prove no call/effect; conservative recovery applies. | If effect occurs before `Completed` commits, external state may have changed without a terminal record. | `Interrupted → RecoveryRequired`; reconcile read-only; never infer no-op or rerun. |
| `Started → Failed` or `Started → Cancelled` | Remain `Started`; outcome is unknown. | Terminal only if operation-specific evidence proves no effect or safe cancellation. | If any effect or ambiguity remains, use `RecoveryRequired`; no automatic retry. |
| `Started → Interrupted → RecoveryRequired` | Crash before either event leaves earlier state; startup must rediscover unresolved `Started` and persist the fence before reconciliation. | `Interrupted` is visible; if the next transition/fence has not committed, startup must finish it before accepting conflicting work. | No handler replay; fence before read-only reconciliation. |
| `Completed → Verified` or `Completed → Failed` | Remain `Completed`; a read-only verifier may be rerun only if its contract says safe. | Verification result is recorded. | Handler is never rerun. Inaccessible/contradictory/uncertain evidence goes to `RecoveryRequired`. |
| Any unresolved state → `RecoveryRequired` | If transition/fence commit did not happen, startup must derive unresolved work from durable prior state and fence it before accepting conflicting actions. | Fence and reason are durable. | Never clear the fence by timeout, cleanup, restart, or client dismissal. |
| `RecoveryRequired → Reconciled` | Remain fenced; retry only safe read-only reconciliation or a separately authorized recovery step. | Disposition, verification evidence, and fence release commit together. | Do not attribute observed state to the interrupted request without evidence. If compensation had side effects before this commit, remain conservative and do not replay it blindly. |
| `RecoveryRequired → RecoveryRequired` | Existing fence remains; attempted event may be absent. | Failure/inconclusive attempt is appended; fence remains. | Read-only retry may be bounded; no automatic original-handler retry. |

### Lifecycle finding

The table gives useful state-level crash semantics, but it does not yet define the complete durable sub-lifecycle for a **state-changing recovery/compensation step**. “Recovery-step events” and separate authorization are mentioned, but there is no normative per-step pre-effect durable start barrier, crash-after-compensation semantics, idempotency/retry rule, or verified completion transition. A crash during a compensating action can itself leave an ambiguous external effect. No such action may be implemented until this is specified and tested. This gap does not block a hostless model that explicitly excludes performing compensation.

Also define the approval-revocation-versus-acceptance race as one serialized/atomic ledger decision, and explicitly document whether a still-valid `Approved` item may survive a broker restart. These are testable once a concrete state store and lifecycle model are selected.

## 8. Verification and rollback

The four verification classes correctly distinguish handler-reported results, same-component readback, a separate verifier, and an independent evidence source. The design appropriately requires evidence source, freshness, target identity, dependencies, and contradiction handling per operation. Process separation alone is not presented as independence. This is suitable contract guidance; it does not establish that any future Windows API is independent or sufficiently trustworthy.

Rollback rules are internally aligned with ADR 0011:

- generic inverse/rollback is prohibited;
- a compensating action is not exact rollback;
- an attempted rollback is not success;
- irreversible or unknown state-changing operations, or operations without a safe recovery disposition, are denied under the current design;
- a separately authorized compensating action requires its own checkpoints and verification; and
- partial or unknown effects remain `RecoveryRequired`.

The restriction is strong and testable in the hostless model. No real operation or rollback path is defined, and none should be added by this review.

## 9. ADR 0011 / ADR 0012 consistency

- **ADR 0011 actually mandates:** the desktop remains non-elevated/read-only; a separate Action Broker **service** is the only future privileged execution boundary; requests are bound, expiring, deny-by-default, replay-resistant, target-revalidated, journaled, and state-changing operations require explicit rollback/recovery contracts. It does not provide an operation-to-rights analysis proving that a service is necessary.
- **ADR 0012 currently does:** propose a demand-start service and named pipe, but explicitly leaves it Proposed, recognizes that no operation proves service necessity, and recommends hostless contracts/tests first. It says ADR 0011 remains unchanged and that a different privileged host requires an explicit new ADR.
- **Can ADR 0012 refine or supersede ADR 0011?** Not while Proposed. It does not silently amend the accepted constraint. A future accepted ADR may explicitly narrow or supersede ADR 0011 while preserving it as history.
- **Current consistency:** Hostless contracts/model work is not a privileged host, so it does not conflict with ADR 0011. A per-user or in-process executable path for privileged work would conflict unless a new ADR explicitly supersedes/narrows ADR 0011. ADR 0012 does not approve any of those alternatives.
- **Disposition:** Keep ADR 0012 **Proposed**. Do not accept the service/named-pipe design absent a concrete operation and rights matrix. A separate ADR is required before implementing a non-service privileged host. Do not edit historical ADRs 0001–0011.

One additional documentation inconsistency remains: [ARCHITECTURE.md §8](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md) still calls named pipes the V2 recommendation, and [PLAN.md §12](./WinForge_Project_Documents/WinForge/PLAN.md) still lists the service and authenticated named-pipe IPC as P0 tasks. These old passages should be marked superseded or revised in a later documentation update; they do not overturn the explicit ADR status but can mislead implementers.

## 10. Findings by severity

No Critical findings are assigned for the scoped hostless-only phase.

### High

#### SIR-H-01 — Human-consent authenticity remains unproven

- **Evidence:** Remediation §2 expressly states that WPF, a separate same-user consent process, UAC, signatures, and challenges do not currently prove exact-preview human intent against same-user malware.
- **Impact:** Any consequential action authorized on the assumption of genuine human intent could be initiated or misrepresented by malware in the user session.
- **Recommendation:** Keep consent-dependent and consequential operations prohibited until a concrete trust boundary is independently accepted; do not rename a session-user confirmation as human intent.
- **Measurable acceptance criteria:** An operation-specific consent threat model identifies the attacker, UI/session boundary, exact bound data, replay/substitution defenses, accessibility/logoff/RDP behavior, and residual risks; an independent reviewer accepts it. Where same-user malware resistance cannot be demonstrated, the operation remains denied.

#### SIR-H-02 — Strong anti-rollback is unavailable for any selected local-only design

- **Evidence:** Remediation §5 says local transactions, ACLs, protected keys, and hash chains do not stop administrator/broker rollback; no independent witness or validated TPM design is selected.
- **Impact:** Restoring old authorization state could resurrect approval or erase accepted execution, violating replay safety.
- **Recommendation:** For any operation dependent on durable one-time authorization across rollback, select and validate a separately controlled anchor or prohibit that operation. Do not overstate a local sequence number as monotonic against snapshots.
- **Measurable acceptance criteria:** Define the covered attacker and anchor trust domain, sequence/freshness protocol, availability/outage behavior, restore/key-rotation ceremony, and tests for backup restore, full snapshot rollback, stale/unavailable anchor, and key compromise. Otherwise mark that operation prohibited.

### Medium

#### SIR-M-01 — Hostless plan risks freezing transport and infrastructure abstractions too early

- **Evidence:** [Implementation Plan Phase 1](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md) calls for “versioned typed transport DTOs,” protocol parser fuzzing, and fake transactional storage before host/transport selection. [Architecture §6](./ACTION_BROKER_V2_ARCHITECTURE.md) lists future Contracts, Application, Service, Infrastructure, and client layers.
- **Impact:** A JSON/wire format, fake store semantics, or provider interfaces may encode assumptions about named pipes, service identity, transaction durability, or consent without a use case. Tests can then pass while validating only the chosen fake model, creating false confidence and rework.
- **Recommendation:** Limit initial implementation to pure typed model values, canonicalization vectors, state-machine/property tests, and an explicitly non-durable fake. Keep transport DTOs, identity/consent providers, durable-storage interfaces, handler abstractions, and Windows adapters uncommitted until grounded in a concrete host/use case.
- **Measurable acceptance criteria:** Phase 1 contains no selected wire schema/transport API, Windows identity or consent implementation, real handler interface, or persistence claim; test names/docs label fake-store results as model-only; no test result is reported as evidence of Windows or power-loss security.

#### SIR-M-02 — OS token-to-process binding is specified as a requirement, not a platform-proven rule

- **Evidence:** Remediation §4 says capture the token associated with a request and the connection-associated PID/process handle; IPC requires rejecting mismatches but specifies no selected API/version matrix or proof that these observations are bound as assumed.
- **Impact:** If token context and client process context can diverge, policy may authorize a request under the wrong principal or apply process restrictions to a different process. PID plus start time does not independently authenticate the token.
- **Recommendation:** Before IPC, specify the exact Windows API sequence and supported builds, token type/effective group interpretation, process-handle/PID binding, disconnect/reconnect timing, and failure handling. Keep this out of hostless assurance claims.
- **Measurable acceptance criteria:** Windows integration tests bind each authorized request to the intended token/process/session; cover token/process mismatch, thread impersonation changes, PID reuse, process exit at approval and acceptance, logoff, reconnect, and impersonation/revert failures; every ambiguous case denies before dispatch.

#### SIR-M-03 — Recovery compensation lacks a per-step durable dispatch/retry model

- **Evidence:** Remediation §6 permits a separately authorized recovery procedure and “recovery-step events,” but does not normatively define a pre-effect durable start barrier and post-crash semantics for each compensating side effect.
- **Impact:** A crash after a compensating effect but before its event could lead to duplicate compensation, an incorrectly released fence, or an unclassified partial recovery.
- **Recommendation:** Before implementing recovery/compensation, define each recovery step as its own operation with durable intent/start/completion evidence, idempotency or no-retry rule, verification, fence retention, and crash recovery.
- **Measurable acceptance criteria:** A state model enumerates every compensation event and before/after crash point; model/property tests prove no step dispatch before its durable barrier, no automatic repeat of uncertain steps, and no fence release before independently verified disposition.

#### SIR-M-04 — Stale normative planning text still presents named pipes/service as the recommendation

- **Evidence:** [ARCHITECTURE.md §8](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md) says the V2 design recommends named-pipe IPC; [PLAN.md §12](./WinForge_Project_Documents/WinForge/PLAN.md) retains a P0 task list for authenticated named pipes and a service. Current V2 docs say no host/transport is selected.
- **Impact:** Implementers may follow old roadmap language and treat the Proposed service/pipe design as already selected, bypassing ADR and operation-scope gates.
- **Recommendation:** In a documentation-only cleanup, explicitly supersede or revise those passages and link to ADR 0012/remediation. Retain ADR 0011 as the accepted privileged-service constraint and preserve historical ADRs.
- **Measurable acceptance criteria:** Search all normative architecture/roadmap docs for claims of selected/recommended V2 transport; each remaining mention clearly says proposed/deferred or is an ADR 0011 requirement scoped to privileged operations.

#### SIR-M-05 — Original identity and lifecycle findings are not implementation-resolved

- **Evidence:** The remediation matrix marks H-03 “Resolved by design” and M-01 “Resolved by design,” while also saying no OS identity, ledger, or lifecycle implementation exists. The transition table depends on a valid durable ledger and an unbypassable dispatch barrier; tests are acceptance criteria, not present evidence.
- **Impact:** Readers may mistake documentary precision for a tested control, especially for `Accepted` without `Started`, revocation-vs-acceptance races, and recovery fencing.
- **Recommendation:** Use “resolved at design level; implementation unverified” consistently in future status summaries. Keep the accepted-versus-started guarantee explicitly conditional on a validated store and enforced dispatch barrier.
- **Measurable acceptance criteria:** No document calls these controls implemented or verified before implementation; model tests cover revocation/acceptance serialization, durable barrier bypass attempts, every transition's pre/post-commit crash, and startup fencing.

### Low

#### SIR-L-01 — Protocol resource limits remain provisional

- **Evidence:** IPC §3 proposes frame, depth, member, string, and deadline limits while requiring later validation.
- **Impact:** Frozen limits may be either too permissive or reject valid requests.
- **Recommendation:** Keep them provisional until a real protocol and bounded payload needs are selected.
- **Measurable acceptance criteria:** For each final limit, document maximum valid payload and test at-limit, one-over-limit, slow-client, and aggregate-pressure cases on supported hosts.

## 11. Required changes and residual risks

### Required before starting hostless contract/model work

1. Scope the phase to pure logical contracts, canonicalization, state-machine/property tests, and a clearly labeled fake store. Do not freeze transport or host APIs.
2. Add model tests for the specified lifecycle constraints, while explicitly stating they do not prove OS authentication, durable storage, anti-rollback, or real execution safety.
3. Keep consent represented as a non-authoritative session-user confirmation event; do not claim that the model proves a human saw the preview.
4. Treat the dispatch barrier as a model invariant only until a concrete durable store and host exist.

### Required before any host, IPC, or executor

1. Choose a concrete operation and rights/scope matrix; decide whether it needs a host, and resolve ADR 0011/0012 governance.
2. Define supported Windows builds and the actual token/process/session API protocol, ACLs, endpoint ownership, and failure handling.
3. Resolve the operation's consent requirement and explicitly deny it if no accepted mechanism meets the threat boundary.
4. Select the durable ledger, backup/restore behavior, key lifecycle, and anti-rollback trust assumption; prohibit operations whose safety cannot be established.
5. Complete the full state/side-effect/recovery-step crash model, including revocation/acceptance races and compensation dispatch.
6. For every operation, specify target resolution, preconditions, effect/idempotency behavior, verifier evidence, rollback/recovery disposition, and bounded operational escalation.
7. Conduct Windows integration tests and independent security/code review. V1 tests and model fakes do not satisfy these gates.

### Residual risks

- Same-user malware can spoof or automate ordinary session UI; exact human intent is not proven.
- Local-only state cannot provide strong administrator/broker-resistant anti-rollback.
- Arbitrary Windows effects are not atomic with the broker ledger; some failures may remain permanently ambiguous.
- A hostless test cannot validate OS identity, endpoint ACLs, service privilege, process/token binding, or target/verifier behavior.
- ADR 0011 constrains future privileged execution to a separate service until explicitly superseded.

## 12. Final disposition

**Verdict: READY FOR HOSTLESS CONTRACTS AND TESTS.**

This verdict permits only a narrowly scoped, host-independent model/test phase. It does **not** accept ADR 0012, select named pipes or any other IPC, approve a Windows service or helper, authorize a real operation, or establish that a future broker is secure. ADR 0012 should remain **Proposed**. Findings H-01 and H-04 remain high-severity blockers for consequential/replay-sensitive execution; H-03 and lifecycle guarantees remain unverified until host/store-specific tests. The hostless phase must not be represented as closing those blockers.

**Files created or modified:** Created only `ACTION_BROKER_V2_SECOND_INDEPENDENT_REVIEW.md`. No other files were modified.

**Boundary confirmation:** No source code, tests, database, migrations, service, IPC, elevation, executor, or Windows-changing operation was created or changed. Action Broker V1 remains unchanged and simulation-only.
