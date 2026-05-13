param(
    [switch]$SkipBuild,
    [switch]$SkipSelfTest,
    [switch]$SkipUpgradeSmoke,
    [switch]$SkipPostPublishUpdateSmoke,
    [switch]$RunPostPublishUpdateSmoke,
    [switch]$KeepSmokeFolder,
    [string]$Version = "",
    [string]$PreviousVersion = "",
    [string]$SmokeRoot = "D:\projects\Codex\sensor-readout\release-smoke"
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$portable = Join-Path $repoRoot 'portable'
$buildScript = Join-Path $repoRoot 'Build.ps1'
$programBuilds = 'D:\Dropbox\backups\SensorReadout\Program Builds'
$sourceSnapshots = 'D:\Dropbox\backups\SensorReadout\Source Snapshots'

function Fail($message) {
    throw "[release-check] $message"
}

function Info($message) {
    Write-Host "[release-check] $message"
}

function Read-AppVersion {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot 'src\SensorReadoutForm.cs') -Raw
    if ($source -notmatch 'AppVersion\s*=\s*"([^"]+)"') {
        Fail "Could not read AppVersion from src\SensorReadoutForm.cs."
    }
    return $Matches[1]
}

function Read-AssemblyFileVersion {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot 'src\AssemblyInfo.cs') -Raw
    if ($source -notmatch 'AssemblyFileVersion\("([^"]+)"\)') {
        Fail "Could not read AssemblyFileVersion from src\AssemblyInfo.cs."
    }
    return $Matches[1]
}

function Read-ChangelogEntry([string]$releaseVersion) {
    $readme = Get-Content -LiteralPath (Join-Path $repoRoot 'README.md') -Raw
    $pattern = '(?ms)^###\s+' + [regex]::Escape($releaseVersion) + '\s*(.*?)(?=^###\s+|\z)'
    $match = [regex]::Match($readme, $pattern)
    if (!$match.Success) {
        Fail "README.md does not contain a changelog section for $releaseVersion."
    }
    return $match.Groups[1].Value.Trim()
}

function Assert-VersionConsistency([string]$releaseVersion) {
    Info "Checking version consistency for $releaseVersion."
    $assembly = Read-AssemblyFileVersion
    if ($assembly -ne "$releaseVersion.0") {
        Fail "Assembly file version is $assembly, expected $releaseVersion.0."
    }

    $readme = Get-Content -LiteralPath (Join-Path $repoRoot 'README.md') -Raw
    if ($readme -notmatch "Current version:\s+$([regex]::Escape($releaseVersion))\.") {
        Fail "README current version does not match $releaseVersion."
    }

    foreach ($manual in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Docs') -Filter 'README-*.html') {
        $text = Get-Content -LiteralPath $manual.FullName -Raw
        if ($text -notmatch [regex]::Escape($releaseVersion)) {
            Fail "$($manual.Name) does not mention $releaseVersion."
        }
    }

    foreach ($manual in Get-ChildItem -LiteralPath (Join-Path $portable 'Docs') -Filter 'README-*.html') {
        $text = Get-Content -LiteralPath $manual.FullName -Raw
        if ($text -notmatch [regex]::Escape($releaseVersion)) {
            Fail "portable Docs $($manual.Name) does not mention $releaseVersion."
        }
    }

    [void](Read-ChangelogEntry $releaseVersion)
}

function Assert-ChangelogClean([string]$releaseVersion) {
    Info "Checking changelog wording."
    $entry = Read-ChangelogEntry $releaseVersion
    $forbidden = @(
        'STAFFORDSHIRE',
        'MERJILLE',
        'self-test',
        'internal',
        'behind the scenes',
        'debug-only',
        'test-only'
    )
    foreach ($term in $forbidden) {
        if ($entry.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Fail "Changelog for $releaseVersion contains forbidden/private wording: $term"
        }
    }
}

