# WinForge — Development Roadmap

**Version:** 1.0  
**Status:** Initial development plan; Action Broker Core v1 simulation workflow is now integrated in the WPF shell.
**Project:** WinForge

## Current milestone

- Implemented the simulation-only Action Broker engine and WPF action workflow.
- Kept the execution model read-only and deny-by-default.
- No privileged Windows execution or real system mutation path is part of this milestone.

---

# 1. Project Objective

Develop WinForge into a complete Windows 11 maintenance, system intelligence, diagnostics, optimization, automation and recovery platform.

Development must be incremental. Each milestone must produce working, tested functionality. Do not implement the entire product in one code-generation operation. Do not mark unfinished functionality as complete.

# 2. Development Rules

1. Inspect the repository before changing anything.
2. Follow `ARCHITECTURE.md`.
3. Work on one milestone at a time.
4. Compile frequently.
5. Test after every meaningful implementation.
6. Keep documentation current.
7. Never bypass security architecture to save development time.
8. Never implement privileged operations before the Action Broker is ready.
9. Preserve existing data during upgrades.
10. Do not create fake features or simulated system results.

# 3. Priority System

- **P0:** Foundational or security-critical.
- **P1:** Required for the first usable product.
- **P2:** Important product functionality.
- **P3:** Advanced functionality.
- **P4:** Optional enhancements.

Dependencies must be respected even when a later feature appears more exciting.

# 4. Phase 0 — Repository and Environment Assessment

**Priority:** P0
**Status:** Complete (2026-11-02). The workspace was documentation-only; Windows 11 x64 and the .NET 10 SDK are available.

Tasks:
- Inspect repository and Git status.
- Identify existing code, project structure and documentation.
- Verify Windows version, .NET SDKs, build tools and Windows SDK.
- Identify missing prerequisites and technical constraints.

Deliverables:
- Environment assessment.
- Repository assessment.
- Initial architecture decisions.
- Identified risks.

Completion criteria:
- Existing work is understood.
- No files have been overwritten blindly.
- Build prerequisites are known.
- Initial implementation approach is documented.

# 5. Phase 1 — Application Foundation

**Priority:** P0
**Status:** In progress. The solution, project boundaries, WPF shell, DI/logging, SQLite migration/backup foundation, and initial tests are implemented. The foundation has not been declared complete until all acceptance work and verification are reported.

Tasks:
1. Create or validate solution structure.
2. Configure project references.
3. Configure dependency injection and logging.
4. Implement application startup.
5. Implement WPF/MVVM foundation and navigation.
6. Establish error handling.
7. Create SQLite integration and schema migrations.
8. Implement database backup.
9. Establish test projects.
10. Establish CI/build validation where appropriate.

Deliverables: compiling solution, functional WPF shell, working SQLite database, versioned migrations, basic logging and initial automated tests.

Completion criteria:
- Application launches.
- Navigation works.
- Database initializes.
- Migration tests pass.
- Build succeeds.
- Errors are logged.

# 6. Phase 2 — PC Passport

**Priority:** P0
**Status:** Hardware inventory increments are implemented. PC Passport v4 software/system inventory is implemented incrementally; verification and the final smoke test are pending. Store applications and some advanced startup/update sources remain unsupported.

Tasks:

### Hardware and operating system
- CPU, GPU, RAM, motherboard, BIOS/UEFI, storage, displays, network adapters, audio devices and input devices.
- OS edition/build and installed Windows updates.

### Devices and drivers
- PnP inventory, hardware IDs, compatible IDs, driver package inventory, provider, version, date, signature and device association.

### Software
- MSI applications, uninstall registry inventory, Store packages, provisioned packages, supplemental WinGet discovery, publisher, version and installation metadata.

### Background components
- Processes, services, startup entries, scheduled tasks, updaters and tray helpers.

### Persistence
- Initial, incremental and targeted scans.
- Scan history, coverage, stale-state detection and error reporting.

Deliverables: PC Passport backend, inventory UI, scan progress, persistent inventory and coverage reporting.

