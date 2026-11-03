# ADR 0001: Initial read-only PC Passport slice

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

The workspace contained the coordinated WinForge specifications but no application source. The architecture calls for a local-first WPF application, clear domain/application/infrastructure boundaries, SQLite migrations, and real PC Passport data before any optimization or privileged operation. It also cautions that WMI providers can be incomplete and should not be the only source for critical inventory.

## Decision

- Establish a .NET 10 WPF solution with separate App, Domain, Application, and Infrastructure projects, plus unit and integration test projects. Add only boundaries with working responsibilities; defer empty Core, Contracts, Action Broker, and feature projects.
- Use `Microsoft.Data.Sqlite` for the local database and the SQLite online backup API for consistent backups. Store user data under the current user's Local Application Data directory.
- Start the read-only inventory slice with individually isolated WMI collectors for OS, processors, physical-memory modules, and WMI-visible PnP devices. Persist provenance, observation time, stable type/identity pairs, scan coverage, and collector failures.
- Keep system-changing actions, administrative execution, and claims of complete device/driver coverage out of this slice. Add SetupAPI and Configuration Manager collectors as later Passport work.

## Alternatives considered

- Create every project listed in the full architecture immediately: rejected because the architecture explicitly says not to create empty projects.
- Use WMI as the complete device inventory: rejected because the Windows API specification warns that WMI is supplemental and provider-dependent.
- Start device discovery with SetupAPI: deferred; it is the documented authoritative direction for device properties but is a larger native interop boundary than needed for the initial working slice.
- Copy the SQLite database file for backups: rejected because an active SQLite database may have WAL data; use the SQLite backup API and verify the resulting database.

## Consequences

- The first screen reports the collector source, scan status, attempted-collector coverage, errors, and observation timestamps. A successful WMI query is only a successful collection of that WMI scope, not a guarantee of full hardware coverage.
- Database observations preserve earlier known records when later scans fail or are partial.
- Future SetupAPI collectors can supplement or replace fields without coupling the UI to Windows APIs.
- System.Management, Microsoft.Data.Sqlite, and Microsoft.Extensions.Hosting are explicit NuGet dependencies; SQLite does not need to be installed globally.

## Security and compatibility

- Initial collectors are read-only and run in the interactive user's process; no elevated broker or privileged changes are introduced.
- WMI queries are bounded by a 15-second provider timeout and cancellation is checked between returned records. A synchronous provider call may take until it returns or times out.
- Hardware identifiers remain local in the per-user database. The application does not send inventory to external services.
