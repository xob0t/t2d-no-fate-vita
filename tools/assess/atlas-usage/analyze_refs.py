"""Static image-reference analysis of Terminator 2D: NO FATE (Rewind's C#).

For every game class it collects the images it can draw:
  * Assets.IMAGE_* / IMAGE_ARRAY_* constants (AssetsEmbed.loadImage[Array])
  * sprite ids (Sprites.images[...] slots; Haxe inlines them as literals) in
    image contexts: changeImage(e), image = e, Sprites.drawSprite(e, ..),
    Sprites.drawShadow(e, ..), Sprites.images[e]; plus literals passed at an
    image parameter position of a constructor/method (parameter flow, fixpoint)
and the classes it can create or call (new C, typeof(C), C.member, base class).

Sprite id -> image comes from running Sprites.loadFromID with csinterp; map
object name id -> classes/sprite ids from running Sprites.addFromJSON with
csinterp plus the native jump tables (native_dispatch.py output).

Writes class_refs.json to --out.
"""
import argparse, collections, glob, json, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csinterp import parse, compile_tree, run, method_lines
from parse_cs import static_ints

HDR = re.compile(r'^\t(?:public|private|internal|protected)[^\n=;]*?\b([A-Za-z_]\w*)\(([^)]*)\)\s*$')
CLASS = re.compile(r'^public (?:sealed |abstract |static )*class (\w+)(?: : ([\w.]+))?', re.M)
INT = re.compile(r'(?<![\w.])(-?\d+)(?![\w.])')
CMP_B = re.compile(r'(==|!=|<=|>=|<|>)\s*$')
CMP_A = re.compile(r'^\s*(==|!=|<=|>=|<|>)')
ARITH = re.compile(r'[-+*/%^]\s*$|^\s*[-+*/%^]')


def split_args(s):
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '([{':
            depth += 1
        elif ch in ')]}':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return [re.sub(r'^\w+:\s*', '', a) for a in out]   # drop named-arg labels


def call_args(text, start):
    """text[start] == '(' ; return (args string, end index)."""
    depth = 0
    for i in range(start, len(text)):
        if text[i] == '(':
            depth += 1
        elif text[i] == ')':
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i
    return text[start + 1:], len(text)


class Method:
    def __init__(self, cls, name, params, body, base_args):
        self.cls, self.name, self.params, self.body, self.base_args = cls, name, params, body, base_args
        self.img_params = set()


def load_classes(src_root):
    classes = {}
    for fn in glob.glob(os.path.join(src_root, 'com', '**', '*.cs'), recursive=True):
        text = open(fn, encoding='utf-8').read()
        m = CLASS.search(text)
        if not m:
            continue
        cname, base = m.group(1), (m.group(2) or '').split('.')[-1]
        lines = text.split('\n')
        methods = {}
        i = 0
        while i < len(lines):
            hm = HDR.match(lines[i])
            if hm:
                name, params = hm.group(1), hm.group(2)
                base_args = None
                j = i + 1
                if j < len(lines) and lines[j].strip().startswith(': base('):
                    s = lines[j].strip()
                    base_args = split_args(call_args(s, s.index('('))[0])
                    j += 1
                if j < len(lines) and lines[j] == '\t{':
                    k = j + 1
                    while k < len(lines) and lines[k] != '\t}':
                        k += 1
                    body = '\n'.join(lines[j + 1:k])
                    pnames = [p.split()[-1] for p in split_args(params)] if params.strip() else []
                    key = '.ctor' if name == cname else name
                    methods[key] = Method(cname, key, pnames, body, base_args)
                    i = k
            i += 1
        rel = os.path.relpath(fn, src_root).replace('\\', '/')
        new = dict(file=rel, base=base, methods=methods, text=text)
        old = classes.get(cname)
        if old is None:
            classes[cname] = new
            continue
        # Classes are keyed by their simple name, as the code names them. Three names are used twice: the platform
        # layer's com.twissgreen.unity.Platform beside the game's sprites.objects.Platform, the base of every
        # platform and lift, two Utils, and two game EnemyCorpse. The last file read used to win, so Platform's
        # changeImage(_image) was lost, with every sprite id a map object passes to a Platform subclass (Future War
        # 3's tunnel platforms, its lifts, ...). The game's class wins over another namespace's; two of the game's
        # merge, a method of both getting both bodies.
        game_old, game_new = (GAME_NS in f for f in (old['file'], rel))
        if game_new and not game_old:
            classes[cname] = new
        elif game_new == game_old:
            for key, m in methods.items():
                o = old['methods'].get(key)
                if o is None:
                    old['methods'][key] = m
                else:
                    o.body += '\n' + m.body
                    o.params = o.params if len(o.params) >= len(m.params) else m.params
            old['text'] += '\n' + text
            old['base'] = old['base'] or base
    return classes