Completion criteria: a user can scan the computer and inspect real discovered hardware, software, drivers, services and processes. Partial access is reported honestly. A failed scan does not erase known records.

## Initial implementation progress — 2026-11-02

- The solution targets .NET 10 and Windows x64 for the WPF app.
- Implemented four cancellable, read-only WMI collectors, per-user SQLite scan/component/observation persistence, migration checksums, collector error reporting, and integrity-checked database backup.
- Implemented the PC Passport screen with scan/cancel controls, live collector progress, latest scan status/coverage, real observed records, and collector error display.
- Added workflow unit tests and Windows integration tests, including a real WMI scan and SQLite persistence/backup checks.
- `dotnet build WinForge.sln --configuration Release --no-restore --nologo`: passed with 0 warnings and 0 errors.
- `dotnet test WinForge.sln --no-restore --nologo`: passed 5 unit tests and 4 Windows integration tests; no tests were skipped.
- Release WPF startup smoke test: the PC Passport window opened and the per-user database initialized.
- This records the initial implementation baseline only. The hardware-enumeration increment below adds SetupAPI/Configuration Manager, expanded hardware categories, and category navigation; the remaining Phase 2 work is listed below.

## PC Passport hardware expansion

**Status:** Hardware-enumeration milestone implemented and verified.

Completed in this increment:

- Added read-only SetupAPI enumeration of currently present devices using `SetupDiGetClassDevs`, `SetupDiEnumDeviceInfo`, and `SetupDiGetDeviceInstanceId`.
- Captured device instance IDs, hardware and compatible IDs, class/class GUID, description/friendly name, manufacturer, enumerator/service/driver key, and Configuration Manager devnode status/problem codes where exposed.
- Identified disabled devices (problem code 22), devices with missing drivers (problem code 28), and other reported problem codes without changing device state.
- Wrapped the device information set in a `SafeHandle`, freed property buffers in `finally`, and checked cancellation between native operations.
- Kept WMI as a supplemental source and reconciled PnP rows by case-insensitive instance identity. SetupAPI values take precedence when present; WMI values fill gaps.
- Expanded WMI hardware inventory to motherboard, BIOS/UEFI SMBIOS information, graphics adapters, physical disk drives, network adapters, displays, audio devices, batteries when reported, system memory total, and the existing per-module RAM inventory.
- Added field availability states (unknown, available, missing/not reported, unavailable, unsupported, inaccessible), category filtering, device detail view, collection warnings, and cautious two-scan comparisons. An item not observed in a scan is retained and is never automatically treated as removed.
- Added SQLite migration 2 for component categories and observation details schema 2. Migration 1 remains unchanged; schema-1 observations remain readable.
- Added unit/integration coverage for WMI/SetupAPI reconciliation, native device enumeration, cancellation, schema-1 upgrade, schema-2 availability round-trip, historical observations, partial results, and backup/persistence regressions.
- `dotnet build WinForge.sln --configuration Release --no-restore --nologo`: passed with 0 warnings and 0 errors.
- `dotnet test WinForge.sln --configuration Release --no-restore --nologo`: passed 6 unit tests and 9 Windows integration tests; no tests were skipped.
- Release application smoke test: the `WinForge - PC Passport` window opened, the local database initialized, and the app closed cleanly. The active database now reports migrations 1 and 2; it contained no prior component/scan rows at smoke-test time.

Remaining PC Passport limitations:

- Driver INF/package, provider/version/date, and Windows-reported signer/`IsSigned` metadata are implemented. An independent cryptographic signature/trust check and package export status are not implemented.
- Storage provider health properties and read-only NVMe SMART/Health log queries are implemented; SATA SMART, RAID/bridge passthrough, and unsupported storage-driver paths may not expose health values.
- Monitor EDID identifiers, Core Audio endpoints, keyboard/pointing devices, BIOS/UEFI boot mode, and ACPI thermal zones are implemented. Firmware setup-variable enumeration, endpoint stream-format enumeration, and vendor-specific sensors are not.
- WMI values and SMBIOS data may be absent, stale, generic, or vendor-dependent. `Win32_VideoController.AdapterRAM` is a 32-bit provider field and may not represent modern GPU memory accurately.
- Empty WMI categories are reported as “no instances reported”; this does not prove that the hardware is physically absent. A display row does not necessarily mean a distinct physical panel.
- SetupAPI currently enumerates present devices only. Non-present historical devices are not included in the live device list.
- WMI provider calls remain synchronous and cannot be interrupted mid-query; cancellation is checked between records and queries.
- Historical comparison reports observed changes and “not observed”; it does not infer removals from partial scans.

