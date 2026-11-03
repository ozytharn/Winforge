# WinForge Action Broker V2 — Architecture Remediation

**Status:** Design-only remediation prepared for a second independent review.  
**Decision:** ADR 0012 remains **Proposed**. No V2 host or execution architecture is accepted by this document.  
**Boundary:** No service, IPC endpoint, helper, elevation, executor, real operation, source change, database change, or migration is authorized. Action Broker V1 remains unchanged and simulation-only.

## 1. Executive outcome

This remediation addresses every Critical, High, and Medium finding in [the independent review](./ACTION_BROKER_V2_INDEPENDENT_REVIEW.md), and also tracks its Low finding.

Four main blocker outcomes:

1. **Consent:** No reviewed approach can currently establish that a human saw and knowingly approved the exact operation against same-user malware. The design no longer calls a broker challenge or a signed client proof of human intent. No consequential V2 operation is eligible until a consent trust boundary is independently accepted.
2. **Hosting:** No current V2 operation requires machine-wide hosting because no real V2 operation has been selected. The first implementation phase is contracts and deterministic tests without a host. A per-user unelevated broker is only a candidate if a concrete user-scoped use case justifies it. A machine-wide service is deferred unless a reviewed operation demonstrates a machine-wide privilege or coordination need.
3. **Identity binding:** The candidate protocol is one request per connection. OS token identity is captured for that request and is authoritative for SID/logon/session authorization. PID and process creation time identify a process instance only as supplementary context; they do not prove consent or trusted UI. Disconnect/reconnect creates a new connection identity and requires reauthorization.
4. **Anti-rollback:** Ordinary local persistence, timestamps, ACLs, DPAPI/CNG keys, or a hash chain do not provide strong rollback resistance against a local administrator or compromised broker. They can detect some accidental corruption and protect against ordinary-user edits under explicit assumptions. Stronger rollback detection would require an independently controlled witness, such as a remote append-only service, or separately validated hardware monotonic state. Neither is selected or guaranteed here. Operations whose authorization safety depends on strong rollback resistance remain prohibited.

The lifecycle inconsistencies are replaced by one canonical transition table. Verification and rollback are given explicit assurance and eligibility rules. These are design corrections, not implementation evidence.

## 2. Consent model and limits

These are separate facts, not interchangeable claims:

1. **Windows identity authentication:** OS token state identifies a principal/logon context associated with a request.
2. **Client process identification:** Connection-associated PID plus process creation time identifies a process instance to the extent the OS APIs and access allow. A signed image can establish publisher/integrity metadata, not uncompromised runtime state.
3. **User-visible presentation:** A component can render a broker-generated canonical preview. The broker can bind the preview bytes/digest to a request; it cannot infer from that alone that the human saw or understood it.
4. **Explicit human consent:** A user-facing affirmative input can be recorded. Ordinary desktop input does not establish human intent against code running in the same user/session context.
5. **Broker authorization:** The broker can enforce policy, expiry, one-time consumption, identity/session scope, request equality, and durable state. This proves that broker rules accepted an authorization record, not that human intent was authentic.

### Options evaluated

| Approach | What broker can verify | What it cannot verify / compromised-client risk | Substitution and replay | Session, usability, complexity |
|---|---|---|---|---|
| **A. WPF client renders preview and collects approval** | The WPF process sent an approval message for a request/challenge; OS token identifies the caller context; broker can re-derive the exact preview and bind a one-time record. | A compromised client can show different text, hide warnings, auto-approve, or send an approval without a human. Signature and hash do not prove what the user saw. | Server-side exact request/challenge binding can prevent cross-request substitution and replay, but cannot prove display/input corresponded to user intent. | Simplest UX; inherits desktop session/accessibility support. Lowest implementation cost, weakest presentation trust. Not acceptable as strong consent for consequential operations. |
| **B. Separately trusted, broker-controlled per-session consent interface** | If implemented, the broker can send canonical preview data to a fixed consent component and accept a response bound to a request, session, expiry, and nonce. It can check component identity and channel properties. | A separate signed process running as the same user is not automatically isolated from same-user malware, UI automation, overlays, injection, or replacement. The broker cannot prove the user read/comprehended content. | A nonce, exact digest, one-time durable state, and session binding can prevent protocol replay/substitution. They do not prove the user saw the genuine interface. | Service runs in Session 0, so UI must be per-session and independently launched/connected. Accessibility, localization, multiple sessions, lock screen, RDP, and logoff add significant work. More complex; still not robust against same-user malware absent a justified OS boundary. |
| **C. OS-mediated consent (for example, elevation/UAC where appropriate)** | The OS can mediate an elevation decision for a binary/process under its supported policy. The broker can verify the resulting token/process context and bind a separately maintained request to it. | A standard elevation prompt authorizes elevation of a program, not the exact operation preview. It does not show all normalized parameters/target or prove the user understood them. UAC alone is not action-specific consent. Credential entry/authentication likewise is not proof of intent to a specific operation. | The helper can still receive a swapped request unless the operation digest is bound across launch and independently checked. UAC consent is not a request-bound one-time approval. | OS-controlled interaction may offer stronger anti-spoofing for the elevation decision, but increases privilege impact, launch races, accessibility and policy variation. Not a general solution for exact-preview consent. |

