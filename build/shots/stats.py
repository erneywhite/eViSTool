# Статистика с входами и выходами игроков и одним падением; даты игроков — ISO (их разбирает окно без путаницы день/месяц).
import json, os, math, random, datetime as dt

S = os.path.dirname(os.path.abspath(__file__))
stats = os.path.join(S, 'app', 'data', 'agents', 'srv1.stats')
random.seed(11)
names = ['Birchwood', 'Moss_Lantern', 'Kettle', 'FernHollow']
today = dt.date.today()
for f in os.listdir(stats):
    if f.endswith('.log'): os.remove(os.path.join(stats, f))
for back in range(7, 0, -1):
    day = today - dt.timedelta(days=back)
    weekend = day.weekday() >= 4
    start, end = (14 * 60, 23 * 60 + 30) if weekend else (18 * 60, 23 * 60)
    lines, online, mem = ['00:00:10\tup'], [], 2300.0
    for m in range(0, 24 * 60):
        h, mi = divmod(m, 60)
        want = 0
        if start <= m < end:
            phase = (m - start) / (end - start)
            want = max(0, min(len(names), round((3 if weekend else 2) * math.sin(math.pi * phase) + 0.3)))
        while len(online) < want:
            n = names[len(online)]; online.append(n); lines.append(f'{h:02}:{mi:02}:05\tj\t{n}')
        while len(online) > want:
            n = online.pop(); lines.append(f'{h:02}:{mi:02}:05\tl\t{n}')
        mem = min(4300, max(2200, mem + (5 if online else -2) + random.uniform(-3, 3)))
        cpu = round(4 + len(online) * 9 + random.uniform(0, 4), 1)
        lines.append(f'{h:02}:{mi:02}:30\ts\t{len(online)}\t{int(mem)}\t{cpu}')
        if back == 4 and m == 21 * 60 + 47:  # падение во вторник вечером и подъём сторожем
            for n in reversed(online): lines.append(f'21:47:04\tl\t{n}')
            online.clear(); lines.append('21:47:05\tcrash'); lines.append('21:47:40\tup')
    with open(os.path.join(stats, day.strftime('%Y-%m-%d') + '.log'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')

p = os.path.join(S, 'srvdata', 'Playerdata', 'playerdata.json')
players = json.load(open(p, encoding='utf-8-sig'))
now = dt.datetime.now()
for i, pl in enumerate(players):
    last = now - dt.timedelta(days=i * 2 + 1, hours=3 + i)
    pl['LastJoinDate'] = last.strftime('%Y-%m-%dT%H:%M:%S')
    pl['FirstJoinDate'] = (last - dt.timedelta(days=40)).strftime('%Y-%m-%dT%H:%M:%S')
json.dump(players, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
print('ok')