function Assert-GitHubIssuesChecked {
    Info "Checking open GitHub issues."
    try {
        $issues = Invoke-RestMethod -Uri 'https://api.github.com/repos/OnjLouis/accessible-sensor-readout/issues?state=open&per_page=100' -Headers @{ 'User-Agent' = 'Sensor Readout release check' }
        $realIssues = @($issues | Where-Object { -not $_.pull_request })
        if ($realIssues.Count -gt 0) {
            $summary = ($realIssues | ForEach-Object { "#$($_.number) $($_.title)" }) -join '; '
            Fail "Open GitHub issues need review before release: $summary"
        }
        Info "No open GitHub issues."
    } catch {
        Fail "Could not check GitHub issues: $($_.Exception.Message)"
    }
}

function Assert-LanguageParity {
    Info "Checking language key parity."
    $english = Get-Content -LiteralPath (Join-Path $portable 'Langs\English.txt') |
        Where-Object { $_ -match '^[^#=]+=' } |
        ForEach-Object { ($_ -split '=', 2)[0] }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $portable 'Langs') -Filter '*.txt') {
        $keys = Get-Content -LiteralPath $file.FullName |
            Where-Object { $_ -match '^[^#=]+=' } |
            ForEach-Object { ($_ -split '=', 2)[0] }
        $missing = @($english | Where-Object { $_ -notin $keys })
        if ($missing.Count -gt 0) {
            Fail "$($file.Name) is missing language keys: $($missing -join ', ')"
        }
    }
}

function Assert-NoNestedPortableFolders {
    Info "Checking portable folder shape."
    $bad = @()
    foreach ($name in @('Langs','Docs','Sounds','Data','Config','Logs','Reports','Plug-Ins')) {
        $nested = Join-Path (Join-Path $portable $name) $name
        if (Test-Path -LiteralPath $nested) {
            $bad += $nested
        }
    }
    if ($bad.Count -gt 0) {
        Fail "Nested duplicate portable folders found: $($bad -join ', ')"
    }
}

function Invoke-GitDiffCheck {
    Info "Running git diff --check."
    & git -C $repoRoot diff --check
    if ($LASTEXITCODE -ne 0) {
        Fail "git diff --check failed."
    }
}

function Invoke-CommandLineReportSmoke([string]$releaseVersion) {
    Info "Running command-line TXT/HTML report smoke."
    $reportDir = Join-Path $SmokeRoot 'reports'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $txt = Join-Path $reportDir "SensorReadout-smoke-$releaseVersion.txt"
    $html = Join-Path $reportDir "SensorReadout-smoke-$releaseVersion.html"
    Remove-Item -LiteralPath $txt,$html -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $portable 'Sensor Readout.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--report-txt', $txt) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $txt)) {
        Fail "TXT report smoke failed."
    }
    $p = Start-Process -FilePath $exe -ArgumentList @('--report-html', $html) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $html)) {
        Fail "HTML report smoke failed."
    }

    $txtText = Get-Content -LiteralPath $txt -Raw
    if ($txtText -notmatch '\[SensorReadoutReportData\]') {
        Fail "TXT smoke report is missing labelled internal report data."
    }
    if ($txtText -match '(?m)^.{600,}$') {
        Fail "TXT smoke report contains an unexpectedly long line."
    }
    $htmlText = Get-Content -LiteralPath $html -Raw
    if ($htmlText -notmatch 'id="sensor-readout-report-data"') {
        Fail "HTML smoke report is missing structured report data."
    }
}

function New-ReleaseZip([string]$releaseVersion) {
    Info "Creating release ZIP."
    New-Item -ItemType Directory -Force -Path $programBuilds | Out-Null
    $zipPath = Join-Path $programBuilds "SensorReadout-$releaseVersion.zip"
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

    $stage = Join-Path $SmokeRoot "package-stage-$releaseVersion"
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    robocopy $portable $stage /E /XD Config Logs Reports 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
    if ($LASTEXITCODE -ge 8) {
        Fail "Could not stage release ZIP. Robocopy exit code $LASTEXITCODE."
    }

    foreach ($folder in @('Config','Logs','Reports')) {
        if (Test-Path -LiteralPath (Join-Path $stage $folder)) {
            Fail "Release staging unexpectedly contains $folder."
        }
    }

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
    Assert-ReleaseZipContents $zipPath
    return $zipPath
}

