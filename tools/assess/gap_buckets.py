"""Gap methods of one assembly outside the asset-table initializers, by marker sites, as in the assessment's
stage-2 table (docs/assessment.md, "How much hand work the code side needs"); and which methods changed state
between two runs.

    python -I tools/assess/gap_buckets.py <methods.tsv> [<methods before.tsv>]

<methods.tsv> is the table RewindStats `stats` writes. The initializers are the staticInit* methods of the
asset-table classes (Assets, AssetsEmbed, AssetsPack, AssetsStaticMap, FontsPack): generated code that fills
tables of asset data, to be regenerated from the native code's constants and not repaired by hand. Hours use the
assessment's rates: 20 minutes for 1 to 5 sites, 1 hour for 6 to 20, 2 hours for 21 to 50, 4 hours above.
With a second table, also lists the methods that went from gaps to complete and back.
"""
import csv
import re
import sys

INITIALIZER = re.compile(r'\.(Assets|AssetsEmbed|AssetsPack|AssetsStaticMap|FontsPack)::staticInit\d*\(')
BUCKETS = [(1, 5, 1 / 3), (6, 20, 1), (21, 50, 2), (51, 10 ** 9, 4)]


def load(path):
    with open(path, encoding='utf-8', newline='') as handle:
        return {(r['assembly'], r['method']): r for r in csv.DictReader(handle, delimiter='\t')}


def summary(rows):
    gaps = [r for r in rows.values() if r['kind'] == 'Gaps']
    init = [r for r in gaps if INITIALIZER.search(r['method'])]
    rest = [r for r in gaps if not INITIALIZER.search(r['method'])]
    print(f'gap methods {len(gaps)}: initializers {len(init)} ({sum(int(r["marker_sites"]) for r in init)} sites), '
          f'outside them {len(rest)} ({sum(int(r["marker_sites"]) for r in rest)} sites)')
    hours = 0.0
    for low, high, rate in BUCKETS:
        n = sum(1 for r in rest if low <= int(r['marker_sites']) <= high)
        hours += n * rate
        label = f'{low} to {high}' if high < 10 ** 9 else f'over {low - 1}'
        print(f'  {label:<10} {n:>5}')
    print(f'  hours at the assessment\'s rates: {hours:.0f}')


def main():
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    after = load(sys.argv[1])
    summary(after)
    if len(sys.argv) == 3:
        before = load(sys.argv[2])
        fixed = sorted(k for k, r in after.items() if r['kind'] == 'Complete' and before.get(k, {}).get('kind') == 'Gaps')
        broke = sorted(k for k, r in after.items() if r['kind'] == 'Gaps' and before.get(k, {}).get('kind') == 'Complete')
        print(f'\ngaps -> complete: {len(fixed)}')
        for k in fixed:
            print(f'  {k[1]}\t(was: {before[k]["marker_sites"]} sites; {before[k]["reasons"]})')
        print(f'\ncomplete -> gaps: {len(broke)}')
        for k in broke:
            print(f'  {k[1]}\t({after[k]["marker_sites"]} sites; {after[k]["reasons"]})')


if __name__ == '__main__':
    main()
