# WinForge Upgrade Guide

## Supported upgrades

WinForge supports upgrade paths from previously published internal release builds when the application can open the existing local database and migration logic accepts the schema.

## Upgrade safety rules

- Never silently reset an existing database.
- Preserve local scan history, Guardian samples, and backup/recovery evidence.
- Perform migration and integrity checks before allowing a new version to proceed.
- If the database is incompatible, present a safe recovery path rather than deleting it automatically.

## Recommended procedure

1. Back up the local app data directory.
2. Install the new version.
3. Launch the app.
4. Validate that the existing database opens successfully.
5. Run a fresh inventory scan if needed.

## Recovery for incompatible databases

- Preserve the original database file.
- Move it aside if required.
- Use the migration logic and diagnostics export to identify what failed.
