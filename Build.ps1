param(
    [string]$OutputPath = "$PSScriptRoot\portable\AccessibleSensorReadout.exe"
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "Could not find the .NET Framework C# compiler at $csc"
}

$portable = Join-Path $PSScriptRoot 'portable'
$source = Join-Path $PSScriptRoot 'src\SensorReadoutApp.cs'
$manifest = Join-Path $PSScriptRoot 'src\SensorReadoutApp.exe.manifest'

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Management.dll',
    (Join-Path $portable 'FanControl.IPC.dll'),
    (Join-Path $portable 'Google.Protobuf.dll'),
    (Join-Path $portable 'Grpc.Core.Api.dll'),
    (Join-Path $portable 'GrpcDotNetNamedPipes.dll'),
    (Join-Path $portable 'LibreHardwareMonitorLib.dll'),
    (Join-Path $portable 'Newtonsoft.Json.dll')
) -join ','

& $csc /nologo /target:winexe /platform:x64 /win32manifest:$manifest /out:$OutputPath /reference:$references $source
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built $OutputPath"
