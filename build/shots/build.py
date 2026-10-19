# Стенд для скриншотов README/ModDB: копия модов и настроек игры (только чтение оригинала), очищенная от личного,
# серверная папка данных, настройки окна (английский), данные агента (статистика за неделю, журнал).
# Запуск: python -I -X utf8 build.py [--keep-server]
import json, os, shutil, sys, math, random, datetime as dt, uuid

S = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(S, 'app')
DATA = os.path.join(APP, 'data')
CLIENT = os.path.join(S, 'clientdata')
SRV = os.path.join(S, 'srvdata')
REAL = os.path.expandvars(r'%APPDATA%\VintagestoryData')
GAME = r'S:\Games\Vintagestory'
keep_server = '--keep-server' in sys.argv
random.seed(7)

def wjson(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f: json.dump(obj, f, ensure_ascii=False, indent=2)

# ---------- клиент: моды, ModConfig, clientsettings без личного
if os.path.exists(CLIENT): shutil.rmtree(CLIENT)
os.makedirs(CLIENT)
shutil.copytree(os.path.join(REAL, 'Mods'), os.path.join(CLIENT, 'Mods'))
shutil.copytree(os.path.join(REAL, 'ModConfig'), os.path.join(CLIENT, 'ModConfig'))
cs = json.load(open(os.path.join(REAL, 'clientsettings.json'), encoding='utf-8-sig'))
for section in cs.values():
    if not isinstance(section, dict): continue
    for k in list(section):
        if any(w in k.lower() for w in ('token', 'session', 'uid', 'email', 'password', 'mail')): section[k] = ''
if 'stringSettings' in cs: cs['stringSettings']['playername'] = 'Player'
cs.setdefault('stringListSettings', {})['multiplayerservers'] = [
    'Friends,192.168.1.20,', 'Whispers of Oldwood,oldwood.example.org:42420,', 'Weekend crew,crew.example.net:42421,secret']
cs['stringListSettings']['modPaths'] = ['Mods', os.path.join(CLIENT, 'Mods')]
wjson(os.path.join(CLIENT, 'clientsettings.json'), cs)

# ---------- сервер: папка данных с конфигом, ролями, игроками, модами (часть клиентских)
if not keep_server:
    if os.path.exists(SRV): shutil.rmtree(SRV)
    os.makedirs(os.path.join(SRV, 'Mods'))
    server_mods = ['carryon', 'primitivesurvival', 'expandedfoods', 'butchering', 'footprints', 'autolootreforged',
                   'bedspawn', 'bloodtrail', 'canjewelry', 'betterruins', 'xskills', 'xlib', 'morepiles', 'smithingplus']
    copied = []
    for f in sorted(os.listdir(os.path.join(CLIENT, 'Mods'))):
        low = f.lower()
        if any(low.startswith(m) for m in server_mods) and len(copied) < 14:
            shutil.copy2(os.path.join(CLIENT, 'Mods', f), os.path.join(SRV, 'Mods', f)); copied.append(f)
    print('модов сервера:', len(copied))
    now = dt.datetime.now()
    names = [('Birchwood', 'admin'), ('Moss_Lantern', 'suplayer'), ('Kettle', 'suplayer'), ('FernHollow', 'suplayer'), ('grumpy_crow', 'suplayer')]
    players, white = [], []
    for i, (n, role) in enumerate(names):
        uid = uuid.UUID(int=random.getrandbits(128)).hex[:24]
        last = now - dt.timedelta(days=i * 2 + 1, hours=random.randint(0, 9))
        players.append({'PlayerUID': uid, 'RoleCode': role, 'PermaPrivileges': [], 'DeniedPrivileges': [], 'PlayerGroupMemberships': {},
                        'AllowInvite': True, 'LastKnownPlayername': n, 'CustomPlayerData': {}, 'ExtraLandClaimAllowance': 0,
                        'ExtraLandClaimAreas': 0, 'FirstJoinDate': (last - dt.timedelta(days=40)).strftime('%d.%m.%Y %H:%M:%S'),
                        'LastJoinDate': last.strftime('%d.%m.%Y %H:%M:%S'), 'LastCharacterSelectionDate': None})
        if i < 4: white.append({'PlayerUID': uid, 'PlayerName': n, 'UntilDate': (now + dt.timedelta(days=50 * 365)).isoformat(),
                                'Reason': '', 'IssuedByPlayerName': 'Birchwood'})
    wjson(os.path.join(SRV, 'Playerdata', 'playerdata.json'), players)
    wjson(os.path.join(SRV, 'Playerdata', 'playerswhitelisted.json'), white)
    wjson(os.path.join(SRV, 'Playerdata', 'playersbanned.json'), [])

# ---------- настройки окна
profiles = [
    {'Id': 'main', 'Name': 'Main', 'Kind': 'Client', 'GameDir': GAME, 'DataDir': CLIENT, 'IconColor': 'moss', 'IconGlyph': 'tree',
     'PinnedMods': {}, 'BlockedVersions': {}, 'LinkedProfiles': ['srv1'], 'DefaultServer': '192.168.1.20'},
    {'Id': 'srv1', 'Name': 'Survival', 'Kind': 'Server', 'GameDir': GAME, 'DataDir': SRV, 'IconColor': 'rust', 'IconGlyph': 'castle',
     'PinnedMods': {}, 'BlockedVersions': {}, 'LinkedProfiles': [], 'DefaultServer': None},
]
settings_path = os.path.join(DATA, 'settings.json')
old = json.load(open(settings_path, encoding='utf-8-sig')) if os.path.exists(settings_path) else {}
old.update({'Profiles': profiles, 'ActiveProfileId': 'main', 'Language': 'en', 'AutoCheckUpdates': True, 'AllowUnstable': False,
            'CopyWorldsBeforeModChanges': True, 'GameBackground': True})
old.setdefault('Layout', {}).update({'Width': 1400.0, 'Height': 860.0, 'Maximized': False, 'CatalogDetailsWidth': 760.0, 'ModsCardWidth': 420.0})
wjson(settings_path, old)

# ---------- каналы оповещений (секретов нет — вкладке они не нужны)
wjson(os.path.join(DATA, 'notify-channels.json'), [
    {'Id': 'ch-tg', 'Name': 'Telegram', 'Kind': 0, 'ChatTitle': 'Our server › Announcements'},
    {'Id': 'ch-nt', 'Name': 'ntfy', 'Kind': 2},
    {'Id': 'ch-ds', 'Name': 'Discord', 'Kind': 1, 'ChatTitle': '#server-news'},
])

# ---------- статистика за неделю и журнал
stats = os.path.join(DATA, 'agents', 'srv1.stats')
if os.path.exists(stats): shutil.rmtree(stats)
os.makedirs(stats)
open(os.path.join(stats, 'enabled'), 'w').close()
today = dt.date.today()
for back in range(7, 0, -1):
    day = today - dt.timedelta(days=back)
    evening = day.weekday() >= 4  # пятница–воскресенье — дольше
    lines = ['00:00:10\tup']
    mem = 2300
    for m in range(0, 24 * 60, 1):
        h, mi = divmod(m, 60)
        start, end = (18 * 60, 23 * 60) if not evening else (14 * 60, 24 * 60 - 30)
        online = 0
        if start <= m < end:
            phase = (m - start) / (end - start)
            online = max(0, round((3 if evening else 2) * math.sin(math.pi * phase) + random.uniform(-0.4, 0.4)))
        mem = min(4200, max(2200, mem + (6 if online else -2) + random.uniform(-3, 3)))
        cpu = round(4 + online * 9 + random.uniform(0, 4), 1)
        lines.append(f'{h:02}:{mi:02}:30\ts\t{online}\t{int(mem)}\t{cpu}')
    if back == 4: lines.insert(len(lines) * 21 // 24, '21:47:05\tdown')
    with open(os.path.join(stats, day.strftime('%Y-%m-%d') + '.log'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')

def ago(minutes): return (dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=minutes)).strftime('%Y-%m-%dT%H:%M:%SZ')
journal = [
    {'At': ago(6), 'Action': 'command', 'Args': ['/announce Restart in 10 minutes'], 'Who': 'GAMING-PC', 'Ip': '192.168.1.15', 'Remote': True},
    {'At': ago(48), 'Action': 'command', 'Args': ['/time set day'], 'Who': 'LAPTOP', 'Ip': '85.254.12.40', 'Remote': True},
    {'At': ago(95), 'Action': 'start', 'Args': [], 'Who': 'GAMING-PC', 'Ip': '192.168.1.15', 'Remote': True},
    {'At': ago(97), 'Action': 'modInstall', 'Args': ['carryon_1.10.4.zip'], 'Who': 'GAMING-PC', 'Ip': '192.168.1.15', 'Remote': True,
     'Details': [{'Kind': 'modVersion', 'Args': ['Carry On', '1.10.4', '1.10.3']}]},
    {'At': ago(130), 'Action': 'players', 'Args': [], 'Who': 'GAMING-PC', 'Ip': '192.168.1.15', 'Remote': True,
     'Details': [{'Kind': 'playerWhitelist', 'Args': ['Kettle', 'on']}, {'Kind': 'playerRole', 'Args': ['Moss_Lantern', 'suplayer', 'admin']}]},
    {'At': ago(140), 'Action': 'stop', 'Args': [], 'Who': 'GAMING-PC', 'Ip': '192.168.1.15', 'Remote': True},
]
with open(os.path.join(DATA, 'agents', 'srv1.journal.jsonl'), 'w', encoding='utf-8', newline='\n') as f:
    for e in journal: f.write(json.dumps(e, ensure_ascii=False) + '\n')
print('ok:', S)