The software and system inventory increment is recorded in the v4 section below. PC Passport remains read-only; scans are independently selectable by hardware, software, and system group. Store-package enumeration and complete servicing/update history remain unsupported.

## PC Passport v3 — Deep Hardware Intelligence

**Status:** Deep hardware collectors implemented; validation covers the current Windows 11 x64 machine. Data availability remains hardware-, provider-, and permission-dependent.

Completed in this increment:

- Added SetupAPI driver INF/provider/version/date/description properties plus supplemental `Win32_PnPSignedDriver` signer and `IsSigned` fields. Shared package-to-device associations are preserved during reconciliation.
- Added `MSFT_PhysicalDisk` provider health/reliability fields and a read-only `IOCTL_STORAGE_QUERY_PROPERTY` request for the NVMe SMART/Health log. 128-bit counters are retained without narrowing; unsupported/inaccessible protocol data remains explicit.
- Added EDID-derived monitor identifiers using `WmiMonitorID`, Core Audio MMDevice endpoint identity/state/direction/default flags, keyboard and pointing-device details, `GetFirmwareType` BIOS/UEFI boot mode, and ACPI thermal-zone temperature where supported.
- Added live integration coverage for driver metadata and extended collector data-or-issue/cancellation behavior, plus a unit test for shared driver package association reconciliation.
- No SQLite migration was added: new component types use the existing text discriminator and observation JSON schema 2. Migration 1/2 definitions and checksums remain unchanged.
- Read-only boundary preserved. No device, driver, registry, service, or firmware configuration is changed.
- Release build passed with 0 warnings and 0 errors; the full Release suite passed 7 unit and 12 Windows integration tests with no skips.
- Release application smoke test opened `WinForge - PC Passport` and exited cleanly after a normal window-close request.

Remaining limitations:

- Reported signer text and `IsSigned` are provider metadata, not current cryptographic trust, certificate-chain, or revocation verification.
- NVMe SMART/Health fields require the storage stack/device to support the protocol-specific IOCTL. SATA SMART translation, RAID/USB bridge passthrough, and device-specific vendor logs are not implemented.
- EDID data may be absent/zeroed or virtualized; WMI display rows are not forcibly equated with EDID monitor rows.
- Thermal data is limited to Windows-exposed ACPI thermal zones; core-level CPU, GPU, NVMe, battery, and vendor sensor catalogs are not implemented.
- Audio inventory covers endpoint identity/state, not full supported format, channel map, or live stream measurements.
- The rest of Phase 2 (software, services, processes, startup items, scheduled tasks, targeted/incremental scans) remains outstanding.

## PC Passport v4 — Software & System Intelligence

**Status:** Implemented and verified on the current Windows 11 x64 machine.

Implemented:

