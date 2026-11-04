# WinForge First Run Guide

## Overview

WinForge inspects local Windows inventory, system health, and recovery state without modifying the machine.

## On first launch

1. The app creates its local SQLite database under `%LOCALAPPDATA%\WinForge` if it does not already exist.
2. It validates local app data access and version metadata.
3. It presents the overview and scan workflows.
4. It allows the user to run a scan and review evidence.

## Important notes

- Empty or missing data is shown as unavailable or unknown, not healthy.
- Local scan results are historical evidence, not proof of causation.
- The app intentionally avoids automatic repairs or privileged actions.
