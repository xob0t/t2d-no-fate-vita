# ruff: noqa: SIM115, UP031
"""Writes scenes.json, the rule data that says which images each scene of the game draws and which runtime keys
enter that scene. repack.py groups frames into pages by it and writes scenes.bytes from it.

The data comes from the static analysis in tools/assess/atlas-usage (its scenes.json and maps.json, see that
README); this script only renames it: images by their Assets.IMAGE_* constant names, scenes by the keys the
runtime hooks pass to Atlas (rules.md, R6):

  app:<Class>      Atlas.EnterApp, after Transition.getApp returns an App of that class
  level:<id>       Atlas.EnterLevel, at the start of Game.loadScriptByLevelID(id), only while the App is Game
  map:<id>         Atlas.EnterMap, at the start of GameMap.loadFile(id, level); ids are MAP_* + 1

Usage: python -I scene_sets.py --usage work/atlas-usage --frames frames.json --out tools/downgrade/atlas/scenes.json
(frames.json from frametable.py, for the image names.) Rerun it when the analysis changes.
"""

import argparse
import json
import re

# Tanker Chase has no hook of its own: Transition makes it after Freeway Chase's last scene (level 8).
EXTRA_KEYS = {'Tanker Chase': ['level:8'], 'Controls tutorial': ['app:LevelControls']}
LEVEL_APPS = ['Game']

# Draws the static analysis can't follow (an image id from arithmetic or a table), found on the console from the
# atlas's miss log with the image of each miss: image -> the scenes that draw it ('levels': every level scene, the demos and the
# controls tutorial; 'all': every scene). Each was a 190 to 250 ms frame in play, the page's synchronous load and
# first draw.
EXTRA_IMAGES = {
    # A player bullet's hit (PlayerProjectile -> GameObject.addHitSpark); missed in every level where Sarah shoots.
    'IMAGE_BULLET_HIT_SPARK': 'levels',
    'IMAGE_BULLET_H': ['L12 Cyberdyne Factory', 'L13 Cyberdyne Research', 'Controls tutorial'],
    'IMAGE_HAZMAT_GUY': ['L12 Cyberdyne Factory', 'L13 Cyberdyne Research'],
    'IMAGE_HAZMAT_ALT_GUY': ['L12 Cyberdyne Factory', 'L13 Cyberdyne Research'],
}
# A Future War bike's parts as it breaks up: the engine missed in Future War 1 (runs f3, g3 of the Sarah-level work,
# and the first playtest); the other parts fly off with it.
EXTRA_IMAGES.update({'IMAGE_FUTURE_BIKE_' + part: ['L00/16 Future War 1'] for part in ('ENGINE', 'EXHAUST', 'VISOR', 'DEBRIS')})
# A crate's debris pieces, picked by number: the analysis finds 0 and 4 only.
EXTRA_IMAGES.update({'IMAGE_DESERT_CRATE_DEBRIS_%d' % i: ['L11 Desert Town'] for i in range(9)})
# The title fade (Transition.draw, style 1: any App's way back to the main menu) draws the main menu's logos through
# MainMenu.drawLogo, which the analysis stops at (a hub); and the images no scene reaches shared one page that nearly
# every level, menu and cutscene loaded at a draw (p083 of the Steam build's repack, beside the logos' p076: a 200 ms
# frame each, 70 misses in the runs of Nov 2026). Small images, so every scene has them.
EXTRA_IMAGES.update({n: 'all' for n in ('IMAGE_NO_FATE_LOGO', 'IMAGE_TERMINATOR_T2D_LOGO')})
EXTRA_IMAGES.update({n: 'all' for n in ('IMAGE_DOT', 'IMAGE_DOT_LARGE', 'IMAGE_HITBOX', 'IMAGE_MAP_INDICATORS',
                                         'IMAGE_SMALL_BLACK_RECT', 'IMAGE_SOLID_TILES')})