### Consent decision

**Trustworthy human consent cannot currently be guaranteed for a consequential V2 action.** Approach B is the best candidate for a broker-authored exact preview in a future prototype, but is not accepted as a defense against same-user malware. Approach C may authorize elevation, but not the exact operation. Cryptographic signatures, hashes, and challenges bind bytes and state; they do not prove the human saw those bytes.

Therefore:

- No operation that requires meaningful human consent for safety may be enabled under the current design.
- No consequential, security-sensitive, machine-wide, destructive, or difficult-to-reverse operation is eligible.
- A future low-impact, user-scope action may be considered only after its threat model explicitly accepts that same-user malware can act with the user's available rights. This is not a claim of strong human consent.
- Any V2 approval event must be named and documented as a **session-user confirmation** unless and until the consent boundary is independently validated. Do not label it authenticated human intent.
- Client disconnect, logoff, session switch, preview change, policy change, client-process replacement, or challenge expiry revokes any unconsumed confirmation. A consumed approval is never transferred to a reconnecting client.

## 3. Hosting decision analysis

No actual V2 real operation is currently selected in [ACTION_CATALOG.md](./ACTION_CATALOG.md). Existing V1 simulation entries do not establish a V2 privilege requirement. The current evidence therefore supports **no machine-wide service requirement**.

| Criterion | Demand-start machine-wide service | Per-user unelevated broker | Narrow UAC helper | Local RPC / COM-RPC |
|---|---|---|---|---|
| Least privilege | Can hold capabilities not available to the user; impact depends on service token/ACL and handlers. | Naturally limited to the logged-on user's token; no elevation boundary. | Often receives an elevated token broader than one action unless further constrained. | IPC choices, not hosting/privilege choices; inherit host authority. |
| Multi-user coordination | Central state and global conflict coordination are possible; cross-user isolation becomes a critical service responsibility. | Session-isolated by default; shared-resource coordination needs a separate design. | No durable global coordinator by default. | Does not itself solve multi-user authorization. |
| Session isolation/consent | Session 0 needs a separate session UI and secure broker/UI rendezvous. | UI and broker are within one user session, but same-user malware remains in scope. | OS-mediated elevation UI is available, but not exact operation consent. | Requires explicit RPC/COM security configuration; no consent guarantee. |
| Attack surface | Service endpoint, SCM controls, service binary/update path, ledger, and cross-session handling. Demand-start reduces idle time, not exposure while running. | Smaller system-wide attack surface; same-user process can interact with broker. | Short-lived helper but elevated startup/argument/IPC surface. | Mature platform mechanisms but configuration and endpoint attack surface remain. |
| Operational cost | Installer, service identity, ACLs, upgrade/downgrade, recovery, support, multi-session testing. | Per-user lifecycle and data isolation; no machine-wide servicing. | Per-operation launch/consent and recovery complexity. | Additional endpoint/interface/security and deployment configuration. |
| Recovery/anti-rollback | Central ledger possible, but service compromise can alter state; external anchor still needed for stronger rollback guarantees. | User-writable rollback risk unless separately protected; no automatic durable anti-rollback. | Cross-launch dedupe/recovery is difficult without a durable broker. | Depends on host and store; RPC does not provide transactionality with Windows effects. |
| Suitable current scope | No selected operation proves need. | Candidate for a future user-scope, non-privileged use case after consent/risk approval. | Not a general broker; only narrow operation-specific review. | Candidate transport if a host is justified; no preference established over named pipes yet. |

### Hosting recommendation

1. **First V2 implementation phase: no runtime host.** Build only typed contracts, deterministic canonicalization vectors, lifecycle model, fault-injectable storage abstractions, and tests. No endpoint and no executable action.
2. **If a concrete user-scope use case is later approved:** compare a per-user process with in-process orchestration and choose the least complex boundary. A per-user process does not defend against same-user malware and must not be presented as a privilege boundary.
3. **Only if a concrete operation requires machine-wide authority or coordination:** reopen the service decision with an operation-to-rights matrix, service token/ACL model, session policy, consent decision, durable state model, and failure analysis. No current evidence justifies prebuilding a service shell.
4. **IPC selection is deferred until host selection.** Named pipes, Local RPC, and COM/RPC must be compared for the chosen supported Windows versions and security model. A pipe must not be treated as authenticated merely because it is local or hard to guess.

