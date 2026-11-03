# WinForge Action Broker V2 — Security Design and Threat Model

**Status:** Security design plus hostless model rules; no OS security control is implemented or independently validated.  
**Decision boundary:** No real operation is approved by this document.
**Hosting/consent status:** No host or transport is selected. Trustworthy human intent against same-user malware is not currently established; consequential operations remain prohibited.

## 1. Security goals and assumptions

The `WinForge.ActionBroker.V2` library models canonical request binding, policy/lifecycle inputs, and typed unverified identity values. Its SHA-256 digest binds a deterministic byte representation; it is not a signature, caller authenticator, consent proof, or anti-rollback anchor. The model accepts no assertion that its host-provided identity evidence is authenticated. Passing its tests does not establish a Windows token/process relationship, trustworthy UI, durable one-time consumption, or operation safety.

Goals: keep the desktop app non-elevated; prevent unauthorized or confused-deputy execution; bind approval to exact request and authenticated session; fail closed on stale policy/target; detect duplicates; preserve evidence across crashes; and report uncertainty honestly.

Assumptions: a standard local user can run arbitrary processes under their own account; a client may be compromised; Windows kernel/LSASS/admin compromise is outside the broker's ability to defeat; a service binary or service account compromise can defeat controls available to that same principal. The design must not claim protection against a malicious administrator or kernel attacker.

### Assets

- Windows state, user data, protected components, and recovery capability.
- Service executable/configuration, operation allowlist, policy version and signing/update keys.
- OS caller token, logon LUID/session, approval challenges, request/execution IDs.
- Durable journal, verification/recovery evidence, service credential/key material.
- V1 simulation data and user understanding of what “simulation” means.

### Trust boundaries

1. Untrusted UI/input ↔ broker named pipe.
2. User/session token ↔ service identity.
3. Consent UI ↔ broker approval state.
4. Broker policy ↔ operation handler.
5. Handler ↔ Windows API/target.
6. Service ↔ ledger, key store, backup and update paths.
7. Different users/logon sessions ↔ shared service and machine resources.
8. V1 simulation ↔ future V2 service; no authority or state crosses by default.

## 2. Caller identity and authorization

The service captures the caller token from the pipe connection and binds user SID, AuthenticationId/LUID, session ID, connection challenge, process PID and process creation time. A process signer/hash/path is defense in depth only. A client-supplied username, process name, session, risk, approval flag, target status or digest is never trusted.

Caller authentication proves which OS security context opened the channel; it does **not** prove human intent, safe client code, or authorization. Authorization separately evaluates operation allowlist, caller/session scope, policy version, target and protected-component status, preconditions, exact normalized parameters, approval, expiry, replay state and conflict locks.

The `current-user` value in V1 is not an identity claim and must not be carried as an authenticated V2 subject. Cross-user access is denied even when the caller knows a request ID. Logoff revokes pending approvals; started operations move through durable recovery.

