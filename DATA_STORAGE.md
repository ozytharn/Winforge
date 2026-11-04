# WinForge Data Storage

## Local data locations

WinForge stores local operational and evidence data under `%LOCALAPPDATA%\WinForge`.

## Included data

- `winforge.db` for SQLite inventory and evidence storage
- `settings.json` for persisted local preferences
- support exports and diagnostic reports
- transient application state files

## Excluded data

- passwords
- tokens
- private documents
- arbitrary system files
- remote upload payloads

## Security guidance

The local data directory is intended for local diagnostics and evidence review. WinForge does not automatically upload or share this data.
