"""Put two RewindStats `stats` reports side by side: before and after a change to Rewind or its inputs.

    python -I tools/assess/compare_stats.py <before stats.txt> <after stats.txt> [--top N]

Reads the text `RewindStats stats` prints (one assembly, or several) and prints, per assembly: bodies, complete,
gaps and marker sites; then for the first assembly in the report the gap reasons, cause buckets, exact cause sets,
the "<= N sites" distribution, the largest methods and the unidentified native functions, each with before,
after and the difference.
"""
import re
import sys

SECTIONS = {
    'gap reasons': 'reasons',
    'causes (runtime': 'causes',
    'methods by exact set of causes': 'cause_sets',
    'marker sites in gap methods': 'largest',
    'unidentified native functions': 'unidentified',
}


def parse(path):
    report = {'assemblies': {}, 'sections': {k: {} for k in SECTIONS.values()}, 'buckets': {}}
    section = None
    first = None
    for line in open(path, encoding='utf-8'):
        line = line.rstrip('\n')
        m = re.match(r'^(\S[^:]*): (\d+) bodies: (\d+) complete \(([\d.]+)%\), (\d+) with gaps.*?, (\d+) placeholder(?:.*marker sites (\d+))?', line)
        if m:
            name = m.group(1)
            report['assemblies'][name] = dict(bodies=int(m.group(2)), complete=int(m.group(3)), pct=float(m.group(4)),
                                              gaps=int(m.group(5)), placeholder=int(m.group(6)),
                                              sites=int(m.group(7)) if m.group(7) else None)
            if first is None and name != 'ALL':
                first = name
            # Only the first assembly's detail sections are compared.
            section = None if name != first else section
            continue
        stripped = line.strip()
        for key, name in SECTIONS.items():
            if stripped.startswith(key):
                section = name
                if name == 'largest':
                    m = re.search(r'gap methods: (\d+)', stripped)
                    report['marker_sites'] = int(m.group(1)) if m else None
                break
        else:
            m = re.match(r'gap methods with <= (\d+) sites: (\d+)', stripped)
            if m:
                report['buckets'][int(m.group(1))] = int(m.group(2))
                continue
            m = re.match(r'(\d+)\s+(.+)$', stripped)
            if m and section and first is not None:
                report['sections'][section][m.group(2)] = int(m.group(1))
    return report


def table(title, before, after, top=None, sort_after=True):
    keys = list(dict.fromkeys(list(after) + list(before)))
    keys.sort(key=lambda k: -(after.get(k, 0) if sort_after else before.get(k, 0)))
    if top:
        keys = keys[:top]
    print(f'\n{title}')
    print(f'  {"before":>7} {"after":>7} {"diff":>7}  item')
    for k in keys:
        b, a = before.get(k, 0), after.get(k, 0)
        print(f'  {b:>7} {a:>7} {a - b:>+7}  {k}')


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    top = 25
    if '--top' in sys.argv:
        top = int(sys.argv[sys.argv.index('--top') + 1])
        args = [a for a in args if a != str(top)]
    if len(args) != 2:
        sys.exit(__doc__)
    before, after = parse(args[0]), parse(args[1])
    print(f'  {"assembly":<40} {"bodies":>7} {"complete before":>16} {"complete after":>15} {"diff":>6} {"gaps b/a":>12} {"sites b/a":>16}')
    for name, a in after['assemblies'].items():
        b = before['assemblies'].get(name)
        if not b:
            continue
        sites = f'{b["sites"]}/{a["sites"]}' if a['sites'] is not None else ''
        print(f'  {name:<40} {a["bodies"]:>7} {b["complete"]:>8} ({b["pct"]:>4.1f}%) {a["complete"]:>7} ({a["pct"]:>4.1f}%) '
              f'{a["complete"] - b["complete"]:>+6} {b["gaps"]:>5}/{a["gaps"]:<6} {sites:>16}')
    s_b, s_a = before['sections'], after['sections']
    table('gap reasons (methods with each)', s_b['reasons'], s_a['reasons'], top, sort_after=False)
    table('causes (methods with each)', s_b['causes'], s_a['causes'])
    table('exact cause sets', s_b['cause_sets'], s_a['cause_sets'])
    table('gap methods with <= N marker sites', {f'<= {k}': v for k, v in before['buckets'].items()},
          {f'<= {k}': v for k, v in after['buckets'].items()})
    print(f'\nmarker sites in gap methods: {before.get("marker_sites")} -> {after.get("marker_sites")}')
    table('largest gap methods (marker sites)', s_b['largest'], s_a['largest'], 15)
    table('unidentified native functions (methods calling them)', s_b['unidentified'], s_a['unidentified'], top, sort_after=False)


if __name__ == '__main__':
    main()
