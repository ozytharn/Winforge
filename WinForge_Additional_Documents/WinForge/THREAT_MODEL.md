# WinForge — Threat Model

**Version:** 1.0  
**Status:** Initial security baseline  
**Scope:** Windows 11 desktop application, monitoring components, local database, optional research and privileged Action Broker.

---

# 1. Purpose

This document identifies the assets, trust boundaries, threats and mitigations relevant to WinForge.

The threat model must be updated whenever the application adds a new privileged capability, external integration, update mechanism, data collection path or process boundary.

# 2. Security Objectives

WinForge must preserve:

- Integrity of Windows and user configuration.
- Confidentiality of local inventory and diagnostic data.
- Integrity and authenticity of software updates.
- Correct authorization of privileged actions.
- Availability of the user's system and WinForge data.
- Traceability of system-changing operations.
- User control over automation and external data sharing.

# 3. Assets

| Asset | Security concern |
|---|---|
| Windows configuration | Unauthorized or incorrect changes |
| Drivers and devices | Loss of functionality or system stability |
| Security software | Accidental weakening of protection |
| User files | Accidental deletion or disclosure |
| PC Passport | Sensitive system inventory disclosure or tampering |
| Digital Twin | False dependency data leading to unsafe actions |
| SQLite database | Tampering, corruption or disclosure |
| Action approvals | Reuse, alteration or forgery |
| IPC channel | Unauthorized privileged requests |
| Update packages | Supply-chain compromise |
| Research results | Malicious or misleading instructions |
| Logs and reports | Sensitive data leakage |

# 4. Trust Boundaries

1. Interactive desktop process.
2. Monitoring service.
3. Action Broker service.
4. Local database and filesystem.
5. Windows APIs and system services.
6. Online research providers.
7. Update and package distribution sources.
8. User-provided configuration and imported files.

Data crossing a boundary must be validated. A process being part of WinForge does not automatically make its input trustworthy.

# 5. Adversaries and Failure Sources

Consider:
- A standard user or local process attempting unauthorized actions.
- Malware already running under the user's account.
- A compromised or malicious third-party application.
- A malicious or compromised research page.
- A tampered update package or distribution endpoint.
- A corrupted local database.
- A buggy collector or action handler.
- Accidental user approval of an unsafe action.
- Power loss, crashes or interrupted operations.

This threat model does not claim that WinForge can defend against every administrator-level or kernel-level compromise.

# 6. Main Threats and Mitigations

## 6.1 Unauthorized privileged operation

**Threat:** A process sends a request to the Action Broker to modify a service, driver, startup entry or system setting.

**Mitigations:**
- Restrict named-pipe access with an explicit Windows security descriptor.
- Authenticate the client process and user identity.
- Validate the request schema and protocol version.
- Authorize each operation independently in the broker.
- Use typed allowlisted operations.
- Revalidate target identity and preconditions immediately before execution.
- Reject arbitrary shell commands, scripts and executable paths.
- Audit every request and result.
- Fail closed when authorization cannot be established.

## 6.2 Approval tampering or replay

**Threat:** An approval is reused for a different target, altered parameters or a later operation.

**Mitigations:**
- Bind approval to the exact operation type, target identity, parameters, policy version and expiry.
- Use a unique request identifier.
- Enforce one-time approval consumption.
- Reject expired approvals.
- Recompute risk and preconditions in the broker.
- Do not trust a client-provided risk score.

## 6.3 Time-of-check/time-of-use changes

**Threat:** A target changes between preview and execution.

**Mitigations:**
- Re-resolve the target immediately before action.
- Compare stable identity and relevant version/state.
- Recheck dependencies and protected status.
- Abort if the target differs from the approved target.
- Record the changed precondition.

## 6.4 Malicious research content

**Threat:** A web page attempts to instruct the AI or user to run unsafe commands or download malicious software.

**Mitigations:**
- Treat all external content as untrusted data.
- Never execute instructions extracted from research.
- Prefer official vendor and Microsoft sources.
- Verify publisher signatures and download provenance independently.
- Clearly distinguish facts, vendor claims, community reports and inference.
- Do not pass research text directly into privileged execution.

## 6.5 Database tampering or corruption

**Threat:** Local data is modified, corrupted or replaced.

**Mitigations:**
- Use restrictive filesystem permissions.
- Use SQLite transactions and integrity checks.
- Version all schema migrations.
- Validate imported data.
- Keep backups separate from the active database.
- Detect inconsistent or impossible records.
- Never let database recommendations bypass broker authorization.

