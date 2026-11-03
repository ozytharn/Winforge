# WinForge — Recovery Engineering Specification

**Version:** 1.0  
**Status:** Read-only discovery, cautious readiness assessment, and Backup Intelligence v1 are implemented. Provider-limited metadata checks and persisted inspection history do not establish full backup integrity or restoreability. Recovery operations remain future work.

## 0. Implemented scope

The current Recovery Center implements read-only discovery, an evidence-based readiness assessment, and selected backup inspection. Recovery-source metadata remains ordinary version-2 inventory observations; inspection evidence and history use the additive version-4 `backup_inspections` table.

Current discovery sources include:
- WinRE status from `reagentc.exe /info` using a fixed system path and fixed read-only arguments.
- System Restore points and `SystemRestoreConfig` values through WMI when the provider exposes them.
- Mounted volume capacity and candidate `WindowsImageBackup` / `FileHistory` directories. Discovery checks only the ready mounted volume roots and these two fixed folder names; it does not recursively search volumes.
- GPT partition metadata, including identification of the standard Windows recovery partition type.
- BitLocker protection status through the documented volume WMI interface where accessible.
- Selected Windows recovery/shadow-copy services and a bounded sample of recovery-related System event metadata.
- `Win32_BootConfiguration` metadata where exposed.

Each item records status, field availability, source, UTC collection time, limitations, confidence, and warnings. An inaccessible or unsupported source is not classified as absent. Event messages, process command lines, and backup content are not collected. The collector does not elevate, call `wbadmin`, or modify system state.

Directory discovery is only a candidate check. A detected folder is not proof of a complete, intact, current, or restorable backup. The Windows Backup version catalog is explicitly unassessed because its documented command requires elevated or delegated privileges.

The deterministic readiness assessment presents separate `Available`, `Gap`, `Attention`, and `Unknown` findings for observed WinRE status, System Restore evidence, backup candidates, capacity, BitLocker, selected services, and boot metadata. It deliberately does not calculate a single readiness score. Unknown or inaccessible data is not treated as a missing capability. An enabled WinRE report or restore point is not evidence that recovery execution will succeed. Assessment findings are recomputed from persisted discovery observations and are not independently persisted.

Backup Intelligence v1 inspects only candidates surfaced by Recovery discovery and selected by the user. Windows Image Backup inspection is bounded to 256 root entries, 32 group directories, 64 backup sets, and 64 entries per group/set, plus the 32-file and 8 MiB XML metadata limits. It parses limited XML and reads only fixed-size VHD/VHDX signature regions; it does not mount images, hash large containers, or exhaustively validate manifest references, catalogs, differencing chains, or restoreability. File History inspection parses bounded configuration XML but does not validate payload versions or indexes. The inspector rejects UNC paths and reparse points, supports cancellation, and records skipped checks and limitations. Inspection results and a bounded history are stored locally; they are evidence records, not authorization or restore instructions.

The Recovery Center dashboard displays discovery status, assessment summary, warnings, source metadata, timestamps, availability, selected-item diagnostics, backup candidates, bounded inspection checks, history, and comparison. Discovery, assessment, and inspection never create, delete, move, rename, overwrite, mount, or restore backups or restore points; do not change WinRE, boot configuration, partitions, BitLocker, services, or registry state; and do not elevate. Passing checks do not imply that a backup can be restored.

The proposed Action Broker V2 execution-recovery state machine is separate from Recovery Center discovery and backup inspection. It is design-only in [ACTION_BROKER_V2_RECOVERY.md](../../ACTION_BROKER_V2_RECOVERY.md); no V2 recovery coordinator or real operation exists.

---

# 1. Purpose

WinForge must help users prepare for, understand and recover from system changes.

Recovery is not a promise that every Windows operation can be reversed. The interface must clearly distinguish available recovery mechanisms and their limitations.

# 2. Recovery Categories

Keep these categories separate:

1. Configuration backup.
2. Driver package export.
3. Windows restore point.
4. System image.
5. Personal-file backup.
6. Application-specific backup.
7. Recovery drive or installation media.
8. WinForge database and settings backup.

