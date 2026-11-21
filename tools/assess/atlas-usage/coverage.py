"""Static coverage check of the atlas scene data: which images a scene can draw that its scene set doesn't hold.

Each such image is an atlas miss on the console, a frame of about 200 ms (docs/textures.md). The check reads the
recovered C#, the analysis (class_refs.json and build_scenes.compute), the scene data (tools/downgrade/atlas/
scenes.json) and the packing (frames.bytes), and reports:

  1. images the game's code names that are in no scene, with the methods that name them, split into those the
     code of some scene can reach and those no scene reaches (dead, or a reachability blind spot);
  2. per scene, the suspects: images the code that runs in the scene can draw that its set lacks, found by
     - following hub methods method by method (the analysis stops at MainMenu, Database, GameMap, ...: a level
       that calls Game.pause reaches MainMenu.addAudioOptions; a menu that calls Game.loadScriptByLevelID reaches
       every level script's constructor, its preview),
     - field flow: an image read from a field (bulletType, hitSpark, ...) gets every value code assigns to it,
       in the scenes of the class that assigns it,
     - arithmetic on an image id (base + k, base + rand): the base image's numbered family;
  3. the image sites the analysis can't resolve, by kind: arithmetic, fields, other objects' members, method
     results, array elements, native code, delegates; so a blind spot is visible rather than silent;
  4. class names defined twice (the platform layer's Platform once shadowed the game's);
  5. per scene, the pages the suspects' frames are on now and the MB a fix would add.

Usage (from the repository root):
  python -I tools/assess/atlas-usage/coverage.py --src work/rewind-steam/out/src/Assembly-CSharp \
      --usage work/atlas-usage --frames work/atlas/atlas/frames.bytes [--scenes tools/downgrade/atlas/scenes.json] \
      [--out work/coverage] [--names IMAGE_X ...]
Writes coverage.md and coverage.json to --out and prints a summary.
"""
import argparse, collections, glob, json, os, re, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import analyze_refs as ar  # noqa: E402
import build_scenes  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
KW = {'null', 'true', 'false', 'this', 'base', 'image', 'num', 'new', 'int', 'double', 'bool', 'checked', 'unchecked',
      'uint', 'long', 'byte', 'conv', 'u8', 'u4'}
# The analysis's own tables, and the classes that only define what others use.
TABLES = {'Assets', 'AssetsPack', 'AssetsStaticMap', 'AssetsEmbed', 'Res'}
# Makes the next App, whose scene is entered right after (Atlas.EnterApp): its constructor's images are that scene's.
NEXT_APP = {('Transition', 'getApp')}


def read_frames(path):
    """frames.bytes -> (page sizes [(w, h)], rows: per image row [(page, w, h)])"""
    b = open(path, 'rb').read()
    assert b[:4] == b'T2DF', path
    _, nrows, npages = struct.unpack_from('<III', b, 4)
    off = 16
    sizes = []
    for _ in range(npages):
        sizes.append(struct.unpack_from('<HH', b, off))
        off += 4
    rows = []
    for _ in range(nrows):
        (n,) = struct.unpack_from('<I', b, off)
        off += 4
        fr = []
        for _ in range(n):
            pg, _x, _y, w, h = struct.unpack_from('<HHHHH', b, off)
            off += 10
            fr.append((pg, w, h))
        rows.append(fr)
    return sizes, rows


def int_fields(classes):
    """class -> int fields it declares; every int field name."""
    per, names = {}, set()
    for c, cd in classes.items():
        fs = set(re.findall(r'^\t(?:public|private|internal|protected)(?: static)? int (\w+);', cd['text'], re.M))
        per[c] = fs
        names |= fs
    return per, names


