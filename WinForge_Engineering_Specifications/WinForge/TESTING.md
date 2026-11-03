# WinForge — Testing and Quality Strategy

**Version:** 1.0  
**Status:** Mandatory engineering standard

---

# 1. Purpose

This document defines how WinForge functionality is verified.

A successful build is not proof that a feature works. Every milestone must include appropriate tests and evidence.

# 2. Testing Layers

## 2.1 Unit tests
Test domain rules, identity matching, policy decisions, risk evaluation, validation, graph traversal and data transformations without requiring a live Windows system.

## 2.2 Integration tests
Test SQLite persistence, migrations, repositories, IPC contracts, service boundaries and application workflows.

## 2.3 Windows integration tests
Run against supported Windows 11 environments to validate actual APIs, permissions, device discovery, service queries, event collection and package inventory.

## 2.4 Security tests
Test unauthorized access, invalid messages, replay, changed targets, malformed parameters, protected-component rules and approval enforcement.

## 2.5 Acceptance tests
Verify user-visible workflows from start to finish.

## 2.6 Performance tests
Measure scan duration, idle CPU, memory, database growth, startup time and monitoring overhead.

# 3. Test Environments

Use:
- Developer workstation for non-destructive tests.
- Disposable Windows 11 virtual machine for system integration.
- Clean Windows 11 VM for installer and upgrade tests.
- VM snapshots before testing privileged or destructive workflows.

Document Windows edition, build, architecture, memory, CPU and test configuration.

Do not test destructive operations on a primary machine.

# 4. Required Test Cases

## PC Passport
- Successful scan.
- Partial access.
- Collector timeout.
- Collector failure.
- Missing optional API.
- Stale inventory.
- Incremental scan.
- Duplicate records.
- Device removal.
- Inaccessible device.

## Identity
- Same product, new version.
- Same filename, different publisher.
- Changed signature.
- Changed path.
- Shared driver package.
- Similar display names.
- Missing identity evidence.

## Digital Twin
- Direct relationship.
- Inferred relationship.
- Stale relationship.
- Invalid relationship.
- Cycle handling.
- Dependency traversal.
- Historical relationship change.
- Added/changed/not-observed/potentially-removed classification and same-scope confirmation evidence.
- Relationship change evidence, source, confidence, and type filters.

## Monitoring
- Missing sensor.
- Process exits during sampling.
- PID reuse.
- ETW session startup and shutdown.
- Lost events.
- High system load.
- Database backpressure.
- Retention cleanup.
- WinForge self-monitoring.
- Persisted telemetry round-trip, sample/age retention bounds, and bounded time-range queries.
- Historical average/minimum/maximum/latest, previous-period comparison, unavailable readings, and sampling gaps.
- Network counter deltas, disconnected/reset counters, bounded process identity, and explicit unsupported GPU states.
- Two-consecutive-sample alert confirmation, escalation, duplicate suppression, and resolution.

## Guardian and Action Broker
- Observer denies changes.
- Assistant requires approval.
- Guardian enforces configured scope.
- Unknown operation rejected.
- Expired approval rejected.
- Replayed approval rejected.
- Changed target rejected.
- Changed parameters rejected.
- Protected component rejected.
- IPC caller rejected.
- Concurrent conflicting operations blocked.
- Partial execution reported accurately.
- Verification failure reported accurately.

## Recovery
- Fixed-scope backup candidate discovery.
- Recognized and unrecognized structures.
- Valid/malformed bounded XML and VHD/VHDX signature checks.
- Missing/inaccessible files and reparse-point refusal.
- Traversal, metadata, history and persistence bounds.
- Unsupported provider and File History payload limitations.
- Inspection cancellation, partial results and history persistence across restart.
- Migration compatibility from schema versions 1, 2, and 3.
- Backup creation.
- Full provider integrity check and restore test (future capability; not implemented).
- Corrupt backup.
- Insufficient space.
- Permission failure.
- Partial restore.
- Interrupted restore.

## Installer and updates
- Fresh install.
- Upgrade.
- Interrupted upgrade.
- Downgrade policy.
- Invalid signature.
- Missing service permissions.
- Uninstall.
- User data preservation.
- Repair install.

# 5. Performance Budgets

Establish measurable budgets before release.

Measure:
- Idle CPU use.
- Idle memory use.
- Disk writes per hour.
- Database size growth.
- Scan duration by collector.
- UI responsiveness.
- Monitoring overhead by profile.
- ETW overhead during investigation.
- Shutdown and service-stop time.

Do not invent target numbers without measurement. Record baseline hardware and Windows build. Set budgets based on representative tests.

# 6. Test Data

Use synthetic fixtures for unit tests.

For Windows integration tests, capture only necessary metadata and remove personal paths, usernames and sensitive identifiers before sharing.

Never commit live system inventories or diagnostic logs containing private information.

# 7. Reliability Testing

Test:
- App crash during scan.
- Service restart.
- Database lock.
- Disk full.
- Power interruption simulation where practical.
- Cancellation.
- IPC disconnect.
- Device removal during collection.
- Windows API timeout.
- Partial write.
- Migration interruption.

# 8. Test Automation

Run build and unit tests for every change.

Run integration tests for affected modules.

Run Windows-specific tests on supported Windows environments.

Run security tests before any privileged feature is merged.

Run installer and recovery tests before release.

# 9. Definition of Done

A feature is complete only when:
- Requirements are implemented.
- Error paths are handled.
- Unit tests pass.
- Relevant integration tests pass.
- Windows-specific behaviour is validated where applicable.
- Security review is complete if boundaries or privileges changed.
- UI reflects actual functionality.
- Documentation is updated.
- Known limitations are recorded.
- Build succeeds.

# 10. Test Reporting

Each milestone report must include:
- Build command and result.
- Test command and result.
- Test count.
- Passed tests.
- Failed tests.
- Skipped tests and reasons.
- Windows environment used.
- Known limitations.
- Any manual verification performed.

Never claim tests passed if they were not executed.