- Registered desktop application inventory from 32-bit and 64-bit uninstall registry views, for current-user and system registrations. Exact cross-view duplicate registrations are merged. Uninstall command strings are neither read nor stored.
- Startup entries from current-user/common Startup folders and current-user/system Run and RunOnce registry keys. Registry command arguments are omitted; executable targets and source are retained.
- Read-only Task Scheduler COM enumeration of task path/name, author, state, trigger summaries, action executable paths, principal, and run timestamps. Action arguments are intentionally not collected. Boot/logon tasks are categorized as startup tasks.
- Windows services from `Win32_Service`, including state, startup mode, service account, dependencies, service type, and executable path. The path is reduced to the executable target; service command arguments are not stored.
- Process inventory using Windows process enumeration and Toolhelp parent IDs, including PID/start-time identity, executable path where accessible, architecture, memory, cumulative CPU time, and average CPU utilization since process start. Protected or changing processes are retained with unavailable/inaccessible fields where possible. Command lines are not collected.
- Windows updates/hotfixes from `Win32_QuickFixEngineering`; optional features from `Win32_OptionalFeature`; OS edition/build/architecture and boot metadata; Windows activation status where WMI exposes it; and a fixed allowlist of system/current-user environment values.
- Independent Hardware, Software, System, and All scan scopes, category and text filtering, scope-aware historical comparison, and retained prior observations after partial scans.
- New types and fields use the existing version-2 observation payload; no SQLite migration was added. Migration 1 and 2 definitions/checksums are unchanged.
- Added unit and Windows integration coverage for scan-group selection, process/app association, safe process metadata and cancellation, application registration de-duplication and scope, scheduled-task argument omission, system/service collector partial failures, service-state history, and scope-isolated comparisons.
- Release build: passed with 0 warnings and 0 errors. Release tests: 9 unit tests and 20 Windows integration tests passed; none were skipped.
- Release smoke test: `WinForge - PC Passport` opened and responded, the per-user SQLite database was present, and the window closed cleanly.

Limitations and remaining Phase 2 work:

- Microsoft Store/AppX package inventory is not implemented. No supported package-deployment source has been integrated; registry scraping or PowerShell output is not treated as authoritative.
- Per-user application and startup inventory covers the interactive user only, not other user profiles.
- Registered uninstall entries do not discover portable/unregistered applications; `Win32_QuickFixEngineering` is not a complete record of every update/servicing operation, and `Win32_OptionalFeature` does not enumerate every Windows capability or Feature on Demand.
- CPU utilization is an average since process start, not a short-window/live CPU sample. Access to protected process fields depends on Windows permissions.
- Non-Run registry startup mechanisms and non-boot/logon task triggers are represented only by the collected scheduled-task metadata; startup-entry target resolution for shortcuts and broader startup mechanisms remain out of scope.
- Hardware/software/system scopes have independent history. Targeted/incremental scans, complete Store/package discovery, and additional tested servicing sources remain future work.

# 7. Phase 3 — Identity Engine

**Priority:** P0

Tasks:
- Composite identity.
- Publisher/signature matching.
- MSI and package identity.
- Executable, service and driver identity.
- Version tracking and reconciliation.
- Historical identity and confidence.

Deliverables: identity subsystem, persistence, history and tests.

Completion criteria:
- Similar filenames do not create false matches.
- Normal updates preserve logical identity.
- Changed identities preserve history.
- Uncertain matches remain uncertain.

# 8. Phase 4 — Digital Twin

**Priority:** P1
**Status:** Initial evidence-based graph, SQLite persistence, history comparison, bounded queries, and list-based explorer implemented.

Tasks:
- Graph node and edge models.
- Relationship evidence, confidence, validity and expiration.
- Graph persistence and dependency queries.
- Graph exploration UI and historical relationships.

Deliverables: Digital Twin backend, graph explorer, dependency views and relationship evidence display.

Completion criteria: WinForge can explain supported relationships between software, services, devices, drivers and processes. Direct evidence is distinguished from inference.

# 9. Phase 5 — Monitoring Engine

**Priority:** P1
**Status:** System Intelligence v2 adds user-started persistent Guardian telemetry, bounded history and aggregation, measured network/process observations, explicit GPU limitations, improved Digital Twin comparisons, and a real-data overview dashboard. ETW, service/device-state monitoring, vendor-specific GPU sensors, and Guardian policy/automation remain pending.

Tasks:
- CPU, GPU where supported, RAM, disk and network monitoring.
- Process, service and device state monitoring.
- Temperature monitoring where supported.
- ETW integration.
- Monitoring profiles, historical samples and retention.
- WinForge self-monitoring.

Deliverables: monitoring service, resource dashboards, historical charts and configurable profiles.

Completion criteria: monitoring works reliably and remains within defined resource budgets. Unsupported metrics are reported as unavailable rather than fabricated.

