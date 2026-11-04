# WinForge Security and Privacy

## Security posture

WinForge is intentionally designed as a local-first, read-only Windows intelligence application. Its operating model is to inspect, summarize, and store evidence without modifying the system.

## Explicitly prohibited behaviors

- No privileged elevation or UAC bypass
- No automatic repair or remediation
- No registry modification
- No firewall, Defender, BitLocker, or startup changes
- No arbitrary shell execution
- No Windows service installation
- No real Action Broker execution outside simulation
- No silent cleanup or automatic deletion of user data

## Data handling

- SQLite data is stored under `%LOCALAPPDATA%\WinForge` and is intended for local evidence retention.
- Windows API and WMI calls are read-only and bounded.
- Missing or inaccessible metadata is reported as unknown, unavailable, or incomplete; it is never treated as healthy.

## Packaging and distribution boundaries

- The published package is a local runtime bundle, not a privileged installer.
- The build intentionally avoids shipping machine-specific secrets, local logs, or developer-only files.
- The release script excludes debug symbols and other non-runtime artifacts from the final packaged payload.

## Threat model summary

The main security concern is overclaiming confidence from incomplete evidence. WinForge avoids making causal or remediation claims from correlations alone. All data collection remains local to the current machine, and the project uses conservative state semantics to limit false reassurance.