function Assert-ReleaseZipContents([string]$zipPath) {
    Info "Inspecting release ZIP contents."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($prefix in @('Config/','Logs/','Reports/','Update Backups/','Update Temp/')) {
            if ($entries | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }) {
                Fail "Release ZIP contains excluded path $prefix."
            }
        }
        foreach ($nested in @('Langs/Langs/','Docs/Docs/','Sounds/Sounds/','Data/Data/','Plug-Ins/Plug-Ins/')) {
            if ($entries | Where-Object { $_.StartsWith($nested, [StringComparison]::OrdinalIgnoreCase) }) {
                Fail "Release ZIP contains nested duplicate path $nested."
            }
        }
        if (-not ($entries -contains 'Sensor Readout.exe')) {
            Fail "Release ZIP does not contain Sensor Readout.exe at the root."
        }
    } finally {
        $zip.Dispose()
    }
}

function Resolve-PreviousVersion([string]$releaseVersion) {
    if (!([string]::IsNullOrWhiteSpace($PreviousVersion))) {
        return $PreviousVersion.Trim().TrimStart('v','V')
    }

    $tags = @(& git -C $repoRoot tag --list 'v*')
    $versions = foreach ($tag in $tags) {
        $text = if ($null -eq $tag) { '' } else { $tag.Trim().TrimStart('v','V') }
        $parsed = $null
        if ([Version]::TryParse($text, [ref]$parsed)) {
            [pscustomobject]@{ Text = $text; Version = $parsed }
        }
    }
    $current = [Version]$releaseVersion
    $previous = @($versions | Where-Object { $_.Version -lt $current } | Sort-Object Version -Descending | Select-Object -First 1)
    if ($previous.Count -eq 0) {
        Fail "Could not determine previous public version."
    }
    return $previous[0].Text
}

function Find-ProgramBuildZip([string]$buildVersion) {
    $candidates = @(
        (Join-Path $programBuilds "SensorReadout-$buildVersion.zip"),
        (Join-Path $programBuilds "SensorReadout-$buildVersion-portable.zip")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    Fail "Could not find previous build ZIP for $buildVersion in $programBuilds."
}

function Set-SmokeConfig([string]$appDir) {
    $config = Join-Path $appDir 'Config'
    $logs = Join-Path $appDir 'Logs'
    $reports = Join-Path $appDir 'Reports'
    New-Item -ItemType Directory -Force -Path $config,$logs,$reports | Out-Null
    $machine = $env:COMPUTERNAME
    $sentinel = 'release-smoke-preserve-' + [Guid]::NewGuid().ToString('N')
    $shared = [ordered]@{
        AutoRefreshEnabled = $true
        RefreshWhileFocused = $true
        RefreshIntervalSeconds = 5
        TemperatureUnit = 'C'
        DecimalSeparator = ''
        LanguageFile = 'English.txt'
        LanguagePreferenceInitialized = $true
        ShowHideHotKey = 'Ctrl+Alt+F12'
        SpeakTrayHotKey = 'Ctrl+Alt+F11'
        HotKeyCopyDoublePressMs = -1
        StartupSpeechEnabled = $false
        StartupSpeechMessage = $sentinel
        TrayStatusEnabled = $true
        TrayTooltipShowsPartialReadings = $true
        CheckForUpdatesAtStartup = $true
        UpdateCheckFrequency = 'Startup'
        InstallUpdatesQuietly = $true
        ShowUpdateInstallConfirmation = $false
        DiagnosticsSpeakProgress = $false
        DiagnosticsPlaySounds = $false
        StartupSoundFile = ''
        ShutdownSoundFile = ''
    }
    $machineSettings = [ordered]@{
        RunAtStartup = $false
        LoggingLevel = 'Debug'
        TrayItemKeys = @('Performance|Overview|System uptime|overview/system-uptime')
        SpokenHotKeys = @(@{ Name = 'Release smoke spoken hotkey'; HotKey = 'Ctrl+Alt+F10'; ReadingKeys = @('Performance|Overview|System uptime|overview/system-uptime') })
        HiddenReadingKeys = @('release-smoke-hidden-key')
        FanProfiles = @(@{ Name = 'Release smoke fan profile'; HotKey = 'Ctrl+Alt+F9'; Actions = @(); ToggleAutomatic = $false; Speak = $false; SpeechMessage = ''; SoundFile = '' })
        FanCurves = @(@{ Name = 'Release smoke fan curve'; Enabled = $false; TemperatureReadingKey = 'Performance|Overview|System uptime|overview/system-uptime'; FanControlKey = 'release-smoke-fan-control'; LowTemperatureC = 30; LowPercent = 20; HighTemperatureC = 70; HighPercent = 80; EmergencyTemperatureC = 90; EmergencyPercent = 100; MinimumChangePercent = 2 })
        Alarms = @(@{ Name = 'Release smoke alarm'; ReadingKey = 'Performance|Overview|System uptime|overview/system-uptime'; Condition = 'Above'; Threshold = 999999; ThresholdUnit = ''; Enabled = $true; Speak = $false; SoundFile = ''; CooldownSeconds = 1 })
        ReadingSpeechLabels = @{ 'Performance|Overview|System uptime|overview/system-uptime' = 'Release smoke uptime' }
        PlugInsEnabled = @{}
    }
    $shared | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $config 'Shared.json') -Encoding UTF8
    $machineSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $config "$machine.json") -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $logs 'release-smoke.log') -Value $sentinel -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $reports 'release-smoke-report.txt') -Value $sentinel -Encoding UTF8
    return $sentinel
}

