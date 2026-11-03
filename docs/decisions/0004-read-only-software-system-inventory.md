# ADR 0004: Read-only software and system inventory

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

PC Passport v4 adds software, services, processes, startup, scheduled-task, and Windows system information while preserving the existing hardware inventory and strict read-only boundary. Hardware and software scans must be independently usable, and comparisons must not interpret an unscanned category as missing.

## Decision

- Read registered applications from explicit 32-bit/64-bit uninstall registry views in HKLM and the current interactive user's HKCU. Merge matching registrations across views while retaining view provenance. Do not read uninstall command values.
- Read startup Run/RunOnce entries and current-user/common Startup folders. Persist the source and parsed executable target, but not command arguments. Enumerate Task Scheduler definitions through its COM API; capture task metadata, trigger summaries, action executable paths, and principals without collecting action arguments or running tasks. Identify boot/logon-triggered tasks as startup tasks.
- Read services from `Win32_Service` WMI for service description, state, startup mode, service account, dependencies, and type. Store only the executable portion of the service path. This collector does not invoke Service Control Manager operations.
- Collect process metadata using `System.Diagnostics.Process`, a Toolhelp process snapshot for parent PIDs, and `IsWow64Process2` for architecture. Pair PID with start time when accessible, omit command lines, and retain access-denied/unavailable field states. Report CPU time and average utilization since process start rather than claiming a live sample. Associate a process with a registered app only when its executable path is under that app's registered installation location; retain the path-match evidence.
- Use `Win32_QuickFixEngineering`, `Win32_OptionalFeature`, `Win32_OperatingSystem`, `Win32_BootConfiguration`, and Windows-filtered `SoftwareLicensingProduct` WMI queries for supported system metadata. Do not read product keys or arbitrary environment variables.
- Use four scan scopes (All, Hardware, Software, System). Compare only successful scans sharing the most recent successful scan's exact scope; retain older component observations across partial/failed scans.
- Store new types and values in the existing version-2 observation payload. Do not add a migration because the component type is a text discriminator and the detail payload already stores source and field availability.
- Do not add Store/AppX discovery until an adequately supported, read-only package API is integrated and tested. Do not treat undocumented registry scraping or PowerShell output as authoritative package inventory.

## Alternatives considered

- A single scan scope for every new category: rejected because software/system-only scans would falsely make unrelated hardware appear unobserved.
- Add columns/tables for each new Windows component: rejected because current observations already preserve typed component identity, source, timestamp, details, and availability without schema changes.
- Store complete service/startup/task command lines: rejected because arguments can contain credentials or other sensitive values and are not needed for this read-only inventory.
- Enumerate Store apps from undocumented registration keys or shell output: rejected because source completeness and identity would be unreliable.
- Report instantaneous CPU usage from a single process snapshot: rejected because a single cumulative CPU counter is not an instantaneous utilization measurement.

## Consequences and limitations

- Registered-app inventory does not find portable or otherwise unregistered software and does not enumerate other users' profiles.
- Microsoft Store/AppX applications, provisioned packages, full Windows servicing history, every Windows capability, and all startup mechanisms are not covered.
- `Win32_Service` and system WMI classes depend on provider availability and may return incomplete data. WMI queries can only be cancelled between provider calls.
- Process fields may be inaccessible for protected processes or become unavailable if a process exits during collection. CPU utilization is averaged over process lifetime.
- Historical comparisons are scope-aware, but incomplete scans still cannot prove component removal.
- No migration is added; existing schema 1/2 databases retain their original migration checksums and observations.

## Security

All collection is read-only and runs without elevation. No application is installed or removed; no service, process, startup entry, task, registry value, or system configuration is modified. Scheduled tasks are inspected but never executed. Task Scheduler COM and native snapshot handles are released after use.
