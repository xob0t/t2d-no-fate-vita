"""Per-scene image sets, pack sets and texture-memory estimates.

Inputs (from --work): images.json, class_refs.json, maps.json, rect_alpha.json.
Writes scenes.json, scenes.csv, memory.csv, memory.md to --work.

A scene is a level script (+ its maps), a cutscene class or a menu/app class.
Its images are the static closure over the class graph from the scene's root
classes (stopping at hub classes and at other scenes), plus the map objects'
classes and sprite ids, plus a global set (HUD, fonts, Script/Game/Sprites
generic drawing, transitions).
"""
import argparse, collections, csv, json, os, re

MB = 1024 * 1024
LEVELS = [
    # (scene, root classes, maps)
    ('L00/16 Future War 1', ['FutureWar1'], ['T2_FUTURE_WAR_1']),
    ('L01 Biker Bar', ['BikerBar'], ['T2_BIKER_BAR_EXTERIOR', 'T2_BIKER_BAR_INTERIOR', 'T2_BIKER_BAR_INTRO']),
    ('L02 Truck Chase', ['TruckChase'], []),
    ('L03 Hospital', ['Hospital'], ['T2_HOSPITAL_SARAH']),
    ('L04/17 Future War 2', ['FutureWar2'], ['T2_FUTURE_WAR_2']),
    ('L05-07 Cyberdyne HQ', ['CyberdyneHQ'], ['T2_CYBERDYNE_HQ_SARAH', 'T2_CYBERDYNE_HQ_T800']),
    ('L08 Freeway Chase', ['FreewayChase'], []),
    ('L09 Steel Mill', ['SteelMill'], ['T2_STEEL_MILL']),
    ('L10/18 Future War 3', ['FutureWar3'], ['T2_FUTURE_WAR_3', 'T2_FW3_TUNNEL']),
    ('L11 Desert Town', ['DesertTown'], ['T2_DESERT_TOWN']),
    ('L12 Cyberdyne Factory', ['CyberdyneFactory'], ['T2_CYBERDYNE_FACTORY']),
    ('L13 Cyberdyne Research', ['CyberdyneResearch'], ['T2_CYBERDYNE_RESEARCH', 'T2_LAB_BOSS']),
    ('L14 Police Station', ['PoliceStation'], ['T2_POLICE_STATION']),
    ('L15 Future War 4', ['FutureWar4'], ['T2_FUTURE_WAR_4']),
    ('L48 Infinite mode', ['InfiniteMode'], ['T2_INFINITE_0']),
    ('Tanker Chase', ['TankerChase'], []),
    ('Controls tutorial', ['ControlsExplanation', 'LevelControls'], ['T2_CONTROLS_ROOM']),
]
# The attract demos: one Demo script plays four maps in turn (Demo.demoCharacter), each with its own player class and
# its own images in Demo.setupScene's switch. A demo scene per map: Demo's closure without the other demos' player
# classes, and without the images of the other demos' cases.
DEMOS = [
    # (scene, player class, map, the images of its case in Demo.setupScene)
    ('Demo map 9 John', 'John', 'T2_DEMO_JOHN', ['IMAGE_FUTURE_WAR_1_A', 'IMAGE_PORTRAIT_JOHN']),
    ('Demo map 10 Sarah', 'Sarah', 'T2_DEMO_SARAH', ['IMAGE_CYBERDYNEFACTORY_BG_A', 'IMAGE_CYBERDYNEFACTORY_BG_B',
                                                      'IMAGE_CYBERDYNEFACTORY_FG_A', 'IMAGE_CYBERDYNEFACTORY_LAYER0',
                                                      'IMAGE_PORTRAIT_SARAH']),
    ('Demo map 11 T1000', 'T1000Demo', 'T2_DEMO_T1000', ['IMAGE_MENTAL_HOSPITAL_BG', 'IMAGE_MENTAL_HOSPITAL_FOREGROUND',
                                                          'IMAGE_PORTRAIT_T1000']),
    ('Demo map 12 T800', 'T800Minigun', 'T2_DEMO_T800', ['IMAGE_CYBERDYNE_HQ_BG', 'IMAGE_CYBERDYNE_HQ_FRONT',
                                                          'IMAGE_PORTRAIT_T800']),
]
BASES = {'Script', 'Cutscene', 'CompoundScene', 'App'}
HUBS = {'Game', 'Sprites', 'Transition', 'Main', 'MainMenu', 'Database', 'GameMap', 'AssetsEmbed', 'Assets',
        'AssetsPack', 'AssetsStaticMap', 'UnityMain', 'Res'}
