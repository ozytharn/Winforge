# WinForge Action Broker V2 — Independent Architecture Review

**Review date:** 2026-11-03  
**Scope:** Design review only. No source, database, migration, service, IPC, elevation, or execution behavior was changed.  
**Overall finding:** The V2 documents contain strong safety principles, but they do not yet justify accepting the specific service/identity/IPC architecture. The central human-consent and least-privilege questions remain open, and several protocol/state details need to be made unambiguous before implementation.

## 1. Executive verdict

**KEEP ADR 0012 PROPOSED.**

Do not accept ADR 0012 as the settled architecture yet. The proposal is appropriately conservative about unknown outcomes, prohibits automatic handler replay, separates V1 simulation from V2, and does not claim that a local hash chain is tamper-proof. Those are meaningful strengths, but they are not enough to establish that a machine-wide service is necessary or that the proposed approval channel can distinguish informed user consent from a same-user process acting without the user's intent.

No Critical finding is assigned because the V2 architecture and controls are not implemented and the documents prohibit real operations pending review. That is not a security approval. The **High** findings below are blockers to accepting the architecture as sufficient for consequential operations.

The accepted [ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) already requires a separate service as the future privileged boundary. ADR 0012 cannot resolve that decision merely by repeating it with a named pipe and service identity. If the project concludes that a service is not needed for the first V2 scope, a new decision must explicitly supersede or narrow ADR 0011 while preserving its historical record.

## 2. Scope and evidence reviewed

Reviewed all five V2 documents:

- [V2 architecture](./ACTION_BROKER_V2_ARCHITECTURE.md)
- [V2 security and threat model](./ACTION_BROKER_V2_SECURITY.md)
- [V2 IPC and identity protocol](./ACTION_BROKER_V2_IPC.md)
- [V2 durable execution and recovery](./ACTION_BROKER_V2_RECOVERY.md)
- [V2 implementation plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md)

Reviewed [ADR 0001](./docs/decisions/0001-initial-passport-slice.md) through [ADR 0012](./docs/decisions/0012-separate-action-broker-service-and-named-pipe.md), plus the current [Action Broker design](./ACTION_BROKER.md), [Action Security design](./ACTION_SECURITY.md), [Action Catalog](./ACTION_CATALOG.md), [Action Testing plan](./ACTION_TESTING.md), [Action Broker implementation plan](./ACTION_BROKER_IMPLEMENTATION_PLAN.md), [architecture](./WinForge_Project_Documents/WinForge/ARCHITECTURE.md), [security standard](./WinForge_Engineering_Specifications/WinForge/SECURITY.md), [recovery specification](./WinForge_Engineering_Specifications/WinForge/RECOVERY.md), [threat model](./WinForge_Additional_Documents/WinForge/THREAT_MODEL.md), and [roadmap](./WinForge_Project_Documents/WinForge/PLAN.md).

Microsoft platform references cited in the V2 IPC/architecture documents were treated as descriptions of platform APIs, not evidence that WinForge's proposed ACL, token binding, consent, or recovery protocol has been implemented or validated.

## 3. Architecture strengths

- V1 is explicitly retained as an in-process synthetic simulator, with no transfer of approvals or automatic fallback to V2.
- The WPF client is treated as untrusted; broker-owned policy, catalog, target resolution, and approval state are the intended authority.
- Requests are intended to bind caller/session, action and catalog version, target, normalized parameters, policy, preview, expiry, and one-time approval state.
- The proposal rejects unknown actions, generic command execution, silent approval, default `LocalSystem`, and automatic retry of incomplete operations.
- The recovery design correctly distinguishes handler completion from independent verification and states that missing completion evidence is not evidence that no side effect occurred.
- It specifies operation-specific verification and rollback, and does not assume a generic inverse operation is safe.
- Journal integrity limitations are stated: local hash chains do not defeat a compromised service/key or coordinated rollback without an external anchor.
- The implementation plan begins with a no-host/no-Windows-effects harness and gates any real operation behind separate reviews.

These are good constraints, but largely remain requirements and design intentions.

## 4. Architecture weaknesses

