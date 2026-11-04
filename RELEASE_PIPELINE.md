# WinForge Release Pipeline

## Overview

The release pipeline is intentionally conservative: it validates version metadata, runs tests, publishes a Release build, creates the bundle, and records a package manifest with SHA-256 hashes.

## Required commands

```powershell
dotnet restore WinForge.sln
dotnet test WinForge.sln --nologo --verbosity minimal
dotnet build WinForge.sln -c Release --nologo --verbosity minimal
powershell -ExecutionPolicy Bypass -File .\scripts\BuildRelease.ps1 -NoZip
```

## Release gate

- tests must pass
- build must be clean
- manifest must be generated
- hashes must be reviewed
- packaging must remain local-first and non-privileged