# Each level script's preview (Script.preview, IMAGE_PREVIEW_*, 104x104), which the level select, the end screen and the
# main menu's continue entry draw for the level they show (Game.loadScriptByLevelID, a hub the analysis stops at):
# scrolling the level select missed 13 pages, a 200 ms frame each (the user's playtest, Nov 2026). Only those three
# draw them (the briefing draws Script.briefingPortrait), so these scenes replace the level's the analysis gives: with
# one set, the 19 previews share one page, where beside their levels' frames they took 13 pages (26 MB) per menu.
PREVIEW_SCENES = ['menu LevelSelect', 'menu EndScreen', 'menu MainMenu']
PREVIEW_PREFIX = 'IMAGE_PREVIEW_'
# The same for each level's portrait (Script.briefingPortrait): only the briefing draws it (and two mode menus use
# some as buttons); the analysis gives it to its level too, from the script's constructor. Beside its level's frames
# each took a page of its own in the briefing, 4 pages (8 MB) more there; in the menus only, they share one.
MENU_ONLY_FIELDS = ['briefingPortrait']


def level_ids(name):
    m = re.match(r'L(\d+)(?:-(\d+))?(?:/(\d+))?\b', name)
    if not m:
        return []
    lo = int(m.group(1))
    hi = int(m.group(2)) if m.group(2) else lo
    out = list(range(lo, hi + 1))
    if m.group(3):
        out.append(int(m.group(3)))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--usage', required=True, help='work/atlas-usage (scenes.json, maps.json, class_refs.json)')
    ap.add_argument('--frames', required=True, help="frametable.py's output, for IMAGE_* names")
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    scenes = json.load(open(a.usage + '/scenes.json'))
    maps = json.load(open(a.usage + '/maps.json'))['maps']
    consts = json.load(open(a.frames))['constants']
    name_of = {v: k for k, v in consts.items() if k.startswith('IMAGE_') and not k.startswith('IMAGE_ARRAY_')}
    out_scenes, use = [], {}
    for k, s in enumerate(scenes):
        nm = s['scene']
        keys = ['level:%d' % i for i in level_ids(nm)]
        keys += ['map:%d' % maps[m]['load_id'] for m in s['maps']]
        if s['kind'] in ('cutscene', 'menu'):
            keys.append('app:' + s['roots'][0])
        keys += EXTRA_KEYS.get(nm, [])
        if not keys:
            raise SystemExit(f'scene {nm}: no runtime key')
        out_scenes.append({'name': nm, 'keys': sorted(set(keys))})
        for i in s['images']:
            use.setdefault(name_of[i], []).append(k)
    index = {s['name']: k for k, s in enumerate(out_scenes)}
    # The attract demos play levels too (Demo map N).
    levels = [k for k, s in enumerate(out_scenes) if re.match(r'L\d|Demo map ', s['name']) or s['name'] in EXTRA_KEYS]
    for n, where in EXTRA_IMAGES.items():
        if n not in consts:
            continue  # a release without that image
        ks = levels if where == 'levels' else list(range(len(out_scenes))) if where == 'all' else [index[w] for w in where]
        use[n] = sorted(set(use.get(n, [])) | set(ks))
    for n in consts:
        if n.startswith(PREVIEW_PREFIX):
            use[n] = sorted(index[w] for w in PREVIEW_SCENES)
    refs = json.load(open(a.usage + '/class_refs.json'))['classes']
    for i in {i for v in refs.values() for f in MENU_ONLY_FIELDS for i in v.get('bitmap_fields', {}).get(f, ())}:
        n = name_of[i]
        use[n] = [k for k in use.get(n, []) if k not in levels]
    every = [n for n, v in use.items() if len(v) == len(scenes)]
    data = {
        'comment': 'Which images each scene draws (by Assets.IMAGE_* name) and the runtime keys that enter it. '
        'Written by scene_sets.py from tools/assess/atlas-usage; "*" is every scene.',
        'level_apps': LEVEL_APPS,
        'scenes': out_scenes,
        'images': {n: ('*' if n in every else use[n]) for n in sorted(use)},
    }
    with open(a.out, 'w', newline='\n') as fh:
        fh.write('{\n')
        fh.write(' "comment": {},\n'.format(json.dumps(data['comment'])))
        fh.write(' "level_apps": {},\n'.format(json.dumps(data['level_apps'])))
        fh.write(' "scenes": [\n{}\n ],\n'.format(',\n'.join('  ' + json.dumps(s) for s in out_scenes)))
        fh.write(
            ' "images": {{\n{}\n }}\n}}\n'.format(
                ',\n'.join(
                    '  {}: {}'.format(json.dumps(n), json.dumps(v, separators=(',', ':')))
                    for n, v in data['images'].items()
                )
            )
        )
    print('%d scenes, %d images (%d in every scene)' % (len(out_scenes), len(use), len(every)))


if __name__ == '__main__':
    main()
