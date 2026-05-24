param(
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$dataRoot = Join-Path $repoRoot 'Data'
$portableDataRoot = Join-Path (Join-Path $repoRoot 'portable') 'Data'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('SensorReadout-DataUpdate-' + [Guid]::NewGuid().ToString('N'))
$downloadHeaders = @{
    'User-Agent' = 'Sensor Readout data update (https://github.com/OnjLouis/accessible-sensor-readout)'
}

function Fail($message) {
    throw "[data-update] $message"
}

function Info($message) {
    Write-Host "[data-update] $message"
}

function Get-FileSha256([string]$path) {
    if (!(Test-Path -LiteralPath $path)) {
        return ''
    }

    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Assert-UsbIds([string]$path) {
    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt 100000) {
        Fail "Downloaded usb.ids is unexpectedly small ($($item.Length) bytes)."
    }

    $text = Get-Content -LiteralPath $path -Raw
    if ($text -notmatch "List of USB ID's" -or $text -notmatch '# Version:\s+\d{4}\.\d{2}\.\d{2}' -or $text -notmatch '# Date:\s+\d{4}-\d{2}-\d{2}') {
        Fail "Downloaded usb.ids does not look like a valid USB ID Repository snapshot."
    }
}

function Assert-OuiCsv([string]$path) {
    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt 1000000) {
        Fail "Downloaded oui.csv is unexpectedly small ($($item.Length) bytes)."
    }

    $firstLine = Get-Content -LiteralPath $path -TotalCount 1
    if ($firstLine -ne 'Registry,Assignment,Organization Name,Organization Address') {
        Fail "Downloaded oui.csv has an unexpected header: $firstLine"
    }

    $sample = Get-Content -LiteralPath $path -TotalCount 50
    if (!($sample | Where-Object { $_ -match '^MA-L,[0-9A-Fa-f]{6},' })) {
        Fail "Downloaded oui.csv does not contain recognizable IEEE OUI assignments near the top."
    }
}

function Normalize-DownloadedDataFile([string]$name, [string]$path) {
    if ($name -ne 'oui.csv') {
        return
    }

    $lines = [System.IO.File]::ReadAllLines($path)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $lines[$i] = $lines[$i].TrimEnd()
    }
    [System.IO.File]::WriteAllLines($path, $lines, [System.Text.UTF8Encoding]::new($true))
}

function Update-DataFile(
    [string]$name,
    [string]$uri,
    [scriptblock]$validator
) {
    $target = Join-Path $dataRoot $name
    $download = Join-Path $tempRoot $name

    Info "Downloading $name."
    Invoke-WebRequest -Uri $uri -OutFile $download -UseBasicParsing -Headers $downloadHeaders
    & $validator $download
    Normalize-DownloadedDataFile $name $download

    $oldHash = Get-FileSha256 $target
    $newHash = Get-FileSha256 $download
    if ($oldHash -eq $newHash) {
        Info "$name is already current."
        return $false
    }

    if ($CheckOnly) {
        Info "$name has an available update."
        return $true
    }

    Copy-Item -LiteralPath $download -Destination $target -Force
    Info "Updated $name."
    return $true
}

New-Item -ItemType Directory -Force -Path $dataRoot,$tempRoot | Out-Null

try {
    $changed = $false
    $changed = (Update-DataFile 'usb.ids' 'http://www.linux-usb.org/usb.ids' ${function:Assert-UsbIds}) -or $changed
    $changed = (Update-DataFile 'oui.csv' 'https://standards-oui.ieee.org/oui/oui.csv' ${function:Assert-OuiCsv}) -or $changed

    if ($CheckOnly) {
        if ($changed) {
            Fail "Bundled USB or MAC vendor databases are not current. Run .\Update-Data.ps1 before release."
        }

        Info "Bundled USB and MAC vendor databases are current."
        return
    }

    if (Test-Path -LiteralPath $portableDataRoot) {
        New-Item -ItemType Directory -Force -Path $portableDataRoot | Out-Null
        Copy-Item -LiteralPath (Join-Path $dataRoot 'usb.ids') -Destination (Join-Path $portableDataRoot 'usb.ids') -Force
        Copy-Item -LiteralPath (Join-Path $dataRoot 'oui.csv') -Destination (Join-Path $portableDataRoot 'oui.csv') -Force
        Info "Mirrored updated databases to portable\Data."
    }

    if (!$changed) {
        Info "No bundled database updates were needed."
    }
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
