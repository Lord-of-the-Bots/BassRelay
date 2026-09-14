param(
    [switch]$SkipPublish,
    [switch]$SkipTests,
    [string]$CompilerPath,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\.tools\installer-publish'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\output\Installer')
)

$ErrorActionPreference = 'Stop'
$bassProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bassPublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$bassInstallerOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

if (-not $CompilerPath) {
    $bassCompilerDirectory = Join-Path $bassProjectRoot '.tools\innosetup'
    $CompilerPath = Join-Path $bassCompilerDirectory 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        $CompilerPath = Get-ChildItem -LiteralPath $bassCompilerDirectory -Filter 'ISCC.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName -First 1
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Не найден Inno Setup. Поместите компилятор в .tools\innosetup или укажите -CompilerPath с полным путём к ISCC.exe.'
}
$bassCompiler = (Resolve-Path -LiteralPath $CompilerPath).Path

[xml]$bassProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BassRelay.csproj') -Raw
$bassVersion = [string]($bassProject.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($bassVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw 'В BassRelay.csproj нужна числовая версия для установщика, например 1.2.0.'
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests:$SkipTests -OutputDirectory $bassPublishDirectory
}

$bassPayloadFiles = @('BassRelay.exe', 'README.md', 'THIRD-PARTY-NOTICES.txt')
foreach ($bassPayloadFile in $bassPayloadFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $bassPublishDirectory $bassPayloadFile) -PathType Leaf)) {
        throw "В папке публикации отсутствует $bassPayloadFile. Выполните сборку без -SkipPublish."
    }
}
if (-not (Get-ChildItem -LiteralPath (Join-Path $bassPublishDirectory 'Licenses') -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
    throw 'В папке публикации отсутствуют лицензии. Выполните сборку без -SkipPublish.'
}
$bassPublishedExe = Join-Path $bassPublishDirectory 'BassRelay.exe'
$bassPublishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($bassPublishedExe).ProductVersion
if (-not $bassPublishedVersion -or $bassPublishedVersion.Split('+')[0] -ne $bassVersion) {
    throw "Версия опубликованного EXE ($bassPublishedVersion) отличается от версии проекта ($bassVersion). Выполните сборку без -SkipPublish."
}

New-Item -Path $bassInstallerOutputDirectory -ItemType Directory -Force | Out-Null
$bassCompilerArguments = @(
    "/DAppVersion=$bassVersion",
    "/DPublishDir=$bassPublishDirectory",
    "/DInstallerOutputDir=$bassInstallerOutputDirectory",
    (Join-Path $PSScriptRoot 'Installer\BassRelay.iss')
)
& $bassCompiler @bassCompilerArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Не удалось собрать установщик.'
}
$bassInstaller = Join-Path $bassInstallerOutputDirectory "BassRelay-Setup-$bassVersion.exe"
if (-not (Test-Path -LiteralPath $bassInstaller -PathType Leaf)) {
    throw 'Компилятор завершился, но готовый установщик не найден.'
}
Write-Host "Готово: $bassInstaller"
