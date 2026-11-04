# WinForge Uninstall Guide

## Default behavior

WinForge is a local app that stores data under `%LOCALAPPDATA%\WinForge` and does not install system services or privileged components.

## Uninstall behavior

- Remove the app binaries from the chosen installation directory.
- Leave user data in place by default unless the user explicitly chooses to delete it.
- If the application is removed manually, preserve any local evidence and reports for a later recovery or support review.

## Data preservation recommendation

Prefer preserving user data during an uninstall so that historical evidence remains available. Deleting data should always be explicit and confirmable.