function Assert-SmokeConfigPreserved([string]$appDir, [string]$sentinel, [string]$releaseVersion) {
    $exe = Join-Path $appDir 'Sensor Readout.exe'
    if (!(Test-Path -LiteralPath $exe)) {
        Fail "Upgrade smoke app is missing Sensor Readout.exe."
    }
    $fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    if ($fileVersion -ne "$releaseVersion.0") {
        Fail "Upgrade smoke exe version is $fileVersion, expected $releaseVersion.0."
    }
    foreach ($relative in @('Config\Shared.json', "Config\$env:COMPUTERNAME.json", 'Logs\release-smoke.log', 'Reports\release-smoke-report.txt')) {
        $path = Join-Path $appDir $relative
        if (!(Test-Path -LiteralPath $path)) {
            Fail "Upgrade smoke lost $relative."
        }
    }

    $sharedText = Get-Content -LiteralPath (Join-Path $appDir 'Config\Shared.json') -Raw
    if ($sharedText.IndexOf($sentinel, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Fail "Upgrade smoke did not preserve startup speech message in Config\Shared.json."
    }

    $machineText = Get-Content -LiteralPath (Join-Path $appDir "Config\$env:COMPUTERNAME.json") -Raw
    foreach ($term in @('Release smoke spoken hotkey', 'Release smoke fan profile', 'Release smoke fan curve', 'Release smoke alarm', 'release-smoke-hidden-key')) {
        if ($machineText.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Fail "Upgrade smoke did not preserve $term in machine config."
        }
    }

    foreach ($relative in @('Logs\release-smoke.log', 'Reports\release-smoke-report.txt')) {
        $text = Get-Content -LiteralPath (Join-Path $appDir $relative) -Raw
        if ($text.IndexOf($sentinel, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Fail "Upgrade smoke did not preserve sentinel in $relative."
        }
    }
}

function Invoke-LocalUpgradeSmoke([string]$releaseVersion, [string]$candidateZip) {
    if ($SkipUpgradeSmoke) {
        Info "Skipping local upgrade smoke by request."
        return
    }
    $previous = Resolve-PreviousVersion $releaseVersion
    $previousZip = Find-ProgramBuildZip $previous
    Info "Running local package-preservation upgrade smoke: $previous -> $releaseVersion."
    $upgradeRoot = Join-Path $SmokeRoot "upgrade-local-$previous-to-$releaseVersion"
    Remove-Item -LiteralPath $upgradeRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $upgradeRoot | Out-Null
    Expand-Archive -LiteralPath $previousZip -DestinationPath $upgradeRoot -Force
    $sentinel = Set-SmokeConfig $upgradeRoot

    $extract = Join-Path $SmokeRoot "candidate-extract-$releaseVersion"
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $candidateZip -DestinationPath $extract -Force
    robocopy $extract $upgradeRoot /E /XD Config Logs Reports 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
    if ($LASTEXITCODE -ge 8) {
        Fail "Local upgrade smoke copy failed. Robocopy exit code $LASTEXITCODE."
    }
    Assert-SmokeConfigPreserved $upgradeRoot $sentinel $releaseVersion
    $report = Join-Path $upgradeRoot 'Reports\release-smoke-after-upgrade.txt'
    $p = Start-Process -FilePath (Join-Path $upgradeRoot 'Sensor Readout.exe') -ArgumentList @('--report-txt', $report) -WorkingDirectory $upgradeRoot -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) {
        Fail "Upgraded smoke copy could not generate a report."
    }
}

function Invoke-PostPublishUpdateSmoke([string]$releaseVersion) {
    if ($SkipPostPublishUpdateSmoke) {
        Info "Skipping post-publish update smoke by request."
        return
    }
    $previous = Resolve-PreviousVersion $releaseVersion
    $previousZip = Find-ProgramBuildZip $previous
    Info "Running post-publish real updater smoke: $previous -> $releaseVersion."
    $updateRoot = Join-Path $SmokeRoot "upgrade-github-$previous-to-$releaseVersion"
    Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $updateRoot | Out-Null
    Expand-Archive -LiteralPath $previousZip -DestinationPath $updateRoot -Force
    $sentinel = Set-SmokeConfig $updateRoot

    $exe = Join-Path $updateRoot 'Sensor Readout.exe'
    Start-Process -FilePath $exe -WorkingDirectory $updateRoot -WindowStyle Hidden | Out-Null
    $deadline = (Get-Date).AddMinutes(4)
    do {
        Start-Sleep -Seconds 3
        $version = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.FileVersion } else { '' }
        if ($version -eq "$releaseVersion.0") {
            break
        }
    } while ((Get-Date) -lt $deadline)

    Start-Process -FilePath $exe -ArgumentList '--close' -WorkingDirectory $updateRoot -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
    Assert-SmokeConfigPreserved $updateRoot $sentinel $releaseVersion
}