### ADR 0011 governance constraint

[ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) is accepted and says a separate Action Broker service is the future privileged boundary. This remediation does not edit it or pretend it has been superseded. Because no machine-wide operation is selected, ADR 0011 remains a future constraint for privileged operations, while a no-service user-scope runtime would conflict with its current wording. **No such runtime may be implemented until a new explicit ADR narrows or supersedes that constraint.** ADR 0012 stays Proposed and cannot itself silently amend ADR 0011.

## 4. Canonical request identity model

The following is a candidate model for any future IPC design; no IPC mechanism is selected or implemented.

### Identity tuple

```text
ConnectionContext =
  broker-generated connection ID + endpoint instance/epoch
  + one-request nonce + connection creation/expiry

CallerContext =
  OS token user SID + logon AuthenticationId/LUID + Windows session ID
  + token type/impersonation level + relevant group/deny-only claims

ProcessContext =
  OS-reported client PID + held process handle + process creation time
  (supplementary context; never human identity or consent)

AuthorizationContext =
  broker request ID + immutable action/catalog version + stable target ID/version
  + canonical normalized parameter bytes + precondition/policy version
  + preview digest + confirmation/approval ID + expiry + one-time state
```

The approval record also identifies the requesting process instance and the consent-component instance separately when those are distinct; neither is evidence of human intent.

Client-supplied user name, SID, PID, session, process path, risk class, preview digest, policy, approval state, or approval boolean is never authoritative.

### Capture and revalidation rules

| Value | Capture | Revalidate / lifetime rule |
|---|---|---|
| Connection ID, endpoint instance, nonce | Broker creates at accept; unique per connection. | Valid for one bounded request only. Disconnect, timeout, malformed frame, or response closes it. Reconnect gets a new identity and nonce. |
| User SID, AuthenticationId/LUID, session ID, token claims | Capture from the OS token associated with the exact request message; fail closed if token capture is unavailable or ambiguous. | Immutable facts about that captured request, not a forever-valid session assertion. At approval and acceptance, require a fresh authenticated request with the same SID/LUID/session and active logon. If it differs, revoke unconsumed confirmation and deny. Before dispatch, check session liveness only if the operation contract requires an active session; do not require a client reconnect after durable acceptance. |
| PID/process handle/process creation time | Capture from the OS connection; hold a process handle and record creation time. | Confirm process remains live and identity unchanged through approval consumption. PID alone is never reused as identity. If the process exits/restarts before consumption, revoke pending approval and require a new proposal/confirmation. After durable acceptance, client-process exit alone does not cancel broker-owned work. |
| Process image/signature/path | Optional supplemental evidence from OS file/process inspection. | Never sole identity or human-consent proof. Any policy dependence requires a separately reviewed integrity check and supported Windows matrix. |
| Target/action/policy/preconditions | Broker resolves from the allowlist and current state. | Re-resolve at proposal, confirmation, acceptance, and immediately before dispatch. Any material change invalidates approval and requires a new proposal. |
| Approval/expiry/one-time state | Broker persists exact request binding and expiry. | Atomically consume once with accepted state. Expiry is evaluated using a trusted time policy; clock rollback/corruption uncertainty expires pending approvals and blocks acceptance. |

### Request and process lifecycle

- **One request per connection** is the default. Do not reuse a connection-level identity snapshot for multiple messages.
- A request is authorized against the captured token context for that connection. If process primary-token information and pipe-message token evidence conflict, reject rather than infer identity. Process identity remains supplemental; token identity is the authority for access scope.
- PID reuse is addressed with a held process handle plus creation time, but this only prevents confusing process instances; it does not make the process trustworthy.
- Process termination before approval consumption revokes pending confirmation. After durable acceptance/start, process termination does not erase the request; execution follows its operation contract and recovery state.
- User logoff/session end before acceptance revokes pending approval. After `Started`, identity is retained as historical authorization evidence; the operation is not silently cancelled or replayed. Cancellation/recovery is operation-specific.
- Reconnect always authenticates anew. Approval does not transfer to a different process instance or session. Existing execution status can be queried only by a newly authenticated principal satisfying the stored owner/session policy.
- Reauthenticate at approval and acceptance using a fresh one-request connection. Require the same SID/LUID/session and, for an unconsumed approval, same requesting process instance. After durable acceptance, the broker owns the execution; client disconnect/termination does not cancel it. At pre-dispatch, revalidate policy, target, preconditions, and any operation-required session liveness without attributing a new client identity to the accepted request.
- Cross-session access is denied even for the same SID unless a future explicit policy approves it. Session ID alone is insufficient; SID and AuthenticationId/LUID are also bound.
- Impersonation failure means reject and close. Reversion failure means retire the worker; if process-wide service state could be contaminated, fail-stop the host. Never continue under the service token after an impersonation or revert error.
- No identity can establish human intent. Authorization provenance must say “OS-authenticated caller context” and “session-user confirmation,” not “authenticated human consent.”