Implemented increment:
- Added Windows system CPU sampling through `GetSystemTimes`, physical memory through `GlobalMemoryStatusEx`, mounted fixed-volume capacity through `DriveInfo`, and uptime through `Environment.TickCount64`.
- Added explicit unavailable/inaccessible/unsupported readings, including a baseline-only first CPU sample and inaccessible per-volume states.
- Added user-started/cancellable 2–60 second sampling, configurable warning/critical thresholds, consecutive-sample alert confirmation, and explicit observation source/availability.
- Added a passive Observer screen. It does not terminate processes, change services, optimize the system, or invoke the Action Broker.
- Added SQLite migration 5 for bounded, timestamped Guardian samples with age/count retention, 64 KiB sample limits, and indexed time-range queries. Migrations 1–4 remain unchanged.
- Added interface throughput from counter deltas, bounded process CPU/memory observations keyed by PID plus start time where accessible, and GPU adapter identification. GPU utilization, dedicated memory, and temperature remain explicitly Unsupported with the current provider.
- Added historical average/minimum/maximum/latest values, previous-period averages, unavailable counts, sampling-gap counts, filterable history controls, and a dashboard sourced from Passport, Recovery, Backup Intelligence, Guardian, and Digital Twin state.
- Added historical warning/critical threshold-crossing counts over the selected time range, using the active valid threshold configuration or clearly identified defaults.
- Added Digital Twin relationship comparisons, evidence explanations, two-snapshot selection, relationship-type filtering, and cautious `PotentiallyRemoved` classification only with same-scope follow-up evidence. Absence alone remains `NotObserved`.
- Validation: Release solution build succeeded with 0 warnings and 0 errors; 30 unit tests and 44 Windows integration tests passed with 0 skipped. The Release WPF app launched, exposed all six navigation tabs, and Guardian sampling started and stopped through UI Automation. Persistent telemetry across restart and migration 1–5 compatibility were verified.
- ETW, service/device-state monitoring, reliable vendor GPU metrics, charts, configurable multi-profile monitoring, and Guardian policies/approvals remain unimplemented.

# 10. Phase 6 — Intelligence and Research

**Priority:** P1

Tasks:
- Deterministic rule engine.
- Recommendation contracts.
- Evidence, risk and confidence evaluation.
- Research cache and official-source research.
- Unknown component investigation.
- Root Cause Investigator.
- Configuration Drift Intelligence.
- Explain My Computer.

Deliverables: local intelligence engine, recommendation interface, research subsystem and diagnostic explanations.

Completion criteria: recommendations are based on actual inventory and observations. Risk and confidence are independent. The system remains useful offline.

# 11. Phase 7 — Security Foundation

**Priority:** P0
**Status:** Read-only and least-privilege boundaries are implemented for the current collectors and Observer UI. The Action Broker's in-process simulation engine and WPF lifecycle are implemented; a separate privileged broker, OS-authenticated IPC, and real-action authorization/execution are not implemented. Guardian policy evaluation remains separate and incomplete.

This phase must be designed early, even while previous phases are developed. The service and authenticated named-pipe tasks below are deferred: no V2 operation or machine-wide hosting requirement has been established. ADR 0011 remains the accepted service constraint for privileged operations; ADR 0012 remains Proposed. The approved current milestone is hostless contracts and deterministic tests only.

Tasks:
- Threat model.
- Protected-component registry.
- Risk taxonomy.
- Permission model and approval contracts.
- Guardian policy engine.
- Authenticated IPC — deferred until an operation and host/transport decision are approved; named pipes are not selected.
- Action Broker service — deferred; required by ADR 0011 only for any future privileged operation, not part of the current V2 milestone.
- Typed operation registry.
- Request, target and precondition validation.
- Action audit and execution verification.
- Concurrency control.

Deliverables: `SECURITY.md`, `THREAT_MODEL.md`, Guardian engine, Action Broker, IPC contracts and security tests.

Completion criteria:
- Observer cannot modify the system.
- Broker rejects arbitrary commands.
- Invalid permissions are rejected.
- Protected components cannot be changed through ordinary operations.
- Approval cannot be reused for a changed target.
- IPC authentication works.
- Privileged actions are auditable.

