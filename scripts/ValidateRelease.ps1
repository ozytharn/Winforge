param(
    [string]$Version = '0.9.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host 'Running WinForge release validation...'
$testResult = & dotnet test "$root\WinForge.sln" -c Release --nologo --logger 'console;verbosity=minimal'
if ($LASTEXITCODE -ne 0) {
    throw 'Solution tests failed during release validation.'
}

$buildResult = & dotnet build "$root\WinForge.sln" -c Release --nologo -p:WarningLevel=0
if ($LASTEXITCODE -ne 0) {
    throw 'Release build failed during validation.'
}

& powershell -ExecutionPolicy Bypass -File "$root\scripts\BuildRelease.ps1" -Version $Version
if ($LASTEXITCODE -ne 0) {
    throw 'Release packaging failed during validation.'
}

Write-Host 'WinForge release validation succeeded.'
