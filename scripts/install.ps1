# Builds Spotlight and installs it for the current user (no admin rights needed):
#   %LOCALAPPDATA%\Programs\Spotlight\Spotlight.exe, Start menu shortcut, autostart with Windows.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
param([string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Spotlight'))

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Spotlight\Spotlight.csproj'
$exe = Join-Path $InstallDir 'Spotlight.exe'

function Stop-Spotlight {
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--quit' -Wait -ErrorAction SilentlyContinue }
    for ($i = 0; $i -lt 25; $i++) {
        $running = Get-Process Spotlight -ErrorAction SilentlyContinue
        if (-not $running) { return }
        Start-Sleep -Milliseconds 200
    }
    Get-Process Spotlight -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host 'Stopping running instance...'
Stop-Spotlight

Write-Host 'Building...'
# Program files only (settings, caches and logs live in %LOCALAPPDATA%\Spotlight).
if (Test-Path $InstallDir) { Remove-Item (Join-Path $InstallDir '*') -Recurse -Force }
# Uses the installed .NET desktop runtime: a ~0.5 MB exe instead of ~140 MB with the runtime bundled.
dotnet publish $project -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Write-Host 'Creating Start menu shortcut...'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Spotlight.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $InstallDir
$link.Description = 'Spotlight search (Win+Space)'
$link.Save()

Write-Host 'Starting Spotlight (it registers itself to start with Windows)...'
Start-Process $exe -ArgumentList '--background'
Write-Host "Done. Press Win+Space to search. Installed to $InstallDir"