function Mirror-AppCopy([string]$target, [string]$description, [bool]$launchAfterCopy) {
    Info "Mirroring to $description."
    $exe = Join-Path $target 'Sensor Readout.exe'
    if (Test-Path -LiteralPath $exe) {
        Start-Process -FilePath $exe -ArgumentList '--close' -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    robocopy $portable $target /MIR /XD Config Logs Reports 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
    if ($LASTEXITCODE -ge 8) {
        Fail "Could not mirror $description. Robocopy exit code $LASTEXITCODE."
    }
    if ($launchAfterCopy) {
        Start-Process -FilePath $exe | Out-Null
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-AppVersion
}
$Version = $Version.Trim().TrimStart('v','V')

New-Item -ItemType Directory -Force -Path $SmokeRoot,$programBuilds,$sourceSnapshots | Out-Null

Assert-VersionConsistency $Version
Assert-ChangelogClean $Version
Assert-GitHubIssuesChecked

if (!$SkipBuild) {
    if ($SkipSelfTest) {
        Info "Building without self-test."
        & powershell -ExecutionPolicy Bypass -File $buildScript
    } else {
        Info "Building with self-test."
        & powershell -ExecutionPolicy Bypass -File $buildScript -SelfTest
    }
    if ($LASTEXITCODE -ne 0) {
        Fail "Build failed."
    }
}

Assert-LanguageParity
Assert-NoNestedPortableFolders
Invoke-GitDiffCheck
Invoke-CommandLineReportSmoke $Version
$zipPath = New-ReleaseZip $Version
Invoke-LocalUpgradeSmoke $Version $zipPath
Mirror-AppCopy 'C:\Users\OnjLo\AppData\Local\Programs\Sensor Readout' 'installed copy' $true
Mirror-AppCopy 'D:\Dropbox\SOFTWARE\SensorReadout' 'personal Dropbox copy' $false

if ($RunPostPublishUpdateSmoke -and !$SkipPostPublishUpdateSmoke) {
    Invoke-PostPublishUpdateSmoke $Version
}

if (!$KeepSmokeFolder) {
    Info "Cleaning smoke folder."
    Remove-Item -LiteralPath $SmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Info "Release checks passed for $Version."
Info "Program ZIP: $zipPath"
Info "After publishing the GitHub release, run: powershell -ExecutionPolicy Bypass -File .\Release.ps1 -SkipBuild -SkipSelfTest -SkipUpgradeSmoke -RunPostPublishUpdateSmoke"
