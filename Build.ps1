param(
    [string]$OutputPath = "$PSScriptRoot\portable\Sensor Readout.exe",
    [switch]$SelfTest
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
$assemblyInfo = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\AssemblyInfo.cs') -Raw
if ($assemblyInfo -notmatch 'AssemblyFileVersion\("([^"]+)"\)') {
    throw "Could not find AssemblyFileVersion in src\AssemblyInfo.cs"
}

$buildVersion = $Matches[1]
$appSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\SensorReadoutForm.cs') -Raw
if ($appSource -notmatch 'AppVersion\s*=\s*"([^"]+)"') {
    throw "Could not find AppVersion in src\SensorReadoutForm.cs"
}
$appVersion = $Matches[1]
if ($buildVersion -ne "$appVersion.0") {
    throw "Version mismatch: AppVersion is $appVersion but AssemblyFileVersion is $buildVersion."
}

$manifestText = Get-Content -LiteralPath $manifest -Raw
if ($manifestText -notmatch 'assemblyIdentity\s+version="([^"]+)"') {
    throw "Could not find assemblyIdentity version in src\SensorReadoutApp.exe.manifest"
}
if ($Matches[1] -ne $buildVersion) {
    throw "Version mismatch: manifest assemblyIdentity version is $($Matches[1]) but AssemblyFileVersion is $buildVersion."
}

function Measure-SourceFileSize {
    $warnAtLines = 2000
    $failAtLines = 3000
    $sourceRoots = @(
        (Join-Path $PSScriptRoot 'src'),
        (Join-Path $PSScriptRoot 'PlugIns'),
        (Join-Path $PSScriptRoot 'server')
    ) | Where-Object { Test-Path -LiteralPath $_ }
    $sourceFiles = foreach ($root in $sourceRoots) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Include '*.cs', '*.ps1', '*.php' |
            Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\|\\GeneratedAssemblyInfo\\' }
    }

    $stats = foreach ($file in $sourceFiles) {
        $lineCount = 0
        $blankCount = 0
        foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
            $lineCount++
            if ([string]::IsNullOrWhiteSpace($line)) {
                $blankCount++
            }
        }

        [pscustomobject]@{
            Lines = $lineCount
            Blank = $blankCount
            Path = $file.FullName.Substring($PSScriptRoot.Length).TrimStart('\')
        }
    }

    $largest = @($stats | Sort-Object Lines -Descending | Select-Object -First 10)
    if ($largest.Count -gt 0) {
        Write-Host "Source size audit, largest files:"
        foreach ($item in $largest) {
            Write-Host ("  {0,5} lines, {1,4} blank  {2}" -f $item.Lines, $item.Blank, $item.Path)
        }
    }

    $tooLarge = @($stats | Where-Object { $_.Lines -ge $failAtLines } | Sort-Object Lines -Descending)
    if ($tooLarge.Count -gt 0) {
        $names = ($tooLarge | ForEach-Object { "$($_.Path) ($($_.Lines) lines)" }) -join '; '
        throw "Source file size audit failed. Split files at or above $failAtLines lines: $names"
    }

    $warn = @($stats | Where-Object { $_.Lines -ge $warnAtLines } | Sort-Object Lines -Descending)
    if ($warn.Count -gt 0) {
        $names = ($warn | ForEach-Object { "$($_.Path) ($($_.Lines) lines)" }) -join '; '
        Write-Warning "Large source files at or above $warnAtLines lines should be considered for future cleanup: $names"
    }
}

Measure-SourceFileSize

$generatedRoot = Join-Path $PSScriptRoot 'obj\GeneratedAssemblyInfo'
New-Item -ItemType Directory -Force -Path $generatedRoot | Out-Null

