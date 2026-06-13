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

function Test-DotNetFramework48Installed {
    try {
        $release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop
        return ($release -ge 528040)
    } catch {
        return $false
    }
}

function Test-PawnIOInstalled {
    try {
        return (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO')
    } catch {
        return $false
    }
}

function Test-UninstallEntryInstalled {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$NamePatterns
    )

    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($key in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
            try {
                $displayName = (Get-ItemProperty -LiteralPath $key.PSPath -Name DisplayName -ErrorAction Stop).DisplayName
                if (-not $displayName -or $displayName.Trim().Length -eq 0) {
                    continue
                }

                foreach ($pattern in $NamePatterns) {
                    if ($displayName -like $pattern) {
                        return $true
                    }
                }
            } catch {
                continue
            }
        }
    }

    return $false
}

function Test-LibreHardwareMonitorInstalled {
    return Test-UninstallEntryInstalled -NamePatterns @('*LibreHardwareMonitor*', '*Libre Hardware Monitor*')
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

    Write-Host "Installing only if needed: $Name ($Id) ..."
    $listOutput = $null
    try {
        $listOutput = winget list --id $Id -e --source winget 2>$null
    } catch {
        $listOutput = $null
    }

    if ($LASTEXITCODE -eq 0 -and $listOutput -and ($listOutput -match [regex]::Escape($Id))) {
        Write-Host "$Name already appears to be installed. Skipping download."
        return
    }

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

    $installed = choco list --local-only --exact $Id 2>$null
    if ($LASTEXITCODE -eq 0 -and $installed -and ($installed -match ("^" + [regex]::Escape($Id) + "\s"))) {
        Write-Host "$Name already appears to be installed. Skipping download."
        return
    }

    Write-Host "Installing $Name ($Id) with Chocolatey ..."
    choco install $Id -y
    if ($LASTEXITCODE -ne 0) {
        throw "Chocolatey failed while installing $Name. Exit code: $LASTEXITCODE"
    }
}

function Install-PawnIODirect {
    if (Test-PawnIOInstalled) {
        Write-Host 'PawnIO driver already appears to be installed. Skipping download.'
        return
    }

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
    if (Test-PawnIOInstalled) {
        Write-Host 'PawnIO driver already appears to be installed. Skipping download.'
        return
    }

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
    if (Test-DotNetFramework48Installed) {
        Write-Host '.NET Framework 4.8 or newer already appears to be installed. Skipping download.'
    } elseif (Get-Command winget -ErrorAction SilentlyContinue) {
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
    if (Test-LibreHardwareMonitorInstalled) {
        Write-Host 'LibreHardwareMonitor already appears to be installed. Skipping download.'
    } elseif (Get-Command winget -ErrorAction SilentlyContinue) {
        Install-WingetPackage -Id 'LibreHardwareMonitor.LibreHardwareMonitor' -Name 'LibreHardwareMonitor'
    } else {
        Write-Host 'winget was not found, so LibreHardwareMonitor cannot be installed automatically by this script.'
        Write-Host 'Manual download: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases'
    }
}

Write-Host ''
Write-Host 'Done. Start Sensor Readout as administrator so it can read motherboard sensors and control supported fans.'
