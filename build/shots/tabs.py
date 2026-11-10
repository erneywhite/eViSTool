# Данные вкладок для скриншотов: история модов профиля Main, оповещения, объявления, расписание и удалённый доступ
# сервера Survival, заполненный serverconfig.json. Сервер в это время остановлен (serverconfig держит в памяти).
import json, os, base64, secrets, datetime as dt

S = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(S, 'app', 'data')
AG = os.path.join(DATA, 'agents')
SRV = os.path.join(S, 'srvdata')

def wjson(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f: json.dump(obj, f, ensure_ascii=False, indent=2)

utc = dt.datetime.now(dt.timezone.utc)
def at(hours): return (utc - dt.timedelta(hours=hours)).strftime('%Y-%m-%dT%H:%M:%S.0000000Z')

# ---- история модов Main: четыре сеанса, между ними запуски игры
hist = []
def session(sid, source, hours, changes, launch_after=None):
    for i, (mod, name, frm, to) in enumerate(changes):
        e = {'Op': sid, 'At': at(hours - i * 0.01), 'Source': source, 'ModId': mod, 'Name': name, 'From': frm, 'To': to}
        hist.append({k: v for k, v in e.items() if v is not None})
    if launch_after is not None:
        hist.append({'Op': '', 'At': at(launch_after), 'Source': '', 'ModId': '', 'Event': 'launch'})
session('s1', 'updateAll', 74, [('primitivesurvival', 'Primitive Survival', '3.7.5', '3.7.6'),
                                ('expandedfoods', 'Expanded Foods', '1.6.1', '1.6.2')], launch_after=73.5)
session('s2', 'catalog', 50, [('morepiles', 'More Piles', None, '2.3.1')], launch_after=49)
session('s3', 'rollback', 26, [('bloodtrail', 'BloodTrail', '1.2.13', '1.2.12')], launch_after=25)
session('s4', 'updateAll', 1.2, [('footprints', 'Footprints', '1.2.13', '1.2.14'),
                                 ('carryon', 'Carry On', '2.0.0-pre.8', '2.0.0-rc.1'),
                                 ('autolootreforged', 'AutoLootReforged', '1.0.5', '1.0.6')])
os.makedirs(os.path.join(DATA, 'ModHistory'), exist_ok=True)
with open(os.path.join(DATA, 'ModHistory', 'main.jsonl'), 'w', encoding='utf-8', newline='\n') as f:
    for e in hist: f.write(json.dumps(e, ensure_ascii=False) + '\n')

# ---- оповещения сервера: каналы из notify-channels.json, маршруты
channels = json.load(open(os.path.join(DATA, 'notify-channels.json'), encoding='utf-8'))
routes = {
    'ServerCrashed': ['ch-tg', 'ch-nt', 'ch-ds'], 'StartFailed': ['ch-tg', 'ch-nt'], 'BackupFailed': ['ch-tg'],
    'LowDisk': ['ch-tg', 'ch-nt'], 'Overloaded': ['ch-tg'], 'ModsUpdated': ['ch-ds'], 'ModUpdates': ['ch-ds'],
    'ServerStarted': ['ch-ds'], 'RestartSoon': ['ch-ds'],
}
wjson(os.path.join(AG, 'srv1.notify.json'), {'ServerName': 'Survival', 'Channels': channels, 'Routes': routes,
                                             'ChatChannelId': 'ch-ds', 'ChatJoins': True})

# ---- объявления
wjson(os.path.join(AG, 'srv1.announcements.json'), {'OnlyWithPlayers': True, 'Items': [
    {'Text': 'Server rules: no griefing, no stealing, be nice. Full rules on our Discord.', 'IntervalMinutes': 45, 'Enabled': True},
    {'Text': 'Join our Discord: discord.gg/example', 'IntervalMinutes': 60, 'Enabled': True},
    {'Text': 'Daily restart at 05:00 server time. A backup is made right before it.', 'IntervalMinutes': 120, 'Enabled': False},
]})

# ---- расписание: копии мира и перезапуск
wjson(os.path.join(AG, 'srv1.automation.json'), {
    'BackupEnabled': True, 'BackupIntervalHours': 1, 'BackupKeep': 7, 'BackupOnlyWhenPlayed': True, 'BackupAnnounce': True,
    'BackupDir': r'\\nas\backups\vintagestory', 'RestartMode': 'Daily', 'RestartIntervalHours': 12, 'RestartTimes': ['05:00'],
    'RestartWarnMinutes': [10, 5, 4, 3, 2, 1], 'RestartBackup': True, 'RestartUpdateMods': True,
    'UpdatePinned': {}, 'UpdateBlocked': {}, 'UpdateAllowUnstable': False})

# ---- удалённый доступ: сертификат создаст агент при запуске
key = base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip('=')
wjson(os.path.join(AG, 'srv1.remote.json'), {'Enabled': True, 'Port': 41941, 'Key': key, 'Host': '192.168.1.15'})

# ---- serverconfig.json: имя, описание, приветствие
p = os.path.join(SRV, 'serverconfig.json')
cfg = json.load(open(p, encoding='utf-8-sig'))
cfg['ServerName'] = 'Survival with friends'
cfg['ServerDescription'] = 'A small friends-only survival world. Mods are synced with eViSTool.'
cfg['WelcomeMessage'] = 'Welcome, {0}! Backups every hour, restart daily at 5:00.'
cfg['MaxClients'] = 12
json.dump(cfg, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
print('ok')