function New-GeneratedAssemblyInfo {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$OutputName
    )

    $safeTitle = $Title.Replace('\', '\\').Replace('"', '\"')
    $path = Join-Path $generatedRoot $OutputName
    @"
[assembly: System.Reflection.AssemblyTitle("$safeTitle")]
[assembly: System.Reflection.AssemblyProduct("Sensor Readout")]
[assembly: System.Reflection.AssemblyCompany("Andre Louis")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) Andre Louis and Sensor Readout contributors")]
[assembly: System.Reflection.AssemblyVersion("$buildVersion")]
[assembly: System.Reflection.AssemblyFileVersion("$buildVersion")]
[assembly: System.Reflection.AssemblyInformationalVersion("$buildVersion")]
"@ | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

if ($sdkSources.Count -gt 0) {
    $sdkAssemblyInfo = New-GeneratedAssemblyInfo -Title 'Sensor Readout Plug-In SDK' -OutputName 'PluginSdk.AssemblyInfo.cs'
    & $csc /nologo /target:library /platform:x64 /out:$sdkOutput @(@($sdkSources) + @($sdkAssemblyInfo))
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

function Assert-ConfigBindingRedirectsMatchShippedAssemblies {
    $configPath = Join-Path $portable 'Sensor Readout.exe.config'
    if (-not (Test-Path -LiteralPath $configPath)) {
        return
    }

    [xml]$config = Get-Content -LiteralPath $configPath -Raw
    $namespace = New-Object System.Xml.XmlNamespaceManager($config.NameTable)
    $namespace.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
    $redirects = $config.SelectNodes('//asm:dependentAssembly', $namespace)
    foreach ($redirect in $redirects) {
        $identity = $redirect.SelectSingleNode('asm:assemblyIdentity', $namespace)
        $binding = $redirect.SelectSingleNode('asm:bindingRedirect', $namespace)
        if ($identity -eq $null -or $binding -eq $null) {
            continue
        }

        $assemblyName = [string]$identity.name
        $redirectVersion = [string]$binding.newVersion
        if ([string]::IsNullOrWhiteSpace($assemblyName) -or [string]::IsNullOrWhiteSpace($redirectVersion)) {
            continue
        }

        $assemblyPath = Join-Path $portable ($assemblyName + '.dll')
        if (-not (Test-Path -LiteralPath $assemblyPath)) {
            throw "Config binding redirect references $assemblyName $redirectVersion, but $assemblyName.dll is not shipped in the portable folder."
        }

        $actualVersion = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
        if ($actualVersion -ne $redirectVersion) {
            throw "Config binding redirect for $assemblyName points to $redirectVersion, but shipped assembly version is $actualVersion."
        }
    }
}

Assert-ConfigBindingRedirectsMatchShippedAssemblies

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
        Get-ChildItem -LiteralPath $plugIn.FullName -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'plugin.json' } |
            ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $plugInTarget $_.Name) -Force
            }

        if ($plugInSources.Count -gt 0) {
            $plugInOutput = Join-Path $plugInTarget ($plugIn.Name + 'PlugIn.dll')
            $plugInReferences = @(
                'System.dll',
                'System.Core.dll',
                'System.Management.dll',
                (Join-Path $portable 'Newtonsoft.Json.dll'),
                $sdkOutput
            ) -join ','
            $plugInAssemblyInfo = New-GeneratedAssemblyInfo -Title ("Sensor Readout " + $plugIn.Name + " Plug-In") -OutputName ($plugIn.Name + '.AssemblyInfo.cs')
            & $csc /nologo /target:library /platform:x64 /out:$plugInOutput /reference:$plugInReferences @(@($plugInSources) + @($plugInAssemblyInfo))
    if ($LASTEXITCODE -ne 0) {
        throw "Plug-In build failed for $($plugIn.Name) with exit code $LASTEXITCODE"
    }
        }
    }
}

function Assert-BinaryVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $item = Get-Item -LiteralPath $Path
    $fileVersion = $item.VersionInfo.FileVersion
    $productVersion = $item.VersionInfo.ProductVersion
    if ($fileVersion -eq $null) { $fileVersion = '' }
    if ($productVersion -eq $null) { $productVersion = '' }
    $fileVersion = $fileVersion.Trim()
    $productVersion = $productVersion.Trim()
    if ($fileVersion -ne $ExpectedVersion -or $productVersion -ne $ExpectedVersion) {
        throw "Version mismatch for $Path. FileVersion=$fileVersion ProductVersion=$productVersion Expected=$ExpectedVersion."
    }
}

$sensorBinaries = @($OutputPath, $sdkOutput)
$plugInOutputRoot = Join-Path $portable 'Plug-Ins'
if (Test-Path -LiteralPath $plugInOutputRoot) {
    $sensorBinaries += Get-ChildItem -LiteralPath $plugInOutputRoot -Filter '*PlugIn.dll' -Recurse -File | ForEach-Object { $_.FullName }
}
foreach ($binary in $sensorBinaries | Where-Object { Test-Path -LiteralPath $_ }) {
    Assert-BinaryVersion -Path $binary -ExpectedVersion $buildVersion
}

