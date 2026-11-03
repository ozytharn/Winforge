# WinForge — Security Engineering Standard

**Version:** 1.0  
**Status:** Mandatory engineering standard

---

# 1. Purpose

This document converts WinForge's threat model into engineering rules that every module, developer and coding agent must follow.

It complements `THREAT_MODEL.md`. If a feature conflicts with this document, the feature must be redesigned or explicitly reviewed before implementation.

# 2. Security Principles

- Least privilege by default.
- Deny by default when authorization is uncertain.
- No arbitrary privileged command execution.
- Explicit user consent for consequential changes.
- Independent authorization in the privileged broker.
- Revalidate targets immediately before execution.
- Verify every state-changing operation.
- Record security-relevant activity.
- Local-first data handling.
- Fail safely when a dependency is unavailable.

# 3. Process and Service Boundaries

The desktop application must not run elevated for normal use.

Read-only inventory should use the least privileged process capable of collecting it.

Privileged operations must use the Action Broker. Under accepted ADR 0011, the future privileged broker is a separate Windows service; the specific V2 service/IPC proposal remains Proposed and no operation is approved. The broker must:
- Run under the least privileged service identity that supports its separately approved operations.
- Have an explicit service security descriptor.
- Expose only authenticated IPC.
- Accept only versioned, typed requests.
- Enforce an allowlist of operation handlers.
- Validate caller, target, parameters, policy and approval.
- Recheck preconditions.
- Audit results.
- Reject unknown operations.

Do not use a general-purpose administrator command runner.

## 3.1 Current read-only increment

The current Recovery Center and Guardian Observer remain non-elevated, read-only features. Guardian telemetry starts only after explicit user action and can be stopped/cancelled. It persists timestamped resource observations locally using bounded age/count retention; users can configure retention within fixed safety bounds. Process identity is PID plus start time where accessible; command lines and process contents are not collected. Network observations are interval deltas, not cumulative counters. GPU live metrics remain Unsupported unless a reliable source is added and reviewed. Database-write failure is shown explicitly and does not make an unsaved sample appear in history. Threshold findings require consecutive samples and never authorize or trigger operations. Backup discovery checks only fixed candidate folders at ready mounted-volume roots. A user-selected candidate may receive bounded metadata and signature inspection; no backup file is modified, mounted, hashed in full, or represented as recoverable. The inspector rejects UNC paths and reparse points, enforces traversal/metadata limits, and records limitations. These features do not implement privileged Action Broker execution, Guardian policy automation, or recovery operations. A separate Action Broker simulation engine and WPF workflow exist; they are not an authorization boundary for real changes.

## 3.2 Action Broker V1 privileged-broker design blueprint

A future privileged action boundary must not be implemented as a generic shell or command runner. The required design is:

- separate broker service with a minimal service identity and explicit ACL,
- allowlist of typed operation handlers only,
- request-bound authorization with exact target identity and parameter digest,
- expiration and one-time-use constraint on each approval,
- deny-by-default checks on protected components and unsupported action types,
- stale-target and stale-preview revalidation before execution,
- replay prevention via unique request ids and correlation ids,
- execution-state journal covering proposed, approved, started, interrupted, verified, failed, and rolled-back states,
- explicit rollback contracts for any action that modifies state,
- simulation-only security testing before live execution is authorized.

This privileged-service blueprint remains documentation-only. The current V2 review has not established that a service is necessary for any selected operation; there is no selected V2 host or transport. The in-process simulation has no authenticated identity boundary and is not approved for real Windows-changing execution; its local JSON journal is not tamper-proof. Same-user-resistant human consent and strong local anti-rollback are not established. See the [V2 independent review](../../ACTION_BROKER_V2_INDEPENDENT_REVIEW.md) and [architecture remediation](../../ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md) for the limits and ADR 0011 governance gate.

The next-generation separate-service proposal, threat analysis, IPC protocol and recovery state machine are documented in the root V2 Action Broker design package. Those documents are proposals only; no V2 identity, consent, service, journal, or recovery control is implemented or security-certified.

# 4. IPC Security Requirements

Named-pipe implementation must define:
- Pipe name and protocol version.
- Explicit Windows security descriptor and ACL.
- Client identity validation.
- Server identity validation where applicable.
- Message framing.
- Maximum message size.
- Request and response schemas.
- Request IDs and correlation IDs.
- Expiration and replay protection.
- Duplicate request handling.
- Timeouts and cancellation.
- Connection lifecycle and cleanup.
- Authorization checks in the broker.

Do not rely on a pipe name being difficult to guess as an authentication mechanism.

Do not impersonate clients unless a documented operation requires it. If impersonation is used, restrict its scope and revert reliably.

# 5. Approval Contract

Approval must be bound to:
- Exact action type.
- Exact target identity.
- Exact normalized parameters.
- Relevant precondition digest.
- Policy version.
- Approval timestamp.
- Expiry.
- Request ID.
- One-time consumption state.

