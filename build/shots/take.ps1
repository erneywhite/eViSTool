# Снимки для README и ModDB на стенде shots: окно из app\ (data рядом), профиль Main — копия модов и настроек игры
# (clientdata), Survival — сервер в srvdata, запущенный агентом. Каждый экран — отдельный запуск окна в режиме снимка.
param([string[]]$Only)
$S = $PSScriptRoot
$out = Join-Path $S 'out'
$repo = 'C:\Users\erney\Documents\ClaudeCode\eViSTool'
New-Item -ItemType Directory -Force $out | Out-Null
$shots = [ordered]@{
    'mods'           = @('mods.card.primitivesurvival', 'Main', 14)
    'history'        = @('mods.history', 'Main', 8)
    'catalog'        = @('catalog.card', 'Main', 18)
    'mod-settings'   = @('modconfig', 'Main', 8)
    'server'         = @('server.console', 'Survival', 8)
    'players'        = @('server.players', 'Survival', 8)
    'stats'          = @('server.stats.week', 'Survival', 10)
    'journal'        = @('server.journal', 'Survival', 8)
    'announcements'  = @('server.announcements', 'Survival', 8)
    'config'         = @('server.config.general', 'Survival', 8)
    'schedule'       = @('server.schedule', 'Survival', 8)
    'remote'         = @('server.remote', 'Survival', 8)
    'notify'         = @('server.notify', 'Survival', 8)
    'also-install'   = @('dialog.alsoinstall', 'Main', 6)
    'crash'          = @('dialog.crash.server', 'Survival', 6)
    'mod-errors'     = @('dialog.moderrors', 'Main', 6)
    'notify-channel' = @('dialog.notifychannel.telegram', 'Main', 6)
    'profile'        = @('dialog.clientprofile', 'Main', 8)
    'find-server'    = @('dialog.findserver', 'Main', 14)
    'play-menu'      = @('mods.playmenu', 'Main', 10)
    'remote-mods'    = @('mods.card.carryon', 'Home server', 16)
}
# окну нового профиля нужны моды стенда build/snapshot.ps1 (по именам файлов) — кладём на время снимка
$fixture = Join-Path $repo 'tests\snapshots\fixture\vsdata'
$extra = @(@('Mods', 'carryon_1.8.0.zip'), @('Mods', 'primitivesurvival_3.7.6.zip'), @('ModConfig', 'carryon.json'))
foreach ($name in $shots.Keys) {
    if ($Only -and $name -notin $Only) { continue }
    $screen, $profile, $wait = $shots[$name]
    $png = Join-Path $out "$name.png"
    Remove-Item $png -ErrorAction SilentlyContinue
    $placed = @()
    if ($name -eq 'profile') {
        foreach ($e in $extra) {
            $to = Join-Path $S "clientdata\$($e[0])\$($e[1])"
            if (-not (Test-Path $to)) { Copy-Item (Join-Path $fixture "$($e[0])\$($e[1])") $to; $placed += $to }
        }
    }
    $env:EVISTOOL_SNAPSHOT = "screen=$screen;out=$png;size=1400x860;wait=$wait;profile=$profile"
    $p = Start-Process (Join-Path $S 'app\eViSTool.exe') -WorkingDirectory (Join-Path $S 'app') -PassThru
    if (-not $p.WaitForExit(($wait + 60) * 1000)) { $p.Kill(); "$name — не закрылось, снято принудительно" }
    $placed | ForEach-Object { Remove-Item $_ }
    if (Test-Path $png) { "$name ok" } else { "$name — снимка нет" }
}
Remove-Item Env:EVISTOOL_SNAPSHOT -ErrorAction SilentlyContinue