## 5. Anti-rollback authorization state

### Threat classes and assurance

| Attacker/event | Local transactions and ACLs | OS-protected key + local chain/checkpoint | Independent remote witness or validated TPM monotonic state |
|---|---|---|---|
| Accidental older-backup restore | Transactions protect atomic updates but do not inherently identify a stale restored snapshot. A separate current-generation record can detect some restore mistakes only if stored outside that backup. | Detects content alteration if key and latest checkpoint survive; fails if both are restored together. | Can detect stale generation when witness/counter is outside the restored image; an interrupted counter/store update may require recovery. |
| Ordinary non-admin user editing service-owned state | ACLs can deny writes when correctly configured; not proof until tested for DB, WAL, SHM, backups, and key paths. | Key access can prevent ordinary user from recomputing valid MACs, subject to ACL/service identity. | Adds rollback evidence if independently controlled and available. |
| Local administrator malicious rollback | Not prevented. Administrator may alter ACLs, service, local files, backups, and local checkpoints. | Not prevented if administrator can control the service or invoke key use; non-exportability does not stop authorized signing of altered data. | Remote independent witness may detect rollback; hardware counter can detect some disk rollback but does not stop admin from changing service/binary or using permitted counter operations. |
| Broker compromise | Not prevented; broker can falsify ledger/state. | Not prevented if compromised broker can invoke the key or update the ledger. | Remote witness can preserve externally received sequence, but a compromised broker may still lie about events before submission; counter policy may constrain but not eliminate it. |
| Integrity-key compromise | No cryptographic integrity assurance. | Chain can be forged/recomputed by key holder. Rotation cannot retroactively restore trust. | Independent witness can constrain history only after checkpoints were received; compromise before anchoring remains residual. |

### Proposed minimum policy

1. Keep durable authorization state and event history in one transactional ledger with unique IDs, sequence numbers, explicit state version, event/state atomic commits, and a single-writer/fencing rule.
2. Store backups separately and mark them as untrusted until restore validation. Never restore an older ledger and resume acceptance as if current.
3. On startup, compare schema/state sequence, last known generation, event chain, key identity, backup provenance, and any configured witness/counter. Corruption, missing keys, or unexplained generation regression disables acceptance; preserve bytes and require an audited recovery procedure.
4. A local protected key (DPAPI/CNG or equivalent) may protect key material/access against some non-admin file readers and detect modifications. It does **not** prevent rollback of a matching ledger/checkpoint, and does not protect against a compromised broker/service identity or local administrator.
5. A TPM monotonic counter is only a candidate for a separately validated design. It is not assumed to be available, provisioned, suitably protected, sufficiently durable for per-transition writes, or atomically coordinated with a database. Counter advance before ledger commit can leave a gap; database commit before counter advance can leave stale accepted state. Any mismatch fails closed and goes to recovery, never to “empty state.”
6. A remote append-only witness can provide an independent sequence anchor if separately administered and reachable. It changes privacy, availability, account, retention, and threat assumptions. Without the witness, no claim of rollback resistance against an administrator or broker compromise is made.
7. Since no independent anchor has been selected, **strong anti-rollback is unresolved**. No state-changing or replay-sensitive V2 operation may depend on local-only timestamps, file protection, or hash-chain integrity as proof that an approval was not consumed.

## 6. Canonical lifecycle state machine

The table below is the sole normative transition model. Events are immutable records; the current state is a transactionally updated projection. Every transition commits the event and state version atomically before responding. No handler dispatch occurs unless `Started` is durably committed and read/confirmed by the dispatcher. If the storage durability guarantee is uncertain, dispatch is prohibited.