- There is no selected first V2 operation or privilege matrix from which to prove that a machine-wide service is needed. The action catalog is explicitly empty for V2 real operations.
- The service’s authority and the caller’s authority are not yet concretely mapped to any operation. “Dedicated virtual service account” and “restricted service SID” are labels, not a demonstrated capability boundary.
- A broker-issued challenge and a preview retrieved from the broker can bind data, but do not by themselves establish that the human saw that data or intentionally approved it.
- The identity handshake mixes a process identifier with an impersonated pipe-message token without a complete rule for binding those identities and retaining/rechecking that binding over multiple requests.
- The lifecycle and recovery documents disagree on some legal transitions and on what can be known after `Accepted` but before `Started`.
- The journal’s anti-rollback anchor, key compromise response, and durable authorization semantics remain undecided.
- Existing normative documents do not all preserve the proposed status: [ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) already mandates a separate service; portions of [ACTION_BROKER.md](./ACTION_BROKER.md), [ACTION_SECURITY.md](./ACTION_SECURITY.md), and [SECURITY.md](./WinForge_Engineering_Specifications/WinForge/SECURITY.md) state the service boundary as a standing requirement, while ADR 0012 remains proposed.

## 5. Findings

### Critical

**No Critical findings assigned.** The V2 controls are not implemented, V1 remains simulation-only, and the documents prohibit real operations until gates pass. This does not imply the architecture is ready to accept.

### High

#### H-01 — Human consent is not authenticated by the proposed protocol

- **Evidence:** [V2 security](./ACTION_BROKER_V2_SECURITY.md) says a compromised same-session process may automate or spoof the consent UI and calls consent unresolved. [V2 IPC](./ACTION_BROKER_V2_IPC.md) proposes a “broker-controlled consent surface” that fetches the preview, but does not select or establish a trusted presentation/input mechanism. The service runs in Session 0 while the UI must run in an interactive session.
- **Risk and impact:** OS authentication establishes a security principal and connection context, not human intent. A valid SID, signed client, one-time challenge, or exact preview hash cannot prove the user saw the genuine preview. Same-user malware may call `CommitExecution`, automate the approval surface, display a look-alike, or interfere with the real surface. An approval can therefore be cryptographically request-bound yet not meaningfully user-authorized.
- **Recommended correction:** Define the consent threat boundary before accepting any operation. Specify how the service starts/locates the session UI, verifies its identity and channel, binds the exact broker-rendered preview to the affirmative input, prevents request/challenge substitution, and handles client disconnect, logoff, session switch, and process replacement. State explicitly what cannot be proven against same-user malware. If the accepted platform cannot provide adequate intent assurance, prohibit consequential operations rather than claiming authenticated human approval.
- **Acceptance criteria:** Independent security review approves the concrete consent design and threat boundary; tests show altered previews, challenge substitution/replay, same-user unauthorized commit, overlays/spoofing, cross-session use, and disconnect/reconnect fail closed. The design distinguishes “authenticated caller,” “approval UI event,” “broker-accepted approval,” and “human intent,” and makes no unsupported claim that the last is provable.

#### H-02 — The service architecture is not justified by the first V2 use case

- **Evidence:** [V2 architecture](./ACTION_BROKER_V2_ARCHITECTURE.md) calls the service the selected direction and identifies machine-scoped execution as its use case, while [V2 implementation plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md) says the executable catalog is empty and leaves the operation set for future review. The design itself lists service identity, SCM start permissions, session policy, consent, storage, and installer/update servicing as unresolved. [ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) already requires a separate service, but does not provide an operation/privilege analysis that proves the requirement.
- **Risk and impact:** A machine-wide service creates a cross-user endpoint and durable high-value authority before the project has shown that the first supported task needs it. It adds installation, servicing, SCM permissions, service compromise, shared-state isolation, multi-session consent, and recovery complexity. It can become an attractive confused-deputy target while offering no necessary privilege separation for user-scoped operations.
- **Recommended correction:** Before accepting ADR 0012, name and scope the first V2 operations (or explicitly state that V2 initially has no executable operations), then compare the required rights and failure model for each against a service-free contracts/test-harness phase and a per-user unelevated broker. Reserve a service for operations whose reviewed privilege or machine-wide coordination need cannot be met more safely otherwise. Do not use the empty service shell as evidence that the final service architecture is necessary.
- **Acceptance criteria:** A documented operation-to-capability matrix identifies the minimum required token rights, user/machine scope, cross-session need, target ownership, and recovery requirement. An independent reviewer confirms why each cannot be satisfied by the least-privileged alternative. The decision record reconciles this conclusion with ADR 0011 without rewriting ADRs 0001–0011.

