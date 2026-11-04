param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$InstallRoot,
    [switch]$UseLocalAppData,
    [switch]$SkipDesktopShortcut,
    [switch]$SkipStartMenuShortcut
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -Path $PackagePath)) {
    throw "WinForge package path '$PackagePath' does not exist. Provide a built release folder or archive extraction directory."
}

$root = Split-Path -Parent $PSScriptRoot
$resolvedPackagePath = (Resolve-Path -Path $PackagePath).Path

if (-not $InstallRoot) {
    $windowsPrincipal = New-Object 'System.Security.Principal.WindowsPrincipal' ([System.Security.Principal.WindowsIdentity]::GetCurrent())
    $isAdmin = $windowsPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $InstallRoot = if ($UseLocalAppData -or -not $isAdmin) {
        Join-Path $env:LOCALAPPDATA 'WinForge'
    }
    else {
        'C:\Program Files\WinForge'
    }
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
if (-not (Test-Path -Path $InstallRoot)) {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
}

$sourceRoot = if (Test-Path (Join-Path $resolvedPackagePath 'WinForge.App.exe')) {
    $resolvedPackagePath
} elseif (Test-Path (Join-Path $resolvedPackagePath 'WinForge.App.dll')) {
    $resolvedPackagePath
} else {
    $resolvedPackagePath
}

Write-Host "Installing WinForge to $InstallRoot"
Get-ChildItem -Path $sourceRoot -Force | Copy-Item -Destination $InstallRoot -Recurse -Force

$launchPath = Join-Path $InstallRoot 'WinForge.App.exe'
if (-not (Test-Path $launchPath)) {
    $launchPath = Join-Path $InstallRoot 'WinForge.App.dll'
}

$appDataDirectory = Join-Path $env:LOCALAPPDATA 'WinForge'
if (-not (Test-Path $appDataDirectory)) {
    New-Item -ItemType Directory -Path $appDataDirectory -Force | Out-Null
}

$installManifest = [ordered]@{
    product = 'WinForge'
    installRoot = $InstallRoot
    packagePath = $resolvedPackagePath
    installDateUtc = (Get-Date).ToString('o')
    appDataDirectory = $appDataDirectory
    launchPath = $launchPath
    requiresElevation = $false
}
$installManifestFile = Join-Path $InstallRoot 'INSTALLATION_MANIFEST.json'
$installManifest | ConvertTo-Json -Depth 10 | Set-Content -Path $installManifestFile -Encoding UTF8

if (-not $SkipDesktopShortcut) {
    $desktopPath = Join-Path $env:USERPROFILE 'Desktop'
    if (Test-Path $desktopPath) {
        $shortcutPath = Join-Path $desktopPath 'WinForge.lnk'
        $WshShell = New-Object -ComObject WScript.Shell
        $shortcut = $WshShell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $launchPath
        $shortcut.WorkingDirectory = $InstallRoot
        $shortcut.IconLocation = $launchPath + ',0'
        $shortcut.Save()
    }
}

if (-not $SkipStartMenuShortcut) {
    $startMenuRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinForge'
    New-Item -ItemType Directory -Path $startMenuRoot -Force | Out-Null
    $shortcutPath = Join-Path $startMenuRoot 'WinForge.lnk'
    $WshShell = New-Object -ComObject WScript.Shell
    $shortcut = $WshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $launchPath
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.IconLocation = $launchPath + ',0'
    $shortcut.Save()
}

Write-Host "WinForge installation completed. User data stays under $appDataDirectory."
Write-Host "Launch: $launchPath"