## 6.6 Update supply-chain compromise

**Threat:** A malicious or altered WinForge update is installed.

**Mitigations:**
- Sign release artifacts.
- Verify signatures and expected publisher identity.
- Use authenticated transport.
- Protect update metadata against tampering.
- Reject downgrade or rollback attempts unless explicitly supported and authorized.
- Test interrupted updates.
- Keep a documented recovery path.

## 6.7 Accidental destructive cleanup

**Threat:** Cleanup removes personal files, application data or required system files.

**Mitigations:**
- Use explicit cleanup categories and allowlisted locations.
- Preview exact targets and estimated impact.
- Exclude protected and unknown paths by default.
- Require approval for destructive actions.
- Do not automatically delete cloud-synchronized files.
- Verify deletion results and report limitations.

## 6.8 Security software disruption

**Threat:** Optimization stops or disables active antivirus, firewall or endpoint protection.

**Mitigations:**
- Maintain a protected-component registry.
- Identify registered security providers and active protection state.
- Never terminate active protection as an optimization.
- Distinguish UI helpers from security engines.
- Require an explicit user-led workflow for security-provider changes.
- Verify protection state after any approved change.

## 6.9 Monitoring privacy exposure

**Threat:** System inventory or telemetry reveals sensitive user or organizational information.

**Mitigations:**
- Collect only information needed for stated features.
- Do not inspect document contents, messages, passwords or browser history.
- Keep data local by default.
- Guardian telemetry is collected only during explicit user-started sampling and stored in bounded local SQLite history; user-configurable retention has strict minimum and maximum bounds.
- Process samples are bounded, use PID plus start-time identity where accessible, and omit command lines; network rates are derived from local counter deltas; unsupported GPU metrics are not fabricated.
- Require explicit consent before external research shares inventory details.
- Redact usernames, paths and identifiers in exported reports where appropriate.
- Provide retention and export controls.

## 6.10 Denial of service and resource exhaustion

**Threat:** Monitoring, scans or malformed IPC requests consume excessive CPU, RAM, disk or handles.

**Mitigations:**
- Bound queues and message sizes.
- Apply request rate limits.
- Use timeouts and cancellation.
- Limit concurrent scans.
- Set monitoring overhead budgets.
- Clean up ETW sessions and native handles.
- Apply database retention.

## 6.11 Action Broker V1 simulation authorization and future boundary

The current Action Broker is an in-process simulation engine, not a privileged security boundary. Its synthetic state is isolated from Windows and it does not execute general shell commands or system mutations. A future real executor requires the separate service and IPC boundary described in ADR 0011.

The simulation lifecycle is suitable only for simulation-only release and is not approved for real Windows-changing execution. The `current-user` approver value records a UI label, not authenticated identity; the local JSON journal is not tamper-proof; and interrupted-start recovery after forced termination cannot prove what happened to the prior in-memory simulation state. Simulated verification or rollback must never be represented as proof of real-system verification or rollback.

**Current simulation controls:**
- Keep the desktop app and monitoring surfaces non-elevated.
- Require the exact broker-issued request and approval record at execution.
- Normalize and validate action-specific parameter types and bind parameters to the target identity.
- Restrict policy to the supported policy version and known synthetic targets.
- Serialize approval and execution lifecycle transitions and persist a `started` event before simulation.
- Reject old or unregistered request objects after restart and mark incomplete journal starts interrupted.
- Write the local JSON journal through atomic replacement, serialize concurrent writers, and fail closed on corruption.
- Invalidate UI preview and approval when proposal inputs change; cancel and await in-flight simulation on normal window close.
- Treat the local journal as user-writable evidence, not tamper-proof proof.

**Future privileged design requirements:**
- Keep the desktop app and monitoring surfaces read-only and non-elevated.
- ADR 0011 currently requires a separate Windows service for future privileged operations, but no V2 host is selected and no selected operation proves a machine-wide service is necessary. ADR 0012 remains Proposed.
- Accept only typed, versioned action requests from an allowlist of operation handlers.
- Bind authorization to the exact operation type, target identity, preview digest, parameter digest, policy version, and expiry.
- Revalidate the target immediately before execution and reject stale previews or moved targets.
- Detect duplicate requests and replay attempts through request ids, correlation ids, one-time-use approval state, and audit metadata.
- Keep execution and policy evaluation separate; policies are broker-authoritative and cannot be overridden by the client.
- Record execution state transitions, verification results, and interrupted or failed attempts in an append-only journal.
- Require explicit rollback contracts or reject the action as incomplete.
- Keep the initial action catalog narrow, with prohibited categories including arbitrary service changes, driver installation, registry mutation, restore point changes, and boot configuration changes.

