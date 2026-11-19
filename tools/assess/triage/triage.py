"""Triage of the Rewind gap methods left in the rewritten game assembly, for the repair worklist.

    python -I tools/assess/triage/triage.py --graph work/triage/graph.json --config tools/assess/triage/tiers.json \
        --atlas work/atlas-usage --bringup work/bringup/logs --out work/triage [--report artifacts/downgrade/code-report.txt]

Inputs: GapGraph's dump of the rewritten assembly (calls, markers, notes), a tier config (which classes each
stage of play starts, which are replaced or stubbed), the atlas-usage scene data (map objects -> classes) and,
optionally, the bring-up probe's logs (stack traces of gaps the probe reached) and the code stage's report
(to check the gap list matches).

Reachability is rapid type analysis over the IL call graph, run tier by tier in play order: a tier adds its
seed methods and admits its scene classes (levels, cutscenes, menus); a virtual call dispatches only to classes
some reached `newobj` instantiates; a `newobj` or dispatch into a scene class no tier has admitted yet is
deferred. A gap method belongs to the first tier that reaches it. Factories that name every scene or object
class (the map object spawner) are cut, and the map's own objects are seeded per level instead.

Writes worklist.md, worklist.tsv and reach.json to --out. Contains no game code: the config names classes and
methods, as the repair rules do.
"""
import argparse, collections, json, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kinds import PATTERNS, RULE_HOURS, classify  # noqa: E402
from report import write_md  # noqa: E402

# docs/repairs.md's measured rates, by marker sites per method
def hours(sites):
    return 10 / 60 if sites <= 5 else 0.5 if sites <= 20 else 1.0 if sites <= 50 else 2.0


def bucket(sites):
    return '1-5' if sites <= 5 else '6-20' if sites <= 20 else '21-50' if sites <= 50 else '>50'


