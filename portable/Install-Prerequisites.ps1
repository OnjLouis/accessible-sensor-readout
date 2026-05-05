param(
    [switch]$SkipDotNet,
    [switch]$SkipPawnIO,
    [switch]$IncludeLibreHardwareMonitor
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        "`"$PSCommandPath`""
    )

    if ($SkipDotNet) { $arguments += '-SkipDotNet' }
    if ($SkipPawnIO) { $arguments += '-SkipPawnIO' }
    if ($IncludeLibreHardwareMonitor) { $arguments += '-IncludeLibreHardwareMonitor' }

    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs
    exit
}

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

if (-not $SkipPawnIO) {
    Install-WingetPackage -Id 'namazso.PawnIO' -Name 'PawnIO driver'
}

if ($IncludeLibreHardwareMonitor) {
    Install-WingetPackage -Id 'LibreHardwareMonitor.LibreHardwareMonitor' -Name 'LibreHardwareMonitor'
}

Write-Host ''
Write-Host 'Done. Start Sensor Readout as administrator so it can read motherboard sensors and control supported fans.'
