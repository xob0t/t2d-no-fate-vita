"""Split ILVerify errors by whether the method has Rewind gap markers (__Rewind.Opaque/Effect).

    python -I ilverify_summary.py <stats tsv> <ilverify output>...

<stats tsv> is RewindStats `stats` output (one row per method body, kind Complete/Gaps/Placeholder, note 0/1).
NotedOnly: a method with a __Rewind.Note but no Opaque/Effect marker.
Methods are matched by Type::Name (ILVerify and the TSV format parameters differently); a name with
any overload that has gaps counts as a gap method.
"""

import collections
import re
import sys

ERR = re.compile(r'^\[IL\]: Error \[(\w+)\]: \[[^\]]*? : ([^\]]+?)\]')


def method_key(sig):
    name = sig.split('(', 1)[0]
    return name.replace('/', '.').replace('+', '.')


def main():
    stats_path, *outputs = sys.argv[1:]
    kinds = {}
    with open(stats_path, encoding='utf-8') as f:
        next(f)
        for line in f:
            cols = line.rstrip('\n').split('\t')
            key = (cols[0], method_key(cols[1]))
            kind = cols[2]
            # A Note without an Opaque/Effect site: Rewind says parts weren't recovered but marked none.
            if kind == 'Complete' and len(cols) > 8 and cols[8] == '1':
                kind = 'NotedOnly'
            rank = {'Gaps': 3, 'NotedOnly': 2, 'Complete': 1}
            if rank.get(kind, 0) > rank.get(kinds.get(key), 0):
                kinds[key] = kind
    for path in outputs:
        asm = re.sub(r'(\.verbose)?\.txt$', '', path.replace('\\', '/').rsplit('/', 1)[-1])
        by_place = collections.Counter()
        methods = collections.defaultdict(set)
        clean_kinds = collections.Counter()
        unmatched = set()
        with open(path, encoding='utf-8', errors='replace') as f:
            for line in f:
                m = ERR.match(line)
                if not m:
                    continue
                kind, sig = m.groups()
                key = (asm, method_key(sig))
                place = kinds.get(key)
                if place is None:
                    unmatched.add(key[1])
                    place = 'unmatched'
                by_place[place] += 1
                methods[place].add(key[1])
                if place == 'Complete':
                    clean_kinds[kind] += 1
        print(f'{asm}: {sum(by_place.values())} errors')
        for place, n in by_place.most_common():
            print(f'  {place:10} {n:6} errors in {len(methods[place])} methods')
        if clean_kinds:
            print('  in methods without gap markers, by kind: ' + ', '.join(f'{k} {n}' for k, n in clean_kinds.most_common()))
        for u in sorted(unmatched)[:5]:
            print(f'  unmatched: {u}')


if __name__ == '__main__':
    main()
