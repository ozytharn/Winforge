# WinForge Deployment Guide

## Mission

WinForge is a local-first Windows system intelligence application designed to inspect the current PC without modifying it. It is intended for diagnostic, inventory, and evidence review workflows, not real remediation.

## Supported environment

- Windows 10 or Windows 11 x64 is the intended target.
- .NET 10 SDK is required to build from source.
- The app can run from a published release folder on a supported Windows machine.
- No elevation is required for the standard read-only workflows.

## Build from source

```powershell
dotnet restore WinForge.sln
dotnet build WinForge.sln -c Release
dotnet test WinForge.sln --nologo --verbosity minimal
```

## Publish a release package

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\BuildRelease.ps1 -Configuration Release -Runtime win-x64 -Version 0.9.0
```

Optional no-zip output:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\BuildRelease.ps1 -Configuration Release -Runtime win-x64 -Version 0.9.0 -NoZip
```

## Published output

The script writes to:

```text
artifacts\release\WinForge-<version>-<runtime>\
```

The payload contains a release summary, a README, and a `artifact-manifest.json` with relative file paths, sizes, and SHA-256 hashes. The zip archive is generated alongside the publish folder unless `-NoZip` is specified.

## First run behavior

- WinForge initializes a local SQLite database in `%LOCALAPPDATA%\WinForge\winforge.db` if needed.
- Missing or inaccessible data sources are reported as unavailable or unknown rather than as a healthy state.
- The application is read-only and does not change Windows settings, services, firewall state, registry entries, or security posture.

## Known deployment limitations

- This is not a signed MSI or enterprise installer workflow.
- This is not a broad public distribution platform yet; it is an internal release-candidate package.
- Clean-machine validation has not been independently verified on a full external test matrix.
- Unsupported Windows APIs and missing hardware metadata are reported conservatively rather than assumed.
