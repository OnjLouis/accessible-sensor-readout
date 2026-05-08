param(
    [string]$OutputPath = "$PSScriptRoot\portable\Sensor Readout.exe"
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "Could not find the .NET Framework C# compiler at $csc"
}

$portable = Join-Path $PSScriptRoot 'portable'
$sources = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$manifest = Join-Path $PSScriptRoot 'src\SensorReadoutApp.exe.manifest'

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Management.dll',
    (Join-Path $portable 'LibreHardwareMonitorLib.dll'),
    (Join-Path $portable 'Newtonsoft.Json.dll')
) -join ','

& $csc /nologo /target:winexe /platform:x64 /win32manifest:$manifest /out:$OutputPath /reference:$references $sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$dataSource = Join-Path $PSScriptRoot 'Data'
if (Test-Path $dataSource) {
    $dataTarget = Join-Path $portable 'Data'
    New-Item -ItemType Directory -Force -Path $dataTarget | Out-Null
    Copy-Item -LiteralPath (Join-Path $dataSource '*') -Destination $dataTarget -Force
}

Write-Host "Built $OutputPath"
