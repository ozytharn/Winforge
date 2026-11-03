# WinForge Action Broker V2 — IPC and Identity Protocol

**Status:** Deferred candidate protocol only. No pipe, service endpoint, transport DTO/parser, token inspection, consent agent, or authentication implementation exists. The hostless milestone intentionally did not implement or freeze this protocol.  
**Companion design:** [V2 architecture](./ACTION_BROKER_V2_ARCHITECTURE.md) and [V2 security](./ACTION_BROKER_V2_SECURITY.md).
**Selection status:** No host or transport is selected. Named pipes are a candidate only if a machine-wide service is justified by a concrete operation.

## 1. Security objective

All endpoint, handshake, framing, and sample envelope text below is illustrative planning material, not a selected or supported wire protocol. Named pipes remain an ADR 0012 proposal and are not the current recommendation. Hostless model tests do not validate any IPC security property. Do not build an endpoint from this document until a concrete operation, host decision, protocol review, and ADR governance gate are complete.

The service must independently establish which Windows principal and logon session opened the endpoint, validate the connecting process context, and authorize a typed operation using broker-owned state. A pipe name, client-supplied SID/name, process name, executable path, UI control, `current-user` label, correlation ID, or client-computed digest is not authentication or approval.

If later selected, named pipes must reject remote clients, use an explicit security descriptor, and deny access unless an authenticated local token meets the reviewed access policy. Never use a null/default descriptor: Microsoft documents that the default pipe descriptor grants read access to Everyone and anonymous users. Exact SDDL and API flags must be verified against supported Windows versions; this document is not a tested configuration. A local pipe or OS-authenticated token does not establish human consent.

## 2. Endpoints and handshake

Logical endpoint names must be fixed, versioned, and protected by a service-owned namespace. Do not derive a privileged pipe name from untrusted input.

1. Client opens an endpoint only after host selection and a reviewed endpoint-validation protocol. No sensitive request or approval data is sent before server identity validation succeeds.
2. The endpoint accepts one bounded request per connection. It does not reuse an identity snapshot for multiple request messages; disconnect, timeout, malformed input, or response closes the connection.
3. The server captures the OS token associated with that exact request message and derives SID, logon AuthenticationId/LUID, session ID, token type/impersonation level, and required group claims. Missing or ambiguous token evidence rejects the request.
4. The server captures the connection-associated PID, holds a process handle, and records process creation time. PID plus creation time is supplementary process-instance evidence, not user identity or proof of trusted code. If process liveness or connection association is lost before approval consumption, reject; after durable acceptance, client-process exit alone does not cancel broker-owned work.
5. Bind the immutable request context to broker-generated connection ID, endpoint instance/epoch, request nonce, token identity, and process instance. Any mismatch between request token and required owner/session policy rejects. Do not infer identity from client DTO fields.
6. If named-pipe impersonation is used to retrieve the request token, check every impersonation call and revert before parsing, authorization, asynchronous work, or side effects. On impersonation failure, reject. On revert failure, retire the worker; if process identity may be contaminated, fail-stop the host. Never continue under the service token.
7. Reauthenticate at approval and acceptance using a fresh one-request connection. Require the same SID/LUID/session and, for an unconsumed approval, the same requesting process instance. A reconnect or replacement process cannot inherit the prior approval; revoke it and require a new proposal/confirmation. After durable acceptance, the broker owns the execution; before dispatch it revalidates policy, target, preconditions, and only operation-required session liveness. It does not require a client reconnect or assign a new client identity to the accepted request.

Process path, publisher signature, hash, and command line may be recorded only as supplemental diagnostics. They can change, be unavailable, or be misleading and must not be used as the sole caller identity. A signed WinForge client process is still compromised if malicious code executes inside it.

### Impersonation rules