| From → To | Preconditions and authoritative actor | Durable record and authorization | Crash / retry / recovery rule |
|---|---|---|---|
| `None → Proposed` | Broker has parsed bounded input and generated its own request ID, target proposal, catalog version, and preview. | `ProposalCreated` plus immutable proposal; no execution approval. | If commit absent, no request exists. Client may create a new proposal. Same correlation ID is not authority. |
| `Proposed → Validated` | Broker validates typed schema, allowlist, target resolution, policy, preconditions, and preview. | `ProposalValidated` with evidence/version digests. | A crash before commit leaves `Proposed`; revalidate on retry. Do not infer validation from a client display. |
| `Proposed/Validated → Rejected` | Broker policy/validation fails. | `ProposalRejected` with bounded reason. | Terminal for that proposal. A changed request needs a new request ID and preview. |
| `Proposed/Validated → Expired` | Broker time policy finds proposal expired before approval/acceptance. | `ProposalExpired`. | Terminal; new proposal and confirmation required. |
| `Validated → ApprovalPending` | Broker creates a one-time confirmation challenge for the exact versioned preview and authenticated owner/session. | `ConfirmationIssued`; challenge expiry, owner, session and digest persisted. This is not approval. | Crash/restart invalidates outstanding challenge; require fresh presentation/confirmation. |
| `ApprovalPending → Approved` | Consent component records affirmative session-user input for the exact request/challenge; broker independently checks current context, digest, expiry, policy and target. If a distinct consent component is used, authenticate and bind its process instance separately from the requesting process. | `ConfirmationAccepted` and `ApprovalRecorded` committed atomically. Record source and assurance as session-user confirmation, not authenticated human intent. | Crash before commit means no approval. Duplicate callback returns existing status only; cannot create another approval. A distinct component identity does not prove what the user saw or intended. |
| `ApprovalPending → Rejected/Expired/Cancelled` | User denial/close, timeout, logoff, session change, process replacement, target/policy change, or broker denial. | Corresponding revocation/expiry event. | Challenge cannot be reused. New proposal and user interaction required for material changes. |
| `Approved → Expired/Cancelled/Rejected` | Expiry, user revocation, logoff/session change, originating client process replacement, target/policy change, or broker revocation occurs before acceptance. | `ApprovalExpired` or `ApprovalRevoked` atomically invalidates the approval. | No handler dispatch. A reconnect or new client cannot inherit approval; new proposal and confirmation required. |
| `Approved → Accepted` | Fresh request is authenticated as same SID/LUID/session and process instance; approval current; target/policy/preconditions fresh; no duplicate/conflict; approval still unconsumed. | One transaction writes `ExecutionAccepted`, consumes approval, records execution ID/idempotency key and target fence. | Client retries query same execution ID. No second accepted execution. If commit outcome uncertain, query ledger; do not dispatch. |
| `Accepted → Started` | Sole broker dispatcher confirms valid durable `Accepted`, fresh target/policy/preconditions, any operation-required active session, and no cancellation. Client process liveness is not required after acceptance. | `ExecutionStarted` with operation contract/version, target identity, precondition evidence, dispatcher epoch; commit and confirm before the single dispatch call. | **Invariant:** no handler/callback/queue/recovery path can dispatch before this commit. Crash after `Accepted` with valid ledger and no `Started` proves no dispatch under this invariant: close as `Cancelled` with reason `RestartBeforeDispatch`, keep approval consumed, and require a fresh proposal/approval. If ledger integrity/durability is uncertain, `RecoveryRequired`. |
| `Accepted → Cancelled` | Authenticated cancellation, pre-start policy/target invalidation, or required owner-session end occurs before start and dispatcher proves no handler was dispatched. Client-process termination alone is not cancellation. | `ExecutionCancelledBeforeStart` with bounded reason. | Terminal; no automatic retry with consumed approval. New proposal and confirmation required. |
| `Started → Completed` | Handler returned a bounded completion result; this is not proof of target state. | `HandlerCompleted` with handler result and operation checkpoint. | If record is missing after restart, outcome is uncertain; `Interrupted` then `RecoveryRequired`. Never rerun handler. |
| `Started → Failed` | Handler failed and operation contract proves no effect occurred. | `HandlerFailedNoEffect` with evidence. | If effect cannot be ruled out, this transition is forbidden; use `RecoveryRequired`. Retry requires a new proposal and approval. |
| `Started → Cancelled` | Operation contract proves safe cancellation and exact effect state. | `CancellationVerified` with evidence. | If any side effect may have occurred or status is unknown, `RecoveryRequired`. Cancellation is never inferred from client disconnect. |
| `Started → Interrupted` | Startup/recovery observes durable start with no terminal handler outcome. | `ExecutionInterrupted` with startup epoch and last durable checkpoint. | No handler replay. Immediately follow with `Interrupted → RecoveryRequired` and fence conflicts; all reconciliation occurs from `RecoveryRequired`, never by skipping a lifecycle state. |
| `Completed → Verified` | Verifier meets the operation-specific independence contract and confirms postconditions. | `PostconditionVerified` with source, freshness, target identity, result and evidence digest. | Client may query same record; no execution repeat. If evidence later becomes contradictory, add incident/recovery event; do not erase verified history. |
| `Completed → Failed` | Independent verification establishes required postcondition failed and the outcome is known. | `PostconditionFailed` with evidence and effect classification. | If state/effect is uncertain or partial, use `RecoveryRequired`, not `Failed`. |
| `Completed/Interrupted/Failed/Cancelled → RecoveryRequired` | Outcome partial, ambiguous, contradictory, unverifiable, or rollback/recovery failed. | `RecoveryRequired` plus durable resource fence and reason. | Remains fenced across restart. No automatic replay, rollback, fence expiry, or success-shaped fallback. |
| `RecoveryRequired → Reconciled` | Authorized recovery reviewer/flow selects an operation-specific recovery disposition; read-only evidence or separately approved compensating procedure establishes the disposition and satisfies that operation's fence-release conditions. | `RecoveryDisposition`, recovery-step events, and independent `RecoveryVerified` evidence committed; disposition and fence release occur in the same final transaction. | Any interruption returns to `RecoveryRequired`; no automatic retry. If evidence is insufficient, state/effect is uncertain, or attribution is material but unavailable, remain fenced. Reconciliation records what is established; it does not claim the interrupted handler caused an observed target state. |
| `RecoveryRequired → RecoveryRequired` | Reconciliation is inconclusive, unavailable, or fails. | Append bounded `RecoveryAttempt`/failure event; retain fence. | Retry only a read-only verifier or separately approved recovery action; never original handler. |