No privileged management module may enter production implementation before the required foundations are ready.

The earlier Phase 7 service/pipe task wording is a future gate, not current implementation authorization. See the [V2 implementation plan](../../ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md) and [ADR 0012](../../docs/decisions/0012-separate-action-broker-service-and-named-pipe.md) for current selection status.

# 12. Phase 8 — Action and Recovery Framework

**Priority:** P0

Tasks:
- Action lifecycle.
- Pre-action state capture.
- Action result records and verification.
- Cancellation and timeouts.
- Partial failure handling.
- Rollback metadata.
- Recovery readiness and backup integrity checks.
- Operation conflict prevention.

Deliverables: reusable action framework, recovery contracts, audit interface and recovery tests.

Completion criteria: a system-changing operation cannot be reported successful without verification. Partial failures are handled transparently.

## Action Broker Core V1 — simulation-only implementation

**Status:** The typed simulation engine, WPF lifecycle, local execution journal, recovery handling, and focused security/UI tests are implemented. Release readiness is **READY WITH DOCUMENTED LIMITATIONS for simulation-only use**; this does not approve any real Windows-changing execution.

Key requirements captured in the documentation set:
- deny-by-default and allowlist-based execution,
- request-bound authorization with expiry and one-time-use state,
- target revalidation and stale-preview rejection,
- replay and duplicate request prevention,
- execution journal and interrupted-operation handling,
- explicit rollback contracts and verification requirements,
- prohibited categories and protected component exclusions,
- deterministic tests and UI smoke testing without live system modification.

Supported end-to-end workflow: choose a registered synthetic action and target, validate inputs, generate a canonical preview, explicitly approve that exact request, execute against isolated in-memory state, inspect simulated verification and the journal, and observe cancellation or interrupted-start recovery. Journal corruption fails closed. A startup recovery record denotes an incomplete simulation only; after forced termination the prior in-memory state and exact effects cannot be proven.

The `current-user` approver label is not authenticated identity. The local JSON journal is not tamper-proof. The broker remains in-process and is **not approved for real Windows-changing execution**. ADR 0011 remains the design constraint for a future separately hosted privileged boundary; its historical decision record is unchanged.

## Action Broker V2 — separate-host architecture and security design

**Status:** Design only. ADR 0012 is proposed, not accepted or implemented. V1 remains a functioning simulation-only component; no V2 Windows service, IPC endpoint, authentication mechanism, elevation, real executor, or Windows-changing action exists.

No V2 host is selected. Begin with hostless typed contracts and deterministic lifecycle tests because no first operation or machine-wide rights requirement is established. A per-user broker is only a candidate for a future user-scoped use case; the demand-start service and named-pipe proposal in ADR 0012 remains unaccepted. ADR 0011 remains a service constraint for privileged operations unless superseded by a new ADR. Exact-preview human intent against same-user malware and strong anti-rollback against privileged local attackers remain unresolved, so consequential operations are prohibited. See [the independent review](../../ACTION_BROKER_V2_INDEPENDENT_REVIEW.md) and [architecture remediation](../../ACTION_BROKER_V2_ARCHITECTURE_REMEDIATION.md).

V2 implementation phases, required gates, alternatives and threats are recorded in the root documents `ACTION_BROKER_V2_ARCHITECTURE.md`, `ACTION_BROKER_V2_SECURITY.md`, `ACTION_BROKER_V2_IPC.md`, `ACTION_BROKER_V2_RECOVERY.md`, and `ACTION_BROKER_V2_IMPLEMENTATION_PLAN.md`. No real operation may be implemented until the required identity, consent, service, durable recovery, per-operation verification/rollback, and independent review gates pass. This design does not approve production security.

# 13. Phase 9 — Guardian Modes

**Priority:** P1
**Status:** Observer-only telemetry UI implemented; Assistant/Guardian modes, policy editor, approvals, and automation history remain unimplemented.

Observer:
- Inventory, monitoring, analysis and recommendations.

Assistant:
- Approved low-risk automation, approval requests and action history.