- Impersonation is for identity capture only in the default protocol.
- Always revert before parsing an untrusted request, awaiting I/O, authorizing, touching target state, or invoking a handler.
- If an operation-specific user-token execution is ever proposed, isolate it behind an explicit handler contract, use a duplicated minimum-rights token, never allow the client to choose token/impersonation level, and guarantee revert/cleanup. Do not perform asynchronous work while impersonating.
- A caller token proves an OS principal for this request; it does not prove that the human intended the requested action or that the desktop binary is uncompromised.

### Connection and identity lifetime

The canonical tuple, revalidation schedule, process-exit/logoff rules, and failure semantics are specified in [Architecture Remediation §4](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md#4-canonical-request-identity-model). In summary:

- Capture connection ID/endpoint epoch/nonce once per request; close the endpoint after one response.
- Capture SID, AuthenticationId/LUID, and session from the OS request token. Keep these as historical identity evidence, but require a fresh authenticated context at approval and acceptance. Pre-dispatch revalidation is broker-owned and does not require a client connection after acceptance.
- Capture PID plus a held process handle and creation time to distinguish process instances. This does not certify the image or UI.
- Reject PID/token/session inconsistencies, process exit before approval consumption, user logoff before acceptance, and any cross-session request. Client-process exit after durable acceptance is not implicit cancellation. A started operation is not silently cancelled because its client exits; its operation contract controls cancellation/recovery.
- Client-supplied identity claims are never authoritative. Process image signatures and hashes are defense-in-depth only.

## 3. Framing and resource bounds

Use byte-mode streams with a fixed-width, network-byte-order length prefix followed by one versioned UTF-8 message. Require exactly one complete frame per request/response exchange for V2 initial protocol; close after response or explicit cancellation. Avoid named-pipe message-mode assumptions and unbounded streaming.

Provisional design limits (not selected or validated):

- Maximum encoded request: 64 KiB; response: 64 KiB; error detail: 4 KiB.
- Maximum nesting depth: 8; maximum array/object members: 256; maximum string field: 4 KiB.
- Read deadline: 5 seconds for a frame; bounded connection idle timeout; bounded worker queue and per-SID connection/request rates.
- Reject truncated frames, length overflow, invalid UTF-8, duplicate JSON keys, unknown mandatory fields, unsupported schema versions, invalid enum values, noncanonical identifiers, and trailing data.
- Do not log raw request payloads, secrets, file contents, or unrestricted exception text. Log request IDs, schema/error codes, and privacy-reviewed digests only.

Any numeric limits must be derived from bounded typed DTOs, validated under realistic and hostile load, and reviewed for denial-of-service effects before protocol freeze. Limits are protocol policy, not hidden defaults.

## 4. Typed envelope

```json
{
  "protocol": "winforge.action-broker",
  "major": 2,
  "minor": 0,
  "messageType": "CreateProposal",
  "requestId": "client-correlation-uuid",
  "correlationId": "trace-uuid",
  "connectionChallenge": "opaque-short-lived-value",
  "payload": {}
}
```

The sample is illustrative only. It must not be treated as a supported request or executable action. The service assigns its own authoritative operation/request ID; client IDs are correlation fields and cannot choose durable keys.

Required response envelope fields: protocol/version, message type, server request ID, correlation ID, broker decision code, current lifecycle state, server time, and typed payload or bounded error. Never return an exception stack trace or secret-bearing raw Windows state to the client.

### Message types

| Request | Purpose | Important rule |
|---|---|---|
| `GetCapabilities` | Return supported protocol versions and service readiness. | Does not reveal other users’ records or imply an action is available. |
| `CreateProposal` | Submit one typed operation ID, typed parameters, explicit target reference, and user intent. | Broker re-resolves catalog/target and generates normalized preview; caller cannot set risk/policy/approval state. |
| `GetProposal` | Retrieve the caller/session-bound proposal summary. | Requires authenticated owner/session; no cross-user lookup. |
| `RequestApproval` | Start broker-owned approval challenge for an existing exact proposal. | Does not accept `approved=true`, username, or caller-generated approval token. |
| `ResolveApproval` | Return a broker-owned session-user confirmation event tied to a challenge, if an approved consent interface exists. | This event is not proof of human intent. UI authenticity and same-user malware resistance remain unresolved; no consequential operation is eligible. |
| `CommitExecution` | Ask broker to execute one already approved proposal. | Broker atomically consumes approval after durable acceptance; repeated calls return existing state or deny, never launch twice. |
| `GetExecution` | Query caller-authorized lifecycle/result. | Redact target and parameter details by policy. |
| `CancelExecution` | Request cancellation of a specific active execution. | Cancellation is a request, not proof of rollback or non-execution. |
| `AcknowledgeRecovery` | Record an authorized recovery decision for a `RecoveryRequired` item. | Does not itself resume/retry a handler. Operation-specific recovery policy applies. |

## 5. Authentication, authorization, and session binding

The broker derives:

- User SID and relevant group/deny-only SID claims from the OS token.
- Logon AuthenticationId/LUID and Windows session ID.
- Client process PID plus process creation time, tied to the connected pipe.
- Connection nonce, issue time, and endpoint instance.

The broker does not accept identity claims from the DTO. It does not equate process image identity with user identity. File signer verification can reject unexpected client binaries as defense in depth, but same-user malware may still use or inject into an allowed client. If the broker cannot bind the pipe, token, process instance, and session, deny.

Policy decisions are server-side and must include operation allowlist version, user/session scope, relevant machine policy version, action risk, target classification, approval requirement, and revocation/expiry. A client cannot supply an “allow” decision, risk class, protected flag, target resolution result, or policy version that overrides server state.

## 6. Preview and request binding

Broker proposal response contains broker-assigned request ID, action/catalog version, exact canonical target identity or a privacy-reviewed display projection, canonical normalized parameters, expected effect, precondition evidence digest, risk, policy version, creation/expiry, and preview digest.

Canonicalization requirements:

- Define a versioned canonical serialization with duplicate object keys rejected, stable property ordering, normalized strings/Unicode, explicit null/number rules, invariant timestamps, and no culture-dependent formatting.
- Digest the entire authoritatively generated request, including authenticated owner/session, operation ID/version, exact target identity/version, normalized parameters, preconditions, policy version, approval requirements, preview version, expiry, and request nonce.
- Use a cryptographic digest for equality/integrity binding, not as authentication. Never accept a digest as proof that the client may choose its contents.
- At approval and immediately before handler start, recompute the canonical digest and re-resolve target/preconditions. Any mismatch invalidates the approval and requires a new proposal/consent.
- Keep secrets out of parameters and previews. Where sensitive values are unavoidable in a future operation, use opaque references and separately review memory/logging handling.

## 7. Approval protocol

1. Broker creates a random challenge bound to one request, owner SID, logon LUID/session, exact preview digest, policy version, and short expiry.
2. A broker-controlled consent surface retrieves the preview directly from the service and shows target, exact effect, risk, reversibility, and limitations; it does not trust a client-supplied summary.
3. User denial, window close, timeout, logoff, or changed policy consumes/revokes the challenge without approval.
4. On affirmative input, the consent component sends a challenge response over an authenticated channel. The broker validates challenge ownership, session, expiry, digest and consent component identity, then persists an approval record.
5. Broker records approval evidence and issues only an opaque approval ID to the owner/session. The authoritative approval record remains server-side; it is not a transferable bearer token.
6. `CommitExecution` reauthenticates caller/session and locks the request. In one durable transaction, verify proposal/approval/policy/target and request state, then mark the request accepted and approval consumed before dispatch. If the commit fails, no handler starts.

The [remediation consent analysis](./ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md#2-consent-model-and-limits) evaluates WPF, broker-controlled per-session UI, and OS-mediated consent. No approach is currently accepted as proving that a human saw and intentionally approved the exact operation against same-user malware. A challenge/digest binds bytes and server-side state only. UAC consent is not by itself a request-bound approval; the standard prompt does not display the full broker preview. Until an independent review accepts a concrete trust boundary, consequential actions remain prohibited.

## 8. Replay, duplicates, concurrency, and downgrade

- Unique broker request ID and unique approval ID; database constraints enforce one approval per request/decision and one accepted execution per request.
- Idempotency key is broker-generated and bound to canonical request hash and authenticated owner/session. A repeat with identical bytes returns current state; same key/different digest is rejected and audited.
- Persist accepted/started state before handler dispatch. A process restart reads this state before accepting execution commands.
- Use a global resource lock keyed by resolved stable target identity plus operation conflict class; acquire in a durable/transactionally coordinated way and expire only after recovery reconciliation.
- Apply policy version monotonicity and a signed/ACL-protected policy manifest. Reject unknown, downgraded, revoked, or inconsistent versions. Approval is invalidated on any relevant policy/catalog change.
- Negotiate protocol major version explicitly. Unknown major versions fail closed; minor extensions are accepted only when declared optional and safely ignored.
- Logoff expires unstarted approvals for the logon LUID; started work proceeds to recovery determination. Expiry is enforced by service clock policy and monotonic elapsed time within a live process where appropriate.

Exactly-once effects are not guaranteed merely by request IDs. External Windows APIs may commit an effect before a crash, with no durable completion event. For operations without a reliable idempotency key or read-after-write reconciliation, the broker must enter `RecoveryRequired` and must not retry automatically.

## 9. Cancellation and disconnection

Pipe closure and client process exit do not cancel an accepted operation implicitly. Client cancellation is a typed request linked to the broker execution ID. Broker records `CancellationRequested`; the handler contract decides whether safe cancellation is possible. If the operation is already in a non-interruptible or side-effecting section, it finishes or enters `RecoveryRequired`. The response states the final broker state, not a client assumption.

Service disconnect, transport timeout, or response loss is ambiguous to the client. The client queries `GetExecution` using its authenticated owner/session and request ID; it must not submit a new request to “try again” until broker state is known.

## 10. Error model and observability

Return stable codes such as `ProtocolUnsupported`, `AuthenticationFailed`, `CallerNotAuthorized`, `ProposalExpired`, `PolicyChanged`, `TargetStale`, `ApprovalRequired`, `ApprovalExpired`, `ApprovalConsumed`, `DuplicateRequest`, `ConflictInProgress`, `RecoveryRequired`, `RateLimited`, `BrokerUnavailable`, and `JournalUnavailable`.

Errors must distinguish authentication, validation, policy, storage, handler, verification, cancellation, and recovery states without exposing sensitive system data. Broker logs correlate with durable journal sequence IDs. The client may display text but cannot reinterpret a rejected/unknown state as success.

## 11. Verification prerequisites

Before any endpoint is implemented: first select a host and supported Windows versions, then perform integration tests for unauthorized SID/session, malicious local client, PID reuse/process exit, endpoint squatting, remote-client rejection where applicable, server spoofing, per-request token binding, impersonation/revert, cross-session access, malformed/oversized frames, rate limits, protocol downgrade, response loss, approval challenge replay/substitution, identical and changed idempotency requests, client disconnect, cancellation, and restart between every durable transition. Numeric limits remain provisional until valid-payload and hostile-load evidence exists.

### Windows API references

- [Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights) — explicit pipe security descriptors, default descriptor behavior, and session access considerations.
- [CreateNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea) — pipe creation flags and security attributes; exact flags remain a supported-OS verification item.
- [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid) — connection-associated client PID, not an authenticated user identity by itself.
- [ImpersonateNamedPipeClient](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient) — impersonation applies to the last message read and requires checking failure and reverting.

These are design references, not implementation evidence. Token/session binding, exact security descriptors, pipe modes, remote-client rejection, failure handling, and supported Windows versions require implementation-specific review and tests.