`Verified` means only that the declared postcondition evidence passed at the recorded time after a normal `Completed` outcome. `Reconciled` records a separately authorized disposition after recovery; a disposition such as “desired state observed” does not attribute that state to the interrupted request unless the evidence supports attribution. Neither state means audit evidence is tamper-proof, that the operation was harmless, or that future system state remains unchanged.

### Cancellation and state-changing recovery

- Client disconnection is not cancellation.
- A cancellation request is an event against a specific execution; after `Started`, the operation contract decides whether safe cancellation is possible.
- Rollback/recovery is a separate authorized operation with its own preview, approval if required, durable `RecoveryRequired` fence, execution steps, verification, and failure path.
- No generic automatic rollback or automatic handler replay is allowed.
- An unknown result never maps to success, ordinary failure, or safe retry.

## 7. Verifier independence

Executor return status is **handler-reported**, not independently verified.

| Assurance class | Definition | What it can support | Limit |
|---|---|---|---|
| `HandlerReported` | Handler reports success/failure only. | Progress/error display. | Never enough to claim verified state. |
| `SameComponentReadback` | Same handler/component performs a separate read after its write. | Detects some immediate failures and stale writes. | Shared implementation/API/cache/compromise can create correlated false results. |
| `SeparateVerifier` | Distinct verifier module with separate logic re-reads target through a documented API. | Stronger logical separation; can validate postcondition against current state. | Same process/service identity, OS API stack, and host compromise remain shared. |
| `IndependentEvidenceSource` | A separately justified source or observation path confirms relevant postconditions, with provenance/freshness and contradiction handling. | Highest practical confidence for the specific contract. | Sources may still share OS/provider/kernel trust; process separation alone is not independence. |

Each operation contract must select the required class and document shared dependencies. Verification reads the current target by stable identity, not handler memory or intended values. Missing, stale, inaccessible, contradictory, or non-attributable evidence is `Unknown`/`RecoveryRequired`, not success. Tests must show a successful handler return alone cannot produce `Verified`.

No universal process isolation is required by this design; independence is evidence- and operation-specific. If the impact requires assurance unavailable from any justified source, the operation remains prohibited.

## 8. Strict operation and rollback contract

Every future state-changing operation must define all fields below before allowlisting:

1. **Identity/scope:** immutable operation ID/version, caller scope, required token rights, service need (if any), cross-user effects, and hard-deny cases.
2. **Preconditions:** source, freshness, acceptable range, dependencies, policy/catalog version, and abort conditions.
3. **Target identity:** stable OS identity/version, resolver and alias/replacement/reparse handling, ownership/scope checks, and exact revalidation points.
4. **Expected effects:** canonical typed parameter schema, expected state delta, bounds, side effects, timeouts, idempotency, and effects that cannot be attributed.
5. **Reversibility class:** `Reversible` (validated prior state can be restored), `Compensable` (a distinct compensating action does not claim exact restoration), `Irreversible`, or `Unknown`.
6. **Rollback eligibility:** captured pre-state, time window, concurrent-change rules, dependencies, data loss risk, and explicit deny cases.
7. **Rollback procedure:** exact typed compensating steps, separate authorization, checkpoints, cancellation points, and side-effect limits. Generic inverse operations are prohibited.
8. **Rollback verification:** independent evidence of prior-state restoration; partial/unknown results remain unresolved. An attempted rollback is not a successful rollback.
9. **Partial failure/recovery:** per-step state mapping, read-only reconciliation, target fence, escalation, retry prohibition, and terminal evidence.
10. **Verification:** assurance class from Section 7, data source, freshness, shared dependencies, and contradiction handling.

