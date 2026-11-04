# WinForge Testing

## Commands run for validation

```powershell
cd "C:\Users\Abhinav Kumar\Desktop\projects\winforge"
dotnet test WinForge.sln --nologo --verbosity minimal
```

Result:

- Unit tests: 103 passed, 0 failed
- Integration tests: 51 passed, 0 failed
- Total: 154 passed

```powershell
dotnet build WinForge.sln -c Release --nologo --verbosity minimal
```

Result:

- 0 warnings
- 0 errors

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\BuildRelease.ps1 -NoZip
```

Result:

- Release package produced successfully under `artifacts\release\WinForge-0.9.0-win-x64`
- Manifest and summary files generated
- Publish step succeeded without runtime or dependency errors

## Coverage emphasis

The project includes deterministic tests for:

- Storage and driver health
- Security/privacy assessment states
- Unknown and unavailable observations
- Partial inventory and failed scans
- SQLite migration compatibility
- Guardian telemetry retention
- Configuration drift and digital twin comparisons
- Action Broker V2 lifecycle and validation behavior

## Additional validation note

The repository includes a Windows-targeted integration test suite that performs read-only inventory collection. Those tests are valuable, but they are not a substitute for full external validation on a clean public-release target machine.