**Risk posture:**
- deny by default,
- reject unknown targets,
- reject protected components by default,
- require verification before reporting success,
- treat uncertainty as a reason to stop.

The independent implementation audit and residual-risk record is [ACTION_SECURITY_AUDIT.md](../../ACTION_SECURITY_AUDIT.md).

## 6.12 Action Broker V2 separate-process design (not implemented)

The V2 proposal, adversarial review, and remediation are documented in [ACTION_BROKER_V2_SECURITY.md](../../ACTION_BROKER_V2_SECURITY.md), [ACTION_BROKER_V2_INDEPENDENT_REVIEW.md](../../ACTION_BROKER_V2_INDEPENDENT_REVIEW.md), and [ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md](../../ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md), with companion architecture, IPC, and recovery documents. No V2 host or transport is selected. The service/named-pipe proposal in ADR 0012 remains Proposed; no code exists. V1 remains the in-process synthetic simulator.

The V2 threat analysis explicitly covers a malicious desktop client, unauthorized local process, cross-user access, IPC impersonation, privilege escalation, confused deputy, replay/tampering, broker compromise, journal tampering, forced termination, partial execution, recovery failure, policy downgrade, denial of service, stale targets, and consent spoofing. Each entry records assumptions, impact, proposed mitigations, residual risk, and verification strategy.

Key unresolved risks are consent authenticity against malware in the same user session; the impossibility of atomically committing arbitrary Windows side effects with a local journal; administrators or a compromised broker modifying local evidence; service privilege creep; and target APIs without stable identity or reliable postcondition checks. A hash chain alone does not make local audit storage tamper-proof. `current-user` remains only a V1 UI label, not authentication.

No V2 controls are implemented or independently validated. Current design does not establish exact-preview human intent against same-user malware or strong anti-rollback against administrator/broker compromise. No consequential operation is eligible. Before any endpoint or operation, the host/rights decision, consent threat boundary, identity binding, state recovery, rollback disposition, and required independent tests must be approved, implemented, and independently assessed. ADR 0011 remains a constraint for privileged operations unless superseded by a new ADR; ADR 0012 is not accepted. This threat model is not a security certification or permission to add an executor.

# 7. Protected Components

The initial protected set must include:
- Windows boot-critical components.
- Core operating-system services.
- Kernel drivers and device support components.
- Active antivirus and endpoint protection engines.
- Firewall and security infrastructure.
- Storage and filesystem drivers.
- Input, display and network support required for system operation.
- Components required for WinForge recovery and safe shutdown.

The list must be evidence-based and version-aware. Unknown components are not automatically safe to modify.

# 8. Security Invariants

These must always hold:

1. The UI cannot directly perform privileged changes.
2. The broker rejects unknown operation types.
3. AI output cannot authorize actions.
4. Approval is bound to exact parameters and target.
5. A changed target invalidates approval.
6. Uncertainty causes denial or a request for more evidence.
7. No action is reported successful without verification.
8. All privileged actions are auditable.
9. Research content is never executable authority.
10. User data is not externally transmitted by default.

# 9. Residual Risks

WinForge cannot guarantee protection from:
- A fully compromised Windows kernel.
- A malicious administrator with control of the machine.
- Hardware or firmware compromise.
- Bugs in Windows or third-party drivers.
- Data loss caused by unsupported restoration mechanisms.
- Incorrect information from external vendors.

Communicate these limitations honestly.

# 10. Required Security Tests

Test:
- Unauthorized named-pipe connection.
- Wrong user or process identity.
- Malformed and oversized IPC messages.
- Unknown operation type.
- Expired approval.
- Replayed approval.
- Changed target after approval.
- Changed parameters after approval.
- Protected component modification attempt.
- Concurrent conflicting requests.
- Interrupted action.
- Broker restart during an action.
- Database corruption.
- Tampered update metadata.
- Malicious research instructions.
- Cleanup path traversal and protected-path attempts.

# 11. Review Triggers

Revisit this threat model when:
- A new privileged action is introduced.
- A new Windows service or process is added.
- External AI or research capabilities change.
- Update or installer architecture changes.
- Data collection expands.
- New backup or recovery integrations are added.
- A security incident or serious defect is discovered.
