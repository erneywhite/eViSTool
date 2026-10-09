# Сборка релиза eViSTool: dist\eViSTool-<версия>\ и zip рядом; для Linux — dist\eViSTool-<версия>-linux-x64\ и tar.gz.
#
#   pwsh build/publish.ps1                 — версия из Directory.Build.props
#   pwsh build/publish.ps1 -Version 0.1.0-alpha.2
#
# Окно (eViSTool.exe) — один файл без рантайма внутри: ему нужен .NET 10 Desktop Runtime, тот же, что и клиенту
# игры, поэтому у игроков он уже стоит. Агент (eViSTool.Agent.exe) — один файл с рантаймом внутри: ему нужен ещё
# и ASP.NET Core, которого у игроков обычно нет, а агент должен запускаться и на голой машине с сервером.
# Рантайм в агенте обрезан до того, что он использует (PublishTrimmed) — иначе exe весил бы ~50 МБ.
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
# релиз — всегда с чистого листа: иначе сборка может счесть старые файлы актуальными (например, после перевода часов назад)
Get-ChildItem (Join-Path $root 'src') -Directory | ForEach-Object {
    Remove-Item -Recurse -Force (Join-Path $_.FullName 'bin/Release'), (Join-Path $_.FullName 'obj/Release') -ErrorAction SilentlyContinue
}

# нативная библиотека SQLite (копии мира) вшивается в exe: в релиз попадают только сами программы
$common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true')

dotnet publish (Join-Path $root 'src/eViSTool.App') @common --self-contained false -o $out
if ($LASTEXITCODE) { throw "publish eViSTool.App: $LASTEXITCODE" }

dotnet publish (Join-Path $root 'src/eViSTool.Agent') @common --self-contained true `
    '-p:EnableCompressionInSingleFile=true' `
    '-p:PublishTrimmed=true' '-p:TrimMode=full' '-p:EnableTrimAnalyzer=false' -o $out  # обрезка: ~17 МБ вместо ~50; что сохраняется целиком — в eViSTool.Agent.csproj
if ($LASTEXITCODE) { throw "publish eViSTool.Agent: $LASTEXITCODE" }

# в релиз — только сами программы; папку data программа создаст рядом при первом запуске
Get-ChildItem $out -File | Where-Object { $_.Name -notin 'eViSTool.exe', 'eViSTool.Agent.exe' } | Remove-Item -Force
Get-ChildItem $out -Directory | Remove-Item -Recurse -Force
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.ru.md') {
    if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $out }
}

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
# контрольная сумма рядом с архивом: по ней программа проверяет скачанное, когда API GitHub недоступен (лимит запросов)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path $zip -Leaf)`n")
Get-ChildItem $out, $zip, "$zip.sha256" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

# ---- Linux: только агент (окна там нет) — так же один файл с обрезанным рантаймом внутри, .NET на машине не нужен.
# Архив — tar.gz, распаковывается в отдельную папку рядом с сервером (папку игры обновляют через rm -rf).
$linuxOut = Join-Path $dist "$name-linux-x64"
$tgz = Join-Path $dist "$name-linux-x64.tar.gz"
Remove-Item -Recurse -Force $linuxOut, $tgz, "$tgz.sha256" -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'src/eViSTool.Agent') -c Release -r linux-x64 "-p:Version=$Version" `
    '-p:PublishSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:IncludeNativeLibrariesForSelfExtract=true' `
    --self-contained true '-p:EnableCompressionInSingleFile=true' `
    '-p:PublishTrimmed=true' '-p:TrimMode=full' '-p:EnableTrimAnalyzer=false' -o $linuxOut
if ($LASTEXITCODE) { throw "publish eViSTool.Agent linux-x64: $LASTEXITCODE" }

Get-ChildItem $linuxOut -File | Where-Object { $_.Name -ne 'eViSTool.Agent' } | Remove-Item -Force
Get-ChildItem $linuxOut -Directory | Remove-Item -Recurse -Force
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.ru.md') {
    if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $linuxOut }
}

# tar собираем сами (System.Formats.Tar из .NET, на котором работает pwsh), с правами Unix в каждой записи: агент —
# rwxr-xr-x, остальное — rw-r--r--. У файлов на Windows этих прав нет, и ни Compress-Archive, ни tar.exe их не поставят,
# а распаковать архив и получить «Permission denied» на первом же запуске — плохое начало
$stream = [IO.File]::Create($tgz)
try {
    $gz = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal, $true)
    $tar = [Formats.Tar.TarWriter]::new($gz, [Formats.Tar.TarEntryFormat]::Ustar, $true)
    foreach ($file in Get-ChildItem $linuxOut -File | Sort-Object Name) {
        $entry = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $file.Name)
        $entry.Mode = [IO.UnixFileMode][Convert]::ToInt32($(if ($file.Name -eq 'eViSTool.Agent') { '755' } else { '644' }), 8)
        $entry.ModificationTime = [DateTimeOffset]$file.LastWriteTimeUtc
        $data = $file.OpenRead()
        try {
            $entry.DataStream = $data
            $tar.WriteEntry($entry)
        }
        finally { $data.Dispose() }
    }
    $tar.Dispose() # конец архива
    $gz.Dispose()
}
finally { $stream.Dispose() }
$hash = (Get-FileHash $tgz -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$tgz.sha256", "$hash  $(Split-Path $tgz -Leaf)`n")
Get-ChildItem $linuxOut, $tgz, "$tgz.sha256" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
