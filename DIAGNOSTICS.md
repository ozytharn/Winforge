# WinForge Diagnostics

## Application diagnostics

WinForge includes a local application health service that checks:

- application version
- runtime availability
- application data directory
- database accessibility
- compatibility metadata and local diagnostics state

## Diagnostic export

The diagnostic export service creates a privacy-conscious report that includes:

- app version and runtime data
- local data directory information
- database metadata and health state
- standard user-visible system constraints

The export intentionally omits secrets, private files, and unrelated filesystem content.
