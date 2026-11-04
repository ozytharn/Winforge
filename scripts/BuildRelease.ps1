param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [string]$Version = "0.9.0",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$allowedRuntimes = @("win-x64", "win-x86", "win-arm64")
if ($Runtime -notin $allowedRuntimes) {
    throw "Unsupported runtime '$Runtime'. Supported values: $($allowedRuntimes -join ', ')"
}

$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root "src\WinForge.App\WinForge.App.csproj"
$artifactsPath = Join-Path $root "artifacts\release"
$publishPath = Join-Path $artifactsPath "WinForge-$Version-$Runtime"
$zipPath = Join-Path $artifactsPath "WinForge-$Version-$Runtime.zip"
$manifestPath = Join-Path $publishPath "artifact-manifest.json"
$summaryPath = Join-Path $publishPath "RELEASE_SUMMARY.txt"

if (-not (Test-Path $projectPath)) {
    throw "WinForge app project was not found at $projectPath"
}

New-Item -ItemType Directory -Path $artifactsPath -Force | Out-Null
if (Test-Path $publishPath) {
    Remove-Item -Recurse -Force $publishPath
}
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

$publishArgs = @(
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $publishPath,
    "-p:UseAppHost=true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=false",
    "-p:ContinuousIntegrationBuild=true",
    "-p:SelfContained=$($SelfContained.ToString().ToLowerInvariant())"
)

Write-Host "Publishing WinForge release build..."
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for WinForge."
}

$requiredFiles = @(
    'WinForge.App.dll',
    'WinForge.App.deps.json',
    'WinForge.App.runtimeconfig.json',
    'README.txt',
    'Install-WinForge.ps1',
    'Uninstall-WinForge.ps1',
    'INSTALLER_MANIFEST.json',
    'artifact-manifest.json'
)

@"
WinForge local release package
============================

This package contains the read-only WinForge desktop application for local Windows system intelligence.

Requirements:
- Windows 11 x64 or supported Windows 10/11 x64 environment with the .NET 10 runtime if not self-contained.
- Local user permissions only; no elevated privileges are required.
- The app reads inventory and telemetry locally and never performs privileged remediations.

Install / deploy:
1. Extract this package to a directory you control, or run scripts\Install-WinForge.ps1 with the package path.
2. Use the generated shortcuts or run WinForge.exe from the installed directory.
3. Keep the local app data under %LOCALAPPDATA%\WinForge and preserve it during upgrade or uninstall.

Notes:
- The application stores local evidence and scan data under %LOCALAPPDATA%\WinForge.
- The Action Broker remains simulation-only and does not execute privileged operations.
- No automated repairs, service installation, or system modifications are performed by WinForge.
"@ | Set-Content -Path (Join-Path $publishPath "README.txt")

Copy-Item -Path (Join-Path $root 'scripts\Install-WinForge.ps1') -Destination (Join-Path $publishPath 'Install-WinForge.ps1') -Force
Copy-Item -Path (Join-Path $root 'scripts\Uninstall-WinForge.ps1') -Destination (Join-Path $publishPath 'Uninstall-WinForge.ps1') -Force

$installerManifest = [ordered]@{
    product = 'WinForge'
    version = $Version
    runtime = $Runtime
    selfContained = [bool]$SelfContained
    installInstructions = 'Run Install-WinForge.ps1 with the package path to install or upgrade WinForge without altering system settings.'
    appDataDirectory = '%LOCALAPPDATA%\\WinForge'
    preserveUserDataOnUninstall = $true
    requiresElevation = $false
}
$installerManifest | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $publishPath 'INSTALLER_MANIFEST.json') -Encoding UTF8

$summary = @(
    'WinForge Release Summary',
    '=======================',
    "Product: WinForge",
    "Version: $Version",
    "Configuration: $Configuration",
    "Runtime: $Runtime",
    "Self-contained: $($SelfContained.ToString().ToLowerInvariant())",
    "Output directory: artifacts/release/WinForge-$Version-$Runtime",
    'This bundle is intended for local-first, read-only Windows evaluation and is not an installer or privileged deployment package.',
    'It does not install services, modify registry settings, or execute privileged remediation flows.'
)
$summary | Set-Content -Path $summaryPath -Encoding UTF8

$publishedFiles = Get-ChildItem -Path $publishPath -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath -and $_.FullName -ne $summaryPath -and $_.Extension -notin @('.pdb','.log','.tmp') } |
    Sort-Object FullName

$manifestEntries = @()
$publishRoot = [System.IO.Path]::GetFullPath($publishPath)
foreach ($file in $publishedFiles) {
    $relativePath = $file.FullName.Substring($publishRoot.Length + 1).Replace('\\', '/')
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
    $manifestEntries += [ordered]@{
        path = $relativePath
        sizeBytes = $file.Length
        sha256 = $hash
    }
}

$manifest = [ordered]@{
    product = 'WinForge'
    version = $Version
    configuration = $Configuration
    runtime = $Runtime
    selfContained = [bool]$SelfContained
    generatedAtUtc = (Get-Date).ToString('o')
    summary = $summary
    files = $manifestEntries
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $manifestPath -Encoding UTF8

foreach ($required in $requiredFiles) {
    if (-not (Test-Path (Join-Path $publishPath $required))) {
        throw "Required file '$required' was not created in the release package."
    }
}

if (-not $NoZip) {
    Write-Host "Compressing WinForge release package..."
    Compress-Archive -Path (Join-Path $publishPath "*") -DestinationPath $zipPath -Force
}

Write-Host "Release package ready: $publishPath"
Write-Host "Artifact manifest: $manifestPath"
Write-Host "Release summary: $summaryPath"
if (-not $NoZip) {
    Write-Host "Archive ready: $zipPath"
}