class Tracer:
    """Like analyze_refs.resolve, but says why an expression doesn't resolve."""

    def __init__(self, classes, fields_of, field_names):
        self.classes, self.fields_of, self.field_names = classes, fields_of, field_names

    def class_fields(self, c):
        out, seen = set(), set()
        while c in self.classes and c not in seen:
            seen.add(c)
            out |= self.fields_of.get(c, set())
            c = self.classes[c]['base']
        return out

    def trace(self, expr, m, depth=0):
        """-> (ints, params, issues: set of 'kind:detail', arith bases)"""
        ints, params, issues, bases = set(), set(), set(), set()
        km = re.search(r'\)\s*\+\s*(\d+)\s*$', expr)     # (flag ^ 1) + K, (cond ? 1 : 0) + K: the analysis's case
        if km:
            k = int(km.group(1))
            return {k, k + 1}, set(), set(), set()
        if re.search(r'Assets\.IMAGE_|(?<![\w.])Res\.', expr):
            return set(), set(), set(), set()       # a BitmapData the analysis reads by its Assets name
        dm = re.fullmatch(r'\s*(?:\(\w+\)\s*)?(?:(?:this|base|[A-Za-z_][\w.]*)\.)?([A-Za-z_]\w*)\s*', expr)
        if dm and dm.group(1) in self.field_names and dm.group(1) != 'image' and (
                '.' in expr or dm.group(1) in self.class_fields(m.cls)):
            issues.add('field:' + dm.group(1))     # the value is a field: field flow follows it
            return ints, params, issues, bases
        if '__Rewind' in expr or '__native' in expr:
            issues.add('native')
        for mm in re.finditer(r'(?<![\w.])(?!this\b|base\b)([A-Za-z_]\w*)((?:\.\w+)+)(?![\w(])', expr):
            last = mm.group(2).rsplit('.', 1)[-1]
            if last == 'image':
                issues.add('member:image')     # another object's current image, set where it was drawn
            elif mm.group(1) not in ('Sprites',) or last not in ('images',):
                issues.add('member:%s%s' % (mm.group(1), mm.group(2)))
        for mm in re.finditer(r'(?<![\w.])(\w+)\[', expr):
            if mm.group(1) != 'images':
                issues.add('index:' + mm.group(1))
        for mm in re.finditer(r'(?<![\w])([A-Za-z_]\w*)\s*\(', expr):
            if mm.group(1) != 'Next':
                issues.add('call:' + mm.group(1))
        e2 = re.sub(r'rand\.Next\((-?\d+), (-?\d+)\)', lambda r: ' ', expr)
        for rm in re.finditer(r'rand\.Next\((-?\d+), (-?\d+)\)', expr):
            ints.update(range(int(rm.group(1)), int(rm.group(2))))
        e2 = re.sub(r'rand\.Next\([^)]*\)', ' ', e2)
        e2 = re.sub(r'"[^"]*"', ' ', e2)
        for im in ar.INT.finditer(e2):
            before, after = e2[:im.start()], e2[im.end():]
            if ar.ARITH.search(before) or ar.ARITH.search(after):
                issues.add('arith')
                bases.add(int(im.group(1)))
                continue
            if ar.CMP_B.search(before) or ar.CMP_A.search(after):
                continue
            ints.add(int(im.group(1)))
        cf = self.class_fields(m.cls)
        for ident in set(re.findall(r'(?<![\w.])([A-Za-z_]\w*)(?![\w(.\[])', e2)):
            if ident in KW:
                continue
            if ident in m.params:
                params.add(ident)
                continue
            rhss = [am.group(1) for am in re.finditer(r'(?<![\w.])%s = ([^;\n]+);' % re.escape(ident), m.body)]
            if any('loadImage' in r for r in rhss):
                rhss = [r for r in rhss if 'loadImage' not in r]
                if not rhss:
                    continue                        # a BitmapData local: the analysis reads its Assets name
            if rhss:
                if depth >= 3:
                    issues.add('deep')
                    continue
                for rhs in rhss:
                    a, b, c, d = self.trace(rhs, m, depth + 1)
                    ints |= a
                    params |= b
                    issues |= c
                    bases |= d
            elif ident in cf:
                issues.add('member:this.' + ident)  # a field in a condition or a sum; not followed
            else:
                issues.add('unknown:' + ident)     # a local set by ref/out or in a loop, or a field of a class not read
        ints.discard(-1)
        return ints, params, issues, bases


