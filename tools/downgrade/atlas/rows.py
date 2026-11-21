# ruff: noqa: UP031
"""Names the images of the atlas's miss lines: the player logs `miss, page pNNN in <scene>, image row R`, R the frame
table row (an Assets.IMAGE_* value). With a log it lists each miss with its image's name and the scenes scenes.json
gives it; with numbers, those rows.

Usage: python -I rows.py --asm <Rewind>/assemblies/Assembly-CSharp.dll (--log log.txt | R [R ...])
"""

import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import frametable
import il

MISS = re.compile(r'miss, page (p\d+(?:_\d+)?) in (.+?), image row (-?\d+)')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--asm', required=True, help="Rewind's Assembly-CSharp.dll")
    ap.add_argument('--scenes', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), 'scenes.json'))
    ap.add_argument('--log', help="a player's log")
    ap.add_argument('rows', nargs='*', type=int)
    a = ap.parse_args()
    names = {}
    for k, v in frametable.constants(il.Assembly(a.asm)).items():
        if k.startswith('IMAGE_') and not k.startswith('IMAGE_ARRAY_'):
            names.setdefault(v, k)
    sc = json.load(open(a.scenes))
    scene_names = [s['name'] for s in sc['scenes']]

    def where(n):
        v = sc['images'].get(n)
        return 'every scene' if v == '*' else 'no scene' if v is None else ', '.join(scene_names[i] for i in v)

    if a.log:
        for line in open(a.log, encoding='utf-8', errors='replace'):
            m = MISS.search(line)
            if m:
                r = int(m.group(3))
                n = names.get(r, '?')
                print('%s %s in %s: row %d %s (scenes.json: %s)' % (line.split()[0], m.group(1), m.group(2), r, n, where(n)))
    for r in a.rows:
        n = names.get(r, '?')
        print('row %d %s (scenes.json: %s)' % (r, n, where(n)))


if __name__ == '__main__':
    main()
