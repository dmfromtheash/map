# Builds H:\MapLibrary\plan\sets.json: every upscaled object/shop file assigned to one library set.
#   plan.py <scratch_dir_with_chunks> <inventory.json>
import argparse, json, os, re, sys, collections
import refs2_source
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('classification')
parser.add_argument('inventory')
refs2_source.add_arguments(parser, classifications=True)
args = parser.parse_args()
S, INV = args.classification, args.inventory
LIB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # deployed: H:\MapLibrary\pipeline\plan.py
if not os.path.exists(os.path.join(LIB, 'map-library.json')):  # repository copy: tools/library-pipeline/plan
    LIB = os.environ.get('MAP_LIBRARY_ROOT', r'H:\MapLibrary')

SECTIONS = {
 'nature': ('Природа', {'trees': 'Деревья', 'conifers': 'Хвойные', 'palms': 'Пальмы и тропики', 'dead-trees': 'Сухие деревья', 'bushes': 'Кусты', 'flowers': 'Цветы', 'grass': 'Трава', 'crops': 'Поля и огороды', 'mushrooms': 'Грибы', 'rocks': 'Камни', 'cliffs': 'Скалы и обрывы', 'mountains': 'Горы', 'hills': 'Холмы', 'volcanoes': 'Вулканы', 'desert': 'Пустыня', 'ice': 'Лёд и снег', 'water-nature': 'Вода', 'caves': 'Пещеры', 'crystals': 'Кристаллы', 'fantasy-plants': 'Волшебные растения', 'sky': 'Небо и облака'}),
 'buildings': ('Постройки', {'houses': 'Дома', 'town-blocks': 'Кварталы', 'castles': 'Замки', 'towers': 'Башни', 'temples': 'Храмы', 'farm-buildings': 'Фермы', 'shops-guilds': 'Лавки и мастерские', 'ruins': 'Руины', 'tents': 'Шатры и лагеря', 'docks': 'Причалы', 'bridges': 'Мосты', 'settlements': 'Города (значки)'}),
 'structure': ('Части построек', {'walls': 'Стены', 'fences': 'Заборы', 'doors-gates': 'Двери и ворота', 'windows': 'Окна', 'stairs': 'Лестницы', 'roofs': 'Крыши', 'pillars': 'Колонны и арки', 'platforms': 'Помосты', 'floors-pieces': 'Люки и решётки', 'paths': 'Дороги'}),
 'furniture': ('Мебель', {'tables': 'Столы', 'seating': 'Стулья и скамьи', 'beds': 'Кровати', 'storage': 'Шкафы и сундуки', 'rugs': 'Ковры', 'bath': 'Ванная', 'kitchen-fixtures': 'Печи и кухня', 'office': 'Офис и школа', 'decor': 'Украшения'}),
 'items': ('Вещи', {'containers': 'Бочки и ящики', 'food': 'Еда и напитки', 'tableware': 'Посуда', 'tools': 'Инструменты', 'weapons': 'Оружие', 'books': 'Книги и свитки', 'treasure': 'Сокровища', 'clothes': 'Одежда', 'toys-misc': 'Мелочи', 'alchemy': 'Алхимия', 'medical': 'Медицина', 'bones': 'Кости'}),
 'light': ('Свет и огонь', {'lamps': 'Лампы', 'candles': 'Свечи и факелы', 'fires': 'Костры и очаги'}),
 'transport': ('Транспорт', {'carts': 'Телеги', 'ships': 'Корабли', 'vehicles': 'Машины', 'air': 'Воздушные'}),
 'creatures': ('Существа', {'animals': 'Животные', 'birds-fish': 'Птицы и рыбы', 'monsters': 'Чудовища', 'people': 'Люди', 'statues': 'Статуи'}),
 'magic': ('Магия и эффекты', {'magic-circles': 'Круги и символы', 'effects': 'Эффекты', 'blood-stains': 'Пятна и грязь', 'weather': 'Погода'}),
 'scifi': ('Техника будущего', {'machines': 'Машины и станки', 'electronics': 'Электроника', 'scifi-structure': 'Конструкции', 'robots': 'Роботы', 'signs': 'Вывески'}),
 'cartography': ('Оформление карты', {'frames': 'Рамки', 'labels': 'Таблички', 'legend': 'Легенды', 'compass': 'Компасы', 'icons': 'Значки', 'heraldry': 'Гербы и флаги', 'borders': 'Границы'}),
 'textures': ('Текстуры', {'ground-tex': 'Земля', 'water-tex': 'Вода', 'floor-tex': 'Полы', 'brush-tex': 'Краски', 'overlay-tex': 'Наложения'}),
}
SUB_SECTION = {sub: sec for sec, (_, subs) in SECTIONS.items() for sub in subs}
PLACES = {'tavern': 'Таверна', 'kitchen': 'Кухня', 'bedroom': 'Спальня', 'living-room': 'Гостиная', 'bathroom': 'Ванная', 'dining-hall': 'Трапезная', 'throne-room': 'Тронный зал', 'library': 'Библиотека', 'study': 'Кабинет', 'school': 'Школа', 'office': 'Офис', 'shop': 'Лавка', 'market': 'Рынок', 'smithy': 'Кузница', 'workshop': 'Мастерская', 'alchemy-lab': 'Алхимия', 'laboratory': 'Лаборатория', 'hospital': 'Больница', 'temple': 'Храм', 'crypt': 'Склеп', 'graveyard': 'Кладбище', 'prison': 'Тюрьма', 'dungeon': 'Подземелье', 'barracks': 'Казарма', 'armory': 'Оружейная', 'treasury': 'Сокровищница', 'ship': 'Корабль', 'port': 'Порт', 'farm': 'Ферма', 'garden': 'Сад', 'stable': 'Конюшня', 'forest': 'Лес', 'jungle': 'Джунгли', 'swamp': 'Болото', 'desert': 'Пустыня', 'mountains': 'Горы', 'cave': 'Пещера', 'mine': 'Шахта', 'arctic': 'Север', 'beach': 'Берег', 'river': 'Река', 'street': 'Улица', 'plaza': 'Площадь', 'castle': 'Замок', 'camp': 'Лагерь', 'festival': 'Праздник', 'sewer': 'Канализация', 'factory': 'Завод', 'spaceship': 'Космический корабль', 'city-ruins': 'Руины города', 'battlefield': 'Поле боя', 'wilderness': 'Дикие земли', 'world-map': 'Карта мира'}
SETTINGS = {'fantasy': 'Фэнтези', 'medieval': 'Средневековье', 'eastern': 'Восток', 'desert-culture': 'Южная пустыня', 'nordic': 'Север', 'elven': 'Эльфы', 'dwarven': 'Гномы', 'orcish': 'Орки', 'dark': 'Мрачное', 'fey': 'Феи', 'pirate': 'Пираты', 'steampunk': 'Стимпанк', 'modern': 'Современность', 'scifi': 'Научная фантастика', 'post-apoc': 'Постапокалипсис', 'prehistoric': 'Древность', 'ancient': 'Античность', 'holiday': 'Праздники'}
STATES = {'whole': 'целое', 'broken': 'сломанное', 'ruined': 'разрушенное', 'burnt': 'сгоревшее', 'empty': 'пустое', 'full': 'полное', 'lit': 'горит', 'unlit': 'погасшее', 'open': 'открыто', 'closed': 'закрыто', 'on': 'включено', 'off': 'выключено', 'tattered': 'рваное', 'overgrown': 'заросшее', 'frozen': 'обледенелое', 'bloody': 'в крови', 'wet': 'мокрое', 'folded': 'сложено', 'knocked-over': 'опрокинуто'}
SEASONS = {'winter': 'Зима', 'autumn': 'Осень', 'spring': 'Весна', 'summer': 'Лето', 'holiday': 'Праздник', 'halloween': 'Хэллоуин'}
# pack folder -> (catalog folder on the drive, style title, map scale)
PACKS = {
 'Battlemaps Classic': ('battlemaps-classic', 'Классика', 'Сцена'),
 'Fantasy Battlemaps': ('fantasy-battlemaps', 'Фэнтези', 'Сцена'),
 'Fantasy Regional': ('fantasy-regional', 'Фэнтези', 'Регион'),
 'Fantasy World': ('fantasy-world', 'Фэнтези', 'Мир'),
 'Parchment World': ('parchment-world', 'Пергамент', 'Мир'),
 'Parchment World 2.0': ('parchment-world-2', 'Пергамент', 'Мир'),
 'Sci-Fi Battlemaps': ('scifi-battlemaps', 'Научная фантастика', 'Сцена'),
 'Watercolor Battlemaps': ('watercolor-battlemaps', 'Акварель', 'Сцена'),
 'Watercolor Cities': ('watercolor-cities', 'Акварель', 'Город'),
 '_Магазин — мебель (превью)': ('modern-interiors', 'Современный интерьер', 'Сцена'),
}