#### H-03 — OS identity is not yet bound unambiguously to the connection and request

- **Evidence:** [V2 IPC](./ACTION_BROKER_V2_IPC.md) says to read a frame, query the pipe client PID, inspect process creation time, then impersonate the pipe client. The documented impersonation semantics are tied to the last message read. The design also says to revert before parsing/authorizing, and describes identity as connection-bound, but does not specify whether every request is independently authenticated or how a connection is invalidated if the token context changes.
- **Risk and impact:** A PID plus creation time identifies a process instance; it does not prove that the impersonated message token belongs to that process or that the token context remains the same for later requests. A client can disconnect/reconnect, change thread impersonation context, or issue a later message under a different token context. If the service retains an earlier identity snapshot while processing later requests, authorization can be applied to stale or mismatched identity. Failure to impersonate or revert must never fall back to service authority.
- **Recommended correction:** Define an authentication frame and exact binding rules: capture the OS token associated with that frame; bind SID, logon AuthenticationId/LUID, session, pipe instance, process handle/creation time, and connection epoch; decide whether every subsequent message is re-authenticated or the connection is closed after one request. Reject identity-context changes and revalidate process liveness immediately before authorization/dispatch. Specify what happens on disconnect, token-query failure, impersonation failure, and `RevertToSelf` failure; retire the worker or fail-stop the service so no later work can run under the service token.
- **Acceptance criteria:** Supported-Windows integration tests prove the authorized token is the token associated with the exact request, not merely a claimed PID; cover PID reuse, client exit, reconnect, per-thread impersonation changes, token/session mismatch, cross-session attempts, and impersonation/revert failures. No handler dispatch occurs if any binding is missing, stale, or inconsistent.

#### H-04 — Authorization-state rollback and audit anchoring are unresolved

- **Evidence:** [V2 security](./ACTION_BROKER_V2_SECURITY.md) and [V2 recovery](./ACTION_BROKER_V2_RECOVERY.md) require durable one-time approval consumption and anti-rollback controls, but leave the ledger technology and external anchor open. The security design states that restoring an older valid journal can evade detection without an independent anchor and that a compromised service identity can rewrite the journal/key. [V2 implementation plan](./ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md) defers the durable ledger and anchor design to a later phase.
- **Risk and impact:** A valid old database/backup can potentially restore an unconsumed approval or remove a later `Accepted`/`Started` record. A local HMAC/hash chain does not prevent this if the state and key are controlled by the same service boundary. This threatens replay prevention and authorization correctness, not merely the quality of audit reports. An external Windows event channel is not automatically independent if the same administrator/service can clear or rewrite its evidence.
- **Recommended correction:** Separate the authorization/idempotency ledger from the human-readable audit export. Specify the rollback adversary and the anti-rollback trust anchor before any non-idempotent operation: anchor ownership, monotonic sequence, freshness, outage behavior, restore/rekey ceremony, key compromise response, and what happens when the anchor is unavailable. If an independent anchor is rejected for local-first/privacy reasons, state that rollback resistance is not provided and prohibit operations whose safety depends on it.
- **Acceptance criteria:** A reviewed threat model states precisely which local-user, administrator, broker-compromise, and key-compromise attackers are covered. Restore/rollback tests cannot resurrect consumed approvals or erase accepted executions within the accepted threat model. Anchor loss, stale anchor, key loss, and unavailable anchor all fail closed; no local hash chain is described as tamper-proof.

### Medium

#### M-01 — Lifecycle transition definitions are internally inconsistent