$dataSource = Join-Path $PSScriptRoot 'Data'
$dataTarget = Join-Path $portable 'Data'
if (Test-Path $dataSource) {
    New-Item -ItemType Directory -Force -Path $dataTarget | Out-Null
    $dataFiles = Get-ChildItem -LiteralPath $dataSource -File -Force
    if ($dataFiles.Count -gt 0) {
        Copy-Item -LiteralPath $dataFiles.FullName -Destination $dataTarget -Force
    }
}

$docsSource = Join-Path $PSScriptRoot 'Docs'
$docsTarget = Join-Path $portable 'Docs'
if (Test-Path $docsSource) {
    New-Item -ItemType Directory -Force -Path $docsTarget | Out-Null
    $docsFiles = Get-ChildItem -LiteralPath $docsSource -Filter 'README-*.html' -File -Force
    if ($docsFiles.Count -gt 0) {
        Copy-Item -LiteralPath $docsFiles.FullName -Destination $docsTarget -Force
    }
}

$langTarget = Join-Path $portable 'Langs'
if (Test-Path $langTarget) {
    New-Item -ItemType Directory -Force -Path $dataTarget | Out-Null
    $languageHashes = [ordered]@{}
    foreach ($languageFile in Get-ChildItem -LiteralPath $langTarget -Recurse -File | Sort-Object FullName) {
        $relative = $languageFile.FullName.Substring($langTarget.Length).TrimStart('\')
        $languageHashes[$relative] = (Get-FileHash -LiteralPath $languageFile.FullName -Algorithm SHA256).Hash
    }

    $manifest = [ordered]@{
        Version = 1
        UpdatedUtc = [DateTime]::UtcNow.ToString('o')
        Files = $languageHashes
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dataTarget 'BundledLanguageHashes.json') -Encoding UTF8
}

if (Test-Path $plugInOutputRoot) {
    New-Item -ItemType Directory -Force -Path $dataTarget | Out-Null
    $plugInHashes = [ordered]@{}
    foreach ($plugInFile in Get-ChildItem -LiteralPath $plugInOutputRoot -Recurse -File | Sort-Object FullName) {
        $relative = $plugInFile.FullName.Substring($plugInOutputRoot.Length).TrimStart('\')
        $plugInHashes[$relative] = (Get-FileHash -LiteralPath $plugInFile.FullName -Algorithm SHA256).Hash
    }

    $manifest = [ordered]@{
        Version = 1
        UpdatedUtc = [DateTime]::UtcNow.ToString('o')
        Files = $plugInHashes
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dataTarget 'BundledPlugInHashes.json') -Encoding UTF8
}

foreach ($preservedFolderName in @('Config', 'Logs', 'Reports')) {
    $preservedFolder = Join-Path $portable $preservedFolderName
    if (Test-Path -LiteralPath $preservedFolder) {
        Get-ChildItem -LiteralPath $preservedFolder -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Force -Path $preservedFolder | Out-Null
    }
}

Write-Host "Built $OutputPath"

if ($SelfTest) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $selfTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) "SensorReadout-SelfTest-$stamp"
    $selfTestApp = Join-Path $selfTestRoot 'App'
    $selfTestOutput = Join-Path $selfTestRoot 'Results'
    New-Item -ItemType Directory -Force -Path $selfTestApp | Out-Null
    New-Item -ItemType Directory -Force -Path $selfTestOutput | Out-Null

    robocopy $portable $selfTestApp /E /XD Config Logs Reports Backups 'Update Backups' 'Update Temp' /XF '*.pdb' /R:2 /W:1 /NFL /NDL /NP | Out-Host
    if ($LASTEXITCODE -ge 8) {
        throw "Could not create self-test app copy. Robocopy exit code $LASTEXITCODE"
    }

    foreach ($folder in @('Config', 'Logs', 'Reports')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $selfTestApp $folder) | Out-Null
    }

    $selfTestExe = Join-Path $selfTestApp 'Sensor Readout.exe'
    $selfTestProcess = Start-Process -FilePath $selfTestExe -ArgumentList @('--self-test', $selfTestOutput) -WorkingDirectory $selfTestApp -WindowStyle Hidden -Wait -PassThru
    $selfTestExit = $selfTestProcess.ExitCode
    $summary = Join-Path $selfTestOutput 'SelfTest-summary.txt'
    if (Test-Path -LiteralPath $summary) {
        Get-Content -LiteralPath $summary | Out-Host
    }

    if ($selfTestExit -ne 0) {
        throw "Self-test failed with exit code $selfTestExit. Results: $selfTestOutput"
    }

    Write-Host "Self-test passed. Results: $selfTestOutput"
}
