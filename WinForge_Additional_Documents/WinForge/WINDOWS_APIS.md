# WinForge — Windows API Integration Guide

**Version:** 1.0  
**Target:** Windows 11 x64  
**Status:** Research and implementation checklist

---

# 1. Purpose

This document guides implementation of WinForge's Windows integration layer.

It is not a claim that every listed API is available on every Windows build or that every API exposes complete system information. Verify each API against official documentation and the actual target environment before implementation.

# 2. General Rules

- Prefer documented Windows APIs over parsing undocumented output.
- Use the least privileged interface that meets the requirement.
- Handle access denied and partial results.
- Record Windows version/build requirements.
- Avoid blocking the UI thread.
- Close native handles and release unmanaged resources.
- Use safe P/Invoke declarations and correct structure layouts.
- Validate all strings, handles and returned buffers.
- Do not assume an API's success means the requested state change persisted.
- Add integration tests on supported Windows builds.

# 3. Hardware and Device Discovery

## 3.1 Configuration Manager
Potential use:
- Enumerating device nodes.
- Querying device status.
- Discovering device relationships and properties.

Research and validate:
- `CM_Get_DevNode_Status`
- `CM_Get_Parent`
- `CM_Get_Child`
- `CM_Get_Sibling`
- Device property query APIs.

Use cases:
- Device status.
- Device tree.
- Hardware relationships.
- Device problem codes.

## 3.2 SetupAPI
Potential use:
- Enumerating device information sets.
- Reading device properties.
- Inspecting installed device drivers.

Research and validate:
- `SetupDiGetClassDevs`
- `SetupDiEnumDeviceInfo`
- `SetupDiGetDeviceProperty`
- `SetupDiDestroyDeviceInfoList`

Always release device information sets. Correctly handle buffer-size query patterns and Unicode structures.

## 3.3 WMI/CIM
Potential use:
- Supplemental hardware and operating-system inventory.
- Storage and network information.
- Sensor data where supported.

Limitations:
- Provider availability varies.
- Some values may be stale or missing.
- WMI can be slower than native APIs.
- Some classes expose incomplete or vendor-specific information.

Do not use WMI as the only source for critical inventory.

## 3.4 Authoritative present-device enumeration

The PC Passport hardware collector uses SetupAPI for authoritative enumeration of currently present Plug and Play device information elements. WMI's `Win32_PnPEntity` inventory remains supplemental.

| API | Use and handling |
|---|---|
| `SetupDiGetClassDevsW` | Creates an all-classes device information set with `DIGCF_PRESENT | DIGCF_ALLCLASSES`; no device configuration is changed. |
| `SetupDiEnumDeviceInfo` | Enumerates `SP_DEVINFO_DATA` entries until `ERROR_NO_MORE_ITEMS`. Other enumeration errors are reported as incomplete coverage. |
| `SetupDiGetDeviceInstanceIdW` | Reads the stable PnP instance ID using the required-character-count buffer pattern. The ID is retained as a field and normalized case-insensitively for matching. |
| `SetupDiGetDeviceRegistryPropertyW` | Reads device description, hardware IDs, compatible IDs, class/class GUID, driver key, manufacturer, friendly name, service and enumerator where available. Multi-string values are preserved as newline-separated entries. |
| `SetupDiDestroyDeviceInfoList` | Releases the device information set through a `SafeHandle`; per-property unmanaged buffers are released in `finally`. |
| `CM_Locate_DevNodeW` | Resolves the current device instance ID using normal (configured-device) lookup. The cancel-removal flag is never used. |
| `CM_Get_DevNode_Status` | Reads devnode status flags and the Configuration Manager problem code. Code 22 is reported as disabled; code 28 means device drivers are not installed; other nonzero problem codes are shown as device errors. |
| `CM_MapCrToWin32Err` | Converts Configuration Manager return codes for availability/error classification. |

These APIs are documented for versions older than the Windows 11 x64 target. The collector runs in the interactive, non-elevated application process; access-denied and unavailable properties are represented as field states rather than causing the device record to be discarded. Calls are synchronous, so cancellation is checked between native calls and devices, not during a native call. A device can disappear between enumeration and Configuration Manager status lookup; this is reported as unavailable, not as evidence of device removal.

