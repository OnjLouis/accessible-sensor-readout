param(
    [switch]$SkipDotNet,
    [switch]$SkipPawnIO,
    [switch]$IncludeLibreHardwareMonitor
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

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
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed while installing $Name. Exit code: $LASTEXITCODE"
    }
}

function Install-ChocolateyPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    Write-Host "Installing $Name ($Id) with Chocolatey ..."
    choco install $Id -y
    if ($LASTEXITCODE -ne 0) {
        throw "Chocolatey failed while installing $Name. Exit code: $LASTEXITCODE"
    }
}

function Install-PawnIODirect {
    Write-Host 'winget and Chocolatey were not found.'
    Write-Host 'Downloading the latest PawnIO setup directly from the official GitHub releases...'
    $headers = @{ 'User-Agent' = 'Sensor Readout prerequisite installer' }
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest' -Headers $headers -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match '^PawnIO.*setup.*\.exe$' -or $_.name -eq 'PawnIO_setup.exe' } | Select-Object -First 1
    if (-not $asset -or [string]::IsNullOrWhiteSpace($asset.browser_download_url)) {
        throw 'Could not find PawnIO_setup.exe in the latest PawnIO.Setup GitHub release. Open https://github.com/namazso/PawnIO.Setup/releases and install it manually.'
    }

    $downloadPath = Join-Path $env:TEMP ('PawnIO_setup_' + [guid]::NewGuid().ToString('N') + '.exe')
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $downloadPath -UseBasicParsing
    Write-Host 'Installing PawnIO silently...'
    $process = Start-Process -FilePath $downloadPath -ArgumentList @('-install', '-silent') -Wait -PassThru
    Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
    if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
        throw "PawnIO setup failed. Exit code: $($process.ExitCode)"
    }

    if ($process.ExitCode -eq 3010) {
        Write-Host 'PawnIO installed. A restart may be required.'
    }
}

function Install-PawnIO {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Install-WingetPackage -Id 'namazso.PawnIO' -Name 'PawnIO driver'
        return
    }

    if (Get-Command choco -ErrorAction SilentlyContinue) {
        Install-ChocolateyPackage -Id 'pawnio' -Name 'PawnIO driver'
        return
    }

    Install-PawnIODirect
}

if (-not $SkipDotNet) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Install-WingetPackage -Id 'Microsoft.DotNet.Framework.Runtime' -Name '.NET Framework Runtime'
    } else {
        Write-Host 'winget was not found, so .NET Framework Runtime cannot be installed automatically by this script.'
        Write-Host 'If Sensor Readout already opens, .NET Framework is already present.'
    }
}

if (-not $SkipPawnIO) {
    Install-PawnIO
}

if ($IncludeLibreHardwareMonitor) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Install-WingetPackage -Id 'LibreHardwareMonitor.LibreHardwareMonitor' -Name 'LibreHardwareMonitor'
    } else {
        Write-Host 'winget was not found, so LibreHardwareMonitor cannot be installed automatically by this script.'
        Write-Host 'Manual download: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases'
    }
}

Write-Host ''
Write-Host 'Done. Start Sensor Readout as administrator so it can read motherboard sensors and control supported fans.'
