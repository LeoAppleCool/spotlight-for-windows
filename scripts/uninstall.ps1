# Removes Spotlight: stops it, removes autostart, Start menu shortcut, program files and (unless -KeepData) its data.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 [-KeepData]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Spotlight'),
    [switch]$KeepData
)

$exe = Join-Path $InstallDir 'Spotlight.exe'
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--quit' -Wait -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800
Get-Process Spotlight -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Spotlight -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name Spotlight -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Spotlight' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Spotlight.lnk') -ErrorAction SilentlyContinue
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
if (-not $KeepData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'Spotlight') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Spotlight was removed.'