Property availability distinguishes `Available`, `Missing` (the provider/API did not report that field), `Unavailable`, `Unsupported`, `Inaccessible`, and legacy `Unknown`. `Missing` describes a field, not physical hardware absence. An empty WMI category is reported as “no instances reported” and does not prove that hardware is absent.

Official references:

- [SetupDiGetClassDevsW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetclassdevsw)
- [SetupDiEnumDeviceInfo](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdienumdeviceinfo)
- [SetupDiGetDeviceInstanceIdW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceinstanceidw)
- [SetupDiGetDeviceRegistryPropertyW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw)
- [SetupDiDestroyDeviceInfoList](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdidestroydeviceinfolist)
- [CM_Locate_DevNodeW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew)
- [CM_Get_DevNode_Status](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status)
- [CM_MapCrToWin32Err](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_mapcrtowin32err)
- [Code 22 — CM_PROB_DISABLED](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-disabled)
- [Code 28 — CM_PROB_FAILED_INSTALL](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-failed-install)

# 4. Driver Inventory and Management

The read-only driver inventory uses:

- `SetupDiGetDevicePropertyW` with the documented driver device-property keys to read the published INF name, provider, version, date, and description from present devices. The property key format ID is `A8B865DD-2E3D-4094-AD97-E593A70C75D6`; property IDs are DriverDate (2), DriverVersion (3), DriverDesc (4), DriverInfPath (5), and DriverProvider (9).
- `Win32_PnPSignedDriver` as a supplemental source for INF-associated devices, reported signer text, and the provider's `IsSigned` value. These values report what Windows exposes; they are not an independent cryptographic verification of current trust or revocation status.
- Device instance IDs and the driver key from SetupAPI for package association. When multiple present devices share an INF package, their device IDs are retained together in the package observation.

Signer/signature and package export status remain unsupported unless a validated read-only source is added. No driver package is installed, removed, updated, exported, or modified. A driver version/date is inventory metadata only, not a compatibility or update recommendation.

Do not infer that a driver package is unused merely because no obvious device association is visible.

Driver installation must be a separate, approval-controlled workflow with compatibility checks and post-install verification.

# 5. Services

Use Service Control Manager APIs.

Potential functions:
- `OpenSCManager`
- `OpenService`
- `QueryServiceStatusEx`
- `QueryServiceConfig`
- `EnumServicesStatusEx`
- `CloseServiceHandle`

Important distinctions:
- Stopped is not the same as disabled.
- Demand-start services may be stopped normally.
- Trigger-start services may start only when an event occurs.
- A service can host multiple service instances in a shared process.
- Service dependencies do not reveal every application-level dependency.

Do not disable a service solely because it is stopped or has low CPU usage.

# 6. Processes

Potential interfaces:
- `CreateToolhelp32Snapshot`
- `Process32First`
- `Process32Next`
- `OpenProcess`
- `GetProcessTimes`
- `QueryFullProcessImageName`
- Windows performance counters.
- ETW process events.

Requirements:
- Pair PID with process start time to avoid PID reuse errors.
- Handle protected processes and access denied.
- Do not assume process filename identifies its publisher.
- Avoid retaining command lines unless required and disclosed.
- Do not terminate processes as a generic cleanup action.

# 7. Scheduled Tasks

Use the Task Scheduler COM API or supported interfaces.

Capture:
- Task path and name.
- Enabled state.
- Current state.
- Trigger summary.
- Action target association where safely available.
- Last-run and next-run information where available.

Task arguments can contain sensitive data. Avoid storing them unless a feature genuinely requires them.

# 8. Software Inventory

Potential sources:
- Uninstall registry keys for 32-bit and 64-bit views.
- Windows Installer APIs.
- Windows package deployment APIs.
- AppX package cmdlets or supported package APIs.
- WinGet as supplemental discovery.

Limitations:
- No single source discovers every installed application.
- Portable applications may not register.
- Per-user and per-machine installations differ.
- Display names and publisher strings are not reliable unique identifiers.

Reconcile multiple sources using composite identity.

Do not invoke uninstall strings blindly. Validate the product identity and show the removal scope.

# 9. Registry

Use documented registry APIs and explicit registry views.