Guardian:
- Explicit policies, permission scopes, workload awareness, automatically allowed operations, protected-component enforcement, exclusions and failure escalation.

Deliverables: Guardian UI, policy editor, approval centre and automation history.

Completion criteria: all modes enforce their permission boundaries. Guardian cannot exceed user-defined authorization.

# 14. Phase 10 — Driver Intelligence

**Priority:** P1

Tasks:
- Driver Compatibility Ledger.
- Hardware ID verification.
- Shared-package detection.
- Official-source research.
- Signature verification and compatibility analysis.
- Release-note review.
- Driver package export.
- Update workflow and installation verification.
- Driver Update Flight Recorder.

Deliverables: driver management UI, compatibility reports, driver backup and update history.

Completion criteria: a driver is not recommended solely because it has a higher version. Installation is approval-controlled and post-verified.

# 15. Phase 11 — Software Center

**Priority:** P1

Tasks:
- Application inventory and publisher verification.
- Software updates.
- Installation observation.
- Uninstall management and leftover detection.
- Dependency display.
- Package management and installation history.

Deliverables: Software Center, software identity view, installation observer and controlled uninstall workflow.

Completion criteria: no automatic uninstall. Every uninstall previews the target and expected removal scope.

# 16. Phase 12 — Performance Governor

**Priority:** P2

Tasks:
- Gaming, productivity, idle, battery and custom profiles.
- Workload detection and maintenance scheduling.
- Approved process priority management.
- Approved power-plan changes.
- Performance measurement and before/after comparison.

Deliverables: Performance Governor, workload profiles and performance history.

Completion criteria: actions are measured and verified. No fake FPS claims or indiscriminate process termination.

# 17. Phase 13 — Storage and Cleanup

**Priority:** P2

Tasks:
- Temporary-file and cache discovery.
- Browser-cache cleanup.
- Large-file and duplicate-file discovery.
- Disk treemap.
- Broken-shortcut discovery.
- Recycle Bin management.
- Cleanup preview, protected-path policies and deletion verification.

Deliverables: storage dashboard, cleanup preview, cleanup engine and disk visualization.

Completion criteria: unknown and personal files are not automatically deleted. Cleanup targets are classified and validated.

# 18. Phase 14 — Security and Privacy

**Priority:** P2

Tasks:
- Defender and antivirus provider status.
- Firewall status.
- Security configuration overview.
- Privacy settings.
- Security drift detection.
- Controlled setting management.

Deliverables: security dashboard, privacy dashboard and diagnostic reports.

Completion criteria: installed security software is distinguished from active protection. WinForge never silently weakens security.

# 19. Phase 15 — Diagnostics and Repair

**Priority:** P2

Tasks:
- Windows Event Log analysis.
- SFC and DISM integration.
- Network and device diagnostics.
- Application crash analysis.
- Boot analysis.
- Root-cause reports.

Deliverables: diagnostics centre, repair workflows and reports.

Completion criteria: diagnostics use real system information and report command failures accurately.

# 20. Phase 16 — Recovery Center

**Priority:** P1
**Status:** Read-only discovery, a cautious evidence-based readiness assessment, a bounded backup candidate inspector, persisted inspection history, and a Recovery Backups UI are implemented. Inspection is limited to Windows Image Backup metadata/container headers and File History configuration metadata; full backup integrity, payload completeness, and recovery operations remain unimplemented.

### Phase 1 — Recovery discovery
- [x] WinRE status query through fixed-path, read-only `reagentc /info`.
- [x] System Restore point and configuration metadata where WMI exposes it.
- [x] Mounted-volume capacity and WindowsImageBackup/FileHistory directory candidates.
- [x] Recovery GPT partition metadata, BitLocker protection status, selected recovery services, and bounded recovery-event metadata.
- [x] Windows boot configuration metadata where WMI exposes it.
- [x] Persist source, timestamp, availability, warnings, confidence, and limitations in existing observations without schema changes.
- [x] Keep Recovery scans independently selectable and retain prior observations after partial or failed scans.
- [x] Verify Windows integration, migration compatibility, cancellation, and Release application launch/close.

