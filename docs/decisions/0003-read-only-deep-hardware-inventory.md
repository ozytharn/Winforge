# ADR 0003: Read-only deep hardware inventory sources

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

PC Passport v3 requires driver package metadata, storage health and NVMe SMART information, monitor EDID identity, audio endpoints, input devices, firmware boot mode, and safely accessible sensors. No single unprivileged Windows API supplies all of these fields. Hardware and provider availability varies, and the PC Passport must preserve uncertainty without issuing system-changing operations.

## Decision

- Extend SetupAPI device-property reads with the documented driver INF, provider, version, date, and description keys. Use `Win32_PnPSignedDriver` only as a supplemental source for Windows-reported signer and `IsSigned` values; do not present either as independent cryptographic verification.
- Reconcile driver package components by published INF and retain all observed associated device IDs when multiple devices share a package.
- Use `MSFT_PhysicalDisk` for provider-exposed storage reliability fields and query the NVMe health log through `IOCTL_STORAGE_QUERY_PROPERTY` only. Open physical-drive handles with `GENERIC_READ`, share read/write, and own them with `SafeFileHandle`. Preserve NVMe 128-bit counters as decimal strings; do not send write, firmware, or configuration commands.
- Use `WmiMonitorID` for EDID-derived display identifiers and Core Audio MMDevice interfaces for endpoint IDs, names, state, direction, and default flags. Read endpoint property stores only; do not open audio streams.
- Use `Win32_Keyboard` and `Win32_PointingDevice` for supplemental input metadata, `GetFirmwareType` for current boot mode, and `MSAcpi_ThermalZoneTemperature` for supported ACPI thermal zones. Convert the ACPI tenths-of-Kelvin reading to Celsius and omit invalid sentinel values.
- Keep each source's value and field availability in the existing version-2 observation payload. Do not add a SQLite migration solely for new text component types or JSON detail keys.
- When a class, property, protocol, permission, or device path is unavailable, preserve the issue/availability state and never infer physical absence or clear prior inventory.

## Alternatives considered

- Treat WMI signer/signature properties as cryptographic validation: rejected because the properties report provider metadata and do not establish current trust, chain validity, or revocation status.
- Query storage with a vendor-specific management utility or add an external dependency: rejected to keep the inventory read-only, least-privileged, and dependency-light.
- Infer SATA, RAID, USB bridge, or vendor-specific SMART details from NVMe or generic health values: rejected because those transports expose different interfaces and semantics.
- Read firmware configuration variables or modify settings: rejected as outside this read-only milestone and unnecessary for current boot mode.
- Add fixed SQLite columns for every hardware field: rejected because the existing observation JSON already stores source-specific details and availability without changing migration history.

## Consequences and limitations

- NVMe SMART/Health data depends on storage-stack pass-through support and access. Non-NVMe media and devices behind unsupported bridges/RAID remain unsupported or unavailable.
- EDID may be missing, zero-filled, or virtualized. EDID monitor rows are not assumed to be one-to-one with WMI desktop-monitor rows.
- The audio inventory does not enumerate every supported stream format or open audio streams.
- ACPI thermal zones do not provide a complete CPU/GPU/NVMe/vendor sensor list and may be stale or absent.
- Driver signer text and signing flags are not trust decisions, and package export/update/install remains out of scope.
- New components remain backward compatible with existing migration 1/2 databases. Historical observations remain append-only, including partial scans.

## Security

All collection is read-only and runs without elevation. No driver, device, registry, firmware, service, or audio endpoint configuration is modified. Native handles and COM objects are released on all normal/error/cancellation paths.