Rules:
- Do not assume 32-bit and 64-bit views are identical.
- Use allowlisted keys for inventory and configuration.
- Do not implement a generic registry cleaner.
- Capture a relevant backup before approved changes.
- Validate value type and current value before writing.
- Verify the resulting value after writing.

# 10. ETW and Performance

Potential interfaces:
- Event Tracing for Windows (ETW).
- Performance Counters.
- Process and thread tracing.
- Disk and network performance providers.

Rules:
- Define ETW session ownership.
- Stop sessions during normal shutdown and recovery.
- Prevent duplicate sessions.
- Bound event queues.
- Handle lost events.
- Keep high-volume tracing temporary.
- Document provider and event schema versions.
- Do not infer causation from simple time correlation.

# 11. Storage and Disk Health

Potential interfaces:
- Storage Management APIs.
- Volume management APIs.
- `GetDiskFreeSpaceEx`.
- Storage health and reliability interfaces where supported.
- SMART or NVMe health through supported storage interfaces and vendor support where necessary.

Limitations:
- Some USB bridges hide health data.
- Some controllers do not expose all attributes.
- Temperature and wear values may be unavailable.
- A missing health value is not proof of a healthy drive.

Never fabricate drive health values.

# 12. Windows Security State

Potential interfaces:
- Windows Security Center provider status.
- Microsoft Defender management interfaces and supported PowerShell cmdlets.
- Windows Firewall APIs.
- Windows Update interfaces where applicable.

Distinguish:
- Installed security product.
- Registered security provider.
- Active protection state.
- UI process.
- Update service.
- Companion utilities.

Never assume Microsoft Defender is actively protecting the system solely because its application is installed.

# 13. Operating System and Update Inventory

Potential sources:
- Windows version APIs.
- Registry version metadata.
- Windows Update Agent APIs.
- Servicing stack and package inventory interfaces.

Record:
- Edition.
- Build and revision.
- Architecture.
- Installed updates where discoverable.
- Pending restart state where supported.

Do not assume a displayed update history is a complete record of all servicing operations.

# 14. Native Interop Requirements

For every P/Invoke:
- Use correct calling convention.
- Use correct Unicode/ANSI declaration.
- Use correct structure packing and field types.
- Use `SafeHandle` where appropriate.
- Validate returned handles.
- Check `GetLastError` when documented.
- Avoid unsafe code unless justified.
- Test x64 layout.
- Document ownership and disposal.

Prefer existing .NET wrappers or well-maintained libraries when they provide correct abstractions.

# 15. Command-Line Tools

Tools such as PnPUtil, DISM, SFC and WinGet may be integrated for supported workflows.

Rules:
- Use fixed executable paths resolved through trusted Windows mechanisms where possible.
- Pass structured arguments; do not concatenate untrusted strings into a command.
- Capture exit code, standard output and standard error.
- Apply timeouts and cancellation.
- Do not execute arbitrary commands generated by AI.
- Verify resulting system state independently.
- Document elevation requirements.

# 16. API Documentation Record Template

For each API or command used, document:

| Field | Required information |
|---|---|
| Name | Exact API, interface or command |
| Purpose | WinForge feature using it |
| Official documentation | Microsoft documentation reference |
| Supported versions | Minimum and tested Windows builds |
| Permissions | Required user/service privileges |
| Data quality | Known missing or unreliable fields |
| Failure modes | Access denied, unavailable, timeout, etc. |
| Resource cost | Expected overhead |
| Cleanup | Handles, sessions or resources to release |
| Fallback | Alternative source if unavailable |
| Tests | Unit/integration tests covering it |

# 17. Implementation Gate

Before an API is used for a privileged or destructive operation:
1. Confirm official documentation.
2. Confirm supported Windows versions.
3. Confirm permission requirements.
4. Confirm exact target semantics.
5. Confirm failure behavior.
6. Implement precondition validation.
7. Implement postcondition verification.
8. Add security tests.
9. Document recovery limitations.

# 18. Official Documentation Starting Points

Use Microsoft Learn and Windows SDK documentation as the authoritative starting point. Search by the exact API or interface name and verify the page applies to the target Windows version.

Useful documentation areas:
- Windows Driver Kit and SetupAPI.
- Configuration Manager.
- Service Control Manager.
- Task Scheduler.
- Event Tracing for Windows.
- Windows Performance Counters.
- Windows Installer.
- Windows App SDK and package deployment.
- Windows Security Center.
- Storage Management APIs.
- Windows Update Agent.