### Remaining phases
- Configuration backup.
- Driver export.
- Restore-point integration.
- System-image integration.
- Personal-file backup integrations.
- Broader provider-specific backup catalog and metadata support.
- Full manifest-reference and backup-set consistency validation where provider formats safely expose it.
- Payload hashing, virtual disk chain validation, and restore testing.
- Expanded readiness coverage and explainable gaps beyond the currently assessed WinRE, System Restore, backup candidates, storage, BitLocker, selected services, and boot metadata.
- Recovery guidance.
- PC Migration Blueprint.
- Recovery Center dashboard improvements, including deeper diagnostics and reliable historical comparisons.

### Backup Intelligence v1

- [x] Discover the existing Recovery candidates without recursively scanning mounted volumes.
- [x] Inspect only a selected, local candidate with bounded directory traversal and reparse-point rejection.
- [x] Parse bounded Windows Image Backup XML metadata and inspect VHD/VHDX signatures without mounting or hashing large files.
- [x] Parse bounded File History configuration XML while reporting payload/index integrity as unsupported.
- [x] Distinguish Verified, Warning, Failed, Incomplete, Unsupported, Inaccessible, Unknown, and Cancelled results without equating metadata success with restoreability.
- [x] Persist inspection evidence and bounded history using additive SQLite migration 4.
- [x] Add provider/state filtering, candidate details, inspection history, comparison, and inspect/cancel actions to the Recovery Center UI.
- [ ] Validate referenced backup files and backup-set consistency for supported provider formats.
- [ ] Inspect additional providers and safely verify larger payloads or virtual-disk chains.

The current `Verified` state means only the bounded checks recorded in that inspection passed. Windows Image Backup references are not exhaustively validated and File History payload/catalog integrity is unsupported.

Deliverables: recovery dashboard, recovery-readiness score, backup management and migration export/import.

Completion criteria: backup categories and limitations are explicit. Recovery integrity is tested where possible.

# 21. Phase 17 — Advanced Intelligence

**Priority:** P3

Tasks:
- Optimization Memory.
- Maintenance Impact Learning.
- PC Readiness Predictor.
- Application Dependency Substitution.
- Regression Detection.
- Historical system analysis.
- Advanced change-impact analysis.

Deliverables: advanced intelligence modules and historical recommendation improvements.

Completion criteria: all recommendations remain evidence-backed.

# 22. Phase 18 — Production Release

**Priority:** P0 before public release

Tasks:
- UI polish and accessibility.
- High-DPI support.
- Installer and uninstaller.
- Service installation and removal.
- Signed builds and secure update mechanism.
- Database upgrade testing.
- Interrupted-update recovery.
- Security audit and performance testing.
- Documentation and release pipeline.

Deliverables: installable application, signed release package, release notes, user documentation and test report.

Completion criteria: a clean Windows installation can install, run, upgrade and uninstall WinForge without damaging unrelated system configuration.

# 23. Mandatory Cross-Phase Requirements

These apply throughout the project:
- Unit tests.
- Integration tests.
- Security tests.
- Documentation.
- Error handling.
- Logging.
- Cancellation.
- Resource measurement.
- Database migrations.
- Privacy review.
- Windows compatibility review.

Do not postpone all testing until the end.

# 24. Milestone Reporting

After every milestone, report:
1. Milestone completed.
2. Files created or modified.
3. Features implemented.
4. Build result.
5. Tests executed.
6. Tests passed and failed.
7. Known limitations.
8. Security implications.
9. Remaining tasks.
10. Next milestone.

Never claim completion without evidence.

# 25. Release Definition

WinForge is ready for release only when:
- Core inventory works.
- Database persistence works.
- Security boundaries are tested.
- Guardian policies work.
- Privileged operations are controlled.
- Action results are verified.
- Recovery limitations are documented.
- No critical known security defect remains.
- Installation and uninstallation are tested.
- No major feature is represented by a fake placeholder.

The roadmap may be adjusted when technical research requires it, but changes must be documented.

This plan is the project execution source of truth.
