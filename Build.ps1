param(
    [string]$OutputPath = "$PSScriptRoot\portable\Sensor Readout.exe"
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "Could not find the .NET Framework C# compiler at $csc"
}

$portable = Join-Path $PSScriptRoot 'portable'
$sdkOutput = Join-Path $portable 'SensorReadout.PluginSdk.dll'
$sdkSources = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src\PluginSdk') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$sources = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$manifest = Join-Path $PSScriptRoot 'src\SensorReadoutApp.exe.manifest'

if ($sdkSources.Count -gt 0) {
    & $csc /nologo /target:library /platform:x64 /out:$sdkOutput $sdkSources
    if ($LASTEXITCODE -ne 0) {
        throw "Plug-In SDK build failed with exit code $LASTEXITCODE"
    }
}

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Management.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    (Join-Path $portable 'LibreHardwareMonitorLib.dll'),
    (Join-Path $portable 'Newtonsoft.Json.dll'),
    $sdkOutput
) -join ','

& $csc /nologo /target:winexe /platform:x64 /win32manifest:$manifest /out:$OutputPath /reference:$references $sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$plugInRoot = Join-Path $PSScriptRoot 'PlugIns'
if (Test-Path $plugInRoot) {
    foreach ($plugIn in Get-ChildItem -LiteralPath $plugInRoot -Directory) {
        $sourceFolder = Join-Path $plugIn.FullName 'src'
        $plugInSources = @()
        if (Test-Path $sourceFolder) {
            $plugInSources = Get-ChildItem -Path $sourceFolder -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
        }

        $plugInTarget = Join-Path (Join-Path $portable 'Plug-Ins') $plugIn.Name
        New-Item -ItemType Directory -Force -Path $plugInTarget | Out-Null
        $manifestSource = Join-Path $plugIn.FullName 'plugin.json'
        if (Test-Path $manifestSource) {
            Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $plugInTarget 'plugin.json') -Force
        }

        if ($plugInSources.Count -gt 0) {
            $plugInOutput = Join-Path $plugInTarget ($plugIn.Name + 'PlugIn.dll')
            $plugInReferences = @(
                'System.dll',
                'System.Core.dll',
                (Join-Path $portable 'Newtonsoft.Json.dll'),
                $sdkOutput
            ) -join ','
            & $csc /nologo /target:library /platform:x64 /out:$plugInOutput /reference:$plugInReferences $plugInSources
            if ($LASTEXITCODE -ne 0) {
                throw "Plug-In build failed for $($plugIn.Name) with exit code $LASTEXITCODE"
            }
        }
    }
}

$dataSource = Join-Path $PSScriptRoot 'Data'
if (Test-Path $dataSource) {
    $dataTarget = Join-Path $portable 'Data'
    New-Item -ItemType Directory -Force -Path $dataTarget | Out-Null
    Copy-Item -LiteralPath (Join-Path $dataSource '*') -Destination $dataTarget -Force
}

Write-Host "Built $OutputPath"
