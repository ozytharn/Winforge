# ADR 0002: Authoritative read-only hardware inventory

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

The initial PC Passport used WMI for PnP device enumeration. The Windows API guide and product requirements require authoritative PnP discovery, stable device instance IDs, device problem status, graceful handling of partial metadata, and historical observations. WMI remains useful for hardware categories that do not have one complete, unprivileged source.

## Decision

- Use `SetupDiGetClassDevsW` with `DIGCF_PRESENT | DIGCF_ALLCLASSES` and `SetupDiEnumDeviceInfo` to enumerate present devices. Use `SetupDiGetDeviceInstanceIdW` and `SetupDiGetDeviceRegistryPropertyW` for identity and available device properties.
- Use `CM_Locate_DevNodeW`, `CM_Get_DevNode_Status`, and `CM_MapCrToWin32Err` to query status and problem codes. Problem code 22 indicates disabled; code 28 indicates that device drivers are not installed. No device configuration API is called.
- Own the device information set with a `SafeHandle`; release allocated property buffers in `finally`. Check cancellation between native operations and records because calls themselves are synchronous.
- Keep WMI PnP/hardware collectors as supplemental sources. Reconcile PnP records by case-insensitive device instance identity, prefer non-null SetupAPI values, and retain WMI values where SetupAPI metadata is absent.
- Represent metadata field availability separately from field values. A missing field is not evidence that hardware is physically absent. Empty WMI categories report no instances without asserting absence.
- Add SQLite migration 2 for component category and details schema version 2. Keep migration 1 immutable; schema 2 snapshots details, per-field availability, display name, category, and source in each observation so later current-state updates do not rewrite historical metadata.
- Compare only the two latest scans with results. Report “not observed” rather than removing records, including after partial scans.
- Add PC Passport category filtering, detail inspection, status/warning presentation, and historical comparison without introducing driver management or other system-changing behavior.

## Alternatives considered

- Continue with WMI-only PnP discovery: rejected because WMI can omit devices and is supplemental by specification.
- Use SetupAPI for every hardware category: rejected because SetupAPI's device information properties do not expose all desired motherboard, memory, battery, storage, display, and adapter attributes.
- Infer physical absence from a missing WMI row or partial scan: rejected because providers and access vary; this would create false removals.
- Normalize all identity strings in storage: rejected because changing existing type-scoped keys would duplicate or break existing records. Matching is case-insensitive while the observed identity string remains preserved.
- Alter the initial SQLite migration: rejected because applied migrations are immutable; migration 2 is additive.

## Consequences and limitations

- Present devices are enumerated; non-present historical devices are not listed by the authoritative collector.
- Device instance IDs and hardware identifiers are sensitive inventory and remain local in the per-user database.
- WMI values are provider-dependent. BIOS data is SMBIOS metadata and does not prove UEFI boot mode. GPU adapter RAM may be truncated; monitor rows need not map one-to-one to physical panels; storage health data is not collected.
- Driver package identity, version/date, publisher/signature, export, and installation remain out of scope.
- WMI provider calls cannot be interrupted while in progress; the application checks cancellation between calls and records.
- Migration tests verify the existing schema-1 data shape upgrades without losing component identity or observations. Older binaries reject a newer schema version.

## Security

All new APIs are read-only and run in the interactive user's process without elevation. There is no driver install/update/removal, registry mutation, device enable/disable, service modification, or Action Broker behavior.
