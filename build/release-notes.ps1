# Текст релиза на GitHub из списка изменений для ModDB (docs/moddb/changelog-<версия>.txt): английская часть,
# «---», русская. Первая строка английской части и заголовки разделов («New», «Fixes», «Новое», «Исправления») — жирным,
# после английской части — ссылки на обе страницы ModDB, у русской строка-итог без «eViSTool <версия>: ».
# Окно «Вышла новая версия» разбирает именно такой текст (ReleaseNotes.cs).
#
#   pwsh build/release-notes.ps1 -Version 0.10.5 [-Recommended]   → dist/notes-0.10.5.md
#
# -Recommended — первой строкой невидимая на GitHub пометка «<!-- evistool: recommended -->»: окно выделит обновление.
param([Parameter(Mandatory)][string]$Version, [switch]$Recommended)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "docs/moddb/changelog-$Version.txt"
if (-not (Test-Path $source)) { throw "Нет списка изменений: $source" }

$parts = ([IO.File]::ReadAllText($source) -replace "`r`n", "`n") -split "`n---`n"
if ($parts.Count -ne 2) { throw "В $source должно быть две части, разделённые строкой «---»" }
$headings = 'New', 'Fixes', 'Новое', 'Исправления'

function Format-Part([string]$part, [bool]$english) {
    $lines = $part.Trim() -split "`n"
    $out = for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($i -eq 0) {
            if ($english) { "**$line**" }
            else { $s = $line -replace '^eViSTool \S+: ', ''; $s.Substring(0, 1).ToUpper() + $s.Substring(1) }
        }
        elseif ($headings -contains $line.Trim()) { "**$($line.Trim())**" }
        else { $line }
    }
    $out -join "`n"
}

$moddb = 'On ModDB: [eViSTool — Windows](https://mods.vintagestory.at/evistoolwin) and [eViSTool — Linux](https://mods.vintagestory.at/evistoollinux).'
$body = (Format-Part $parts[0] $true) + "`n`n$moddb`n`n---`n`n" + (Format-Part $parts[1] $false) + "`n"
if ($Recommended) { $body = "<!-- evistool: recommended -->`n" + $body }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$target = Join-Path $dist "notes-$Version.md"
[IO.File]::WriteAllText($target, $body, [Text.UTF8Encoding]::new($false))
Write-Host "Текст релиза: $target"
