# Сборка релиза eViSTool: dist\eViSTool-<версия>\ и zip рядом.
#
#   pwsh build/publish.ps1                 — версия из Directory.Build.props
#   pwsh build/publish.ps1 -Version 0.1.0-alpha.2
#
# Окно (eViSTool.exe) — один файл без рантайма внутри: ему нужен .NET 10 Desktop Runtime, тот же, что и клиенту
# игры, поэтому у игроков он уже стоит. Агент (eViSTool.Agent.exe) — один файл с рантаймом внутри: ему нужен ещё
# и ASP.NET Core, которого у игроков обычно нет, а агент должен запускаться и на голой машине с сервером.
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$name = "eViSTool-$Version"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
$zip = Join-Path $dist "$name-win-x64.zip"
Remove-Item -Recurse -Force $out, $zip -ErrorAction SilentlyContinue

$common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false')

dotnet publish (Join-Path $root 'src/eViSTool.App') @common --self-contained false -o $out
if ($LASTEXITCODE) { throw "publish eViSTool.App: $LASTEXITCODE" }

dotnet publish (Join-Path $root 'src/eViSTool.Agent') @common --self-contained true `
    '-p:EnableCompressionInSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o $out
if ($LASTEXITCODE) { throw "publish eViSTool.Agent: $LASTEXITCODE" }

# в релиз — только сами программы; папку data программа создаст рядом при первом запуске
Get-ChildItem $out -File | Where-Object { $_.Name -notin 'eViSTool.exe', 'eViSTool.Agent.exe' } | Remove-Item -Force
Get-ChildItem $out -Directory | Remove-Item -Recurse -Force
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.ru.md') {
    if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $out }
}

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-ChildItem $out, $zip | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