Consent is an unresolved high-priority issue. A compromised process in the same user session may invoke the approved client, automate its UI, or spoof a window. A broker-generated challenge and preview digest bind data and state; they do not prove that the user saw or understood the preview. A signed client is not proof of uncompromised UI. A standard UAC dialog authorizes an elevation decision, not the exact operation details. The assessment of WPF, a separate per-session consent UI, and OS-mediated consent is in [Architecture Remediation §2](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md#2-consent-model-and-limits). Until a concrete consent boundary and same-user malware residual risk are independently accepted, no consequential operation may rely on session UI confirmation. Any current approval signal must be described as a session-user confirmation, not authenticated human intent.

## 3. Request authorization protocol

Broker authorization binds:

```text
request ID + authenticated user SID + logon LUID/session
+ action ID and catalog version + stable exact target identity/version
+ canonical normalized parameter bytes + precondition evidence digest
+ risk/policy version + preview digest + approval record/challenge
+ creation/expiry + nonce + one-time consumption state
```

The broker—not UI—creates canonical preview and digests, persists the approval, enforces expiry, and atomically consumes approval when committing `Accepted`. The approved envelope is immutable. Target and preconditions are revalidated immediately before `Started`. Before acceptance, any change in target, policy/catalog version, parameters, expiry, caller session or preview invalidates the approval. After acceptance, client-process exit alone does not revoke the consumed authorization; the broker revalidates policy, target, preconditions, and any operation-required session liveness before dispatch without requiring client reconnection.

Replay controls: broker-assigned IDs, unique DB constraints, idempotency key bound to canonical digest and caller, server-owned approval records, durable request state, resource locks, and restart-time checks. Identical retries return existing broker state; altered retries are rejected. An approval never transfers between users, sessions, actions, targets, requests, or policy versions.

Policy downgrade controls: signed/versioned policy manifests, explicit revocations, no client-selected policy, and hard denial of unknown policies or versions known to be older. A locally stored accepted-policy sequence does not detect rollback of the entire local state; rollback-sensitive policy enforcement requires an independently trusted anchor. Policy/catalog update invalidates approvals created under incompatible versions. Key rotation and rollback-safe deployment need separate review.

No hash alone authenticates an approval. A digest only binds content; the broker database, connection identity, confirmation evidence, and one-time state remain authoritative. Durable state rollback is also unresolved: local transactions, ACLs, timestamps, protected keys, or a hash chain do not defeat a local administrator or compromised broker restoring an older valid ledger. Strong anti-rollback requires a separately controlled witness or separately validated hardware monotonic state. Until one is selected and tested, no operation may depend on local-only evidence that an approval was consumed exactly once across rollback.

## 4. Threat analysis

| ID / threat | Attack assumptions | Impact | Proposed mitigation | Residual risk | Verification strategy |
|---|---|---|---|---|---|
| T1 Malicious/compromised desktop client | Client can send arbitrary frames, alter previews, lie about target/risk, or be injected. | Unauthorized operation or disclosure. | Treat client as hostile; broker owns catalog, policy, canonicalization, target lookup, approval and execution; bounded typed protocol; no executor reference in app. | Same-user malware may automate trusted UI; consent remains unresolved. | Hostile-client fuzzing, tampering, direct-call tests, code dependency review. |
| T2 Unauthorized local process | Process attempts guessed pipe name, pipe squatting, or endpoint spoof. | Unauthorized requests, data theft or user deception. | Explicit DACL; local-only pipe; server-owned endpoint; client verifies service PID/token/signature/install; server authenticates token. | Same-user process may share permitted SID or spoof UX; admin can replace endpoint. | Wrong-SID tests, pipe-squatting/spoof tests, ACL review on each supported Windows build. |
| T3 Cross-user request | One user learns another request/approval ID or accesses shared files. | Cross-user execution/data exposure. | Bind all records to SID+LUID+session; per-user authorization and redaction; service-owned ledger ACL; no ID-based authorization. | Admin/service compromise can access all records; service bugs. | Multi-account, Fast User Switching, RDP/logoff and cross-user ID tests. |
| T4 IPC impersonation/token confusion | PID reuse, token theft/duplication, wrong impersonation level, missed revert. | Privilege confusion or confused deputy. | Capture token from connected pipe; bind PID+creation time; minimal token rights; impersonate only for identity capture; revert in `finally`; no async while impersonating. | Windows/API misuse, same-user code injection; operation-specific token use remains high risk. | Windows integration fault injection, PID reuse, impersonation/revert failure tests, independent code review. |
| T5 Privilege escalation/service abuse | Service has broad rights, writable binary/config path, or generic command handler. | System compromise. | Restricted service identity/SID, hardened service/binary DACL, no generic executor, narrow handlers, explicit privilege matrix, signed updates, deny if privilege unavailable. | Service exploit can use rights granted to it; OS/admin vulnerabilities. | Static and dynamic attack-surface review, service ACL/token inspection, signed update tests, no-command-route tests. |
| T6 Confused deputy | Authenticated low-privilege caller tricks service into acting on unrelated/machine-wide target. | Unauthorized cross-scope change. | Scope target to caller/session; server re-resolves exact identity; separate caller authorization from service capability; no client-supplied path/handle as authority; operation-specific policy. | Target resolver mistakes or incorrect scope mapping. | Cross-user target substitution, aliases/reparse-point, protected-target, TOCTOU tests. |
| T7 Replay/tampering/cross-request approval | Attacker resubmits old request, swaps target/parameters or reuses approval. | Repeated or misdirected effect. | Canonical immutable request; request/approval IDs and unique constraints; server-stored approval; expiry; atomic consumption; revalidation at accept/start. | Ledger rollback/restore may resurrect state absent anti-rollback anchor. | Recomputed digest tampering, restart replay, backup-restore rollback, concurrent duplicate tests. |
| T8 Broker compromise | Attacker executes code as service or exploits handler. | Attacker inherits service rights and can alter ledger. | Minimize service rights/code, separate parsers/handlers, process isolation where warranted, signed binaries, secure update, resource caps, no broad network access. | Full compromise of service identity defeats in-process authorization and journal integrity. | Threat-led penetration test, handler isolation review, vulnerability scanning, red-team exercise. |
| T9 Journal tampering | User/admin edits, deletes, reorders or restores journal/database/keys. | False audit history or missed replay. | Service-owned ACL; append-only API; sequence numbers; hash chain/HMAC/signatures; protected key; periodic externally anchored checkpoints; corruption/rollback detection; backups. | Same service/admin/key compromise can rewrite; offline rollback before anchor may remain undetected; no local-only tamper-proof claim. | Alter/delete/reorder/truncate/restore snapshot tests, key access audit, anchor gap tests. |
| T10 Forced termination/power loss | Service/client/OS terminates at any transition or after external side effect. | Partial/unknown operation; duplicate on retry. | Write-ahead `Started`; restart reconciliation; no automatic replay; `Interrupted`→`RecoveryRequired`; target fence; operation-specific read-back. | External side effect and durable record cannot be committed atomically; target may be inaccessible or ambiguous. | Kill/power-fault injection at each transition, external state reconciliation tests. |
| T11 Partial execution/recovery failure | Multi-step operation fails after some effects; verifier/rollback also fails. | Inconsistent Windows state or false success. | Explicit steps/checkpoints, partial state, recovery contract, independent verification; retain fence and require escalation. | Some effects are irreversible or unobservable; rollback may fail. | Fault injection after each step; verifier/rollback failure; recovery drill in disposable VM. |
| T12 Policy downgrade/stale policy | Client replays older policy/catalog or service restores stale config. | Previously denied action becomes allowed. | Signed monotonic policy versions, revocation list, minimum accepted version, service-owned update, invalidate pending approvals. | Signing key compromise or incorrect rollback protection. | Downgrade, stale backup restore, revoked action, key rotation tests. |
| T13 Denial of service | Local process floods connections or sends huge/slow messages; handler stalls. | Service unavailable; resource exhaustion. | Frame/queue/concurrency caps, per-SID rate limits, deadlines, bounded parsing, circuit breakers, quotas and SCM recovery. | Authorized local users can still consume resources; throttles may delay legitimate requests. | Flood, slow-client, memory/handle pressure and recovery tests. |
| T14 Stale/replaced target | Object/path/service identity changes after preview or during execution. | Wrong target or unintended scope. | Stable resolver identity, revalidate at proposal/approval/accept/start, handle/transaction semantics where available, abort on mismatch. | Windows target APIs may have TOCTOU windows; stable identity may not exist. | Rename/replacement/reparse-point/alias and race tests on supported OS. |
| T15 User intent/approval spoofing | Malware in same session triggers or overlays approval UI. | User did not knowingly approve exact effect. | Broker-fetched details, independent consent component, challenge binding, visible publisher/target/effect, no hidden automatic approval. Evaluate secure-desktop/UAC limitations. | No selected design currently proves human intent against same-user malware; prohibit higher-impact actions pending decision. | Consent usability and spoofing study, UI automation abuse tests, independent security review. |

## 5. Audit integrity

The current V1 local JSON store has atomic replacement and corruption detection but remains user-writable and is not tamper-proof. V2 must not claim stronger properties without implementation and tests.

Proposed layered controls:

1. **Access control:** service-owned directory/database; deny ordinary clients write/delete/replace; service writes through a narrow API; administrators remain trusted adversaries/out of scope.
2. **Append-only application model:** immutable event rows and transitions; corrections are compensating events, never silent edits. This is not OS-enforced immutability if service/admin can rewrite storage.
3. **Integrity chain:** each event includes sequence, prior hash and canonical event hash; HMAC/signature key is protected under service identity, ideally CNG non-exportable key with reviewed ACL. Hash chain detects accidental/partial edits but a writer with key and full storage access can recompute it.
4. **Anti-truncation/rollback:** an independently controlled checkpoint/witness is only a candidate, not an accepted control. A Windows event channel is not assumed independent from an administrator or compromised broker. A remote append-only sink changes privacy, availability, and retention assumptions. A TPM monotonic counter is also only a candidate and must be validated for availability, policy, write rate, and crash behavior. Without an independently controlled anchor, an attacker can restore an older complete valid journal and chain.
5. **Key lifecycle:** separate event-integrity key from update-signing keys; rotate with signed key-transition events; retain old verification keys for historical validation; recovery requires controlled ceremony. DPAPI or CNG protects key material at rest but does not protect from a compromised service identity or administrator.
6. **Recovery:** detect sequence gaps, invalid hashes, stale checkpoint, truncated tail, malformed schema and backup rollback. Preserve original bytes/evidence, stop accepting operations, expose an integrity incident, and require reviewed recovery. Do not “repair” by treating journal as empty.
7. **Retention:** retention and export are policy-defined; retain unresolved action records and anchors; deletion is itself an audited administrative event. Backups are protected and restore-tested.

None of these controls alone makes a local journal tamper-proof or rollback-resistant. Against ordinary unprivileged users, service-owned ACLs and protected key access may reduce modification risk if validated. Against a local administrator, compromised broker, or compromised integrity key, local audit evidence is not trustworthy. See [Architecture Remediation §5](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md#5-anti-rollback-authorization-state) for threat-class limits and fail-closed recovery.

## 6. Mandatory security gates

Before any endpoint: choose the host and transport based on a concrete operation; approve per-request token/PID/session binding, endpoint principals, supported OS matrix and failure handling; validate the typed schema and hostile-load limits.

Before any real operation: resolve the consent threat boundary; establish a durable authorization state and state exactly which rollback attackers are covered; implement and test the canonical lifecycle; specify action-specific target identity, privilege, side effects, verifier assurance, rollback disposition and recovery; execute hostile local/multi-user and crash/restore tests; complete independent security review; and approve each operation separately. No current V2 operation meets these gates.