def shop_base(stem):
    base = re.split(r' \d|\s-\s|_', stem)[0].strip()
    base = re.sub(r'\b(Colorable [A-Z]|Black|White|Gray|Grey|Red|Blue|Green|Brown|Gold|Pink|Yellow|Purple|Orange|Teal|Dead|Empty)\b.*$', '', base).strip()
    return base or stem

cls = {}
for name in sorted(os.listdir(os.path.join(S, 'chunks_out'))):
    for rec in json.load(open(os.path.join(S, 'chunks_out', name), encoding='utf-8')):
        cls[rec['key']] = rec
inv = json.load(open(INV, encoding='utf-8'))
# Texture decisions (owner + review of Sol-5) and Sol-5's per-file verdicts (fill-ok / overlay / …).
_ovr = os.path.join(LIB, 'plan', 'texture_overrides.json')
TEX_OVR = json.load(open(_ovr, encoding='utf-8')) if os.path.exists(_ovr) else {}
TEX_VERDICT = {}
_rep = os.path.join(LIB, 'sol', 'sol5', 'tex_report.json')
if os.path.exists(_rep):
    _r = json.load(open(_rep, encoding='utf-8'))
    for item in (_r.get('files') if isinstance(_r, dict) else _r) or []:
        rel_ = item.get('source') or item.get('rel') or item.get('path') or ''
        rel_ = rel_.replace('/', '\\')
        if 'Pictures\\refs\\' in rel_: rel_ = rel_.split('Pictures\\refs\\', 1)[1]
        TEX_VERDICT[rel_] = item.get('verdict')
