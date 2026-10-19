# Скриншоты для README и ModDB

Витрина: окно в `app/` (портативное, `data` рядом), клиентский профиль **Main** на копии модов и настроек игры
(`clientdata`, оригинал только читается, токены, адреса и пароли из `clientsettings.json` убираются), сервер **Survival**
в `srvdata` под агентом и удалённый профиль **Home server** на того же агента по коду подключения.

1. Сборка окна и агента в `app/`:
   `dotnet publish src/eViSTool.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o build/shots/app`
   (то же для `src/eViSTool.Agent`).
2. `python -I -X utf8 build/shots/build.py` — копия модов, папка сервера, настройки окна, неделя статистики, журнал.
3. Запустить агента с сервером (аргументы — как у окна, `AgentLauncher`): `app/eViSTool.Agent.exe --profile srv1 --exe
   <игра>/VintagestoryServer.exe --data build/shots/srvdata --agents-dir build/shots/app/data/agents --start --lang en`.
4. Остановить агента и сервер (по PID), `setup2.py` и `fix3.py` (вкладки сервера, история модов, статистика с игроками),
   снова запустить агента.
5. Код удалённого профиля: `dotnet run --project build/shots/codegen -- build/shots/app/data/agents`, вписать его в
   `RemoteCode` профиля «Home server» в `app/data/settings.json`.
6. `pwsh build/shots/take.ps1 [-Only mods,stats]` → `build/shots/out/*.png`, скопировать в `docs/screenshots`.

Путь к игре в `build.py` — `S:\Games\Vintagestory`, поправь под себя. После съёмки останови агента и сервер по PID
(не по имени процесса: рядом может работать настоящий eViSTool).