**Policy consequence:** An `Irreversible` or `Unknown` state-changing operation, or one with no safe recovery disposition, is prohibited under the current V2 design. A `Compensable` action does not count as rolled back unless its contract says what was restored and independent evidence verifies it. Any exception requires a separate explicit architectural/security decision; no such exception is granted here.

## 9. Implementation phases and gates

1. **Phase 0 — decision and scope:** resolve ADR 0011/0012 status; identify first use case and rights; settle consent threat boundary. Until then, no runtime host.
2. **Phase 1 — hostless contracts/model:** typed DTOs, canonicalization vectors, state machine, fake transactional store, model/property/crash-injection tests. No Windows API calls or real handler interfaces.
3. **Phase 2 — host decision:** only after Phase 1 and a concrete use case, compare per-user host/service/helper/RPC alternatives. No service shell by default.
4. **Phase 3 — candidate transport and identity:** if approved, implement a non-executing test endpoint with empty action catalog, only after supported OS, identity tuple, ACL and connection semantics are independently reviewed.
5. **Phase 4 — authorization ledger:** decide rollback witness/key model and test crash/restore behavior. No effectful handler until authorization rollback assumptions are accepted.
6. **Phase 5 — consent prototype:** validate presentation/input and same-user malware boundary. Failure to establish accepted consent means no operation requiring consent is allowed.
7. **Phase 6 — per-operation proposal:** separate approval and security review, target/privilege mapping, verification, recovery, and isolated tests. No blanket catalog approval.

All phases preserve V1 without migration or V1 approval transfer. ADR 0012 stays Proposed; this remediation does not accept a replacement architecture.

## 10. Finding traceability matrix

Statuses describe the **design response only**, not implementation proof.