# What was seen on the pictures (look/out/*.jsonl): common name, detail, how drawn, view, colour, fixes.
DRAWN = {'ink-outline': 'Контур тушью', 'ink-filled': 'Тушь с заливкой', 'color': 'Цветной рисунок', 'watercolor': 'Акварель',
         'sepia-parchment': 'Сепия на пергаменте', 'silhouette': 'Силуэт', 'render-3d': 'Объёмный', 'flat-vector': 'Плоский'}
VIEW = {'top': 'Сверху', 'side': 'Сбоку', 'iso': 'Изометрия', 'icon': 'Значок карты', 'plan': 'План'}
look = {}
look_dir = os.path.join(LIB, 'look', 'out')
if os.path.isdir(look_dir):
    for f in os.listdir(look_dir):
        if f.endswith('.jsonl'):
            for line in open(os.path.join(look_dir, f), encoding='utf-8'):
                line = line.strip()
                if line:
                    try:
                        r = json.loads(line); look[r['key']] = r
                    except Exception:
                        pass
for key, r in look.items():
    if key in cls and isinstance(r.get('fix'), dict):
        for field, value in r['fix'].items():
            if field in ('sub', 'use', 'places', 'setting', 'state', 'season', 'material'):
                cls[key][field] = value
done = {}
# PC (manifest.jsonl) and Mac (manifest-mac.jsonl) write disjoint parts of the same upscaled/ tree
for name in ('manifest.jsonl', 'manifest-mac.jsonl'):
    man = os.path.join(LIB, 'upscaled', name)
    if not os.path.exists(man):
        continue
    for line in open(man, encoding='utf-8-sig'):
        try:
            r = json.loads(line)
        except ValueError:
            continue
        if r.get('ok'): done[r['rel']] = r