def method_sites(m, classes, target_method):
    """(kind, expr) of every image site in a method."""
    out = [('ctx', e) for e in ar.image_exprs(m.body)]
    sites = list(ar.call_sites(m.body, classes, m.cls))
    if m.base_args is not None:
        sites.append((classes[m.cls]['base'], '.ctor', m.base_args))
    for tc, key, args in sites:
        tm = target_method(tc, key)
        if tm is None:
            continue
        for p in tm.img_params:
            k = tm.params.index(p)
            if k < len(args):
                out.append(('arg %s.%s' % (tm.cls, key), args[k]))
    for mm in re.finditer(r'loadImage(?:Array)?\(', m.body):
        arg = ar.call_args(m.body, mm.end() - 1)[0].strip()
        if not arg.startswith('Assets.') and arg not in ('int name',) and not re.match(r'^\w+ \w+$', arg):
            out.append(('loadImage', arg))
    for mm in re.finditer(r'(?:new |\(\(|typeof\()(com\.bitmapbureau\.terminator\.[\w.]+)[)(]', m.body):
        out.append(('qualified', mm.group(1)))   # a class named by its namespace: call_sites' patterns miss it
    if '__ldftn(' in m.body:
        for mm in re.finditer(r'__ldftn\(([\w.]+)\)', m.body):
            if mm.group(1).endswith('changeImage') or 'draw' in mm.group(1).lower():
                out.append(('delegate', mm.group(1)))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--src', required=True, help="Rewind's C# (out/src/Assembly-CSharp)")
    ap.add_argument('--usage', required=True, help='the analysis (class_refs.json, images.json, maps.json, ...)')
    ap.add_argument('--frames', required=True, help="the packing's frames.bytes")
    ap.add_argument('--scenes', default=os.path.join(REPO, 'tools/downgrade/atlas/scenes.json'))
    ap.add_argument('--out', default=os.path.join(REPO, 'work/coverage'))
    ap.add_argument('--names', nargs='*', default=[], help='images to look up (from miss logs)')
    ap.add_argument('--later', help='a later scene data: report which of the pairs it adds this check flags')
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    classes = ar.load_classes(a.src)
    T = os.path.join(a.src, 'com/bitmapbureau/terminator')
    imgc, arr_images, res_fields, text_assets = ar.asset_tables(classes, T)
    name_of = {v: k for k, v in imgc.items()}
    target_method = ar.param_flow(classes)
    cr = json.load(open(os.path.join(a.usage, 'class_refs.json')))
    C = cr['classes']
    sprite_img = {int(k): v for k, v in cr['sprite_img'].items()}
    images, computed = build_scenes.compute(a.usage)
    sc = json.load(open(a.scenes))
    scene_names = [s['name'] for s in sc['scenes']]
    n_sc = len(scene_names)
    have = collections.defaultdict(set)      # scene index -> image ids
    for n, v in sc['images'].items():
        if n not in imgc:
            continue
        for k in (range(n_sc) if v == '*' else v):
            have[k].add(imgc[n])
    in_any = set().union(*have.values())
    usage_index = {s[0]: k for k, s in enumerate(computed)}
    scene_roots = {s[0]: set(s[2]) for s in computed}
    scene_dirs = {'scripts', 'cutscenes', 'apps'}

    def scene_cls(c):
        return C[c]['file'].split('/')[-2] in scene_dirs and c not in build_scenes.BASES
    scene_classes = {k: computed[usage_index[nm]][6] if nm in usage_index else set() for k, nm in enumerate(scene_names)}
    sizes, rows = read_frames(a.frames)
    page_mb = [w * h * 2 / 1048576 for w, h in sizes]

    def sids_imgs(sids):
        out = set()
        for s in sids:
            out.update(sprite_img.get(s, ()))
        return out

    def method_images(c, key):
        mv = C.get(c, {}).get('by_method', {}).get(key)
        if not mv:
            return set()
        return set(mv['assets_images']) | sids_imgs(mv['sprite_ids'])

    # -- 4. class names defined twice
    defs = collections.defaultdict(list)
    for fn in glob.glob(os.path.join(a.src, '**', '*.cs'), recursive=True):
        mm = ar.CLASS.search(open(fn, encoding='utf-8', errors='replace').read())
        if mm:
            defs[mm.group(1)].append(os.path.relpath(fn, a.src).replace('\\', '/'))
    dups = {c: fs for c, fs in defs.items() if len(fs) > 1 and any(ar.GAME_NS in f for f in fs)}
    # The analysis keys classes by simple name: a game class it read from another namespace's file is shadowed.
    shadowed = {c: C[c]['file'] for c in dups if c in C and ar.GAME_NS not in C[c]['file']}

    # -- 3. sites
    fields_of, field_names = int_fields(classes)
    tr = Tracer(classes, fields_of, field_names)
    sites = []           # dict(cls, meth, kind, expr, issues, ints, bases)
    for c, cd in classes.items():
        if c in TABLES:
            continue
        for key, m in cd['methods'].items():
            for kind, e in method_sites(m, classes, target_method):
                if kind in ('delegate', 'qualified'):
                    sites.append(dict(cls=c, meth=key, kind=kind, expr=e, issues=[kind], ints=[], bases=[]))
                    continue
                ints, params, issues, bases = tr.trace(e, m)
                issues.discard('member:image')
                if issues:
                    sites.append(dict(cls=c, meth=key, kind=kind, expr=e[:80], issues=sorted(issues),
                                      ints=sorted(i for i in ints if 0 < i < 726), bases=sorted(bases)))

    # field flow: every value code assigns to a field that an image site reads, attributed to the assigning class
    img_fields = {i.split(':', 1)[1] for s in sites for i in s['issues'] if i.startswith('field:')}
    img_fields.discard('image')
    field_vals = collections.defaultdict(lambda: collections.defaultdict(set))   # field -> class -> sprite ids
    loader = re.compile(r'loadImage')
    for c, cd in classes.items():
        if c in TABLES:
            continue
        for key, m in cd['methods'].items():
            for f in img_fields:
                for am in re.finditer(r'(?<![\w])(?:[\w\]\)]+\.)?%s = ([^;\n]+);' % re.escape(f), m.body):
                    rhs = am.group(1)
                    if 'new ' in rhs or loader.search(rhs):
                        continue
                    ints, _, _, _ = tr.trace(rhs, m)
                    ids = {i for i in ints if 0 < i < 726}
                    if ids:
                        field_vals[f][c] |= ids

    # -- 2. suspects per scene
    hubs = build_scenes.HUBS
    calls = {}

    def callees(c, key):
        if (c, key) not in calls:
            m = classes[c]['methods'].get(key)
            out = set()
            if m is not None:
                for tc, tkey, _ in ar.call_sites(m.body, classes, c):
                    tm = target_method(tc, tkey)
                    if tm is not None:
                        out.add((tm.cls, tm.name))
            calls[(c, key)] = out
        return calls[(c, key)]

    fam = collections.defaultdict(set)
    for im in images:
        f = build_scenes.family(im['name'])
        if f:
            fam[f].add(im['id'])

    # The demos' scenes leave out the other demos' players (build_scenes.DEMOS).
    stops = {d[0]: {o[1] for o in build_scenes.DEMOS if o is not d} for d in build_scenes.DEMOS}

    def derives_go(c):
        seen = set()
        while c in classes and c not in seen:
            if c == 'GameObject':
                return True
            seen.add(c)
            c = classes[c]['base']
        return False

    def sprite_site(s):
        """A site whose value is a sprite id (not a BitmapData or a menu's image)."""
        return s['kind'] == 'ctx' or derives_go(s['kind'].split(' ', 1)[-1].split('.')[0])

    suspects = {}        # scene index -> image id -> set of reasons
    for k, nm in enumerate(scene_names):
        cl = scene_classes[k]
        roots = scene_roots.get(scene_names[k], ())
        found = collections.defaultdict(set)
        # Hub methods, method by method, from the scene's own code and the hubs the analysis counts whole. Into a
        # helper class the analysis didn't reach: the whole class. Into another scene's class (a menu that makes a
        # level script to show it): only the BitmapData fields it loads that this scene's code reads (preview).
        whole = ('Game', 'Sprites', 'Transition')
        start = [(c, key) for c in cl if c not in hubs or c in whole or c in roots
                 for key in classes.get(c, {}).get('methods', {})
                 if (c, key) not in build_scenes.HUB_METHODS_EXCLUDED and (c, key) not in NEXT_APP]
        text = '\n'.join(classes[c]['text'] for c in cl if c in classes and (c not in hubs or c in roots))
        seen, stack = set(start), list(start)
        while stack:
            c, key = stack.pop()
            for tc, tkey in callees(c, key):
                if (tc, tkey) in seen or (tc in cl and (tc not in hubs or tc in whole or tc in roots)):
                    continue
                if (tc, tkey) in build_scenes.HUB_METHODS_EXCLUDED or (tc, tkey) in NEXT_APP or tc in TABLES \
                        or tc in stops.get(nm, ()):
                    continue
                seen.add((tc, tkey))
                if scene_cls(tc) and tc not in hubs:
                    body = classes[tc]['methods'][tkey].body
                    for f, n in re.findall(r'(?<![\w.])(\w+) = AssetsEmbed\.loadImage\(Assets\.(IMAGE_\w+)\)', body):
                        if n in imgc and re.search(r'\.%s\b(?!\s*=[^=])' % f, text):
                            found[imgc[n]].add('via %s.%s, field %s' % (tc, tkey, f))
                    continue
                if tc not in hubs and tc not in cl:
                    for k2 in classes[tc]['methods']:
                        if (tc, k2) not in seen:
                            seen.add((tc, k2))
                            stack.append((tc, k2))
                    for i in set(C[tc]['assets_images']) | sids_imgs(C[tc]['sprite_ids']):
                        found[i].add('via %s (from %s.%s)' % (tc, c, key))
                    continue
                stack.append((tc, tkey))
                for i in method_images(tc, tkey):
                    found[i].add('via %s.%s' % (tc, tkey))
        # field flow
        for f, per in field_vals.items():
            readers = {s['cls'] for s in sites if 'field:' + f in s['issues']}
            if not readers & cl:
                continue
            for c, ids in per.items():
                if c in cl:
                    for i in sids_imgs(ids):
                        found[i].add('field %s.%s' % (c, f))
        # arithmetic bases: the base image's family
        for s in sites:
            if s['cls'] in cl and 'arith' in s['issues'] and sprite_site(s):
                for b in s['bases']:
                    for i in sids_imgs([b]) if 0 < b < 726 else ():
                        f = build_scenes.family(images[i]['name'])
                        for j in fam.get(f, {i}):
                            found[j].add('arith %s.%s' % (s['cls'], s['meth']))
        # A numbered family (IMAGE_X_0, _1, ...; debris) the scene has part of: the game picks its members by
        # arithmetic on the first one's id.
        for i in list(have[k]):
            f = build_scenes.family(images[i]['name'])
            for j in fam.get(f, ()):
                found[j].add('family %s' % f[6:])
        lang_ok = {i for i in found if not any(images[i]['name'].endswith('_' + l) for l in build_scenes.LANGS)}
        suspects[k] = {i: found[i] for i in lang_ok if i not in have[k]}

    # -- 1. referenced anywhere, in no scene
    refs = collections.defaultdict(set)      # image -> 'Class.method'
    in_table = set()                          # images of a sprite id: Sprites.loadFromID's table
    for c, v in C.items():
        if c in TABLES:
            continue
        for key, mv in v.get('by_method', {}).items():
            if (c, key) in build_scenes.HUB_METHODS_EXCLUDED:
                in_table |= set(mv['assets_images']) | sids_imgs(mv['sprite_ids'])
                continue
            for i in set(mv['assets_images']) | sids_imgs(mv['sprite_ids']):
                refs[i].add('%s.%s' % (c, key))
    for f, per in field_vals.items():
        for c, ids in per.items():
            for i in sids_imgs(ids):
                refs[i].add('%s (field %s)' % (c, f))
    for nid, o in cr['objects'].items():
        for i in sids_imgs(o['sprite_ids']):
            refs[i].add('map object %s' % o['name'])
    reach_cls = set().union(*scene_classes.values())
    reach_any = set().union(*(set(v) for v in suspects.values())) | in_any
    no_scene = {i: sorted(r) for i, r in refs.items() if i not in in_any}
    no_scene_reach = {i: r for i, r in no_scene.items()
                      if i in reach_any or any(x.split('.')[0].split(' ')[0] in reach_cls for x in r)}
    no_scene_dead = {i: r for i, r in no_scene.items() if i not in no_scene_reach}
    # A sprite only the table names: no code names its id, so code reaches it by arithmetic, a field, a table or
    # not at all. Those in no scene are where a blind spot would be.
    table_only = sorted((i for i in in_table if i not in refs and i not in in_any), key=lambda i: name_of.get(i, ''))
    unreferenced = [im['id'] for im in images if im['id'] not in in_any and im['id'] not in refs and im['id'] not in in_table]

    # -- 5. cost
    def cost(k, ids):
        pages = {p for i in have[k] for p, _, _ in rows[i]}
        new = {p for i in ids for p, _, _ in rows[i]} - pages
        area = sum((w + 4) * (h + 4) for i in ids for _, w, h in {(p, w, h) for p, w, h in rows[i]}) * 2 / 1048576
        return sorted(new), sum(page_mb[p] for p in new), area

    # -- report
    md = ['# Atlas scene data coverage', '']
    md += ['Scene data: %s; analysis: %s; packing: %s.' % (os.path.relpath(a.scenes, REPO), a.usage, a.frames), '']
    md += ['## Suspects per scene', '',
           '| Scene | suspects | pages now (MB) | frames (MB) | images |', '| --- | --- | --- | --- | --- |']
    js_sus = {}
    for k, nm in enumerate(scene_names):
        ids = suspects[k]
        if not ids:
            continue
        new, mb, area = cost(k, ids)
        names = sorted(name_of.get(i, str(i)) for i in ids)
        js_sus[nm] = {name_of.get(i, str(i)): sorted(r) for i, r in ids.items()}
        md.append('| %s | %d | %d (%.0f) | %.2f | %s |' % (nm, len(ids), len(new), mb, area,
                                                          ', '.join(n[6:] for n in names[:12]) + (' ...' if len(names) > 12 else '')))
    md += ['', '### Why', '']
    for nm, d in js_sus.items():
        md.append('- %s: ' % nm + '; '.join('%s (%s)' % (n[6:], ', '.join(sorted(r)[:2])) for n, r in sorted(d.items())))
    md += ['', '## Images in no scene', '',
           '%d the code names: %d that a scene\'s code reaches, %d that no scene reaches. %d sprites only the sprite '
           'table names. %d images nothing names (dead: the other languages\' fonts and the like).'
           % (len(no_scene), len(no_scene_reach), len(no_scene_dead), len(table_only), len(unreferenced)), '']
    for title, d in (('Named by code, reached', no_scene_reach), ('Named by code, no scene reaches', no_scene_dead)):
        md += ['### ' + title, '']
        for i in sorted(d, key=lambda i: name_of.get(i, '')):
            md.append('- %s: %s' % (name_of.get(i, i), ', '.join(d[i][:4]) + (' ...' if len(d[i]) > 4 else '')))
        md.append('')
    md += ['### Only the sprite table names them', '', ', '.join(name_of.get(i, str(i))[6:] for i in table_only), '']
    md += ['## Image sites the analysis can\'t resolve', '']
    kinds = collections.Counter(i.split(':')[0] for s in sites for i in s['issues'])
    md.append(', '.join('%s %d' % kv for kv in kinds.most_common()))
    md.append('')
    for s in sorted(sites, key=lambda s: (s['issues'][0].split(':')[0], s['cls'], s['meth'])):
        md.append('- %s.%s [%s] `%s`: %s%s' % (s['cls'], s['meth'], s['kind'], s['expr'], ', '.join(s['issues']),
                                                ' (resolves %s)' % s['ints'] if s['ints'] else ''))
    md += ['', '## Fields read as images', '']
    for f in sorted(field_vals):
        md.append('- %s: %s' % (f, '; '.join('%s %s' % (c, sorted(v)) for c, v in sorted(field_vals[f].items()))))
    md += ['', '## Class names defined twice', '']
    for c, fs in sorted(dups.items()):
        md.append('- %s: %s%s' % (c, ', '.join(fs), ('; SHADOWED: the analysis read %s' % shadowed[c]) if c in shadowed else ''))
    if a.later:
        # Pairs (image, scene) a later scene data adds, and whether this check flags them on the earlier one.
        lt = json.load(open(a.later))
        lnames = [s['name'] for s in lt['scenes']]
        added = collections.defaultdict(set)
        for n, v in lt['images'].items():
            if n not in imgc:
                continue
            for k2 in (range(len(lnames)) if v == '*' else v):
                k = scene_names.index(lnames[k2]) if lnames[k2] in scene_names else None
                if k is not None and imgc[n] not in have[k]:
                    added[n].add(k)
        caught, missed = collections.defaultdict(set), collections.defaultdict(set)
        for n, ks in added.items():
            for k in ks:
                i = imgc[n]
                ok = i in suspects[k] or i in no_scene or i in table_only
                (caught if ok else missed)[n].add(scene_names[k])
        npairs = sum(len(v) for v in added.values())
        ncaught = sum(len(v) for v in caught.values())
        md += ['', '## Against %s' % os.path.relpath(a.later, REPO), '',
               '%d (image, scene) pairs it adds, %d images; flagged here: %d pairs, %d images.' % (
                   npairs, len(added), ncaught, len(caught)), '', '### Not flagged', '']
        for n in sorted(missed):
            md.append('- %s: %s' % (n, ', '.join(sorted(missed[n]))[:300]))
        print('against %s: %d of %d added pairs flagged (%d of %d images); not flagged: %s' % (
            os.path.relpath(a.later, REPO), ncaught, npairs, len(caught), len(added),
            ', '.join(n[6:] for n in sorted(missed))[:600]))
    if a.names:
        md += ['', '## Names asked for', '']
        for n in a.names:
            i = imgc.get(n)
            where = [scene_names[k] for k in range(n_sc) if i in suspects[k]]
            md.append('- %s: %s' % (n, ('suspect in ' + ', '.join(where)) if where else
                                    'in no scene, named by code' if i in no_scene else 'not flagged'))
    open(os.path.join(a.out, 'coverage.md'), 'w', encoding='utf-8', newline='\n').write('\n'.join(md) + '\n')
    json.dump(dict(suspects=js_sus, no_scene_reached={name_of.get(i, str(i)): r for i, r in no_scene_reach.items()},
                   no_scene_dead={name_of.get(i, str(i)): r for i, r in no_scene_dead.items()},
                   table_only=[name_of.get(i, str(i)) for i in table_only],
                   unreferenced=len(unreferenced), sites=sites, duplicates=dups, shadowed=shadowed),
              open(os.path.join(a.out, 'coverage.json'), 'w'), indent=1)
    tot = sum(len(v) for v in suspects.values())
    print('suspects: %d (scene, image) pairs in %d scenes; in no scene: %d named by code (%d reached, %d not), '
          '%d only by the sprite table, %d by nothing; unresolved sites: %d (%s); duplicate classes: %s' % (
              tot, sum(1 for v in suspects.values() if v), len(no_scene), len(no_scene_reach), len(no_scene_dead),
              len(table_only), len(unreferenced), len(sites), ', '.join('%s %d' % kv for kv in kinds.most_common()),
              ', '.join(c + (' (SHADOWED by %s)' % shadowed[c] if c in shadowed else '') for c in sorted(dups))))
    for n in a.names:
        i = imgc.get(n)
        where = [scene_names[k] for k in range(n_sc) if i in suspects[k]]
        print('  %s: %s' % (n, ('suspect in ' + ', '.join(where[:4]) + (' ...' if len(where) > 4 else '')) if where
                            else 'in no scene, named by code' if i in no_scene
                            else 'in no scene, only the sprite table names it' if i in table_only else 'not flagged'))
    print('report:', os.path.join(a.out, 'coverage.md'))


if __name__ == '__main__':
    main()