| Finding | Original severity | Existing design deficiency | Proposed correction | Affected documents | Evidence of design response | Acceptance criteria | Remaining risk | Status |
|---|---|---|---|---|---|---|---|---|
| H-01 | High | Consent surface was only a candidate; same-user spoofing unresolved. | Explicitly distinguish identity, process, display, input, and authorization; prohibit consequential operations until consent boundary accepted. | This doc §§2, 9; [security](./ACTION_BROKER_V2_SECURITY.md); [IPC](./ACTION_BROKER_V2_IPC.md); [plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md). | A/B/C consent comparison documents exact limits; confirmation is named session-user confirmation, not human-intent proof. | Independent review of a concrete consent boundary; test substitution/replay/spoof/session cases; no false human-intent claim. | Same-user malware can influence ordinary session UI; no operation eligible if accepted risk is insufficient. | **Partially resolved** |
| H-02 | High | No first operation/privilege matrix justified a machine-wide service. | No host in initial V2 phase; service only after demonstrated operation need. | This doc §3; [architecture](./ACTION_BROKER_V2_ARCHITECTURE.md); [plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md); [ADR 0012](./docs/decisions/0012-separate-action-broker-service-and-named-pipe.md). | Matrix identifies no selected V2 operation requiring machine-wide rights and defers host choice. | Operation-to-rights matrix and decision explaining alternative selection. | ADR 0011 currently requires a service for privileged operations; any no-service executable approach needs a new ADR. | **Partially resolved** |
| H-03 | High | PID/token/session binding and multi-message identity lifetime ambiguous. | One bounded request per connection, exact token capture, process identity supplementary, explicit revocation/revalidation. | This doc §4; [IPC](./ACTION_BROKER_V2_IPC.md). | Connection/process/caller/authorization tuples and capture/recheck rules are specified; failures reject/retire/fail-stop. | Supported Windows tests prove token/connection binding and failure closure for PID reuse, exit, reconnect, session mismatch, impersonation/revert failure. | Process identity is not UI integrity or human consent; design not implemented. | **Resolved by design** |
| H-04 | High | Local ledger/checkpoint rollback and key compromise could resurrect consumed approval. | Tiered threat assessment; local protection limits; independent witness/TPM are candidates, not assumed; prohibit reliance on local-only rollback evidence. | This doc §5; [security](./ACTION_BROKER_V2_SECURITY.md); [recovery](./ACTION_BROKER_V2_RECOVERY.md). | Distinguishes accidental restore, ordinary user, admin, broker, and key compromise; requires fail-closed on mismatch. | Select independently controlled anchor or explicitly accept narrow threat model; restore/rollback tests; fail closed on mismatch. | No strong anti-rollback guarantee against admin/broker compromise without independent anchor. | **Partially resolved** |
| M-01 | Medium | State table, diagrams and crash matrix contradicted. | Single canonical transition table and dispatcher invariant. | This doc §6; [recovery](./ACTION_BROKER_V2_RECOVERY.md). | All transitions have guards, actor, durable event, crash/retry/recovery semantics; diagrams defer to table. | Model/property tests prove only legal transitions and no dispatch before durable `Started`. | Storage/hardware may violate durability assumptions; must be measured and stated. | **Resolved by design** |
| M-02 | Medium | Independent verifier criteria undefined. | Four assurance classes; per-operation source/dependency/freshness criteria. | This doc §7; [recovery](./ACTION_BROKER_V2_RECOVERY.md). | Separates handler-reported, same-component readback, separate verifier, and independent source evidence. | Each operation chooses class and test rejects handler-only success. | Shared OS/provider/service compromise can correlate failures. | **Resolved by design** |
| M-03 | Medium | Pipe ACL/session population/ownership not selected. | Defer transport until host decision; specify prerequisites and fail-closed requirements. | This doc §§3–4; [IPC](./ACTION_BROKER_V2_IPC.md); [plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md). | IPC is explicitly unselected; one-request semantics and endpoint review/test gates are specified. | Exact supported OS, DACL, endpoint and multi-session tests before endpoint implementation. | No endpoint design is selected; service candidate remains unverified. | **Partially resolved** |
| M-04 | Medium | ADR 0011 rollback requirement conflicted with optional rollback wording. | No safe rollback/recovery means deny; compensable is not exact rollback. | This doc §8; [recovery](./ACTION_BROKER_V2_RECOVERY.md). | Reversibility classes and explicit prohibition for Irreversible/Unknown/no-safe-disposition operations are specified. | Tests deny operation without explicit approved recovery disposition. | Some effects remain intrinsically irreversible/uncertain and are prohibited. | **Resolved by design** |
| M-05 | Medium | Some documents treated service as settled while ADR 0012 was Proposed; ADR 0011 also requires it. | Reconcile current wording and retain ADR 0011 conflict as a governance gate. | [architecture](./ACTION_BROKER_V2_ARCHITECTURE.md); [ADR 0012](./docs/decisions/0012-separate-action-broker-service-and-named-pipe.md); [ARCHITECTURE.md](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md); [SECURITY.md](./WinForge_Engineering_Specifications/WinForge/SECURITY.md); [ACTION_BROKER.md](./ACTION_BROKER.md); [ACTION_SECURITY.md](./ACTION_SECURITY.md). | Documents now distinguish no host selected, proposed service, and ADR 0011's accepted constraint. | One accepted decision identifies host for any executable scope; no implementation starts while ADRs conflict. | Historical ADR 0011 remains as written; resolving conflict requires a new ADR, not edits to history. | **Partially resolved** |
| L-01 | Low | Proposed frame/time limits lack workload rationale. | Keep every number provisional; require DTO-derived sizing and hostile-load validation. | [IPC](./ACTION_BROKER_V2_IPC.md); this doc §9. | Limits are labeled provisional and protocol freeze is gated on valid/hostile-load evidence. | Valid/over-limit, slow-client, and aggregate-pressure tests justify frozen limits. | Workload changes may require versioned limit review. | **Resolved by design** |

## 11. Remaining risks and second-review questions

- Does the accepted product threat model require resistance to same-user malware for any intended action? If yes, what OS-supported consent path demonstrably supplies it?
- Which first V2 operation is genuinely needed, and what exact rights does it require? Until answered, service necessity is unproven.
- Will ADR 0011 be narrowed or retained as a strict prerequisite for any privileged V2 work? No code may resolve this implicitly.
- Is a remote independent witness acceptable for anti-rollback, including privacy/availability/retention tradeoffs? If not, which operations can safely operate without rollback-resistant approval state?
- Which OS builds and token/pipe APIs will be supported if IPC is selected?
- What operational escalation is available when a target remains permanently `RecoveryRequired`?
- Which verifier sources are genuinely independent enough for each future action? Some actions may have no adequate verifier and must remain prohibited.

## 12. Files and validation

This document is the detailed remediation source. Companion V2 documents, the project threat model, and directly conflicting normative summaries are updated to point to these decisions. ADRs 0001–0011 remain unchanged; ADR 0012 remains Proposed.

Documentation validation should verify local Markdown links and headings/traceability. No build or test run is appropriate for this design-only milestone. Documentation does not demonstrate that any control works.