GAMEPLAY_GLOBAL = ['HUD', 'HospitalHUD', 'Script', 'GameObject', 'Fonts', 'Message', 'OpaqueLayer', 'DialogBox', 'Prompt']
ALL_GLOBAL = ['Fonts', 'App', 'Menu', 'TextButton', 'Text', 'TitleText', 'BmpFont', 'EngageLayer']
HUB_METHODS_EXCLUDED = {('Sprites', 'addFromJSON'), ('Sprites', 'loadFromID')}
# Methods of a hub that the analysis doesn't follow, called from every level: the pause menu's audio options
# (Game.pause -> MainMenu.addAudioOptions, its sliders).
GAMEPLAY_HUB_METHODS = [('MainMenu', 'addAudioOptions')]
# Images a map object brings that no code path names: a weapon pickup's bullets and hit sparks, which the player's
# code picks by arithmetic on the bullet type (Player.bulletType, HOMING_BULLET + k). Object name -> images; a scene
# whose maps place the object gets them.
OBJECT_IMAGES = {
    'homing_pickup': ['IMAGE_HOMING_FIRE', 'IMAGE_HOMING_HIT_SPARK'],
    'spread_pickup': ['IMAGE_SPREAD_FIRE', 'IMAGE_SPREAD_HIT_SPARK'],
    'laser_pickup': ['IMAGE_LASER_FIRE', 'IMAGE_LASER_HIT_SPARK'],
}
LANGS = ('CNS', 'CNT', 'JP', 'KR')