- **Evidence:** [V2 recovery](./ACTION_BROKER_V2_RECOVERY.md) permits `Accepted` to `Started`, `Cancelled`, `Failed`, or `RecoveryRequired`, while the crash diagram also uses `Accepted → Interrupted`. The diagram permits `Completed → Interrupted`, but the state table does not. The `Verified` row calls the state terminal and then permits `RecoveryRequired`. The crash table says `Accepted` before `Started` should be interrupted if dispatch status is unknown, even though the durable protocol says the handler must never be dispatched unless `Started` was committed.
- **Risk and impact:** Implementers may make different decisions about whether work could have been dispatched, whether a state is terminal, and which transitions require a journal event. A false `Cancelled`/`Failed` could imply no effect; an unnecessary `RecoveryRequired` can block work indefinitely. Ambiguous transitions also weaken uniqueness and replay invariants.
- **Recommended correction:** Publish one transition table as the authority, with event/state distinction and exact dispatch invariant. If dispatch is structurally impossible before durable `Started`, encode that as a tested invariant and define recovery for `Accepted` without `Started`. If that invariant cannot be guaranteed, use an explicit ambiguous state. Add explicit durable rollback/recovery substates or events for rollback-started, rollback-completed, and rollback-verified.
- **Acceptance criteria:** Every transition in every diagram and fault table appears in the normative state model with guard, durable write, allowed actor, and recovery behavior. Model/property tests cover crash points before/after each commit and handler dispatch; impossible states are rejected.

#### M-02 — “Independent verification” has no operational independence criterion

- **Evidence:** [V2 recovery](./ACTION_BROKER_V2_RECOVERY.md) requires independent postcondition checks but does not define whether verifier code, process, API/source, privileges, or evidence must be independent from the handler. The handler and verifier remain inside the same proposed service architecture.
- **Risk and impact:** A handler returning success and a verifier reading the same cached/in-memory result or relying on the same faulty API can produce correlated false success. A compromised handler may also influence evidence presented as independent.
- **Recommended correction:** Define independence per operation: what observation must be re-read, whether it must use a separate code path/source, how freshness and provenance are recorded, and how contradictory or inaccessible evidence is classified. Do not imply that process separation is universally required, but explain the selected assurance and its residual risk for each operation.
- **Acceptance criteria:** Each future operation contract identifies verifier source, trust relationship to the executor, evidence freshness, and failure/contradiction behavior. Tests demonstrate verification does not pass solely because the handler returned success.

#### M-03 — Pipe ACL and multi-session policy are not specified enough to support the security claims

- **Evidence:** [V2 IPC](./ACTION_BROKER_V2_IPC.md) requires an explicit DACL and remote-client rejection, while [V2 architecture](./ACTION_BROKER_V2_ARCHITECTURE.md) intends a machine-wide service usable by multiple users and sessions. No concrete access principal, logon-SID/session ACL policy, endpoint ownership/startup race handling, or supported-build matrix is selected.
- **Risk and impact:** A local-only pipe is not inherently restricted to the intended user/session. A permissive DACL can expose the service to other local principals; an overly narrow session ACL can block legitimate multi-session use. Pipe squatting or server endpoint confusion can misdirect client secrets/approval inputs. Client-side publisher checks alone do not authenticate the pipe endpoint.
- **Recommended correction:** Define the caller population and session model first, then document endpoint creation/ownership, DACL principals, remote rejection flags, first-instance/name collision handling, client/server authentication, and behavior on endpoint mismatch. Validate the design on the exact supported Windows versions.
- **Acceptance criteria:** ACL review and integration tests cover allowed/denied users, different logon sessions, Fast User Switching/RDP policy, remote clients, pipe squatting, service restart, and client validation of the actual server endpoint. No request or approval data is sent before server validation succeeds.

#### M-04 — Rollback requirements conflict across accepted and proposed documents

- **Evidence:** [ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) says state-changing actions require explicit rollback contracts and that actions without defined/verified recovery remain prohibited. [V2 recovery](./ACTION_BROKER_V2_RECOVERY.md) says rollback is optional and may be ineligible, without explicitly stating whether such an action is then prohibited or whether an approved non-rollbackable category is possible.
- **Risk and impact:** A future implementer may interpret “optional” as permission to execute an irreversible action with no compensating path, contrary to the accepted constraint. Conversely, requiring rollback when impossible may encourage unsafe or misleading “rollback” behavior.
- **Recommended correction:** Preserve the requirement for an explicit operation-specific recovery contract. The contract may state “rollback is ineligible,” but the policy consequence must be explicit: deny the operation unless a separately approved policy and recovery strategy allow it. Never call a compensating action rollback without verified restoration.
- **Acceptance criteria:** ADR 0011 and V2 contracts have a consistent normative rule; tests reject a state-changing operation that lacks an approved recovery disposition. Any permitted non-rollbackable operation requires a separate explicit decision and recovery-risk acceptance.

#### M-05 — Normative documentation prematurely states the service design as settled