GAME_NS = 'com/bitmapbureau/terminator/'


def resolve(expr, m, depth=0):
    """-> (set of int literals, set of param names, dynamic flag)"""
    ints, params, dyn = set(), set(), False
    km = re.search(r'\)\s*\+\s*(\d+)\s*$', expr)     # (flag ^ 1) + K  /  (cond ? 1 : 0) + K
    if km:
        k = int(km.group(1))
        return {k, k + 1}, set(), True
    for rm in re.finditer(r'rand\.Next\((-?\d+), (-?\d+)\)', expr):
        ints.update(range(int(rm.group(1)), int(rm.group(2))))
    expr2 = re.sub(r'rand\.Next\([^)]*\)', ' ', expr)
    for im in INT.finditer(expr2):
        before, after = expr2[:im.start()], expr2[im.end():]
        if ARITH.search(before) or ARITH.search(after):
            dyn = True
            continue
        if CMP_B.search(before) or CMP_A.search(after):
            continue          # compared against, not drawn
        ints.add(int(im.group(1)))
    for ident in set(re.findall(r'(?<![\w.])([A-Za-z_]\w*)(?![\w(])', expr2)):
        if ident in ('null', 'true', 'false', 'this', 'image', 'num', 'new', 'int'):
            continue
        if ident in m.params:
            params.add(ident)
            continue
        if depth < 2:
            for am in re.finditer(r'(?<![\w.])%s = ([^;\n]+);' % re.escape(ident), m.body):
                rhs = am.group(1)
                if 'loadImage' in rhs:
                    continue
                a, b, c = resolve(rhs, m, depth + 1)
                ints |= a
                params |= b
                dyn |= c
        else:
            dyn = True
    ints.discard(-1)
    return ints, params, dyn


IMG_CTX = [re.compile(r'changeImage\('), re.compile(r'Sprites\.drawSprite\('), re.compile(r'Sprites\.drawShadow\('),
           re.compile(r'(?<![\w])drawShadow\(')]


def image_exprs(body):
    out = []
    for pat in IMG_CTX:
        for mm in pat.finditer(body):
            args = split_args(call_args(body, mm.end() - 1)[0])
            if args:
                out.append(args[0])
    for mm in re.finditer(r'(?<![\w])image = ([^;\n]+);', body):
        if 'loadImage' not in mm.group(1) and 'BitmapData' not in mm.group(1):
            out.append(mm.group(1))
    for mm in re.finditer(r'Sprites\.images\[([^\]]+)\]', body):
        out.append(mm.group(1))
    return out