sets = collections.OrderedDict()
unclassified = collections.Counter()
for rel, size, w, h, mode, alpha in inv:
    parts = rel.split('\\')
    if parts[0].startswith('_Магазин') and len(parts) == 3:
        continue
    if parts[0].startswith('_Текстуры'):
        # Tiling textures and brush paints are already 2048-4096 px: imported from the originals, not upscaled.
        stem = os.path.splitext(parts[-1])[0]
        tname = re.sub(r'_\d+$', '', stem)
        key = 'TEX:' + tname
        c = cls.get(key)
        if c is None or c['use'] == 'skip' or tname in TEX_OVR.get('exclude', []):
            unclassified[key] += 1; continue
        seen = look.get(key) or {}
        use = TEX_OVR.get('use', {}).get(tname, c['use'])
        overlay_part = tname in TEX_OVR.get('split_overlays', []) and TEX_VERDICT.get(rel) == 'overlay' and use != 'overlay'
        if overlay_part: use = 'overlay'
        # Overlays get their own tag: the fill picker lists only Земля/Вода/Пол/Краска (and plain «Текстура» = Земля).
        kind = {'ground': 'Земля', 'water': 'Вода', 'floor': 'Пол', 'brush': 'Краска', 'overlay': 'Наложение'}.get(use)
        tags = ['Тестовое', 'Текстура/' + kind if kind else 'Текстура', f'Набор/{parts[1]}'] + c.get('search', [])
        if seen.get('color'): tags.append(f'Цвет/{seen["color"]}')
        set_key = f'textures|{key}' + ('|overlay' if overlay_part else '')
        base_name = seen.get('name') or c['ru']
        s = sets.setdefault(set_key, {'key': set_key, 'catalog': 'textures',
                                      'name': base_name + (' · наложение' if overlay_part else ''),
                                      'one': seen.get('name') or c['ruOne'], 'use': use, 'detail': seen.get('detail'),
                                      'named': bool(seen), 'source': 'refs',
                                      'collection': ['Тестовое', 'Текстуры', {'ground': 'Земля', 'water': 'Вода', 'floor': 'Полы',
                                                     'brush': 'Краски', 'overlay': 'Наложения'}.get(use, 'Земля')],
                                      'tags': list(dict.fromkeys(tags)), 'files': []})
        s['files'].append(rel)
        continue
    pack = parts[0]
    stem = os.path.splitext(parts[-1])[0]
    if pack.startswith('_Магазин'):
        key = 'SHOP:' + shop_base(stem); folder = parts[1]
    else:
        folder = parts[-2]; key = 'OBJ:' + folder
    if folder == '_Other':  # catch-all folders: one set per item name, classified by name
        key = 'OTHER:' + re.sub(r'_\d+$', '', stem)
    c = cls.get(key)
    if c is None:
        unclassified[key] += 1
    catalog, style, scale = PACKS[pack]
    if c is None:  # one set per file-name stem, named from the file until it is classified
        en = re.sub(r'_\d+$', '', stem)
        set_key = f'{catalog}|other|{en}'
        name, one, section, sub, tags = en, en, 'items', 'toys-misc', ['Тестовое', 'Не разобрано']
        use = 'object'
    else:
        set_key = f'{catalog}|{key}' + (f'|{folder}' if pack.startswith('_Магазин') else '')
        if c['use'] == 'skip':
            continue
        name, one = c['ru'], c['ruOne']
        sub = c['sub']; section = SUB_SECTION.get(sub, 'items'); use = c['use']
        tags = ['Тестовое'] + [f'Место/{PLACES[p]}' for p in c['places'] if p in PLACES] \
            + [f'Сеттинг/{SETTINGS[s]}' for s in c['setting'] if s in SETTINGS] \
            + [f'Состояние/{STATES[s]}' for s in c['state'] if s in STATES] \
            + ([f'Сезон/{SEASONS[c["season"]]}'] if c['season'] in SEASONS else []) \
            + c.get('search', [])
    tags += [f'Масштаб/{scale}', f'Стиль/{style}', f'Набор/{pack.strip("_")}']
    seen = look.get(key) if c is not None else None
    detail = None
    if seen:
        name = seen.get('name') or name
        one = name if not one else one
        detail = seen.get('detail')
        if seen.get('drawn') in DRAWN: tags.append(f'Исполнение/{DRAWN[seen["drawn"]]}')
        if seen.get('view') in VIEW: tags.append(f'Вид/{VIEW[seen["view"]]}')
        if seen.get('color'): tags.append(f'Цвет/{seen["color"]}')
    s = sets.setdefault(set_key, {'key': set_key, 'catalog': catalog, 'name': name, 'one': one, 'use': use, 'detail': detail, 'named': bool(seen),
                                  'collection': ['Тестовое', SECTIONS[section][0], SECTIONS[section][1].get(sub, sub)],
                                  'tags': list(dict.fromkeys(tags)), 'files': []})
    s['files'].append(rel)

out = list(sets.values())
# Large packs are split into catalogs of at most CHUNK files (whole sets): one index stays far
# below the program's 32 MB load budget. The split follows the plan order, so it is stable.
CHUNK = 4000
per_pack = collections.Counter()
for s in out: per_pack[s['catalog']] += len(s['files'])
running = collections.Counter()
for s in out:
    base = s['catalog']
    if per_pack[base] > CHUNK:
        s['catalog'] = f"{base}-{running[base] // CHUNK + 1}"
    running[base] += len(s['files'])
for s in out:
    s['ready'] = s.get('source') == 'refs' or all(f in done for f in s['files'])
    s['factor'] = sorted({done[f]['factor'] for f in s['files'] if f in done})
os.makedirs(os.path.join(LIB, 'plan'), exist_ok=True)
import apply_fixes  # reviewed corrections from plan/fixes (names, tags, use); see apply_fixes.py
fix_counts, fix_skipped = apply_fixes.apply(out, LIB)
print('fixes applied', fix_counts, 'skipped', fix_skipped)
if args.refs2_inventory:
    # Keep refs catalog splits, reviewed fixes and applied.json exactly as in the refs-only run.
    extra, stats = refs2_source.build(args, cls, globals(), done)
    out.extend(extra)
    print('refs2', stats)
json.dump(out, open(os.path.join(LIB, 'plan', 'sets.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
json.dump(dict(unclassified.most_common()), open(os.path.join(LIB, 'plan', 'unclassified.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print('sets', len(out), 'ready', sum(s['ready'] for s in out), 'files', sum(len(s['files']) for s in out),
      'unclassified files', sum(unclassified.values()), 'catalogs', collections.Counter(s['catalog'] for s in out))