- **Evidence:** [ADR 0012](./docs/decisions/0012-separate-action-broker-service-and-named-pipe.md) is Proposed, but [ADR 0011](./docs/decisions/0011-action-broker-authorization-and-lifecycle.md) already accepts a separate service as the only future privileged boundary. [ACTION_BROKER.md](./ACTION_BROKER.md) section 2 and [ACTION_SECURITY.md](./ACTION_SECURITY.md) also phrase the service as a requirement, while the V2 documents call the specific choice a proposal.
- **Risk and impact:** Teams may treat the service and named pipe as approved and begin implementing them despite unresolved consent, identity, and service-necessity questions. Treating ADR 0011 as binding also makes the V2 comparison of alternatives non-decisive.
- **Recommended correction:** Keep ADR 0012 proposed. Before implementation, either affirm ADR 0011’s service requirement with a documented operation/privilege rationale and update only conflicting normative phrasing, or create a new ADR that explicitly supersedes/narrows it. Do not edit historical ADRs 0001–0011.
- **Acceptance criteria:** Every normative architecture document distinguishes accepted constraints from proposed V2 decisions. The project has one explicit, current decision for the first V2 host and IPC model; no implementation begins based on ambiguous prose.

### Low

#### L-01 — Initial IPC numeric limits are examples, not justified operating limits

- **Evidence:** [V2 IPC](./ACTION_BROKER_V2_IPC.md) lists 64 KiB frames, nesting/member/string caps, and a five-second read deadline, while explicitly stating the values are initial design limits to validate.
- **Risk and impact:** Limits that are too generous permit resource exhaustion; limits that are too restrictive can reject legitimate previews or recovery details. This is a protocol implementation concern rather than a reason by itself to reject the architecture.
- **Recommended correction:** Keep the numbers explicitly provisional and derive final values from bounded DTOs, realistic maximum operation payloads, and hostile-client load tests before protocol freeze.
- **Acceptance criteria:** Each accepted limit has a documented rationale and boundary tests, including maximum valid, one-over-limit, slow-client, and aggregate queue pressure cases.

## 6. Identity and consent analysis

| Property | What the design can technically establish, if correctly implemented | What it cannot establish by itself |
|---|---|---|
| Authenticated Windows user | The security token associated with a pipe message can identify an OS principal and logon context. | That the user personally initiated the request or approved it. |
| Client process | A connection-associated PID and a process handle/start time can identify a process instance for diagnostics and some policy checks. | That the process is uncompromised, that its UI is genuine, or that its message token necessarily remains bound to that process without explicit protocol rules. |
| Preview integrity | A broker-generated canonical request and digest can bind action/target/parameters/policy to a proposal. | That the user saw the exact preview or understood the consequences. |
| Explicit approval event | A trusted, broker-validated input path can record a decision for an exact challenge and proposal. | The user's subjective intent, freedom from coercion, or comprehension; nor does a normal same-session UI necessarily resist same-user malware. |
| Broker acceptance | Durable broker state can enforce expiry, one-time use, owner/session constraints, and no automatic execution after proposal/approval. | That an approval whose integrity is valid was knowingly made by the human, unless the consent trust boundary is separately justified. |

Approval binding is useful and necessary, but it does not eliminate the trust problem in the user-facing presentation/input path. A process signature is not a human-consent proof. A challenge is not a trusted display. A one-time approval does not prevent same-session malware from consuming a still-valid approval unless the protocol intentionally binds the approval to an unforgeable second user-presence event or an accepted weaker threat model.

## 7. IPC analysis

- **Client SID/logon/session:** Token-derived identity is the correct starting point. The protocol must bind it to each authorized request and define whether one pipe handles one request or many. Do not reuse a connection identity snapshot across requests if the per-message token context can differ.
- **PID and creation time:** Useful for identifying a process instance and reducing PID-reuse confusion, but not a substitute for OS token identity. Confirm that the process handle, token, and pipe connection refer to the same authenticated request context.
- **Cross-session:** “Local-only” excludes remote transport; it does not define local-user/session authorization. Specify whether the service accepts all local sessions, only interactive users, or one logon session and how disconnected/RDP sessions are treated.
- **ACLs:** An explicit DACL is mandatory. Define concrete principals and rights; do not rely on defaults or pipe-name secrecy. Validate server endpoint ownership and name-collision/pipe-squatting behavior before sending sensitive messages.
- **Impersonation:** Check every call. On failure, reject the request; on revert failure, retire the worker and prevent further dispatch under service identity. Keep impersonation scope synchronous and minimal. The safest baseline is to close after each request unless a reviewed multi-message identity protocol is required.
- **Staleness/disconnection:** Revalidate the caller and session at approval/commit boundaries. Client disconnect does not cancel accepted work, but the service must not allow a different same-user process to silently complete an approval without the approval policy explicitly allowing it.

