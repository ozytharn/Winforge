# WinForge Installation Guide

## Supported environment

- Windows 10 or Windows 11 x64
- .NET 10 runtime, or a self-contained publish bundle
- Standard user account is sufficient for the read-only workflows

## Installation model

WinForge is designed as a local-first desktop application. It stores local evidence under `%LOCALAPPDATA%\WinForge`, not in the installation directory. The project does not install services, drivers, or privileged components.

## Local package installation

1. Extract the release package.
2. Launch `WinForge.exe` from the extracted folder.
3. Accept the first-run guidance if shown.

## Requirements and limitations

- No elevation is required for the standard experience.
- The app reads local system data and reports unavailable or unknown values conservatively.
- It does not modify Windows security settings, firewall, Defender, BitLocker, or recovery configuration.
