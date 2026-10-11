<#
.SYNOPSIS
    Снимки экранов окна на WPF и на Avalonia — для сверки при переносе окна на Avalonia.

.DESCRIPTION
    Собирает оба окна во временную папку, кладёт рядом с каждым данные стенда (tests/snapshots/fixture: серверный
    профиль «Стенд» с четырьмя модами, без игры и без сети) и запускает их с переменной EVISTOOL_SNAPSHOT. Окно само
    переходит на экран, ждёт, сохраняет себя в PNG и закрывается. Данные стенда перед каждым запуском — свежая копия,
    поэтому снимки повторяемы. Настоящие данные пользователя не трогаются: окно берёт папку data рядом с собой.

    Экраны и их имена — в src/eViSTool.Presentation/Snapshots.cs (mods, mods.card, mods.history, catalog, modconfig,
    settings, about, server[.console|players|stats|announcements|config[.general|world|roles|advanced]|schedule|
    remote|notify]).

.EXAMPLE
    pwsh build/snapshot.ps1 -Screens mods,mods.card,settings
    # → <Out>\mods.wpf.png, <Out>\mods.avalonia.png, ...

.EXAMPLE
    pwsh build/snapshot.ps1 -Screens server.config.world -Only Avalonia -NoBuild -Out C:\snap
#>
param(
    [Parameter(Mandatory)][string[]]$Screens,
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'evistool-snap'),
    [ValidateSet('Both', 'Wpf', 'Avalonia')][string]$Only = 'Both',
    [string]$Size = '1400x860',
    [double]$Wait = 4,
    [string]$Profile = 'Стенд',
    # не пересобирать: взять сборки из прошлого запуска
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Screens = $Screens -split ',' | ForEach-Object Trim | Where-Object { $_ } # pwsh -File отдаёт «a,b» одной строкой
$root = Split-Path $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) 'evistool-snap-work'
$fixture = Join-Path $root 'tests/snapshots/fixture'
if (-not $IsWindows -and $Only -ne 'Avalonia') { $Only = 'Avalonia' } # окна на WPF есть только на Windows

$apps = @()
if ($Only -in 'Both', 'Wpf') { $apps += @{ Name = 'wpf'; Project = 'src/eViSTool.App'; Exe = 'eViSTool.exe' } }
if ($Only -in 'Both', 'Avalonia') {
    $apps += @{ Name = 'avalonia'; Project = 'src/eViSTool.Desktop'; Exe = $(if ($IsWindows) { 'eViSTool.Desktop.exe' } else { 'eViSTool.Desktop' }) }
}

New-Item -ItemType Directory -Force $work, $Out | Out-Null

foreach ($app in $apps) {
    $dir = Join-Path $work $app.Name
    if (-not $NoBuild) {
        Write-Host "Сборка $($app.Project)…"
        dotnet build (Join-Path $root $app.Project) -c Debug -o $dir --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Не собралось: $($app.Project)" }
    }
    $app.Dir = $dir
}

# данные стенда: свежая копия на каждый запуск окна (окно может что-то записать — например, отключить мод)
function Reset-Fixture([string]$appDir) {
    $vsdata = Join-Path $work 'vsdata'
    $game = Join-Path $work 'game'
    if (Test-Path $vsdata) { [IO.Directory]::Delete($vsdata, $true) }
    Copy-Item (Join-Path $fixture 'vsdata') $vsdata -Recurse
    New-Item -ItemType Directory -Force $game | Out-Null

    $data = Join-Path $appDir 'data'
    if (Test-Path $data) { [IO.Directory]::Delete($data, $true) }
    New-Item -ItemType Directory -Force $data | Out-Null
    $json = { param($s) ($s | ConvertTo-Json).Trim('"') } # экранировать путь для JSON
    $settings = (Get-Content (Join-Path $fixture 'settings.template.json') -Raw -Encoding utf8).
        Replace('{{VSDATA}}', (& $json $vsdata)).Replace('{{GAME}}', (& $json $game))
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'), $settings)
}

$made = @()
foreach ($screen in $Screens) {
    foreach ($app in $apps) {
        Reset-Fixture $app.Dir
        $png = Join-Path $Out "$screen.$($app.Name).png"
        if (Test-Path $png) { Remove-Item $png }
        $env:EVISTOOL_SNAPSHOT = "screen=$screen;out=$png;size=$Size;wait=$Wait;profile=$Profile"
        $p = Start-Process (Join-Path $app.Dir $app.Exe) -WorkingDirectory $app.Dir -PassThru
        if (-not $p.WaitForExit([int](($Wait + 60) * 1000))) {
            $p.Kill() # завис — закрыть именно этот процесс (по нему, не по имени: рядом может работать настоящий eViSTool)
            Write-Warning "$($app.Name) $screen — окно не закрылось само, снято принудительно"
        }
        if (Test-Path $png) { $made += $png }
        else { Write-Warning "$($app.Name) $screen — снимка нет (журнал ошибок: $(Join-Path $app.Dir 'data/logs'))" }
    }
}
Remove-Item Env:EVISTOOL_SNAPSHOT -ErrorAction SilentlyContinue
$made | ForEach-Object { Write-Host $_ }
