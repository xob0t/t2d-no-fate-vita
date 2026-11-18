"""Group RewindStats `apigap` TSVs into categories for sizing a downgrade rewriter's rules.

    python -I apigap_categories.py <apigap tsv>... [--top N] [--stats <stats tsv>]

Per TSV and over all of them: items, use sites and game methods per category, then the top N items of the
first TSV by use sites. With --stats (RewindStats `stats` output), the methods column also says how
many of those game methods already have Rewind gaps (they need hand work whatever the rules do). Sites of a non-public type whose members are also listed (System.ThrowHelper and
ThrowHelper::ThrowIndexOutOfRangeException) are the same instructions; the type's row is folded into its
members' category and not counted again.

Categories (unity6_visibility 'nonpublic' = the game's IL names something its own Unity 6 libraries keep
private: IL2CPP inlined a public call and Rewind recovered the inlined body):
  U1a  engine binding calls (*_Injected) inlined from public properties/methods
  U1b  engine binding plumbing: null checks and native pointers (Bindings.ThrowHelper, Unmarshal,
       Object.GetCachedPtr, m_Ptr fields)
  U1c  private fields of engine types (inlined struct/class accessors, backing fields)
  U1d  other internal engine members
  U2   public Unity 6 API that 2018.2 lacks or doesn't make public
  U3   engine types in another module in 2018.2 (retarget only)
  B1   BCL internals that 2018.2's unityaot lacks (another implementation of the same class)
  B2   BCL internals 2018.2 has, non-public (work under IL2CPP, which doesn't check access)
  B3   public BCL API (.NET Standard 2.1 additions) unityaot lacks
  C    compiler / runtime support types (System.Runtime.CompilerServices *Attribute, IsExternalInit ...)
"""

import collections
import csv
import sys

NAMES = {
    'U1a': 'engine binding calls (*_Injected), inlined',
    'U1b': 'engine binding plumbing (null check, native ptr), inlined',
    'U1c': 'private engine fields (inlined accessors)',
    'U1d': 'other internal engine members, inlined',
    'U2': 'public Unity 6 API missing in 2018.2',
    'U3': 'engine type in another module (retarget)',
    'B1': 'BCL internals missing in 2018.2, inlined',
    'B2': 'BCL internals present but non-public in 2018.2, inlined',
    'B3': 'public BCL API missing (.NET Std 2.1)',
    'C': 'compiler/runtime support types',
}
PLUMBING = ('UnityEngine.Bindings.ThrowHelper', 'UnityEngine.Bindings.Unmarshal', 'UnityEngine.Object::GetCachedPtr')


def category(r):
    api, kind, vis = r['api'], r['kind'], r['unity6_visibility']
    unity = r['unity6_assembly'].startswith('UnityEngine')
    if api.startswith('System.Runtime.CompilerServices.') and ('Attribute' in api.split('::')[0] or 'IsExternalInit' in api):
        return 'C'
    if kind == 'type-moved':
        return 'U3' if unity else 'B3'
    if unity:
        if vis != 'nonpublic':
            return 'U2'
        if api.startswith(PLUMBING) or '::m_Ptr :' in api:
            return 'U1b'
        member = api.split('::', 1)[1] if '::' in api else ''
        if '_Injected(' in member:
            return 'U1a'
        if '::' in api and '(' not in member:
            return 'U1c'
        return 'U1d'
    if vis != 'nonpublic':
        return 'B3'
    return 'B1' if kind.endswith('missing') else 'B2'


def main():
    args = sys.argv[1:]
    top = 30
    if '--top' in args:
        i = args.index('--top')
        top = int(args[i + 1])
        del args[i:i + 2]
    gap_methods = set()
    if '--stats' in args:
        i = args.index('--stats')
        with open(args[i + 1], encoding='utf-8') as f:
            next(f)
            for line in f:
                cols = line.rstrip('\n').split('\t')
                if cols[2] == 'Gaps' or (len(cols) > 8 and cols[8] == '1'):
                    gap_methods.add(cols[0] + '|' + cols[1].split('(', 1)[0])
        del args[i:i + 2]
    def methods(u):
        if not gap_methods:
            return f'{len(u):7}'
        g = sum(1 for x in u if x in gap_methods)
        return f'{len(u):7} ({g:3} with gaps)'
    total = collections.defaultdict(lambda: [0, 0, set()])
    first_rows = None
    for path in args:
        rows = list(csv.DictReader(open(path, encoding='utf-8'), delimiter='\t'))
        if first_rows is None:
            first_rows = rows
        member_types = {r['api'].split('::', 1)[0] for r in rows if '::' in r['api']}
        asm = path.replace('\\', '/').rsplit('/', 1)[-1].rsplit('.', 1)[0]
        per = collections.defaultdict(lambda: [0, 0, set()])
        for r in rows:
            if r['kind'] == 'type-nonpublic' and r['api'] in member_types:
                continue  # same sites as its members' rows
            c = category(r)
            users = set(r['all_users'].split(';')) if r.get('all_users') else set()
            for agg in (per[c], total[c]):
                agg[0] += 1
                agg[1] += int(r['sites'])
                agg[2] |= {asm + '|' + u for u in users}
        print(f'{path}')
        all_users = set().union(*(v[2] for v in per.values()))
        print(f'  game methods touching any: {methods(all_users).strip()}')
        print(f'  {"cat":4} {"items":>5} {"sites":>6} {"methods":>7}  category')
        for c in NAMES:
            if c in per:
                n, s, u = per[c]
                print(f'  {c:4} {n:5} {s:6} {methods(u)}  {NAMES[c]}')
    if len(args) > 1:
        print('all assemblies')
        for c in NAMES:
            if c in total:
                n, s, u = total[c]
                print(f'  {c:4} {n:5} {s:6} {methods(u)}  {NAMES[c]}')
    if first_rows and top:
        print(f'top {top} of {args[0]} by use sites (moved types excluded)')
        rows = [r for r in first_rows if r['kind'] != 'type-moved']
        rows.sort(key=lambda r: (-int(r['sites']), r['api']))
        for r in rows[:top]:
            print(f'  {int(r["sites"]):4} {int(r["users"]):3}  {category(r):4} {r["kind"]:16} {r["api"]}')


if __name__ == '__main__':
    main()