# 19. Initial PC Passport Collectors

The first Windows inventory slice uses `System.Management` against the local `root\cimv2` namespace. Collection is read-only and each collector runs independently.

| WMI class | Initial fields | Limitations |
|---|---|---|
| `Win32_OperatingSystem` | Caption, version, build number, OS architecture | Reports provider data; this is not a full servicing/update history. |
| `Win32_Processor` | Device ID, name, manufacturer, core and logical-processor counts | Provider values can be absent or differ from hardware-specific inventory. |
| `Win32_PhysicalMemory` | Tag, device locator, manufacturer, capacity, speed, SMBIOS memory type | Firmware/provider data may omit or misreport module details. |
| `Win32_PnPEntity` | PnP device ID, name, manufacturer, class, status, configuration-manager code | WMI-visible devices are not a complete substitute for SetupAPI and Configuration Manager device enumeration. |

Each query has a 15-second enumeration timeout, checks cancellation between records, records source and observation time, and reports query failures without discarding previous observations. WMI provider calls are synchronous; cancellation cannot forcibly interrupt a provider call already in progress.

Official references checked on 2026-11-02:

- [Win32_OperatingSystem](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem)
- [Win32_Processor](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processor)
- [Win32_PhysicalMemory](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory)
- [Win32_PnPEntity](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pnpentity)

# 20. Expanded PC Passport Hardware Sources

The following read-only WMI classes supplement SetupAPI device enumeration. A successful class query does not guarantee every device or field is present.

| WMI class | Collected examples | Known limitations |
|---|---|---|
| `Win32_BaseBoard` | Product, manufacturer, serial, version | SMBIOS/vendor data can be generic or missing. |
| `Win32_BIOS` | Manufacturer, BIOS version, release date, serial | SMBIOS identity is not proof of UEFI boot mode or firmware configuration. |
| `Win32_VideoController` | PnP/device identity, name, adapter RAM, driver version/date, video processor/mode | Adapter RAM is a 32-bit provider value and may be truncated or inaccurate. |
| `Win32_DiskDrive` | PnP identity, model, manufacturer, serial, interface, media type, size, status | Does not provide a complete storage-health/SMART/NVMe view. |
| `Win32_NetworkAdapter` | PnP/GUID identity, name, manufacturer, MAC, enabled/physical flags, type, speed, service | Includes virtual/software adapters; not all properties are populated. |
| `Win32_DesktopMonitor` | PnP/device identity, name, manufacturer/type, reported resolution and DPI | May not map one-to-one to physical displays and may not provide EDID/current mode. |
| `Win32_SoundDevice` | PnP/device identity, name, manufacturer, product, status | Provider coverage and endpoint granularity vary. |
| `Win32_Battery` | Chemistry, design/full-charge capacity, charge estimate, status | Only returns provider-reported batteries; values depend on the provider. |
| `Win32_ComputerSystem` | Total physical memory and system manufacturer/model | Aggregate provider value; per-module details remain sourced from `Win32_PhysicalMemory`. |

References: [Win32_BaseBoard](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-baseboard), [Win32_BIOS](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-bios), [Win32_VideoController](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-videocontroller), [Win32_DiskDrive](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-diskdrive), [Win32_NetworkAdapter](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-networkadapter), [Win32_DesktopMonitor](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-desktopmonitor), [Win32_SoundDevice](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-sounddevice), [Win32_Battery](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-battery), and [Win32_ComputerSystem](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem).

## 20.1 Deep hardware inventory sources

