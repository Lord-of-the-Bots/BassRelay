param(
    [string]$SimHubDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\output\BassRelay-SimHub-preview')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$pluginDotnet = Join-Path $pluginRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $pluginDotnet)) {
    $pluginDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
if (-not $SimHubDirectory) { $SimHubDirectory = $env:SIMHUB_INSTALL_PATH }
if (-not $SimHubDirectory) { $SimHubDirectory = Join-Path ${env:ProgramFiles(x86)} 'SimHub' }
if (-not (Test-Path -LiteralPath (Join-Path $SimHubDirectory 'SimHub.Plugins.dll'))) {
    throw 'Pass -SimHubDirectory pointing to an installed or extracted SimHub 9.12.8 folder.'
}
$SimHubDirectory = (Resolve-Path -LiteralPath $SimHubDirectory).Path
& $pluginDotnet build (Join-Path $PSScriptRoot 'BassRelay.SimHub.csproj') -c Release "-p:SimHubDir=$SimHubDirectory" -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$pluginBin = Join-Path $PSScriptRoot 'bin\Release\net48'
# SimHub supplies these dependencies. Never replace the host's copies.
foreach ($dependency in @('NAudio.Core.dll', 'NAudio.Wasapi.dll')) {
    $hostDependency = Join-Path $SimHubDirectory $dependency
    $buildDependency = Join-Path $pluginBin $dependency
    if (-not (Test-Path -LiteralPath $hostDependency)) { throw "Missing host dependency: $dependency" }
    if ((Get-FileHash -LiteralPath $hostDependency).Hash -ne (Get-FileHash -LiteralPath $buildDependency).Hash) {
        throw "Host $dependency differs from the validated version. Check compatibility before packaging."
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$pluginFiles = @('BassRelay.SimHub.dll', 'BassRelay.Audio.dll')
foreach ($file in $pluginFiles) { Copy-Item -LiteralPath (Join-Path $pluginBin $file) -Destination $OutputDirectory -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $OutputDirectory -Force
$archivePath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputDirectory))) 'BassRelay-SimHub-0.3.0-preview.zip'
# Explicit files prevent unrelated logs, settings or test tools entering the package.
$archiveFiles = @($pluginFiles + 'README.md' | ForEach-Object { Join-Path $OutputDirectory $_ })
Compress-Archive -LiteralPath $archiveFiles -DestinationPath $archivePath -Force
Write-Output "Plugin package: $archivePath"
