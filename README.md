# WinForge

WinForge is a local-first Windows 11 maintenance and PC intelligence application. Its guiding principle is **understand the PC before changing it**. The first implementation is read-only and contains no system-changing or privileged operations.

## Current implementation

The PC Passport performs a read-only local scan for:

- Windows edition/version/build and architecture.
- Processor name, manufacturer, core counts, and logical processor counts.
- Physical memory modules and system-reported total memory.
- Present Plug and Play devices enumerated through SetupAPI and Configuration Manager, including instance IDs, hardware/compatible IDs, class, manufacturer, friendly names, driver key, status, and problem codes.
- Motherboard, BIOS/UEFI SMBIOS metadata, graphics adapters, physical storage devices, network adapters, displays, audio devices, and batteries where Windows providers report them.

Scan metadata, component identities, categories, source provenance, field availability, and timestamped observations are stored in a per-user SQLite database at `%LOCALAPPDATA%\WinForge\winforge.db`. Partial or failed collector results are reported; observations from earlier scans are retained. The UI offers scan progress and cancellation, category filtering, device details, and cautious comparisons with previous scans.

SetupAPI and Configuration Manager are authoritative for present PnP device enumeration. WMI is supplemental and provider-dependent; an empty category means no instances were reported, not that hardware is certainly absent. Device configuration is never modified.

The application also includes a real-data Overview dashboard, persistent evidence-based Digital Twin snapshots and relationship comparisons, bounded impact analysis for selected entities, read-only Recovery Center discovery/readiness, and bounded user-selected inspection for Windows Image Backup metadata/container signatures and File History configuration metadata. Guardian is a user-started read-only observer for CPU, physical memory, fixed-volume capacity, uptime, interval-based network throughput, bounded process CPU/working-set observations, and GPU adapter identity. Timestamped Guardian history is persisted locally with bounded configurable retention and historical summaries. GPU utilization, dedicated memory, and temperature are explicitly unsupported by the current provider. Backup inspection checks do not prove backup completeness or restoreability. The platform also includes evidence-backed configuration drift analysis for successive snapshots, a conservative root-cause investigation layer that correlates telemetry, inventory changes, and topology evidence without claiming causation, a bounded impact engine that maps direct and indirect dependency influence without asserting causation, and a read-only Security and Privacy assessment that interprets Defender, firewall, BitLocker, UAC, Secure Boot, startup persistence, and update metadata without modifying any system settings or performing privileged actions. The Passport view now exposes a security/privacy findings list with evidence summaries, related-entity context, and manual investigation guidance so users can inspect the underlying evidence instead of only reading a top-level status line. None of these surfaces performs system-changing operations.

The WPF shell also includes an Action Broker simulation workflow for registered synthetic actions, explicit request-bound approval, in-memory simulation, simulated verification, and local journal review. It does not modify Windows state and is not approved for real system-changing execution. Its `current-user` approver label is not authenticated identity, its local JSON journal is not tamper-proof, and forced termination can leave simulated effects unprovable.

## Build, test, and run

On Windows 11 x64 with the .NET 10 SDK:

```powershell
dotnet restore WinForge.sln
dotnet build WinForge.sln
dotnet test WinForge.sln
dotnet run --project src\WinForge.App\WinForge.App.csproj
```

### Release packaging

The repository now includes a repeatable packaging script for generating a local release bundle. It publishes the desktop app to a release artifact folder and adds a plain-language installation note that spells out the read-only operational model.

```powershell
pwsh .\scripts\BuildRelease.ps1
```

Options:

```powershell
pwsh .\scripts\BuildRelease.ps1 -Configuration Release -Runtime win-x64 -Version 0.9.0
pwsh .\scripts\BuildRelease.ps1 -Configuration Release -Runtime win-x64 -SelfContained -NoZip
```

The script writes a publish folder under `artifacts\release` and optionally creates a ZIP archive for handoff or distribution. The packaged build remains local-first and read-only; it does not install services or elevate the process.

The integration tests include a real, read-only Windows inventory scan. NuGet supplies the application’s SQLite and WMI dependencies; a global SQLite installation is not required.

## Solution layout

- `src\WinForge.App`: WPF shell, MVVM screen, dependency injection, and logging.
- `src\WinForge.Domain`: inventory, recovery, Digital Twin, and telemetry records.
- `src\WinForge.Application`: collector/repository contracts, scan workflow, Digital Twin comparison/graph analysis, recovery assessment, and Guardian history/alert assessment.
- `src\WinForge.Infrastructure`: SetupAPI/Configuration Manager and WMI collectors, Windows telemetry, SQLite persistence/migrations.
- `tests\WinForge.UnitTests`: workflow, Digital Twin, recovery assessment, and monitoring assessment tests.
- `tests\WinForge.IntegrationTests`: SQLite, Windows inventory, and Windows telemetry tests.
- `docs\decisions`: architecture decision records, numbered sequentially.

The full product roadmap and coordinated requirements remain in the supplied specification folders. Reliable GPU live sensors, ETW, service/device-state monitoring, predictive analysis, full backup-reference/payload verification, recovery operations, Guardian policies/approvals, and real privileged Action Broker execution are not implemented. The Action Broker is currently simulation-only and is not approved for real Windows-changing operations. SMBIOS, WMI, and Windows recovery providers may expose incomplete or vendor-dependent data; WinForge reports unavailable metadata rather than inventing it.

## Release Candidate status

WinForge is currently in an internal release-candidate state. It is functionally strong, tested, and packageable, but it remains a conservative read-only Windows intelligence application rather than a broad public installer platform. See the release docs for the current capability matrix, deployment notes, and remaining limitations:

- [RELEASE_READINESS.md](./RELEASE_READINESS.md)
- [DEPLOYMENT.md](./DEPLOYMENT.md)
- [TESTING.md](./TESTING.md)
- [SECURITY.md](./SECURITY.md)
- [KNOWN_LIMITATIONS.md](./KNOWN_LIMITATIONS.md)
- [RELEASE_CAPABILITY_MATRIX.md](./RELEASE_CAPABILITY_MATRIX.md)