## 8. Recovery and audit analysis

### Recovery

The write-ahead `Started` rule is sound only if it is an enforceable dispatch barrier: no handler path, callback, retry queue, or recovery worker can reach a side effect before the durable `Started` commit. A crash after `Started` but before the effect must still be treated conservatively; a crash after the external effect but before `Completed` is inherently ambiguous. A missing completion record is never proof of no effect. Never automatically replay a state-changing handler.

The `Accepted`/`Started` window should be deterministic if dispatch is impossible before `Started`; the current documents both suggest that guarantee and allow uncertainty about whether dispatch occurred. Resolve that contradiction. `Completed` without `Verified` may safely rerun only a read-only verifier, if the verifier contract proves that doing so is safe. Recovery or rollback itself needs durable transitions, cancellation semantics, target fencing, and verified terminal evidence.

### Audit integrity

- **Ordinary local user:** Correctly protected service-owned ACLs can prevent direct file edits, but this depends on exact directory, database, WAL/SHM, backup, and key ACLs.
- **Administrator:** Out of the stated security boundary, an administrator can generally change ACLs, replace service binaries, manipulate local evidence, or disable controls. Do not claim resistance.
- **Compromised broker:** A broker with access to the ledger and integrity key can rewrite records and recompute a local chain. A local HMAC is not independent from the service that uses it.
- **Signing-key access:** Non-exportability reduces extraction risk but does not stop an authorized compromised service from asking the key provider to authenticate attacker-selected data. Key custody and usage authorization need explicit design.
- **Journal/checkpoint rollback:** Restoring both journal and local checkpoint may be undetectable. A useful anti-rollback anchor must be outside the same rollback/administrative trust domain or the design must state that this threat is not covered.
- **Rotation/backups/corruption:** Preserve unresolved operation evidence and integrity metadata; restore must not silently become an empty ledger. Define retention, sequence continuity, restore authorization, key rotation, and failure readiness before execution is considered.

## 9. Alternatives comparison

| Approach | Advantages | Principal risks/costs | Review conclusion |
|---|---|---|---|
| **Per-user unelevated broker** | Smallest privilege footprint; naturally scoped to one user/logon session; no SCM/service installation; appropriate for contracts, read-only work, and user-token-only tasks. | Does not create a privilege boundary against same-user malware; per-session processes need coordination for shared resources; cannot perform machine-wide work unavailable to the user. | Best default for a first executable V2 scope if that scope is user-only. The phase-1 no-host harness is safer still until an operation is selected. |
| **Demand-start Windows service + local named pipe** | Central policy/ledger; can coordinate machine-wide conflicts; separates client from service-held authority; no continuously running service if demand-start works as intended. | Service installation/servicing and SCM permissions; shared cross-user state; service compromise inherits service rights; Session 0 consent complexity; multi-session ACL and recovery burden. Demand-start does not remove attack surface while running. | Plausible only if concrete operations need service authority or machine-wide coordination. Current evidence does not establish that need. |
| **Narrow UAC/elevated helper** | Short lifetime; explicit OS elevation interaction; can avoid a persistent service. | UAC approves elevation of a binary, not necessarily the exact broker preview; client-controlled launch and arguments create substitution risk; elevated token can be broad; durable dedupe/recovery across launches is harder. | Not a general replacement. Consider only for a narrowly defined operation with a separate request-binding, consent, and recovery proof. |
| **Local RPC** | Windows RPC has mature local authentication/security facilities and an established interface model. | Interface/endpoint ACLs, authentication level, security QoS, RPC activation/registration, and deployment still need careful review; it does not solve human consent or external-side-effect atomicity. | Must be compared concretely with named pipes before IPC selection; “more mature” does not mean automatically safe. |
| **COM local server / COM-RPC** | OS-supported activation and access-control mechanisms; integration can be appropriate for COM-centric clients. | Registration/activation, proxy/stub/versioning, COM security configuration, and lifecycle complexity; same consent and recovery gaps remain. | Viable only if ecosystem integration justifies complexity; no current evidence favors it. |
| **Contracts and deterministic harness only (no host yet)** | Validates canonicalization, authorization model, and crash-state semantics without creating an endpoint or authority. | Provides no real IPC or user operation. | Required and sufficient for the first implementation phase; do not deploy an empty privileged service merely to validate platform mechanics. |