| Source | Inventory | Boundaries and availability |
|---|---|---|
| `SetupDiGetDevicePropertyW` | Published driver INF, provider, version, date, description | Read-only per-present-device properties. Missing properties are not evidence of no driver. |
| `Win32_PnPSignedDriver` | Supplemental device association, INF, signer text, `IsSigned` | Provider-reported metadata only; not a cryptographic trust/revocation check. |
| `MSFT_PhysicalDisk` in `root\Microsoft\Windows\Storage` | Provider-reported physical disk identity, health/operation status, and supported reliability properties | Class/properties vary by Windows/provider. Missing properties are marked unsupported or missing; they are not inferred. |
| `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceProtocolSpecificProperty` | NVMe SMART/Health log page fields including critical warning, composite temperature, available spare, percentage used, data units read/written, and power-on hours | Opens `\\.\PhysicalDriveN` with read access only and disposes the handle. Bridge devices, RAID/virtual disks, drivers, permissions, and non-NVMe media may not expose protocol data. No SMART writes or device configuration commands are sent. |
| `WmiMonitorID` and `WmiMonitorBasicDisplayParams` in `root\wmi` | EDID-derived manufacturer, product, serial, friendly-name, manufacture date, active state, and physical image dimensions where exposed | Provider may omit or zero EDID fields; monitors are not forced into one-to-one matches with desktop-monitor rows. Image dimensions are provider values, not measured panel geometry. |
| Core Audio MMDevice API | Endpoint ID, friendly name, render/capture direction, endpoint state, and default endpoint flags | Reads the endpoint property store with `STGM_READ`; it does not open audio streams or alter endpoint state. |
| `Win32_Keyboard`, `Win32_PointingDevice` | Keyboard and pointing-device identity and provider-reported capabilities | Supplemental WMI inventory; composite and HID devices may appear at several layers. |
| `GetFirmwareType` | Current Windows boot firmware type (BIOS or UEFI) | Reports boot mode only; it does not read or change firmware setup settings. |
| `MSAcpi_ThermalZoneTemperature` in `root\wmi` | ACPI thermal-zone identity and current temperature when exposed | Converts the provider's tenths-of-Kelvin value to Celsius. Often absent or stale; not a per-core, GPU, NVMe, or vendor-specific sensor catalog. |

These collectors run without elevation. Native calls are synchronous and cancellation is checked between devices and calls. Access denied, unsupported protocols/classes, missing properties, and provider failures are reported separately where the API exposes enough information. A failed or partial collection never removes prior observations.

