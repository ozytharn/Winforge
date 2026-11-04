WinForge local release package
============================

This package contains the read-only WinForge desktop application for local Windows system intelligence.

Requirements:
- Windows 11 x64 or supported Windows 10/11 x64 environment with the .NET 10 runtime if not self-contained.
- Local user permissions only; no elevated privileges are required.
- The app reads inventory and telemetry locally and never performs privileged remediations.

Install / deploy:
1. Extract this package to a directory you control, or run scripts\Install-WinForge.ps1 with the package path.
2. Use the generated shortcuts or run WinForge.exe from the installed directory.
3. Keep the local app data under %LOCALAPPDATA%\WinForge and preserve it during upgrade or uninstall.

Notes:
- The application stores local evidence and scan data under %LOCALAPPDATA%\WinForge.
- The Action Broker remains simulation-only and does not execute privileged operations.
- No automated repairs, service installation, or system modifications are performed by WinForge.
