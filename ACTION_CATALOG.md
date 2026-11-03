# WinForge Action Catalog V1 (Design Only)

**Status:** The future real-operation allowlist proposal below is not implemented. V1 separately registers synthetic simulation actions; they do not authorize or modify real Windows targets. V2 has no real executable catalog.

V2 catalog and per-operation prerequisites are specified in [ACTION_BROKER_V2_ARCHITECTURE.md](./ACTION_BROKER_V2_ARCHITECTURE.md) and [ACTION_BROKER_V2_SECURITY.md](./ACTION_BROKER_V2_SECURITY.md). No action listed in this document is approved for real execution by the V2 design.

## 1. Purpose

This catalog documents the only action classes that could ever be considered for authorization by the Action Broker. It is intentionally conservative and does not infer that a category is safe merely because it appears to be familiar.

## 2. Catalog model

Each catalog entry contains:
- action id,
- action family,
- target type,
- risk class,
- required approval,
- required verification,
- rollback contract,
- protected-component exclusions,
- notes.

## 3. Approved design categories

### 3.1 Read-only inspection actions

These are currently considered informational and do not require privileged execution.

- inventory refresh,
- service enumeration,
- startup-item inspection,
- scheduled task inspection,
- current telemetry overview,
- file metadata inspection under known safe roots,
- backup catalog inspection in bounded read-only scopes,
- recovery discovery in bounded read-only scopes.

Policy:
- no system modification,
- no privileged execution path,
- no service or driver action,
- no request-bound authorization required beyond explicit user intent.

### 3.2 Low-risk user-scoped configuration

These actions may be candidate broker actions only after dedicated review.

- user preference persistence,
- local file cleanup in allowlisted directories,
- removal of transient research artifacts in known workspace folders,
- user-scoped startup-entry toggle in a reviewed, non-protected path,
- optional feature state inspection and safe opt-in evidence collection.

Policy:
- target must be user-scoped,
- no protected or system-wide component,
- rollback path or restore plan must be clear,
- no deletion of unknown user data.

### 3.3 Moderate-risk operations

These require explicit policy gating and a complete verification record.

- enabling or disabling a non-critical startup mechanism for the current user,
- creating a user-scoped scheduled task with a narrow, reviewed target,
- toggling a non-critical convenience setting with documented recovery,
- limited local configuration changes in approved settings scopes.

Policy:
- must be one-time, request-scoped, and reviewed,
- must be revalidated before execution,
- must include a rollback or restoration plan when available,
- must not affect security software, recovery infrastructure, or boot-critical components.

### 3.4 High-risk operations

These are not suitable for the initial broker allowlist without a dedicated review.

- device driver install or removal,
- service state change,
- firewall policy change,
- network adapter configuration change,
- boot order or boot configuration change,
- BitLocker state change,
- startup configuration changes outside current-user approved scope,
- recovery configuration changes,
- backup deletion, overwrite, move, or restore operations,
- changes to protected security or dependency components.

Policy:
- explicit user approval required,
- strong verification requirement,
- rollback contract required,
- protected-component checks mandatory,
- traceable journal recording required.

## 4. Prohibited operations

These are excluded by design and remain forbidden unless a separate security review explicitly authorizes them.

- arbitrary shell execution,
- arbitrary PowerShell or CMD invocation,
- general process kill or service stop operations,
- registry mutation or direct HKLM writes,
- arbitrary driver installation, update, or removal,
- partition or volume modification,
- restore-point creation, deletion, or reset,
- WinRE or boot-loader modification,
- network or power-plan settings changes outside a narrow approved category,
- deletion of user-managed documents outside the vetted safe scope,
- any action without a valid verification path.

## 5. Exclusion and safety rules

The action catalog imposes these hard rules:
- protected components are denied by default,
- reparse points, symlinks, and unknown destinations are denied,
- the same action cannot be replayed without a fresh authorization token,
- no action may rely on stale preview data,
- rollback is not assumed; it must be explicit and verifiable,
- unsupported or uncertain actions remain in the prohibited bucket.

## 6. Recommended initial allowlist for future implementation

Only after a design review should these be considered:
- `user-preferences.update`,
- `workspace.cleanup.transient-artifacts`,
- `startup-entry.toggle.current-user`,
- `task.create.user-scoped`,
- `settings.apply.allowlisted`,
- `service.inspect`,
- `driver.inspect`.

The initial implementation should remain empty or nearly empty until the broker design passes formal review.

## 7. Design intent

This catalog is intentionally narrow. Its purpose is to prevent privilege creep, reduce attack surface, and keep WinForge aligned with local-first, read-only system intelligence. The broker must be explicit, auditable, and conservative.