Any material change to the target, parameters, policy or preconditions invalidates approval.

Approval UI must describe:
- What will change.
- Why it is proposed.
- What could be affected.
- Risk and confidence separately.
- Whether a restart is needed.
- What recovery is available.
- What cannot be reversed.

# 6. Risk Classification

Use a documented risk taxonomy, for example:

- Informational: read-only inspection.
- Low: narrowly scoped, reversible user-level change.
- Moderate: meaningful configuration change with a validated recovery path.
- High: system-wide or difficult-to-reverse change.
- Prohibited: unsupported, unsafe or protected operation.

Risk must not be calculated solely from a numeric score. Include hard-deny rules.

A high confidence score does not make a high-risk operation safe.

# 7. Protected Components

Maintain a version-aware protected-component policy.

Protected categories include:
- Boot and startup-critical components.
- Core Windows services.
- Storage and filesystem components.
- Kernel and device drivers.
- Active security protection.
- Firewall infrastructure.
- Required input, display and network components.
- Recovery infrastructure.
- WinForge's own broker and update components.

Unknown components are not automatically unprotected.

Do not permit an ordinary optimization workflow to disable or remove a protected component.

# 8. Secure Coding

- Validate all external input.
- Use parameterized SQL.
- Avoid unsafe deserialization.
- Avoid dynamic code execution.
- Avoid shell-string concatenation.
- Use safe native handles.
- Validate P/Invoke structure layouts.
- Handle integer overflow and path normalization.
- Protect against path traversal and reparse-point surprises.
- Do not store credentials in source code or logs.
- Do not suppress security exceptions.
- Keep dependencies updated and review licences.

# 9. Filesystem Safety

Before a destructive file operation:
- Normalize the path.
- Resolve the actual target.
- Check whether it is a protected location.
- Check reparse points and links.
- Verify the target has not changed.
- Preview the exact scope.
- Require approval appropriate to risk.
- Record the operation.
- Verify the result.

Do not follow arbitrary links into unrelated locations. Do not delete unknown user data.

# 10. Online Research and AI

- External content is untrusted.
- Research cannot grant permission.
- AI cannot execute commands.
- AI cannot alter Guardian policy.
- AI cannot override protected-component rules.
- AI cannot approve its own recommendations.
- External sharing of inventory requires explicit consent.
- Redact unnecessary personal information.
- Store source, retrieval date and applicability with claims.

# 11. Logging and Audit

Log:
- Authentication failures.
- Authorization decisions.
- Approval creation, rejection and expiry.
- Privileged action start and completion.
- Verification failures.
- Recovery attempts.
- Policy changes.
- Update verification results.

Do not log passwords, secrets, tokens or unnecessary personal content.

Audit records should be append-only at the application level. Protect the database file with appropriate Windows ACLs. Do not claim tamper-proof logging against a local administrator.

# 12. Updates and Supply Chain

- Sign release artifacts.
- Verify signature and expected publisher.
- Protect update metadata.
- Use authenticated transport.
- Reject corrupted packages.
- Test interrupted updates.
- Preserve recovery data.
- Document downgrade policy.
- Review third-party dependencies.
- Do not execute downloaded scripts as an update mechanism.

# 13. Security Review Checklist

Before merging a feature, answer:
1. Does it collect new data?
2. Does it cross a process or trust boundary?
3. Does it require elevation?
4. Can it change or delete user/system state?
5. Is authorization enforced by the broker?
6. Are target and preconditions revalidated?
7. Is approval bound to exact parameters?
8. Is the operation auditable?
9. Is the outcome independently verified?
10. What happens on timeout, crash or partial failure?
11. What recovery exists?
12. Are there tests for malicious or malformed input?

A feature must not ship with an unresolved critical security issue.

# 14. Recovery Discovery Boundary

The initial Recovery Center increment is limited to least-privilege, read-only discovery. It may query documented Windows APIs/providers and execute only a fixed-path `reagentc.exe /info` query with fixed arguments, no shell, and a bounded runtime. It must not elevate, run `wbadmin`, or access recovery credentials or BitLocker recovery keys.

Discovery must not create/delete restore points, change WinRE or boot configuration, modify partitions, change BitLocker or service state, execute scheduled tasks, or create/overwrite/delete backups. A detected restore point, recovery partition, or backup directory is not evidence that recovery will work. Access denied, unsupported, missing, and unavailable states must remain distinct. Any future state-changing recovery workflow requires a separate threat review, authorization design, and explicit user approval.

Backup inspection may read only the bounded metadata and fixed signature regions described in `WINDOWS_APIS.md`, and only after explicit candidate selection. Do not mount backup images, follow reparse points, traverse arbitrary locations, invoke elevated backup utilities, or infer restoreability from passing metadata checks. Inspection cancellation must stop further reads; a cancelled result is not an integrity result.
