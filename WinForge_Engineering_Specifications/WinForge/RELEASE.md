# WinForge — Build, Installer and Release Specification

**Version:** 1.0  
**Status:** Initial release requirements

---

# 1. Purpose

This document defines how WinForge is built, packaged, installed, updated, repaired and uninstalled.

# 2. Supported Platform

Initial target:
- Windows 11 x64.

Do not claim support for every Windows 11 build without testing.

Document:
- Minimum supported build.
- Tested builds.
- Required .NET runtime or self-contained deployment choice.
- Required Windows components.
- Known unsupported configurations.

ARM64 support should be treated as a separate compatibility milestone.

# 3. Build Configuration

Maintain:
- Debug.
- Release.
- Automated build validation.
- Reproducible dependency restore where practical.
- Versioned artifacts.
- Build metadata.
- Test reports.

Do not include development secrets in build output.

# 4. Installer Requirements

The installer must:
- Identify publisher and product version.
- Verify package integrity.
- Request elevation only for operations that require it.
- Install services with explicit service configuration and ACLs.
- Create required directories with restrictive permissions.
- Preserve user data during upgrades.
- Support clean uninstall.
- Report errors accurately.
- Avoid bundling unrelated software.
- Avoid telemetry or promotional components.

# 5. Service Installation

For each Windows service, document:
- Service name.
- Display name.
- Executable path.
- Service account.
- Start type.
- Required privileges.
- Service ACL.
- Recovery behaviour.
- Dependencies.
- Upgrade procedure.
- Uninstall procedure.

Do not run a service as LocalSystem by default. Justify the chosen identity and privileges.

# 6. Code Signing

Release executables and installers should be signed with a trusted code-signing certificate.

Verify:
- Signature validity.
- Expected publisher identity.
- Timestamp.
- Certificate chain.
- Artifact hash.

Do not claim that a file is signed until the signature has been checked.

Protect signing keys outside the source repository and ordinary development logs.

# 7. Update Architecture

The updater must:
- Retrieve update metadata through authenticated transport.
- Verify metadata authenticity.
- Verify package signature and hash.
- Check version and compatibility.
- Reject corrupted or unexpected packages.
- Preserve user data.
- Handle interrupted updates.
- Record update history.
- Support a documented recovery path.

Do not execute update scripts downloaded from arbitrary sources.

# 8. Database Upgrades

Before upgrade:
- Detect database schema version.
- Validate database availability.
- Create a consistent backup.
- Apply migrations transactionally where possible.
- Verify integrity.
- Preserve recovery records.

If migration fails, preserve the previous database and provide a recovery path.

# 9. Uninstallation

Uninstall must:
- Stop WinForge services safely.
- Remove WinForge binaries and service registrations.
- Preserve user data by default.
- Explain what will be retained or removed.
- Never remove third-party software.
- Never remove user backups without explicit selection.
- Avoid deleting shared runtimes required by other applications.

# 10. Release Channels

Possible channels:
- Stable.
- Preview.
- Development.

Do not expose preview builds as stable releases.

Display channel and version clearly.

# 11. Release Checklist

- [ ] Supported Windows builds tested.
- [ ] Release build succeeds.
- [ ] Automated tests pass.
- [ ] Security tests pass.
- [ ] Installer tested on clean VM.
- [ ] Upgrade tested.
- [ ] Interrupted upgrade tested.
- [ ] Uninstall tested.
- [ ] Database migration tested.
- [ ] Recovery path tested.
- [ ] Signatures verified.
- [ ] Dependencies reviewed.
- [ ] Release notes prepared.
- [ ] Known issues documented.
- [ ] No secrets included.
- [ ] User data preservation verified.

# 12. Versioning

Use semantic versioning where suitable:
- MAJOR for incompatible public changes.
- MINOR for backward-compatible functionality.
- PATCH for backward-compatible fixes.

Keep database schema version separate from application version.

# 13. Release Notes

Each release must include:
- New functionality.
- Improvements.
- Fixes.
- Security changes.
- Database changes.
- Compatibility changes.
- Known issues.
- Recovery or upgrade instructions.
