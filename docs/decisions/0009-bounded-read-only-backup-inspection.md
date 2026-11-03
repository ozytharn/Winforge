# ADR 0009: Bounded read-only backup inspection

**Status:** Accepted

## Context

Recovery discovery identifies candidate backup folders but cannot establish whether their metadata is readable or whether the structures match a supported format. Unbounded recursive scans, full image mounting, hashing large containers, or treating folder presence as recoverability would create privacy, performance, and data-integrity risks.

## Decision

- Keep discovery bounded to the fixed `WindowsImageBackup` and `FileHistory` folder names at ready mounted-volume roots; do not recursively search every volume.
- Inspect only a candidate surfaced by discovery and explicitly selected by the user.
- Support bounded XML well-formedness checks and fixed VHD/VHDX signature reads for Windows Image Backup, plus bounded configuration XML parsing for File History.
- Reject UNC paths and reparse points; cap traversal at 256 root entries, 32 groups, 64 backup sets, and 64 entries per group/set, and bound metadata size, history length, and persisted evidence.
- Record every performed or skipped check, the source, timestamp, result state, and limitations.
- Persist inspection metadata/history in additive SQLite migration 4 while leaving migrations 1-3 unchanged.
- Define `Verified` as passing the checks actually performed, never as proof of full backup integrity, completeness, or restoreability.
- Keep all backup operations read-only; do not mount, modify, create, delete, rename, move, overwrite, or execute backup payloads.

## Consequences

- Users can inspect a small, explainable metadata surface and compare inspection history without risking backup contents.
- Windows Image Backup manifest references, complete catalog consistency, VHD/VHDX payloads and chains are not fully validated.
- File History payload/index integrity is unsupported.
- Passing metadata and signature checks cannot certify that a backup can be restored; full format validation and restore testing require separate designs and security review.
