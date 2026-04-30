param(
    [switch]$SkipDotNet,
    [switch]$SkipFanControl,
    [switch]$SkipLibreHardwareMonitor
)

$ErrorActionPreference = 'Stop'

function Install-WingetPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    Write-Host "Installing $Name ($Id) ..."
    winget install --id $Id -e --source winget --accept-source-agreements --accept-package-agreements
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget was not found. Install or update App Installer from the Microsoft Store, then run this script again."
}

if (-not $SkipDotNet) {
    Install-WingetPackage -Id 'Microsoft.DotNet.Framework.Runtime' -Name '.NET Framework Runtime'
}

if (-not $SkipFanControl) {
    Install-WingetPackage -Id 'Rem0o.FanControl' -Name 'FanControl'
}

if (-not $SkipLibreHardwareMonitor) {
    Install-WingetPackage -Id 'LibreHardwareMonitor.LibreHardwareMonitor' -Name 'LibreHardwareMonitor'
}

Write-Host ''
Write-Host 'Done. Start FanControl once and complete its first-run hardware setup before using AccessibleSensorReadout.'