def family(name):
    """An image's family, or None: numbered images (IMAGE_X_0, IMAGE_X_1, ...) and debris (IMAGE_X_DEBRIS_*), which the
    game draws by arithmetic on the first one's id (base + k, base + rand), so the analysis finds one or two of them.
    A scene that has one gets them all (about 0.1 MB a scene). Fonts and the IMAGE_*_ARRAY_k tables are not families."""
    if 'FONT' in name or '_ARRAY_' in name:
        return None
    m = re.match(r'(.*_DEBRIS)_', name) or re.match(r'(.*)_\d+$', name)
    return m.group(1) if m else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', required=True)
    ap.add_argument('--lang', default='LATIN', help='LATIN or one of CNS CNT JP KR: font set kept resident')
    a = ap.parse_args()
    W = a.work
    images = json.load(open(os.path.join(W, 'images.json')))['images']
    cr = json.load(open(os.path.join(W, 'class_refs.json')))
    maps = json.load(open(os.path.join(W, 'maps.json')))['maps']
    alpha = json.load(open(os.path.join(W, 'rect_alpha.json')))
    C = cr['classes']
    sprite_img = {int(k): v for k, v in cr['sprite_img'].items()}
    objs = {int(k): v for k, v in cr['objects'].items()}
    scene_dirs = {'scripts', 'cutscenes', 'apps'}
    by_name = {im['name']: im['id'] for im in images}
    name_of_id = {v: k for k, v in json.load(open(os.path.join(W, 'maps.json')))['name_ids'].items()}
    families = collections.defaultdict(set)
    for im in images:
        if family(im['name']):
            families[family(im['name'])].add(im['id'])

    def is_scene_class(c):
        return C[c]['file'].split('/')[-2] in scene_dirs and c not in BASES

    def class_images(c, roots=()):
        v = C[c]
        out = set()
        if c in HUBS and c not in roots:
            if c not in ('Game', 'Sprites', 'Transition'):
                return out        # tables / preloaders: their uses are counted at the use sites
            for meth, mv in v.get('by_method', {}).items():
                if (c, meth) in HUB_METHODS_EXCLUDED:
                    continue
                out.update(mv['assets_images'])
                for s in mv['sprite_ids']:
                    out.update(sprite_img.get(s, []))
            return out
        out.update(v['assets_images'])
        for s in v['sprite_ids']:
            out.update(sprite_img.get(s, []))
        return out

    def closure(roots, stop=()):
        seen, stack = set(), [r for r in roots if r in C]
        while stack:
            c = stack.pop()
            if c in seen or c in stop:
                continue
            seen.add(c)
            if c in HUBS:
                continue          # use its own generic images, don't follow
            for r in C[c]['refs']:
                if r in seen or r not in C:
                    continue
                if is_scene_class(r) and r not in roots:
                    continue      # another scene
                if r in HUBS and r not in ('Game', 'Sprites', 'Transition'):
                    continue
                stack.append(r)
        return seen

    def font_ok(im):
        n = im['name']
        if 'FONT' not in n:
            return True
        suf = n.rsplit('_', 1)[-1]
        if suf in LANGS:
            return suf == a.lang
        return True

    def scene_images(roots, map_names, gameplay, stop=(), drop=()):
        cl = closure(list(roots) + (GAMEPLAY_GLOBAL if gameplay else []) + ALL_GLOBAL, stop)
        ims = set()
        for c, meth in GAMEPLAY_HUB_METHODS if gameplay else ():
            mv = C[c]['by_method'].get(meth, {})
            ims.update(mv.get('assets_images', ()))
            for sid in mv.get('sprite_ids', ()):
                ims.update(sprite_img.get(sid, []))
        for c in cl:
            ims |= class_images(c, roots)
        objclasses = set()
        for mp in map_names:
            for o in maps[mp]['objects']:
                ob = objs.get(o['name_id'])
                if not ob:
                    continue
                objclasses.update(ob['classes'])
                for s in ob['sprite_ids']:
                    ims.update(sprite_img.get(s, []))
        for c in closure(sorted(objclasses)) - cl:
            ims |= class_images(c)
        for mp in map_names:
            for o in maps[mp]['objects']:
                for n in OBJECT_IMAGES.get(name_of_id.get(o['name_id']), ()):
                    ims.add(by_name[n])
        for i in list(ims):
            ims |= families.get(family(images[i]['name']), set())
        ims = {i for i in ims if font_ok(images[i]) and images[i]['name'] not in drop}
        return ims, cl

    scenes = []
    for name, roots, mps in LEVELS:
        scenes.append((name, 'level', roots, mps, True, (), ()))
    for name, player, mp, own in DEMOS:
        others = [d for d in DEMOS if d[1] != player]
        scenes.append((name, 'level', ['Demo', player], [mp], True, {d[1] for d in others},
                       {i for d in others for i in d[3]} - set(own)))
    for c in sorted(C):
        f = C[c]['file'].split('/')[-2]
        if f == 'cutscenes' and c not in BASES:
            scenes.append(('cutscene ' + c, 'cutscene', [c], [], False, (), ()))
        if f == 'apps' and c not in BASES and c != 'LevelControls':
            scenes.append(('menu ' + c, 'menu', [c], [], False, (), ()))

    # rect bookkeeping
    def rects_of(ims):
        rs = set()
        for i in ims:
            for f in images[i]['frames']:
                rs.add((f['pack'], f['x'], f['y'], f['w'], f['h']))
        return rs

    out = []
    for name, kind, roots, mps, gameplay, stop, drop in scenes:
        ims, cl = scene_images(roots, mps, gameplay, stop, drop)
        rs = rects_of(ims)
        packs = sorted({r[0] for r in rs})
        per_pack_area = collections.Counter()
        for r in rs:
            per_pack_area[r[0]] += r[3] * r[4]
        rect_area = sum(r[3] * r[4] for r in rs)
        opaque = sum(alpha['rects']['%d,%d,%d,%d,%d' % r]['opaque'] for r in rs)
        cells = sum(alpha['rects']['%d,%d,%d,%d,%d' % r]['cells'] for r in rs) * alpha['cell'] ** 2
        out.append(dict(scene=name, kind=kind, roots=roots, maps=mps, n_classes=len(cl), n_images=len(ims),
                        images=sorted(ims), packs=packs,
                        pack_area_frac={str(p): round(per_pack_area[p] / 4096 ** 2, 4) for p in packs},
                        rect_area_atlases=rect_area / 4096 ** 2, opaque_atlases=opaque / 4096 ** 2,
                        cell_area_atlases=cells / 4096 ** 2, n_rects=len(rs)))
    json.dump(out, open(os.path.join(W, 'scenes.json'), 'w'), indent=1)
    with open(os.path.join(W, 'scenes.csv'), 'w', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['scene', 'kind', 'images', 'rects', 'packs', 'n_packs', 'rect_area_atlases', 'opaque_atlases', 'cell32_area_atlases'] +
                   ['pack%d_area' % p for p in range(12)])
        for s in out:
            w.writerow([s['scene'], s['kind'], s['n_images'], s['n_rects'], ' '.join(map(str, s['packs'])), len(s['packs']),
                        '%.3f' % s['rect_area_atlases'], '%.3f' % s['opaque_atlases'], '%.3f' % s['cell_area_atlases']] +
                       ['%.3f' % s['pack_area_frac'].get(str(p), 0) for p in range(12)])
    for s in out:
        print('%-36s img=%4d packs=%2d [%s] rect=%.2f opaque=%.2f cells=%.2f' % (
            s['scene'], s['n_images'], len(s['packs']), ','.join(map(str, s['packs'])),
            s['rect_area_atlases'], s['opaque_atlases'], s['cell_area_atlases']))


if __name__ == '__main__':
    main()
