param(
    [string]$Version = "",
    [string]$ProgramZip = "",
    [string]$SourceZip = "",
    [switch]$SkipReleaseCreate
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$repo = 'OnjLouis/accessible-sensor-readout'

function Fail($message) {
    throw "[publish] $message"
}

function Info($message) {
    Write-Host "[publish] $message"
}

function Get-ReleaseToken {
    if (!([string]::IsNullOrWhiteSpace($env:GH_TOKEN))) {
        return $env:GH_TOKEN.Trim()
    }
    if (!([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))) {
        return $env:GITHUB_TOKEN.Trim()
    }

    $path = $env:CODEX_GITHUB_TOKEN_FILE
    if (![string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
        $token = (Get-Content -LiteralPath $path -Raw).Trim()
        if (!([string]::IsNullOrWhiteSpace($token))) {
            return $token
        }
    }

    return ''
}

function Read-AppVersion {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot 'src\SensorReadoutForm.cs') -Raw
    if ($source -notmatch 'AppVersion\s*=\s*"([^"]+)"') {
        Fail "Could not read AppVersion from src\SensorReadoutForm.cs."
    }
    return $Matches[1]
}

function Read-ChangelogEntry([string]$releaseVersion) {
    $readme = Get-Content -LiteralPath (Join-Path $repoRoot 'README.md') -Raw
    $match = [regex]::Match($readme, '(?ms)^###\s+' + [regex]::Escape($releaseVersion) + '\s*(.*?)(?=^###\s+|\z)')
    if (!$match.Success) {
        Fail "README.md does not contain a changelog section for $releaseVersion."
    }
    return $match.Groups[1].Value.Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-AppVersion
}
$Version = $Version.Trim().TrimStart('v', 'V')

$token = Get-ReleaseToken
if ([string]::IsNullOrWhiteSpace($token)) {
    Fail "No GitHub token found in GH_TOKEN, GITHUB_TOKEN, or CODEX_GITHUB_TOKEN_FILE."
}

if ([string]::IsNullOrWhiteSpace($ProgramZip)) {
    $programDir = if ([string]::IsNullOrWhiteSpace($env:SENSOR_READOUT_PROGRAM_BUILDS)) {
        Join-Path $repoRoot 'release\Program Builds'
    } else {
        $env:SENSOR_READOUT_PROGRAM_BUILDS
    }
    $ProgramZip = Join-Path $programDir "SensorReadout-$Version.zip"
}
if ([string]::IsNullOrWhiteSpace($SourceZip)) {
    $sourceDir = if ([string]::IsNullOrWhiteSpace($env:SENSOR_READOUT_SOURCE_SNAPSHOTS)) {
        Join-Path $repoRoot 'release\Source Snapshots'
    } else {
        $env:SENSOR_READOUT_SOURCE_SNAPSHOTS
    }
    $SourceZip = Join-Path $sourceDir "SensorReadout-source-$Version.zip"
}
if (!(Test-Path -LiteralPath $programZip)) {
    Fail "Program ZIP not found: $ProgramZip"
}
if (!(Test-Path -LiteralPath $sourceZip)) {
    Fail "Source ZIP not found: $SourceZip"
}

$bytes = [System.Text.Encoding]::ASCII.GetBytes("x-access-token:$token")
$basic = [Convert]::ToBase64String($bytes)
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$env:GH_TOKEN = $token
$env:GITHUB_TOKEN = $token

Info "Pushing main non-interactively with token-based Basic auth. Git Credential Manager is disabled for this command."
& git -C $repoRoot -c credential.helper= -c core.askpass= -c http.https://github.com/.extraheader="AUTHORIZATION: Basic $basic" push origin main
if ($LASTEXITCODE -ne 0) {
    Fail "git push failed."
}

if ($SkipReleaseCreate) {
    Info "Skipping GitHub release creation by request."
    return
}

$notesPath = Join-Path $env:TEMP "SensorReadout-$Version-release-notes.md"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($notesPath, (Read-ChangelogEntry $Version), $utf8NoBom)

Info "Creating GitHub release v$Version."
& gh release create "v$Version" $ProgramZip $SourceZip --repo $repo --title "Sensor Readout $Version" --notes-file $notesPath
if ($LASTEXITCODE -ne 0) {
    Fail "GitHub release creation failed."
}
