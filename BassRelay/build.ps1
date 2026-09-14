param(
    [switch]$SkipTests,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\output\BassRelay')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$bassDotnet = Join-Path $PSScriptRoot '..\.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $bassDotnet)) {
    $bassDotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
}
if (-not $bassDotnet -or -not (Test-Path -LiteralPath $bassDotnet)) {
    throw 'Для сборки установите .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
}
$bassTestsProject = Join-Path $PSScriptRoot '..\BassRelay.Tests\BassRelay.Tests.csproj'
if (-not $SkipTests -and (Test-Path -LiteralPath $bassTestsProject)) {
    & $bassDotnet run --project $bassTestsProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Проверки завершились с ошибкой.' }
}
& $bassDotnet publish (Join-Path $PSScriptRoot 'BassRelay.csproj') --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $OutputDirectory 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Licenses') -Destination $OutputDirectory -Recurse -Force
Write-Host "Готово: $(Join-Path $OutputDirectory 'BassRelay.exe')"