def call_sites(body, classes, own=None):
    """yield (target_class, method_key, args); own = calling class (for base..ctor and bare calls)"""
    if own in classes:
        for mm in re.finditer(r'(?<![\w.])base\.\.ctor\(', body):
            yield classes[own]['base'], '.ctor', split_args(call_args(body, mm.end() - 1)[0])
        for mm in re.finditer(r'(?<![\w.])(?:this\.|base\.)?(\w+)\(', body):
            yield own, mm.group(1), split_args(call_args(body, mm.end() - 1)[0])
    for mm in re.finditer(r'new (\w+)\(', body):
        c = mm.group(1)
        if c in classes:
            yield c, '.ctor', split_args(call_args(body, mm.end() - 1)[0])
    # inlined constructors: ((Base)v)..ctor(args)  or  v..ctor(args)
    decl = {mm.group(2): mm.group(1) for mm in re.finditer(r'(\w+) (\w+) = \(\w+\)FormatterServices\.GetUninitializedObject', body)}
    for mm in re.finditer(r'(?:\(\((\w+)\)(\w+)\)|(\w+))\.\.ctor\(', body):
        c = mm.group(1) or decl.get(mm.group(3))
        if c in classes:
            yield c, '.ctor', split_args(call_args(body, mm.end() - 1)[0])
    for mm in re.finditer(r'(?<![\w.])(\w+)\.(\w+)\(', body):
        c, meth = mm.group(1), mm.group(2)
        if c in classes:
            yield c, meth, split_args(call_args(body, mm.end() - 1)[0])


def asset_tables(classes, T):
    """-> (Assets.IMAGE_* name -> id, IMAGE_ARRAY_* name -> ids, Res field -> ids, text_assets(text) -> ids)."""
    assets = open(os.path.join(T, 'Assets.cs'), encoding='utf-8').read()
    imgc = static_ints(assets, 'IMAGE_')
    arrc = {k: v for k, v in imgc.items() if k.startswith('IMAGE_ARRAY_')}
    imgc = {k: v for k, v in imgc.items() if not k.startswith('IMAGE_ARRAY_')}
    # arrayentries (Assets.staticInit12): inner arrays tN get elements via
    # __store4((tN + off), Assets.X) or __ArraySetAt(tN, j, conv.u8(Assets.X));
    # the outer array t0 gets them via __store8((t0 + 32 + 8K), tN) or __ArraySetAt(t0, K, tN).
    body12 = assets[assets.index('public static void staticInit12()'):]
    body12 = body12[:body12.index('\n\t}\n')]
    elems = collections.defaultdict(list)
    for mm in re.finditer(r'__store4\(\(t(\d+) \+ \d+L\), Assets\.(\w+)\)|__ArraySetAt\(t(\d+), \d+L, conv\.u8\(Assets\.(\w+)\)\)', body12):
        elems[int(mm.group(1) or mm.group(3))].append(mm.group(2) or mm.group(4))
    arrays = {}
    for mm in re.finditer(r'__store8\(\(t0 \+ (\d+)L\), t(\d+)\)|__ArraySetAt\(t0, (\d+)L, t(\d+)\)', body12):
        k = (int(mm.group(1)) - 32) // 8 if mm.group(1) else int(mm.group(3))
        arrays[k] = elems.get(int(mm.group(2) or mm.group(4)), [])
    arr_images = {name: [imgc[x] for x in arrays.get(idx, []) if x in imgc] for name, idx in arrc.items()}
    # Some sub-arrays use the initializer form; every array also follows the
    # naming rule IMAGE_ARRAY_X -> IMAGE_X_ARRAY_k, so use that and check it.
    for name in arrc:
        base = 'IMAGE_' + name[len('IMAGE_ARRAY_'):] + '_ARRAY_'
        by_name = sorted((int(k[len(base):]), v) for k, v in imgc.items() if k.startswith(base) and k[len(base):].isdigit())
        by_name = [v for _, v in by_name]
        if arr_images[name] and sorted(arr_images[name]) != sorted(by_name):
            print('array mismatch', name, file=sys.stderr)
        if not arr_images[name]:
            arr_images[name] = by_name
    # Res.staticInit preloads images into static fields: FIELD = AssetsEmbed.loadImage(Assets.IMAGE_Y)
    res_src = classes['Res']['text']
    res_fields = collections.defaultdict(set)
    for mm in re.finditer(r'\n\t\t(\w+) = AssetsEmbed\.loadImage(?:Array)?\(Assets\.(IMAGE_\w+)\);', res_src):
        n = mm.group(2)
        res_fields[mm.group(1)].update([imgc[n]] if n in imgc else arr_images.get(n, []))

    def text_assets(t):
        out = set()
        for mm in re.finditer(r'Assets\.(IMAGE_\w+)', t):
            n = mm.group(1)
            out.update([imgc[n]] if n in imgc else arr_images.get(n, []))
        for mm in re.finditer(r'(?<![\w.])Res\.(\w+)', t):
            out.update(res_fields.get(mm.group(1), ()))
        return out

    return imgc, arr_images, res_fields, text_assets


