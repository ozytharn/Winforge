# WinForge Update Model

## Model

The current update model is manual and package-driven. It does not automatically download or execute arbitrary update payloads.

## Safety rules

- versions are surfaced explicitly
- package integrity is verified via SHA-256
- signed updates are supported by policy, but unsigned internal builds are still valid for internal review
- database preservation is prioritized
- incompatible local databases are not silently reset
