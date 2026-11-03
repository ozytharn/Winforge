# ADR 0005: Read-only Recovery Center discovery

**Status:** Accepted  

## Context

WinForge is beginning its Recovery Center work under a strict read-only, least-privilege boundary. Windows recovery information is exposed by several unrelated providers with different permission and availability behavior. A discovered restore point, partition, or backup folder cannot establish that recovery will succeed. Some Windows Backup catalog queries require elevated or delegated permissions.

## Decision

- Implement recovery discovery as an independent `Recovery` inventory scan group.
- Reuse the current component/observation persistence format and version-2 field-availability model; do not add a database migration for recovery metadata.
- Collect WinRE status through a fixed-path, fixed-argument `reagentc.exe /info` query without a shell or elevation.
- Query System Restore, partition, BitLocker, service, and boot metadata through WMI; query bounded recovery-event metadata through the Windows Event Log API; inspect mounted volume capacities and backup-directory candidates without reading backup contents.
- Record per-source status, availability, timestamp, source, confidence, warnings, and limitations. A query failure is never represented as a definitive absence.
- Do not invoke `wbadmin`, because its documented catalog query requires elevation or delegated Backup Operators permissions.
- Keep backup integrity verification and all state-changing recovery operations out of this increment. A later readiness assessment uses these observations but does not claim that recovery will succeed.

## Consequences

- Recovery observations remain compatible with existing databases and independent from hardware/software/system comparisons.
- The scan provides useful source-reported inventory without claiming recoverability or backup integrity.
- Localization, WMI provider coverage, permissions, mounted-volume limitations, and the bounded event sample can result in unavailable or incomplete information.
- Later recovery actions require a separate security review and explicit authorization design.
