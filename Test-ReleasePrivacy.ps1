param(
    [string]$ReleaseZip = '',
    [string]$SourceZip = '',
    [switch]$AllHistory
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

function Get-RelativePath([string]$root, [string]$path) {
    $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($path)
    if ($pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $pathFull.Substring($rootFull.Length)
    }
    return $pathFull
}

function Get-TextFiles([string]$root) {
    $extensions = @(
        '.bat', '.cmd', '.config', '.cs', '.csproj', '.css', '.htm', '.html',
        '.json', '.md', '.ps1', '.txt', '.xml', '.yml', '.yaml'
    )

    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        $paths = git -C $root ls-files --cached --others --exclude-standard
        return $paths |
            ForEach-Object { Join-Path $root $_ } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            ForEach-Object { Get-Item -LiteralPath $_ } |
            Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -eq '.gitignore' }
    }

    Get-ChildItem -LiteralPath $root -Recurse -File -Force |
        Where-Object {
            $_.FullName -notmatch '\\(\.git|release|bin|obj|portable)(\\|$)' -and
            ($extensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -eq '.gitignore')
        }
}

function Test-TextFile([string]$path, [string]$displayPath) {
    $text = Get-Content -LiteralPath $path -Raw
    $bs = [string][char]92
    $privateSyncName = 'Drop' + 'box'
    $workspaceMarker = 'backups' + $bs + 'Codex'
    $codexCurrentMarker = 'Codex' + $bs + 'current'
    $slashWorkspaceMarker = 'backups/' + 'Codex'
    $slashCodexNotesMarker = $privateSyncName + '/txt/' + 'codex'
    $privateUser = 'Onj' + 'Lo'
    $privateMachineOne = 'Mer' + 'jille'
    $privateMachineTwo = 'Ko' + 'bo'
    $privateMachineThree = 'VIP' + '40'
    $fileTokenMarker = 'token' + 'File'
    $sharedTokenMarker = 'shared' + 'Token' + 'File'
    $privateNamesPattern = '(?<![A-Za-z0-9])(?:' +
        [regex]::Escape($privateUser) + '|' +
        [regex]::Escape($privateMachineOne) + '|' +
        [regex]::Escape($privateMachineTwo) + '|' +
        [regex]::Escape($privateMachineThree) +
        ')(?![A-Za-z0-9])'

    $checks = [ordered]@{
        'private Windows local path' = ('(?<![A-Za-z0-9])(?:[DEF]:' + [regex]::Escape($bs) + '|C:' + [regex]::Escape($bs) + 'Users' + [regex]::Escape($bs) + $privateUser + ')')
        'private sync/workspace path wording' = ([regex]::Escape($privateSyncName + $bs + 'backups') + '|' + [regex]::Escape($privateSyncName + $bs + 'txt' + $bs + 'codex') + '|' + [regex]::Escape($workspaceMarker) + '|' + [regex]::Escape($codexCurrentMarker) + '|' + [regex]::Escape($slashWorkspaceMarker) + '|' + [regex]::Escape($slashCodexNotesMarker))
        'local user or machine name' = $privateNamesPattern
        'token loaded from a file in public docs/source' = ('GH_TOKEN\s*=.*Get-Content|GITHUB_TOKEN\s*=.*Get-Content|' + [regex]::Escape($fileTokenMarker) + '|' + [regex]::Escape($sharedTokenMarker))
    }

    foreach ($description in $checks.Keys) {
        if ($text -match $checks[$description]) {
            Fail "$displayPath contains forbidden private/personal release text: $description"
        }
    }
}

function Test-Directory([string]$root, [string]$label) {
    if (-not (Test-Path -LiteralPath $root)) {
        Fail "$label does not exist: $root"
    }
    foreach ($file in Get-TextFiles $root) {
        Test-TextFile $file.FullName "$label\$(Get-RelativePath $root $file.FullName)"
    }
}

function Test-Zip([string]$zipPath, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($zipPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $zipPath)) {
        Fail "$label does not exist: $zipPath"
    }
    $extractRoot = Join-Path ([IO.Path]::GetTempPath()) ('SensorReadout-release-privacy-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        Test-Directory $extractRoot $label
    }
    finally {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-AllHistory {
    $bs = [string][char]92
    $privateSyncName = 'Drop' + 'box'
    $workspaceMarker = 'backups' + $bs + 'Codex'
    $slashWorkspaceMarker = 'backups/' + 'Codex'
    $slashCodexNotesMarker = $privateSyncName + '/txt/' + 'codex'
    $patterns = @(
        ('D:' + [regex]::Escape($bs)),
        ('E:' + [regex]::Escape($bs)),
        ('C:' + [regex]::Escape($bs) + 'Users' + [regex]::Escape($bs) + 'Onj' + 'Lo'),
        [regex]::Escape($privateSyncName + $bs + 'backups'),
        [regex]::Escape($privateSyncName + $bs + 'txt' + $bs + 'codex'),
        [regex]::Escape($workspaceMarker),
        [regex]::Escape($slashWorkspaceMarker),
        [regex]::Escape($slashCodexNotesMarker),
        'GH_TOKEN\s*=.*Get-Content',
        'GITHUB_TOKEN\s*=.*Get-Content'
    )

    $revisions = git -C $repoRoot rev-list --all
    foreach ($pattern in $patterns) {
        $matches = git -C $repoRoot grep -n -I -E $pattern $revisions 2>$null
        if ($LASTEXITCODE -eq 0) {
            $matches | Select-Object -First 20 | ForEach-Object { Write-Error $_ }
            Fail "Git history contains forbidden private/personal release text matching: $pattern"
        }
    }
}

Test-Directory $repoRoot 'working tree'
Test-Zip $ReleaseZip 'release ZIP'
Test-Zip $SourceZip 'source ZIP'

if ($AllHistory) {
    Test-AllHistory
}

Write-Host 'Sensor Readout release privacy check passed.'