class Graph:
    def __init__(self, g):
        self.T = {t['name']: t for t in g['types']}
        self.M = {m['key']: m for m in g['methods']}
        self.by_type = collections.defaultdict(dict)   # type -> name(sig) -> key
        for m in g['methods']:
            self.by_type[m['type']][m['name'] + '(' + m['sig'] + ')'] = m['key']
        self.short = collections.defaultdict(list)
        for t in g['types']:
            self.short[t['short']].append(t['name'])
        self._anc = {}

    def resolve_class(self, short_or_full):
        if short_or_full in self.T:
            return short_or_full
        c = [n for n in self.short.get(short_or_full, []) if '/' not in n]
        if len(c) != 1:
            raise SystemExit(f'class {short_or_full!r}: {len(c)} matches {c}')
        return c[0]

    def bases(self, t):
        out = []
        while t in self.T:
            out.append(t)
            t = self.T[t]['base']
        return out

    def ancestors(self, t):
        """Base chain and every interface along it."""
        if t in self._anc:
            return self._anc[t]
        s = set()
        for b in self.bases(t):
            s.add(b)
            s.update(self.T[b]['interfaces'])
        self._anc[t] = s
        return s

    def implementation(self, s, name_sig, iface):
        name = name_sig.split('(')[0]
        sig = name_sig[len(name):]
        for b in self.bases(s):
            k = self.by_type[b].get(name_sig)
            if k is None and iface:
                for ns, kk in self.by_type[b].items():           # explicit implementation: I.m(sig)
                    if ns.endswith('.' + name + sig):
                        k = kk
                        break
            if k is not None and self.M[k]['body']:
                return k
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--graph', required=True)
    ap.add_argument('--config', required=True)
    ap.add_argument('--atlas', required=True)
    ap.add_argument('--bringup')
    ap.add_argument('--report')
    ap.add_argument('--out', required=True)
    a = ap.parse_args()

    graph = json.load(open(a.graph, encoding='utf-8'))
    cfg = json.load(open(a.config, encoding='utf-8'))
    G = Graph(graph)
    M, T = G.M, G.T
    scenes = {s['scene']: s for s in json.load(open(os.path.join(a.atlas, 'scenes.json')))}
    maps = json.load(open(os.path.join(a.atlas, 'maps.json')))['maps']
    objs = json.load(open(os.path.join(a.atlas, 'class_refs.json')))['objects']

    scene_ns = tuple(cfg['scene_namespaces'])
    scene_bases = {G.resolve_class(c) for c in cfg['scene_base_classes']}
    is_scene = lambda t: T.get(t, {}).get('ns', '') in scene_ns and t not in scene_bases
    cut = set(cfg['cut_methods'])

    def key_of(spec):
        """'Class::method' or 'Class::method(sig)' -> keys."""
        cls, meth = spec.split('::')
        full = G.resolve_class(cls)
        ks = [k for ns, k in G.by_type[full].items() if ns == meth or ns.split('(')[0] == meth]
        if not ks:
            raise SystemExit(f'no method {spec}')
        return ks

    cut_keys = {k for s in cut for k in key_of(s)}
    factory_keys = {k for s in cfg.get('factory_methods', []) for k in key_of(s)}   # their newobj aren't followed

    # ---- incremental RTA -------------------------------------------------------------------------------------
    reached, inst, admitted = set(), set(), set()
    vcalls = collections.defaultdict(set)    # (target type, name(sig), iface) -> callers, pending dispatch
    deferred = collections.defaultdict(set)  # scene class -> callers that wanted it
    tier_of, via = {}, {}
    work = []

    def reach(k, frm, tier):
        if k in reached or k not in M:
            return
        reached.add(k)
        tier_of[k] = tier
        via[k] = frm
        work.append(k)

    def cctor(t, frm, tier):
        k = G.by_type[t].get('.cctor()')
        if k:
            reach(k, frm, tier)

    def instantiate(t, frm, tier):
        if t in inst:
            return
        if is_scene(t) and t not in admitted:
            deferred[t].add(frm)
            return
        inst.add(t)
        cctor(t, frm, tier)
        anc = G.ancestors(t)
        for (tt, ns, iface), callers in list(vcalls.items()):
            if tt in anc:
                k = G.implementation(t, ns, iface)
                if k:
                    reach(k, next(iter(callers)), tier)

    gates = [(re.compile(rx), t) for rx, t in cfg.get('gates', {}).items()]
    tier_index = {t['name']: i for i, t in enumerate(cfg['tiers'])}
    deferred_m = {}                            # gated method -> a caller that wanted it
    cur = [0]

    def short_key(k):
        t, rest = k.split('::', 1)
        return t.rsplit('.', 1)[-1].replace('/', '.') + '::' + rest.split('(')[0]

    def gated(k):
        sk = short_key(k)
        for rx, t in gates:
            if rx.fullmatch(sk):
                return tier_index[t] > cur[0]
        return False

    # calls Rewind kept only inside a marker's text (`Class.method(`, `&Class.method`), resolved by short names
    by_short = collections.defaultdict(list)
    for kk, mm in M.items():
        by_short[(mm['type'].rsplit('.', 1)[-1], mm['name'])].append(kk)
    marker_calls = {}
    for kk, mm in M.items():
        extra = []
        for x in mm['markers']:
            for c, n in re.findall(r'&?([A-Z]\w*)\.(\w+)\(?', x['text']):
                for tk in by_short.get((c, n), []):
                    extra.append(('marker', tk, M[tk]['type']))
        marker_calls[kk] = extra

    def run(tier):
        while work:
            k = work.pop()
            if k in cut_keys:
                continue
            m = M[k]
            for t in m['statics']:
                cctor(t, k, tier)
            for t in m.get('tokens', []):
                if k not in factory_keys and t in T and not T[t].get('interface') and not T[t].get('abstract')                         and t not in inst and t not in scene_bases and any(n.startswith('.ctor(') for n in G.by_type[t]):
                    instantiate(t, k, tier)
            for op, target, ttype in m['calls'] + marker_calls[k]:
                if target not in M:
                    continue
                tm = M[target]
                if gated(target):
                    deferred_m.setdefault(target, (k, op, ttype))
                    continue
                if op == 'newobj' and k in factory_keys and ttype not in inst:
                    continue
                if op == 'newobj':
                    instantiate(ttype, k, tier)
                    if ttype in inst:
                        reach(target, k, tier)
                    continue
                cctor(ttype, k, tier)
                if op in ('callvirt', 'ldvirtftn') and tm['virtual']:
                    iface = T.get(ttype, {}).get('interface', False)
                    ns = tm['name'] + '(' + tm['sig'] + ')'
                    vcalls[(ttype, ns, iface)].add(k)
                    for s in list(inst):
                        if ttype in G.ancestors(s):
                            impl = G.implementation(s, ns, iface)
                            if impl:
                                reach(impl, k, tier)
                    if T.get(ttype, {}).get('base') in ('System.ValueType', 'System.Enum'):
                        reach(target, k, tier)
                else:
                    if is_scene(ttype) and ttype not in admitted and not tm['static'] and tm['name'] != '.ctor':
                        deferred[ttype].add(k)
                        continue
                    reach(target, k, tier)

    # map objects -> classes
    def map_classes(map_names):
        out = set()
        for mp in map_names:
            for o in maps[mp]['objects']:
                ob = objs.get(str(o['name_id']))
                if ob:
                    out.update(ob['classes'])
        return out

    tier_info = []
    for ti, tier in enumerate(cfg['tiers']):
        name = tier['name']
        before_def = set(deferred)
        roots = [G.resolve_class(c) for c in tier.get('admit', [])]
        if 'scene' in tier:
            sc = scenes[tier['scene']]
            roots += [G.resolve_class(c) for c in sc['roots']]
            objc = map_classes(sc['maps'])
        else:
            objc = set()
        objc |= set(tier.get('objects', []))
        cur[0] = ti
        for k in [k for k in deferred_m if not gated(k)]:
            frm, op, ttype = deferred_m.pop(k)
            if op == 'newobj':
                instantiate(ttype, frm, name)
            reach(k, frm, name)
        for c in tier.get('instantiate', []):
            instantiate(G.resolve_class(c), 'seed', name)
        for r in roots:
            admitted.add(r)
        for spec in tier.get('seeds', []):
            for k in key_of(spec):
                reach(k, 'seed', name)
        for r in roots:
            instantiate(r, 'seed', name)
            for ns, k in G.by_type[r].items():           # a scene's engine-called methods
                if M[k]['body'] and not M[k]['static']:
                    reach(k, 'scene:' + r.rsplit('.', 1)[-1], name)
            for c in list(deferred.pop(r, ())):
                pass
        for c in sorted(objc):
            try:
                full = G.resolve_class(c)
            except SystemExit:
                continue
            instantiate(full, 'map', name)
            for ns, k in G.by_type[full].items():
                if ns.startswith('.ctor('):
                    reach(k, 'map:' + c, name)
        run(name)
        newdef = sorted(set(deferred) - before_def - admitted)
        tier_info.append(dict(name=name, roots=[r.rsplit('.', 1)[-1] for r in roots], map_classes=len(objc),
                              reached=sum(1 for k in reached if tier_of[k] == name),
                              newly_deferred=[d.rsplit('.', 1)[-1] for d in newdef]))

    # ---- gap methods -------------------------------------------------------------------------------------------
    moot = cfg['moot']
    def moot_reason(m):
        cls = m['type']
        for spec, why in moot.items():
            c, meth = spec.split('::')
            try:
                full = G.resolve_class(c)
            except SystemExit:
                full = c
            if (cls == full or cls.startswith(full + '/')) and (meth == '*' or meth == m['name'] or
                                                             re.fullmatch(meth, m['name'])):
                return why
        return ''

    probe = set()
    if a.bringup and os.path.isdir(a.bringup):
        for f in os.listdir(a.bringup):
            if f.startswith('run-'):
                for line in open(os.path.join(a.bringup, f), encoding='utf-8', errors='replace'):
                    mm = re.match(r'\s+at ([\w.]+)\.([\w.<>`]+) \(', line)
                    if mm and not mm.group(1).startswith(('System', 'UnityEngine', '__Rewind', 'InControl')):
                        probe.add(mm.group(1) + '::' + mm.group(2))

    order = {t['name']: i for i, t in enumerate(cfg['tiers'])}
    order['unreached'] = len(order)
    rows = []
    for k, m in M.items():
        if not m['markers']:
            continue
        ctx = [x['text'] for x in m['markers']]
        kinds = collections.Counter(classify(x['kind'], x['text'], ctx) for x in m['markers'])
        sites = len(m['markers'])
        tier = tier_of.get(k, 'unreached')
        why = moot_reason(m)
        tname = m['type']
        reasons = []
        if m['note']:
            n = re.sub(r'^\d+ part\(s\) of this method were not recovered: ', '', m['note'])
            for r in n.split('; '):
                r = re.sub(r'0x[0-9A-Fa-f]+', '0xN', r)
                r = re.sub(r'%\d+', '%n', r)
                if r and r not in reasons:
                    reasons.append(r)
        path, x = [], k
        while x in via and len(path) < 12:
            path.append(x)
            x = via[x]
            if x in ('seed', 'map') or x.startswith(('scene:', 'map:')):
                path.append(x)
                break
        rows.append(dict(
            key=k, cls=tname.rsplit('.', 1)[-1].replace('/', '.'), ns=T[tname]['ns'] if tname in T else '',
            method=m['name'], sig=m['sig'], sites=sites, kinds=dict(kinds.most_common()),
            opaque=sum(1 for x in m['markers'] if x['kind'] == 'Opaque'),
            effect=sum(1 for x in m['markers'] if x['kind'] == 'Effect'),
            reasons=reasons, tier=tier, moot=why, probe=(T.get(tname, {}).get('name', tname) + '::' + m['name']) in probe,
            hours=0.0 if why else hours(sites), bucket=bucket(sites),
            path=[p.split('::')[0].rsplit('.', 1)[-1] + '::' + p.split('::')[1].split('(')[0] if '::' in p else p
                  for p in reversed(path)]))

    # ---- pattern groups and the worklist ---------------------------------------------------------------------
    # A general pattern is pulled forward as a rule of its own when it has enough sites or finishes enough methods.
    general = [p for p, v in PATTERNS.items() if v[1] == 'general']
    live = [r for r in rows if not r['moot']]
    early = {t['name'] for t in cfg['tiers'][:3]}           # boot, main menu, first level
    boot = cfg['tiers'][0]['name']
    stats = {}
    for p in PATTERNS:
        ms = [r for r in live if p in r['kinds']]
        stats[p] = dict(methods=len(ms), sites=sum(r['kinds'][p] for r in ms),
                        only=sum(1 for r in ms if len(r['kinds']) == 1),
                        early_sites=sum(r['kinds'][p] for r in ms if r['tier'] in early),
                        boot_sites=sum(r['kinds'][p] for r in ms if r['tier'] == boot))
    pulled = [p for p in general if stats[p]['sites'] >= cfg.get('pull_min_sites', 30) or
              stats[p]['only'] >= cfg.get('pull_min_only', 3)]
    pulled.sort(key=lambda p: (-stats[p]['boot_sites'], -stats[p]['early_sites'], -stats[p]['sites']))
    rule_id = {p: f'R{i}' for i, p in enumerate(pulled, 1)}
    for p in pulled:
        stats[p]['finished'] = sum(1 for r in live if p in r['kinds'] and all(q in pulled for q in r['kinds']))

    for r in rows:
        r['patterns'] = ' '.join(f'{p}:{n}' for p, n in sorted(r['kinds'].items(), key=lambda q: -q[1]))
        left = {p: n for p, n in r['kinds'].items() if p not in pulled}
        r['residual'] = sum(left.values())
        r['residual_kinds'] = ' '.join(f'{p}:{n}' for p, n in sorted(left.items(), key=lambda q: -q[1]))
        r['rules'] = ' '.join(rule_id[p] for p in pulled if p in r['kinds'])
        r['hours_alone'] = 0.0 if r['moot'] else hours(r['sites'])
        r['hours_left'] = 0.0 if r['moot'] or not left else hours(r['residual'])
        seed = r['path'][1] if len(r['path']) > 1 and r['path'][0] == 'seed' else ''
        r['phase'] = ''
        if r['tier'] == boot:
            r['phase'] = 'startup' if seed in ('UnityMain::Awake', 'UnityMain::OnEnable', 'UnityMain::Start') \
                else 'first frames'
        r['note'] = cfg.get('notes', {}).get(r['cls'] + '.' + r['method'], '')
        if not r['note'] and r['tier'] == 'unreached':
            r['note'] = ('no caller in the IL: dead in the recovered program, or the target of an unidentified '
                         'invoker or indirect-call gap; re-run after those repairs')

    # priority: moot last; tier order; boot's startup before its first frames, probe-seen first; work left
    # after the rules before what the rules finish; cheapest first
    def prio(r):
        return (1 if r['moot'] else 0, order[r['tier']], 0 if r['phase'] == 'startup' else 1,
                0 if r['probe'] else 1, 0 if r['residual'] else 1, r['residual'], r['sites'], r['key'])
    rows.sort(key=prio)
    items = [dict(item=rule_id[p], kind='rule', what=p, hours=RULE_HOURS.get(p, 2.0), **stats[p]) for p in pulled]
    n = 0
    for r in rows:
        if r['moot']:
            r['item'] = 'moot'
        elif r['residual'] == 0:
            r['item'] = 'by ' + r['rules']
        else:
            n += 1
            r['item'] = f'M{n}'
            items.append(dict(item=r['item'], kind='method', row=r, hours=r['hours_left']))
    for i, r in enumerate(rows, 1):
        r['rank'] = i

    os.makedirs(a.out, exist_ok=True)
    report_check = ''
    if a.report and os.path.exists(a.report):
        rep = {}
        for line in open(a.report, encoding='utf-8'):
            mm = re.match(r'\s+Recovered\.T2D (.+) sites=(\d+)$', line)
            if mm:
                rep[mm.group(1)] = int(mm.group(2))
        mine = {M[r['key']]['full']: r['sites'] for r in rows}
        diff = {k for k in set(rep) | set(mine) if rep.get(k) != mine.get(k)}
        report_check = (f'code-report.txt lists {len(rep)} methods, {sum(rep.values())} sites; this run sees '
                        f'{len(mine)} methods, {sum(mine.values())} sites; {len(diff)} differ')

    write_tsv(rows, os.path.join(a.out, 'worklist.tsv'))
    write_md(rows, items, stats, pulled, rule_id, list(order), tier_info, report_check, graph['file'],
             os.path.join(a.out, 'worklist.md'))
    json.dump(dict(tiers=tier_info, rows=rows, stats=stats, pulled=pulled, report_check=report_check,
                   graph=graph['file']), open(os.path.join(a.out, 'reach.json'), 'w', encoding='utf-8'), indent=1)
    print(report_check)
    for t in tier_info:
        print(f"{t['name']:<28} roots={','.join(t['roots'])} objs={t['map_classes']} reached={t['reached']}")
    tc = collections.Counter((r['tier'], bool(r['moot'])) for r in rows)
    for t in list(order):
        print(f'  {t:<28} live={tc[(t, False)]} moot={tc[(t, True)]}')


def write_tsv(rows, path):
    cols = ['rank', 'item', 'tier', 'phase', 'cls', 'method', 'sig', 'sites', 'opaque', 'effect', 'patterns', 'rules',
            'residual', 'residual_kinds', 'hours_alone', 'hours_left', 'moot', 'probe', 'ns', 'note', 'reasons', 'path']
    with open(path, 'w', encoding='utf-8', newline='') as f:
        f.write('\t'.join(cols) + '\n')
        for r in rows:
            v = []
            for c in cols:
                x = r[c]
                if isinstance(x, list):
                    x = (' | ' if c == 'reasons' else ' > ').join(x)
                elif isinstance(x, bool):
                    x = 'yes' if x else ''
                elif isinstance(x, float):
                    x = f'{x:.2f}'
                v.append(str(x).replace('\t', ' '))
            f.write('\t'.join(v) + '\n')


if __name__ == '__main__':
    main()