Official references: [SetupDiGetDevicePropertyW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw), [Win32_PnPSignedDriver](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/aa394354(v=vs.85)), [MSFT_PhysicalDisk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk), [IOCTL_STORAGE_QUERY_PROPERTY](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_storage_query_property), [STORAGE_PROPERTY_ID](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ne-winioctl-storage_property_id), [NVMe health information log](https://learn.microsoft.com/en-us/windows/win32/api/nvme/ns-nvme-nvme_health_info_log), [WmiMonitorID](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorid), [WmiMonitorBasicDisplayParams](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorbasicdisplayparams), [IMMDeviceEnumerator](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator), [IMMDeviceCollection](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdevicecollection), [GetFirmwareType](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfirmwaretype), and [MSAcpi_ThermalZoneTemperature](https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/wmi-acpi-thermal-zones).

## 20.2 Software and system inventory sources

The PC Passport v4 collectors use the following read-only sources. These inventories run in independently selectable software and system scan groups.

| Source | Inventory | Boundaries and availability |
|---|---|---|
| `Microsoft.Win32.RegistryKey` uninstall keys | Registered applications from explicit 32-bit/64-bit views, in HKLM and the current user's HKCU | Covers registered entries only and the interactive user only. Cross-view duplicate entries are merged. Uninstall command values are not read or stored. |
| `Microsoft.Win32.RegistryKey` Run/RunOnce keys and `Environment.SpecialFolder.Startup` / `CommonStartup` | Registry and folder startup entries with source and parsed executable target | Command arguments are omitted. No registry writes are made. Other profiles and undocumented startup locations are not enumerated. |
| Windows Task Scheduler COM (`Schedule.Service`) | Task path/name, author, state, trigger summaries, action type/executable path, principal, last/next run time; boot/logon triggers are identified as startup tasks | Task arguments are deliberately not collected. The collector reads definitions only; it never runs or changes tasks. COM objects are released after enumeration. |
| `Win32_Service` in `root\cimv2` | Service name/display name, description, state, startup mode, service account, dependencies, type, and executable path | WMI provider coverage and access vary. Executable path parsing excludes service command arguments. This collector does not open service handles or change service configuration/state. |
| `System.Diagnostics.Process`, Toolhelp `CreateToolhelp32Snapshot` / `Process32FirstW` / `Process32NextW`, and `IsWow64Process2` | PID, name, parent PID, executable path, process architecture, start time, working/private memory, cumulative CPU time and average CPU percent since process start | PID is paired with process start time for identity. Access-denied or exiting processes have unavailable fields. CPU percentage is a lifetime average, not instantaneous utilization. A process is associated with a registered app only when its executable path is under that app's registered installation location. Command lines are not collected. Snapshot handles use `SafeHandle`. |
| `Win32_QuickFixEngineering` | Windows hotfix/update records reported by WMI | Not a complete servicing stack or Windows Update history. Provider availability and dates vary. |
| `Win32_OptionalFeature` | Windows optional-feature names and install state | Does not guarantee coverage of every Windows capability, Feature on Demand, or package. |
| `Win32_OperatingSystem`, `Win32_BootConfiguration`, `SoftwareLicensingProduct` | OS caption/edition/build/architecture, boot metadata, and Windows activation status when available | Licensing queries are restricted to the Windows application ID. Product keys and partial product keys are not collected. |
| Allowlisted current-user and system environment registry values | A small set of OS/program-directory and processor-architecture values | Does not enumerate arbitrary environment variables, which may contain secrets or user data. |

Microsoft Store/AppX package inventory is unsupported in this increment. No undocumented registry source or external PowerShell/cmdlet output is used as a substitute. Portable/unregistered applications, other-user profiles, full update history, live process CPU sampling, and broad startup mechanisms require separately validated sources and remain out of scope.

The installed implementation uses WMI for service and system inventory even though Service Control Manager APIs are listed above as a potential native interface. WMI provides the requested service description, dependency, account, and service-type metadata in one read-only query. No service state/configuration API with write access is used.

Official references: [Win32_Service](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-service), [Win32_QuickFixEngineering](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-quickfixengineering), [Win32_OptionalFeature](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-optionalfeature), [Win32_OperatingSystem](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem), [Win32_BootConfiguration](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-bootconfiguration), [Task Scheduler interfaces](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-interfaces), [CreateToolhelp32Snapshot](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot), [PROCESSENTRY32](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32), and [IsWow64Process2](https://learn.microsoft.com/en-us/windows/win32/api/wow64apiset/nf-wow64apiset-iswow64process2).

## 20.3 Recovery discovery

Recovery discovery runs as an independent `Recovery` scan group and uses read-only sources. It never elevates.

| Source | Inventory | Boundaries and availability |
|---|---|---|
| Fixed-path `%SystemRoot%\System32\reagentc.exe /info` | WinRE status and reported locations | Fixed executable and arguments, no shell, bounded runtime, redirected output. The textual output is localized; unrecognized status is unavailable, not disabled. No WinRE setting is changed. |
| `SystemRestore` and `SystemRestoreConfig` in `root\default` | Restore-point identifiers/descriptions/times and configuration values where returned | Provider classes and instances vary. A restore point is not proof of restoreability or a personal-file backup; empty or inaccessible results are not treated as disabled. |
| `System.IO.DriveInfo` and read-only directory attribute queries | Mounted volume capacity and candidate WindowsImageBackup/FileHistory directories | Only ready mounted volume roots are checked, and only the two fixed folder names are queried. No recursive volume search is performed. Candidate presence is not backup validation; access denied is recorded as inaccessible. |
| `MSFT_Partition` in `root\Microsoft\Windows\Storage` | Partition number, disk number, GPT type, size, mount paths, and recovery-partition classification | Provider access and fields vary. Unmounted partition free space is not inferred; GPT type does not establish that a recovery image exists. |
| `Win32_EncryptableVolume.GetProtectionStatus` in `root\CIMV2\Security\MicrosoftVolumeEncryption` | BitLocker protection status per volume where accessible | Access/provider failures are explicit. No keys or recovery passwords are collected and no protection configuration is changed. |
| `Win32_Service` in `root\cimv2` | State/configuration metadata for selected recovery and shadow-copy services | A bounded, selected service set; does not open write-capable service controls or alter service state. |
| Windows Event Log API (`System.Diagnostics.EventLog`) | Up to 50 recent matching System-channel event metadata records | Event messages are omitted to reduce privacy exposure. This is not a complete recovery history; access/provider failures remain explicit. |
| `Win32_BootConfiguration` in `root\cimv2` | WMI-reported Windows boot directory/configuration metadata | Not a complete BCD inventory and does not modify boot configuration. |

Recovery observations use the existing inventory component and observation format. Per-source errors do not prevent unrelated sources from being collected. Cancellation is checked between source queries and records. The current readiness assessment uses these observations as evidence but does not certify backup integrity or recoverability.

## 20.5 Backup Intelligence v1

The backup inspector receives only a candidate selected from Recovery discovery; it does not enumerate all volumes itself. It rejects UNC paths, validates the candidate path against the provider and root identity, checks path components for reparse points, and traverses only fixed, known levels. Windows Image Backup traversal is capped at 256 root entries, 32 group directories, 64 backup sets, and 64 entries per group or set. Metadata files are capped at 32, each XML file at 1 MiB, and aggregate XML at 8 MiB. Result payload size, history size, and execution cancellation are also bounded.

For Windows Image Backup, the inspector checks bounded XML well-formedness in `BackupSpecs.xml` and known catalog XML files, and reads only the VHD footer signature or VHDX file identifier signature. It does not mount containers, hash large files, parse all manifest references, validate the full catalog, or establish that a restore will succeed. For File History, only configuration XML is parsed; payload versions and catalog/index integrity remain unsupported. The state `Verified` is strictly scoped to checks that were actually performed and must not be presented as complete backup verification.

XML parsing prohibits DTD processing and external resolvers. The inspector does not execute backup commands, invoke elevated tools, or modify backup files. Cancellation is checked during enumeration, XML parsing, and file-header reads. Access-denied and reparse-point failures stop inspection and produce explicit inaccessible/limitation evidence.

## 20.4 Passive Guardian telemetry

Guardian Observer collection is explicitly user-started, read-only, and cancellable. The user selects a 2–60 second interval and bounded history retention; sampling does not start with the application. Persisted history is local to the per-user SQLite database and pruned by migration-5 age/count policy.

| API | Reading | Boundaries |
|---|---|---|
| [`GetSystemTimes`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes) | System-wide CPU utilization calculated from deltas in idle, kernel, and user times | The first sample only establishes a baseline. Counter discontinuities are reported unavailable. This is system-wide utilization, not per-process usage. |
| [`GlobalMemoryStatusEx`](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex) | Total and currently available physical memory | Failure is recorded as inaccessible/unsupported; it is not replaced with a guessed value. |
| `System.IO.DriveInfo` | Total and free capacity for fixed, mounted volumes | Per-volume access errors are retained as inaccessible. No disk health, SMART status, or unmounted partition capacity is inferred. |
| `Environment.TickCount64` | System uptime | Informational only; no reboot or reliability conclusion is inferred. |
| `System.Net.NetworkInformation.NetworkInterface.GetIPv4Statistics` | Interface sent/received byte-counter deltas divided by measured elapsed time | First sample establishes a baseline; disconnected interfaces, counter resets, unsupported counters, and failed reads are unavailable, not zero. Identity uses the interface ID; no packet/content inspection is performed. |
| `System.Diagnostics.Process` | Bounded working-set observations and process CPU-time deltas | Examines at most 128 enumerated process entries and retains at most 20 observations selected by readable working set. Identity uses PID plus start-time ticks where accessible; protected/exiting processes are partial or unavailable. CPU is interval-based and normalized by logical processor count. Command lines are not collected; no process is changed or terminated. |
| `Win32_VideoController` | GPU adapter name and PnP identity | WMI enumeration has a 15-second timeout and cancellation is checked between results and after the provider call. This provider does not provide a reliable portable live utilization, temperature, or dedicated-memory measurement; these metrics are explicitly Unsupported rather than zero. When PnP identity is missing, name plus enumeration position is a limited, potentially unstable identity. WMI access or provider failures remain visible. |

Warning and critical thresholds for CPU, memory, and minimum free storage are local UI values applied when monitoring starts. A finding must persist across two consecutive samples before it becomes an active alert; escalation and resolution are visible with source and observation time. The sampler does not kill processes, change services, optimize settings, use ETW, or invoke the Action Broker. Device/service-state telemetry, ETW, vendor-specific GPU metric providers, and predictive analysis remain unsupported.
