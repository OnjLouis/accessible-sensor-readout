param(
    [switch]$SkipBuild,
    [switch]$SkipSelfTest,
    [switch]$SkipUpgradeSmoke,
    [switch]$SkipPostPublishUpdateSmoke,
    [switch]$SkipVendorDataUpdate,
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
$dataUpdateScript = Join-Path $repoRoot 'Update-Data.ps1'
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
    Assert-ManualHealth $releaseVersion
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

function Assert-ManualHealthForFolder([string]$folder, [string]$releaseVersion, [string]$label) {
    $english = Join-Path $folder 'README-en.html'
    if (!(Test-Path -LiteralPath $english)) {
        Fail "$label README-en.html is missing."
    }

    $englishSize = (Get-Item -LiteralPath $english).Length
    $maxManualSize = [Math]::Max(300000, [int64]($englishSize * 3))
    $badEncodingChars = @([char]0x00C2, [char]0x00C3, [char]0x00E2, [char]0x0192)

    foreach ($manual in Get-ChildItem -LiteralPath $folder -Filter 'README-*.html') {
        if ($manual.Length -gt $maxManualSize) {
            Fail "$label $($manual.Name) is $($manual.Length) bytes, which is far larger than README-en.html ($englishSize bytes)."
        }

        $text = Get-Content -LiteralPath $manual.FullName -Raw
        foreach ($badChar in $badEncodingChars) {
            if ($text.IndexOf($badChar) -ge 0) {
                Fail "$label $($manual.Name) appears to contain mojibake or bad encoding markers."
            }
        }

        $maxLine = 0
        foreach ($line in [System.IO.File]::ReadLines($manual.FullName)) {
            if ($line.Length -gt $maxLine) {
                $maxLine = $line.Length
            }
        }
        if ($maxLine -gt 100000) {
            Fail "$label $($manual.Name) has an unusually long line ($maxLine characters), which can hurt screen readers and usually indicates generated-document corruption."
        }

        $visibleVersion = [regex]::Match(
            $text,
            '<p>[^<]*(Current version|Aktuelle Version|Version actual|Version actuelle|Versione attuale)[^<]*</p>',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (!$visibleVersion.Success) {
            Fail "$label $($manual.Name) does not have a visible top-of-manual version line."
        }
        if ($visibleVersion.Value -notmatch [regex]::Escape($releaseVersion)) {
            Fail "$label $($manual.Name) visible version line does not match ${releaseVersion}: $($visibleVersion.Value)"
        }
    }
}

function Assert-ManualHealth([string]$releaseVersion) {
    Info "Checking manual size, visible versions, and encoding health."
    Assert-ManualHealthForFolder (Join-Path $repoRoot 'Docs') $releaseVersion 'Docs'
    Assert-ManualHealthForFolder (Join-Path $portable 'Docs') $releaseVersion 'portable Docs'
}

function Get-GitHubReleaseHeaders {
    $headers = @{
        'User-Agent' = 'Sensor Readout release check'
        'Accept' = 'application/vnd.github+json'
    }

    $token = Get-GitHubReleaseToken
    if (!([string]::IsNullOrWhiteSpace($token))) {
        $headers['Authorization'] = "Bearer $token"
    }

    return $headers
}

function Get-GitHubReleaseToken {
    if (!([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))) {
        return $env:GITHUB_TOKEN.Trim()
    }
    if (!([string]::IsNullOrWhiteSpace($env:GH_TOKEN))) {
        return $env:GH_TOKEN.Trim()
    }

    $tokenPaths = @(
        (Join-Path (Split-Path -Parent $repoRoot) 'token.txt'),
        (Join-Path $repoRoot 'token.txt'),
        'D:\Dropbox\backups\Codex\current\token.txt'
    )

    foreach ($path in $tokenPaths) {
        if ([string]::IsNullOrWhiteSpace($path) -or !(Test-Path -LiteralPath $path)) {
            continue
        }

        $token = (Get-Content -LiteralPath $path -Raw).Trim()
        if (!([string]::IsNullOrWhiteSpace($token))) {
            return $token
        }
    }

    return ''
}

function Assert-GitHubActivityChecked {
    Info "Checking GitHub issues, pull requests, forks, and traffic."
    $headers = Get-GitHubReleaseHeaders
    try {
        $repo = 'OnjLouis/accessible-sensor-readout'
        $issues = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/issues?state=open&per_page=100" -Headers $headers
        $realIssues = @($issues | Where-Object { -not $_.pull_request })
        if ($realIssues.Count -gt 0) {
            $summary = ($realIssues | ForEach-Object { "#$($_.number) $($_.title)" }) -join '; '
            Fail "Open GitHub issues need review before release: $summary"
        }
        Info "No open GitHub issues."

        $pulls = @(Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/pulls?state=open&per_page=100" -Headers $headers | Where-Object { $_ -and $_.number })
        if ($pulls.Count -gt 0) {
            $summary = ($pulls | ForEach-Object { "#$($_.number) $($_.title)" }) -join '; '
            Fail "Open GitHub pull requests need review before release: $summary"
        }
        Info "No open GitHub pull requests."

        $forks = @(Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/forks?per_page=100&sort=newest" -Headers $headers)
        if ($forks.Count -gt 0) {
            $summary = ($forks | Select-Object -First 5 | ForEach-Object { "$($_.full_name), pushed $($_.pushed_at)" }) -join '; '
            Info "Visible forks: $summary"
        } else {
            Info "No visible forks."
        }

        if ($headers.ContainsKey('Authorization')) {
            $clones = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/traffic/clones" -Headers $headers
            $views = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/traffic/views" -Headers $headers
            Info "Recent GitHub traffic: $($clones.count) clones from $($clones.uniques) unique cloners; $($views.count) views from $($views.uniques) unique visitors."
        } else {
            Info "GitHub traffic check skipped because no GitHub token was available in GITHUB_TOKEN, GH_TOKEN, or token.txt."
        }
    } catch {
        Fail "Could not check GitHub activity: $($_.Exception.Message)"
    }
}

function Write-CommunityMentionReminder {
    Info "Community mention check: search the web and public community spaces for Sensor Readout mentions before release."
    Info "Suggested searches: `"Accessible Sensor Readout`", `"accessible-sensor-readout`", `"Sensor Readout`" `"OnjLouis`", `"Sensor Readout`" `"NVDA`", `"Sensor Readout`" `"JAWS`", `"Sensor Readout`" `"JFW`", `"Sensor Readout`" `"screen reader`", and podcast/email-list/community sites."
    Info "Look for feedback that did not arrive as a GitHub issue, especially accessibility complaints, update problems, missing hardware data, and repeated questions."
}

function Assert-LanguageParity {
    Info "Checking language key parity and encoding health."
    $englishMap = @{}
    Get-Content -LiteralPath (Join-Path $portable 'Langs\English.txt') -Encoding UTF8 |
        Where-Object { $_ -match '^[^#=]+=' } |
        ForEach-Object {
            $parts = $_ -split '=', 2
            $englishMap[$parts[0]] = $parts[1]
        }
    $english = @($englishMap.Keys)
    $badEncodingChars = @([char]0x00C2, [char]0x00C3, [char]0x00E2, [char]0x0192)
    $mustTranslateKeys = @(
        'ui.Compare reports',
        'ui.Choose reports to compare',
        'ui.Sensor Readout report comparison',
        'ui.Before:',
        'ui.After:',
        'ui.Added readings',
        'ui.Removed readings',
        'ui.Changed readings',
        'ui.Detail changes',
        'ui.End of comparison.',
        'a11y.Report comparison results',
        'ui.Save comparison',
        'ui.Save anonymized report',
        'ui.Rows included:',
        'ui.Computer name:',
        'ui.Prepare support report',
        'ui.Open &issue page',
        'ui.&Copy path',
        'ui.Open &folder',
        'a11y.Support report instructions',
        'ui.Data sources',
        'ui.Enable reading history &logging',
        'ui.Add to history &log',
        'ui.Remove from history &log'
    )
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $portable 'Langs') -Filter '*.txt') {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($badChar in $badEncodingChars) {
            if ($text.IndexOf($badChar) -ge 0) {
                Fail "$($file.Name) appears to contain mojibake or bad encoding markers."
            }
        }

        $map = @{}
        Get-Content -LiteralPath $file.FullName -Encoding UTF8 |
            Where-Object { $_ -match '^[^#=]+=' } |
            ForEach-Object {
                $parts = $_ -split '=', 2
                $map[$parts[0]] = $parts[1]
            }
        $keys = @($map.Keys)
        $missing = @($english | Where-Object { $_ -notin $keys })
        if ($missing.Count -gt 0) {
            Fail "$($file.Name) is missing language keys: $($missing -join ', ')"
        }

        if ($file.Name -ne 'English.txt') {
            $untranslated = @($mustTranslateKeys | Where-Object {
                $map.ContainsKey($_) -and $englishMap.ContainsKey($_) -and $map[$_] -eq $englishMap[$_]
            })
            if ($untranslated.Count -gt 0) {
                Fail "$($file.Name) has untranslated release-critical language keys: $($untranslated -join ', ')"
            }
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
    & git -C $repoRoot diff --check -- . ':(exclude)Data/usb.ids' ':(exclude)Data/oui.csv' ':(exclude)portable/Data/usb.ids' ':(exclude)portable/Data/oui.csv'
    if ($LASTEXITCODE -ne 0) {
        Fail "git diff --check failed."
    }
}

function Update-VendorData {
    if ($SkipVendorDataUpdate -or $RunPostPublishUpdateSmoke) {
        Info "Skipping USB and MAC vendor database update."
        return
    }

    Info "Updating bundled USB and MAC vendor databases."
    & powershell -ExecutionPolicy Bypass -File $dataUpdateScript
    if ($LASTEXITCODE -ne 0) {
        Fail "USB and MAC vendor database update failed."
    }
}

function Invoke-CommandLineReportSmoke([string]$releaseVersion) {
    Info "Running command-line TXT/HTML, anonymized, and comparison report smoke."
    $reportDir = Join-Path $SmokeRoot 'reports'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $txt = Join-Path $reportDir "SensorReadout-smoke-$releaseVersion.txt"
    $html = Join-Path $reportDir "SensorReadout-smoke-$releaseVersion.html"
    $anonTxt = Join-Path $reportDir "SensorReadout-smoke-anonymized-$releaseVersion.txt"
    $anonHtml = Join-Path $reportDir "SensorReadout-smoke-anonymized-$releaseVersion.html"
    $comparison = Join-Path $reportDir "SensorReadout-smoke-comparison-$releaseVersion.txt"
    Remove-Item -LiteralPath $txt,$html,$anonTxt,$anonHtml,$comparison -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $portable 'Sensor Readout.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--report-txt', $txt) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $txt)) {
        Fail "TXT report smoke failed."
    }
    $p = Start-Process -FilePath $exe -ArgumentList @('--report-html', $html) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $html)) {
        Fail "HTML report smoke failed."
    }
    $p = Start-Process -FilePath $exe -ArgumentList @('--anonymized-report-txt', $anonTxt) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $anonTxt)) {
        Fail "Anonymized TXT report smoke failed."
    }
    $p = Start-Process -FilePath $exe -ArgumentList @('--anonymized-report-html', $anonHtml) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $anonHtml)) {
        Fail "Anonymized HTML report smoke failed."
    }
    $p = Start-Process -FilePath $exe -ArgumentList @('--compare-reports', $html, $anonHtml, $comparison) -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $comparison)) {
        Fail "Report comparison smoke failed."
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

    $anonTxtText = Get-Content -LiteralPath $anonTxt -Raw
    $anonHtmlText = Get-Content -LiteralPath $anonHtml -Raw
    foreach ($text in @($anonTxtText, $anonHtmlText)) {
        if ($text.IndexOf($env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Fail "Anonymized smoke report still contains the current computer name."
        }
        if ($text -match '\b(?:\d{1,3}\.){3}\d{1,3}\b') {
            Fail "Anonymized smoke report still contains an IPv4 address."
        }
        if ($text -match '\b[0-9A-F]{2}(?:[:-][0-9A-F]{2}){5}\b') {
            Fail "Anonymized smoke report still contains a MAC address."
        }
    }
    $anonTables = [regex]::Matches($anonHtmlText, '(?s)<table>.*?</table>') | ForEach-Object { $_.Value }
    $duplicateTables = @($anonTables | Group-Object | Where-Object Count -gt 1)
    if ($duplicateTables.Count -gt 0) {
        Fail "Anonymized HTML report contains duplicate visible detail tables."
    }

    $comparisonText = Get-Content -LiteralPath $comparison -Raw
    if ($comparisonText -notmatch 'Sensor Readout report comparison') {
        Fail "Report comparison smoke output does not look like a comparison."
    }
}

function Invoke-CommandLineDiagnosticsSmoke([string]$releaseVersion) {
    Info "Running command-line diagnostics ZIP smoke."
    $diagnosticsDir = Join-Path $SmokeRoot 'diagnostics'
    New-Item -ItemType Directory -Force -Path $diagnosticsDir | Out-Null
    $zip = Join-Path $diagnosticsDir "SensorReadout-diagnostics-smoke-$releaseVersion.zip"
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $portable 'Sensor Readout.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--diagnostics', $zip, '--diagnostics-quiet') -WorkingDirectory $portable -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $zip)) {
        Fail "Command-line diagnostics smoke failed."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        if (-not ($entries | Where-Object { $_.EndsWith('.html', [StringComparison]::OrdinalIgnoreCase) })) {
            Fail "Diagnostics ZIP smoke is missing an HTML report."
        }
        if (-not ($entries | Where-Object { $_.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase) })) {
            Fail "Diagnostics ZIP smoke is missing text files."
        }
        if (-not ($entries | Where-Object { $_.EndsWith('Diagnostics-summary.txt', [StringComparison]::OrdinalIgnoreCase) })) {
            Fail "Diagnostics ZIP smoke is missing Diagnostics-summary.txt."
        }
        if ($entries | Where-Object { $_.IndexOf('staging', [StringComparison]::OrdinalIgnoreCase) -ge 0 }) {
            Fail "Diagnostics ZIP smoke contains staging paths."
        }

        $summaryEntry = $archive.Entries | Where-Object { $_.FullName.EndsWith('Diagnostics-summary.txt', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        $reader = New-Object System.IO.StreamReader($summaryEntry.Open())
        try {
            $summaryText = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
        foreach ($term in @('Sensor Readout diagnostics', 'Initial sensor collection', 'Final sensor collection')) {
            if ($summaryText.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                Fail "Diagnostics summary is missing expected text: $term"
            }
        }
    } finally {
        $archive.Dispose()
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

    robocopy $portable $stage /E /XD Config Logs Reports Backups 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
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

function New-SourceSnapshot([string]$releaseVersion) {
    Info "Creating source snapshot ZIP."
    New-Item -ItemType Directory -Force -Path $sourceSnapshots | Out-Null
    $zipPath = Join-Path $sourceSnapshots "SensorReadout-source-$releaseVersion.zip"
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

    $tracked = @(& git -C $repoRoot ls-files)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
        Fail "Could not list tracked files for source snapshot."
    }

    $stage = Join-Path $SmokeRoot "source-stage-$releaseVersion"
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    foreach ($relative in $tracked) {
        if ([string]::IsNullOrWhiteSpace($relative)) {
            continue
        }

        $source = Join-Path $repoRoot $relative
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
            continue
        }

        $destination = Join-Path $stage $relative
        $folder = Split-Path -Parent $destination
        if (!(Test-Path -LiteralPath $folder)) {
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
        }

        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
    return $zipPath
}

function Assert-ReleaseZipContents([string]$zipPath) {
    Info "Inspecting release ZIP contents."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($prefix in @('Config/','Logs/','Reports/','Backups/','Update Backups/','Update Temp/')) {
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
    $legacyUpdateBackup = Join-Path $upgradeRoot 'Config\Update Backups\legacy'
    New-Item -ItemType Directory -Force -Path $legacyUpdateBackup | Out-Null
    Set-Content -LiteralPath (Join-Path $legacyUpdateBackup 'old-language.txt') -Value 'release-smoke legacy backup' -Encoding UTF8
    $nestedPlugIn = Join-Path $upgradeRoot 'Plug-Ins\AsusRog\AsusRog'
    New-Item -ItemType Directory -Force -Path $nestedPlugIn | Out-Null
    Set-Content -LiteralPath (Join-Path $nestedPlugIn 'plugin.json') -Value '{"id":"release-smoke.bad-nested-plugin","name":"Bad nested plugin"}' -Encoding UTF8

    $extract = Join-Path $SmokeRoot "candidate-extract-$releaseVersion"
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $candidateZip -DestinationPath $extract -Force
    foreach ($name in @('Docs','Langs','Data','Plug-Ins')) {
        $incoming = Join-Path $extract $name
        $existing = Join-Path $upgradeRoot $name
        if (Test-Path -LiteralPath $incoming) {
            Remove-Item -LiteralPath $existing -Recurse -Force -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath $incoming -Destination $existing -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath (Join-Path $upgradeRoot 'Config\Update Backups')) {
        $backupRoot = Join-Path $upgradeRoot 'Backups\Updates\release-smoke'
        New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
        Compress-Archive -LiteralPath (Join-Path $upgradeRoot 'Config\Update Backups') -DestinationPath (Join-Path $backupRoot 'Legacy-Config-Update-Backups.zip') -Force
        Remove-Item -LiteralPath (Join-Path $upgradeRoot 'Config\Update Backups') -Recurse -Force
    }
    robocopy $extract $upgradeRoot /E /XD Config Logs Reports Backups Docs Langs Data Plug-Ins 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
    if ($LASTEXITCODE -ge 8) {
        Fail "Local upgrade smoke copy failed. Robocopy exit code $LASTEXITCODE."
    }
    Assert-SmokeConfigPreserved $upgradeRoot $sentinel $releaseVersion
    foreach ($nested in @('Plug-Ins\AsusRog\AsusRog', 'Docs\Docs', 'Langs\Langs', 'Data\Data', 'Plug-Ins\Plug-Ins')) {
        if (Test-Path -LiteralPath (Join-Path $upgradeRoot $nested)) {
            Fail "Local upgrade smoke left nested shipped folder live: $nested"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $upgradeRoot 'Config\Update Backups')) {
        Fail "Local upgrade smoke left legacy Config\Update Backups live."
    }
    if (!(Test-Path -LiteralPath (Join-Path $upgradeRoot 'Backups\Updates\release-smoke\Legacy-Config-Update-Backups.zip'))) {
        Fail "Local upgrade smoke did not move legacy update backups into top-level Backups."
    }
    $startupRepairNested = Join-Path $upgradeRoot 'Plug-Ins\AsusRog\AsusRog'
    New-Item -ItemType Directory -Force -Path $startupRepairNested | Out-Null
    Set-Content -LiteralPath (Join-Path $startupRepairNested 'plugin.json') -Value '{"id":"release-smoke.startup-repair-plugin","name":"Startup repair nested plugin"}' -Encoding UTF8
    $startupRepairLegacy = Join-Path $upgradeRoot 'Config\Update Backups\startup-repair'
    New-Item -ItemType Directory -Force -Path $startupRepairLegacy | Out-Null
    Set-Content -LiteralPath (Join-Path $startupRepairLegacy 'legacy.txt') -Value 'startup repair legacy backup' -Encoding UTF8
    $report = Join-Path $upgradeRoot 'Reports\release-smoke-after-upgrade.txt'
    $p = Start-Process -FilePath (Join-Path $upgradeRoot 'Sensor Readout.exe') -ArgumentList @('--report-txt', $report) -WorkingDirectory $upgradeRoot -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) {
        Fail "Upgraded smoke copy could not generate a report."
    }
    if (Test-Path -LiteralPath $startupRepairNested) {
        Fail "Startup repair did not remove nested Plug-In folder."
    }
    if (Test-Path -LiteralPath (Join-Path $upgradeRoot 'Config\Update Backups')) {
        Fail "Startup repair did not move legacy Config\Update Backups."
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
    robocopy $portable $target /MIR /XD Config Logs Reports Backups 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
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

Update-VendorData
Assert-VersionConsistency $Version
Assert-ChangelogClean $Version
Assert-GitHubActivityChecked
Write-CommunityMentionReminder

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
Invoke-CommandLineDiagnosticsSmoke $Version
$zipPath = New-ReleaseZip $Version
$sourceZipPath = New-SourceSnapshot $Version
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
Info "Source ZIP: $sourceZipPath"
Info "After publishing the GitHub release, run: powershell -ExecutionPolicy Bypass -File .\Release.ps1 -SkipBuild -SkipSelfTest -SkipUpgradeSmoke -RunPostPublishUpdateSmoke"