## 10. Required design changes before ADR acceptance

1. Select the first V2 use case or explicitly state that no operation will be executable in the first V2 release.
2. Provide an operation-to-privilege/scope matrix and demonstrate why a service is needed over a per-user/no-host architecture. Resolve the accepted service requirement in ADR 0011 through an explicit decision, not an undocumented exception.
3. Close the consent architecture at the level of a testable trust boundary. If same-user malware is not mitigated, say exactly what classes of action remain forbidden.
4. Specify token/PID/session/connection binding, per-request identity validation, endpoint ownership, access control, and failure behavior on impersonation/reversion.
5. Normalize the lifecycle state graph and prove the dispatch barrier and recovery transitions, including rollback/reconciliation events.
6. Decide the anti-rollback trust model for the authorization ledger and state the exact attacker classes against which audit evidence is trustworthy.
7. Reconcile rollback policy with ADR 0011: an explicit “no safe rollback” outcome must lead to denial unless separately approved.
8. Correct normative documentation that currently presents the service as settled while ADR 0012 is proposed.

## 11. Unresolved risks

- Same-user malware can potentially automate or spoof ordinary desktop consent; no selected mechanism establishes genuine human intent.
- A compromised service or its administrator can use service-held rights and may rewrite local audit state.
- Local journal rollback cannot be reliably detected without an independent anti-rollback anchor.
- Arbitrary Windows effects cannot be atomically committed with the broker ledger; post-crash state may remain permanently ambiguous.
- Some targets may lack a stable identity or independent, fresh verifier; those operations must remain prohibited.
- Multi-user, Fast User Switching, RDP, disconnected-session, logoff, and service-start authorization behavior has not been selected or tested.
- A service account may be too weak for proposed operations or may accumulate privilege; no concrete operation matrix currently bounds it.
- The WPF application and any same-session consent agent may be compromised even when their binaries are signed.

## 12. Implementation prerequisites

Before any service/IPC endpoint:

- Resolve ADR 0011/0012 governance and approve a scoped architecture.
- Define supported Windows versions, service identity, service/pipe ACLs, installer/update controls, and endpoint ownership.
- Complete independent Windows review of token capture, per-request binding, impersonation/reversion, PID reuse, endpoint verification, and cross-session handling.
- Freeze typed protocol/canonicalization rules and validated resource limits; fuzz parser and compatibility behavior.
- Implement deterministic authorization/state-machine tests before host code.
- Select the ledger, durability settings, key lifecycle, backup/restore, anti-rollback trust anchor, retention, and corruption incident response.
- Define the consent mechanism and explicit residual threat boundary; no approval-dependent operation before this is accepted.
- For each separately proposed operation, provide privilege scope, stable target resolution, preconditions, effect/idempotency model, independent verification, rollback disposition, recovery/fencing, and fault-injection tests.
- Keep V1 separate; no V1 approval, journal, or simulation state may authorize V2.

Before any operation that changes Windows state, additionally require per-operation design approval, disposable-environment testing, independent code/security review, and a release decision. Passing an architecture gate must not approve the catalog.

## 13. Recommended ADR 0012 disposition

**KEEP ADR 0012 PROPOSED.**

This is the single recommended disposition. The service/named-pipe design is a credible candidate, not a sufficiently justified decision. The consent design, first-operation privilege need, identity binding, anti-rollback model, and ADR 0011 relationship are unresolved. Do not implement the service or any real operation while these blockers remain.

Preserve ADRs 0001–0011. If the final review chooses a non-service first implementation, record a new explicit decision that narrows or supersedes ADR 0011 rather than silently changing its accepted history.

## 14. Change and validation statement

This review created only this document. No source code, tests, database schema, migrations, ADRs 0001–0011, or Action Broker V1 behavior were changed. No service, helper, IPC endpoint, elevation mechanism, executor, or Windows-changing operation was implemented. The review is a design assessment, not proof that any proposed control works.
