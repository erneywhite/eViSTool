<#
.SYNOPSIS
    Снимки экранов окна — для проверки вида после правок и для скриншотов.

.DESCRIPTION
    Собирает окно во временную папку, кладёт рядом с ним данные стенда (tests/snapshots/fixture: серверный
    профиль «Стенд» с четырьмя модами, без игры и без сети) и запускает их с переменной EVISTOOL_SNAPSHOT. Окно само
    переходит на экран, ждёт, сохраняет себя в PNG и закрывается. Данные стенда перед каждым запуском — свежая копия,
    поэтому снимки повторяемы. Настоящие данные пользователя не трогаются: окно берёт папку data рядом с собой.

    Экраны и их имена — в src/eViSTool.Presentation/Snapshots.cs (mods, mods.card, mods.history, catalog, modconfig,
    profiles, settings, about, server[.console|players|stats|announcements|config[.general|world|roles|advanced]|schedule|
    remote|notify]).

.EXAMPLE
    pwsh build/snapshot.ps1 -Screens mods,mods.card,profiles,settings
    # → <Out>\mods.png, <Out>\mods.card.png, ... (папка Out печатается в конце)

.EXAMPLE
    pwsh build/snapshot.ps1 -Screens server.config.world -NoBuild -Out C:\snap

.EXAMPLE
    pwsh build/snapshot.ps1 -Screens mods -GameDir C:\snap-game
    # профиль стенда смотрит в C:\snap-game: заставки из assets/game/textures/gui/backgrounds — фон под боковой панелью

.EXAMPLE
    pwsh build/snapshot.ps1 -Screens mods -NoBackdrop
    # без фона из игры: под боковой панелью заглушка
#>
param(
    [Parameter(Mandatory)][string[]]$Screens,
    # по умолчанию — evistool-snap-<метка копии репозитория> во временной папке
    [string]$Out,
    [string]$Size = '1400x860',
    [double]$Wait = 4,
    [string]$Profile = 'Стенд',
    # папка игры профиля стенда (окно её только читает); по умолчанию — пустая временная. Фон из игры окно ищет и в
    # установленной на компьютере игре — на машине с игрой он будет и без GameDir
    [string]$GameDir,
    # выключить «Фон из игры»: под боковой панелью заглушка («горизонтали»), даже если игра установлена
    [switch]$NoBackdrop,
    # не пересобирать: взять сборки из прошлого запуска (если они есть)
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Screens = $Screens -split ',' | ForEach-Object Trim | Where-Object { $_ } # pwsh -File отдаёт «a,b» одной строкой
$root = Split-Path $PSScriptRoot
# своя временная папка у каждой копии репозитория: агенты в разных worktree снимают одновременно и не мешают друг другу
$tag = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Substring(0, 8).ToLowerInvariant()
$work = Join-Path ([IO.Path]::GetTempPath()) "evistool-snap-work-$tag"
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) "evistool-snap-$tag" }
$fixture = Join-Path $root 'tests/snapshots/fixture'

$apps = @(@{ Name = 'avalonia'; Project = 'src/eViSTool.Desktop'; Exe = $(if ($IsWindows) { 'eViSTool.exe' } else { 'eViSTool' }) })

New-Item -ItemType Directory -Force $work, $Out | Out-Null

foreach ($app in $apps) {
    $dir = Join-Path $work $app.Name
    if (-not $NoBuild -or -not (Test-Path (Join-Path $dir $app.Exe))) {
        Write-Host "Сборка $($app.Project)…"
        dotnet build (Join-Path $root $app.Project) -c Debug -o $dir --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Не собралось: $($app.Project)" }
    }
    $app.Dir = $dir
}

# данные стенда: свежая копия на каждый запуск окна (окно может что-то записать — например, отключить мод)
function Reset-Fixture([string]$appDir) {
    $vsdata = Join-Path $work 'vsdata'
    $game = if ($GameDir) { [IO.Path]::GetFullPath($GameDir) } else { Join-Path $work 'game' }
    if (Test-Path $vsdata) { [IO.Directory]::Delete($vsdata, $true) }
    Copy-Item (Join-Path $fixture 'vsdata') $vsdata -Recurse
    if (-not $GameDir) { New-Item -ItemType Directory -Force $game | Out-Null }

    $data = Join-Path $appDir 'data'
    if (Test-Path $data) { [IO.Directory]::Delete($data, $true) }
    New-Item -ItemType Directory -Force $data | Out-Null
    $json = { param($s) ($s | ConvertTo-Json).Trim('"') } # экранировать путь для JSON
    $settings = (Get-Content (Join-Path $fixture 'settings.template.json') -Raw -Encoding utf8).
        Replace('{{VSDATA}}', (& $json $vsdata)).Replace('{{GAME}}', (& $json $game))
    if ($NoBackdrop) {
        $parsed = $settings | ConvertFrom-Json
        $parsed | Add-Member GameBackground $false -Force
        $settings = $parsed | ConvertTo-Json -Depth 20
    }
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'), $settings)
    Copy-Item (Join-Path $fixture 'notify-channels.json') $data # каналы оповещений: логотипы в «Настройках» и у сервера

    # файлы агента профиля (журнал действий): время в шаблоне — «{{AGO:минуты}}» назад от сейчас, чтобы записи не устаревали
    $agents = Join-Path $data 'agents'
    New-Item -ItemType Directory -Force $agents | Out-Null
    $now = [DateTime]::UtcNow
    foreach ($file in Get-ChildItem (Join-Path $fixture 'agents') -File) {
        $text = [regex]::Replace((Get-Content $file.FullName -Raw -Encoding utf8), '\{\{AGO:(\d+)\}\}',
            { param($m) $now.AddMinutes(-[int]$m.Groups[1].Value).ToString('yyyy-MM-ddTHH:mm:ssZ') })
        [IO.File]::WriteAllText((Join-Path $agents $file.Name), $text)
    }
}

$made = @()
foreach ($screen in $Screens) {
    foreach ($app in $apps) {
        Reset-Fixture $app.Dir
        $png = Join-Path $Out "$screen.png"
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
if (-not $made) { exit 1 }