One category does not substitute for another.

# 3. Recovery Readiness

Show the status of each category:
- Not configured.
- Configured.
- Backup created.
- Integrity checked.
- Restore test completed.
- Stale.
- Failed.
- Unknown.

A backup that exists but has not been tested must not be described as fully verified.

The overall Recovery Readiness Score must be explainable. Show which categories contribute and which are missing. Do not hide a critical gap behind a single high score.

# 4. Before an Action

Before a consequential action:
1. Identify the exact target.
2. Capture relevant current state.
3. Determine whether a recovery mechanism exists.
4. Assess whether the recovery data is accessible.
5. Explain recovery limitations.
6. Require additional preparation or approval if appropriate.
7. Abort when mandatory preconditions fail.

Do not create a restore point and then imply it guarantees rollback of every driver, application or personal-file change.

# 5. Configuration Backups

Capture only the settings relevant to the proposed operation.

Record:
- Backup ID.
- Creation time.
- Machine and OS context.
- Target component.
- Captured values.
- Format version.
- Integrity status.
- Restoration procedure.
- Known limitations.

Avoid exporting secrets or unrelated registry data.

# 6. Driver Recovery

Where supported:
- Export the existing driver package.
- Record INF and package metadata.
- Record provider, version, signer and associated devices.
- Record the device state before update.
- Record the package source and signature of the proposed update.
- Verify the device after installation.
- Provide a supported recovery workflow.

Do not assume driver export alone guarantees that Windows can restore the driver in every scenario.

# 7. System Restore and System Images

Integrate with supported Windows recovery mechanisms.

Display:
- Availability.
- Last known creation time where accessible.
- Storage or location where accessible.
- Scope and limitations.
- Whether a restart or recovery environment is required.

Do not silently enable, disable or delete restore infrastructure.

# 8. Personal-File Backups

Personal-file backup must be a separately configured workflow.

Requirements:
- User-selected sources and destinations.
- Preview of included and excluded files.
- Clear handling of permissions and locked files.
- Versioning where supported.
- Integrity verification.
- Restore preview.
- Protection against overwriting newer files.
- No automatic cloud-file deletion.
- No claim that a sync folder is a backup without validating retention and recovery semantics.

# 9. Database and WinForge Settings Recovery

Back up:
- SQLite database using a consistent backup mechanism.
- Schema version.
- Application settings.
- Guardian policies.
- User exclusions.
- Recovery metadata.

Do not include credentials or signing keys.

Test restoration into a separate location before replacing the active database.

# 10. Restore Workflow

Every restore must:
1. Identify the recovery point.
2. Validate format and integrity.
3. Show exact scope.
4. Show expected overwrite or replacement behaviour.
5. Check compatibility with current Windows and hardware state.
6. Obtain approval.
7. Capture current state where possible.
8. Execute through the appropriate supported mechanism.
9. Verify restored state.
10. Record outcome and remaining issues.

# 11. Failure Handling

Handle:
- Missing backup.
- Corrupted backup.
- Insufficient disk space.
- Access denied.
- Locked files.
- Device mismatch.
- OS build mismatch.
- Partial restore.
- Power interruption.
- Reboot requirement.
- Recovery tool unavailable.

Never report a restore as successful when verification failed.

# 12. Recovery Testing

Use disposable virtual machines and test environments.

Test:
- Backup creation.
- Backup integrity.
- Restore to a separate location.
- Configuration restore.
- Driver export and documented recovery.
- Database restore.
- Interrupted operation.
- Insufficient storage.
- Permission failure.
- Partial restore.
- Recovery after application upgrade.

Do not use the developer's primary PC as a destructive recovery test environment.

# 13. User Interface Requirements

The Recovery Center should show:
- Recovery readiness.
- Last backup time.
- Backup location.
- Integrity and test status.
- Available restore points.
- Driver backup status.
- System image status.
- Personal-file backup status.
- WinForge data backup status.
- Recovery instructions and limitations.

Use clear language. Do not use reassuring green status indicators when the backup is untested or stale.
