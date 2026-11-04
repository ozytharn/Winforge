param(
    [string]$InstallRoot,
    [switch]$RemoveUserData
)

$ErrorActionPreference = "Stop"

if (-not $InstallRoot) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA 'WinForge'
    if (-not (Test-Path $InstallRoot)) {
        $InstallRoot = 'C:\Program Files\WinForge'
    }
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
if (-not (Test-Path $InstallRoot)) {
    throw "WinForge install directory '$InstallRoot' was not found. Nothing was removed."
}

$desktopShortcut = Join-Path $env:USERPROFILE 'Desktop\WinForge.lnk'
if (Test-Path $desktopShortcut) { Remove-Item -Path $desktopShortcut -Force }

$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinForge\WinForge.lnk'
if (Test-Path $startMenuShortcut) { Remove-Item -Path $startMenuShortcut -Force }

Remove-Item -Path $InstallRoot -Recurse -Force

if ($RemoveUserData) {
    $userData = Join-Path $env:LOCALAPPDATA 'WinForge'
    if (Test-Path $userData) {
        Remove-Item -Path $userData -Recurse -Force
        Write-Host "Removed user data from $userData."
    }
}
else {
    Write-Host "User data was preserved under $env:LOCALAPPDATA\WinForge."
}

Write-Host "WinForge uninstall completed."
