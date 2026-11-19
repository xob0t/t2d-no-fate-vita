"""How many gap methods a fix would make complete, from the reasons Rewind writes in each method's note.

    python -I tools/assess/note_whatif.py <Rewind C# source folder of one assembly>

Rewind starts every method with gaps with __Rewind.Note("N part(s) of this method were not recovered: <reason>;
<reason>; ..."), with the addresses of unidentified native functions in the reasons. This reads every note
(methods outside the asset-table initializers, as in the assessment) and counts, per fix below, the methods all
of whose reasons that fix (alone, and together with the ones above it) addresses, and the methods left, by the
number of parts the note says were not recovered (close to RewindStats' marker sites) with the assessment's
hours per method. That is an upper bound: a method can need more than its reasons say, a reason the fix
addresses might have more than one cause, and the parts of a method a fix leaves are counted in full.

Each fix is a set of reason patterns (regular expressions over one reason).
"""
import re
import sys
from pathlib import Path

INITIALIZER = re.compile(r'class (Assets|AssetsEmbed|AssetsPack|AssetsStaticMap|FontsPack)\b')
NOTE = re.compile(r'__Rewind\.Note\("(\d+) part\(s\) of this method were not recovered: (.*)"\);')
METHOD = re.compile(r'^\t(?:public|private|protected|internal)[^=;]*\b(\w+)\(')

FIXES = [
    # "ArraySetAt not translated" counts here because Rewind translates SetAt only on an array of known type, and
    # an array from the unnamed allocator has none (HirBuilder.Calls.cs); that is an assumption, not checked per method.
    ('Array::NewSpecific and Object::NewAllocSpecific named (issues 1, 15), with the SetAt calls on their arrays',
     [r'call to unidentified native function 0x1800E2FD0', r'call to unidentified native function 0x1800E3F30',
      r'Klass value used as data', r'runtime helper ArraySetAt not translated']),
    ('bits to double (issue 2)', [r'bits reinterpreted as (double|float)']),
    ('static-field write barrier (issue 3), narrow', [r'load: absolute address 0x1810CD090', r'intrinsic atomic_compare_exchange',
                                                   r'Statics value used as data']),
    ('2-byte bool stores (issue 16)', [r'store: access of 2 bytes spans fields']),
    ('tail-merged virtual calls (issue 17)', [r'ObjKlass value used as data', r'VTable value used as data']),
    ('static-field write barrier, counting "address without a typed base" (issue 3, wide)',
     [r'load: address without a typed base', r'store: address without a typed base']),
    ('the LTCG-specialized invokers 0x180002D50 and 0x180003170 named', [r'call to unidentified native function 0x180002D50',
                                                                         r'call to unidentified native function 0x180003170']),
]


def notes(folder):
    """(method, [reasons]) per method with a note, outside the initializer classes."""
    for path in sorted(Path(folder).rglob('*.cs')):
        text = path.read_text(encoding='utf-8', errors='replace')
        if INITIALIZER.search(text):
            # Skip only the staticInit methods of those classes.
            skip_init = True
        else:
            skip_init = False
        method = None
        for line in text.splitlines():
            m = METHOD.match(line)
            if m:
                method = m.group(1)
            n = NOTE.search(line)
            if n:
                if skip_init and method and method.startswith('staticInit'):
                    continue
                yield f'{path.stem}.{method}', int(n.group(1)), [r.strip() for r in n.group(2).split(';') if r.strip()]


BUCKETS = [(1, 5, 1 / 3), (6, 20, 1), (21, 50, 2), (51, 10 ** 9, 4)]


def buckets(parts):
    """Methods by the parts their note counts (the assessment's site buckets) and hours at its rates."""
    counts = [sum(1 for n in parts if low <= n <= high) for low, high, _ in BUCKETS]
    hours = sum(c * rate for c, (_, _, rate) in zip(counts, BUCKETS))
    return '/'.join(str(c) for c in counts) + f', {hours:.0f} h'


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    methods = list(notes(sys.argv[1]))
    print(f'{len(methods)} methods with a gap note outside the initializers; by parts 1-5/6-20/21-50/>50: '
          f'{buckets([n for _, n, _ in methods])}')
    cumulative = []
    for name, patterns in FIXES:
        alone = sum(1 for _, _, reasons in methods if all(any(re.search(p, r) for p in patterns) for r in reasons))
        cumulative += patterns
        left = [n for _, n, reasons in methods if not all(any(re.search(p, r) for p in cumulative) for r in reasons)]
        print(f'  {alone:>5} alone, {len(methods) - len(left):>5} with the fixes above (left {len(left)}: {buckets(left)})  {name}')


if __name__ == '__main__':
    main()