def param_flow(classes):
    """Marks each method's image parameters (Method.img_params), to a fixpoint; returns target_method(class, key)."""
    def target_method(c, key):
        while c in classes:
            if key in classes[c]['methods']:
                return classes[c]['methods'][key]
            c = classes[c]['base']
        return None

    changed = True
    while changed:
        changed = False
        for c, cd in classes.items():
            for m in cd['methods'].values():
                before = set(m.img_params)
                for e in image_exprs(m.body):
                    m.img_params |= resolve(e, m)[1]
                sites = list(call_sites(m.body, classes, c))
                if m.base_args is not None:
                    sites.append((cd['base'], '.ctor', m.base_args))
                for tc, key, args in sites:
                    tm = target_method(tc, key)
                    if tm is None:
                        continue
                    for p in tm.img_params:
                        k = tm.params.index(p)
                        if k < len(args):
                            m.img_params |= resolve(args[k], m)[1]
                if m.img_params != before:
                    changed = True
    return target_method


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--work', required=True, help='dir with images.json, maps.json, native_addfromjson_*.json')
    a = ap.parse_args()
    root = a.src
    T = os.path.join(root, 'com/bitmapbureau/terminator')
    classes = load_classes(root)
    imgc, arr_images, res_fields, text_assets = asset_tables(classes, T)

    # sprite id -> image id via Sprites.loadFromID
    sp = classes['Sprites']['text']
    code, labels = compile_tree(parse(method_lines(sp, r'public static void loadFromID\(int _id, int _tileset\)')))
    sprite_img = {}
    for sid in range(1, 726):
        ex = run(code, labels, {'num4': sid}, pinned={'num4'})
        names = [mm.group(1) for e in ex for mm in re.finditer(r'Assets\.(IMAGE_\w+)', e)]
        if names:
            sprite_img[sid] = sorted({imgc[n] for n in names})
    spc = static_ints(sp, '[A-Z]')

    target_method = param_flow(classes)

    # per-class references
    out = {}
    for c, cd in classes.items():
        sprites, dyn_sites, cls_refs = set(), [], set()
        by_method = {}
        for m in cd['methods'].values():
            ms = set()
            for e in image_exprs(m.body):
                ints, _, dyn = resolve(e, m)
                ms |= ints
                if dyn and not ints:
                    dyn_sites.append('%s: %s' % (m.name, e[:60]))
            sites = list(call_sites(m.body, classes, c))
            if m.base_args is not None:
                sites.append((cd['base'], '.ctor', m.base_args))
            for tc, key, args in sites:
                tm = target_method(tc, key)
                if tm is None:
                    continue
                for p in tm.img_params:
                    k = tm.params.index(p)
                    if k < len(args):
                        ms |= resolve(args[k], m)[0]
            ma = text_assets(m.body) if c != 'Res' else set()
            sprites |= ms
            if ms or ma:
                by_method[m.name] = dict(sprite_ids=sorted(s for s in ms if 0 < s < 726), assets_images=sorted(ma))
        text = cd['text']
        for mm in re.finditer(r'(?<![\w])new (\w+)\(|typeof\((\w+)\)|(?<![\w.])(\w+)\.\w+', text):
            n = mm.group(1) or mm.group(2) or mm.group(3)
            if n in classes and n != c:
                cls_refs.add(n)
        if cd['base'] in classes:
            cls_refs.add(cd['base'])
        direct = text_assets(text) if c != 'Res' else set()
        out[c] = dict(file=cd['file'], base=cd['base'],
                      sprite_ids=sorted(s for s in sprites if 0 < s < 726),
                      bad_sprite_ids=sorted(s for s in sprites if not 0 < s < 726),
                      assets_images=sorted(direct), dynamic=dyn_sites[:20], refs=sorted(cls_refs),
                      by_method=by_method,
                      img_params={k: sorted(m.img_params) for k, m in cd['methods'].items() if m.img_params})

    # map object name id -> (classes, sprite ids) via Sprites.addFromJSON
    code, labels = compile_tree(parse(method_lines(sp, r'public unsafe static void addFromJSON\(')))
    natives = {}
    for fn in glob.glob(os.path.join(a.work, 'native_addfromjson_*.json')):
        natives.update({int(k): v for k, v in json.load(open(fn)).items()})
    maps = json.load(open(os.path.join(a.work, 'maps.json')))
    used_ids = sorted({o['name_id'] for mp in maps['maps'].values() for o in mp['objects']})
    lname = {v: k for k, v in maps['name_ids'].items()}

    def derives(c, base):
        seen = set()
        while c in classes and c not in seen:
            seen.add(c)
            c = classes[c]['base']
            if c == base:
                return True
        return False

    class _M:   # pseudo method for resolve()
        params, body = [], ''
    objmap = {}
    for nid in used_ids:
        ex = run(code, labels, {'num10': nid}, pinned={'num10'})
        body = '\n'.join(ex)
        pm = _M(); pm.body = body
        cl, sids, how = set(), set(), 'csharp'
        for tc, key, args in call_sites(body, classes):
            if tc in ('GameObject', 'Sprites', 'Game', 'Script', 'Array'):
                continue
            cl.add(tc)
            tm = target_method(tc, key)
            if tm is not None:
                for p in tm.img_params:
                    k = tm.params.index(p)
                    if k < len(args):
                        sids |= resolve(args[k], pm)[0]
        for e in image_exprs(body):
            sids |= resolve(e, pm)[0]
        for mm in re.finditer(r'typeof\((\w+)\)', body):
            if mm.group(1) in classes:
                cl.add(mm.group(1))
        if '__call_indirect' in body and nid in natives:
            how = 'native'
            for call in natives[nid]['calls']:
                nm = call['call']
                if nm.startswith('vcall+0x1b8') or nm == 'store GameObject.image':
                    sids.update(call['imms'])
                    continue
                if '::' not in nm:
                    continue
                c, meth = nm.split('::')
                if c in ('GameObject', 'EnemyBase', 'Game', 'Array', 'Sprites') or c not in classes:
                    continue
                cl.add(c)
                tm = target_method(c, meth)
                if tm is not None:
                    for p in tm.img_params:
                        k = tm.params.index(p)
                        if str(k) in call['args']:
                            sids.add(call['args'][str(k)])
            # An inlined constructor: guess the class from the object name. The case's calls name only what wasn't
            # inlined, often the base class's constructor (fw3_cargo_elevator calls Platform's, and its own class,
            # FW3CargoElevator, brings the HK tank it lifts), so the guess also applies when it derives from a
            # class found.
            guess = lname.get(nid, '').replace('_', '').lower()
            for c in classes:
                if c.lower() == guess and c not in cl and (not cl or any(derives(c, b) for b in cl)):
                    cl.add(c)
                    how = 'native+name'
        objmap[nid] = dict(name=lname.get(nid), classes=sorted(cl), sprite_ids=sorted(s for s in sids if 0 < s < 726), how=how)

    json.dump(dict(classes=out, sprite_img={str(k): v for k, v in sprite_img.items()},
                   sprite_names={str(v): k for k, v in spc.items() if 0 < v < 726},
                   arr_images=arr_images, objects={str(k): v for k, v in objmap.items()}),
              open(os.path.join(a.work, 'class_refs.json'), 'w'), indent=1)
    print('classes', len(out), 'sprite ids mapped', len(sprite_img),
          'classes with img params', sum(1 for v in out.values() if v['img_params']),
          'objects', len(objmap), 'objects without classes/sprites',
          [v['name'] for v in objmap.values() if not v['classes'] and not v['sprite_ids']][:80])


if __name__ == '__main__':
    main()
