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
    ap.add_argument('--usage', required=True, help='work/atlas-usage (scenes.json, maps.json)')
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
            raise SystemExit('scene %s: no runtime key' % nm)
        out_scenes.append(dict(name=nm, keys=sorted(set(keys))))
        for i in s['images']:
            use.setdefault(name_of[i], []).append(k)
    every = [n for n, v in use.items() if len(v) == len(scenes)]
    data = dict(
        comment='Which images each scene draws (by Assets.IMAGE_* name) and the runtime keys that enter it. '
                'Written by scene_sets.py from tools/assess/atlas-usage; "*" is every scene.',
        level_apps=LEVEL_APPS,
        scenes=out_scenes,
        images={n: ('*' if n in every else use[n]) for n in sorted(use)},
    )
    with open(a.out, 'w', newline='\n') as fh:
        fh.write('{\n')
        fh.write(' "comment": %s,\n' % json.dumps(data['comment']))
        fh.write(' "level_apps": %s,\n' % json.dumps(data['level_apps']))
        fh.write(' "scenes": [\n%s\n ],\n' % ',\n'.join('  ' + json.dumps(s) for s in out_scenes))
        fh.write(' "images": {\n%s\n }\n}\n' % ',\n'.join(
            '  %s: %s' % (json.dumps(n), json.dumps(v, separators=(',', ':'))) for n, v in data['images'].items()))
    print('%d scenes, %d images (%d in every scene)' % (len(out_scenes), len(use), len(every)))


if __name__ == '__main__':
    main()
