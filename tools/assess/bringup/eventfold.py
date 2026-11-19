"""Fold inlined event accessors back into C# (a source rule for compile2018's pass-2 C#).

The C# compiler's field-like event accessor is a compare-exchange loop on the delegate field; IL2CPP
inlines Interlocked.CompareExchange, and Rewind leaves it as
    add { T action = Owner.m_Field; while (true) { ... Delegate.Combine(action, value) ...
          __Rewind.Opaque<long>("__atomic_compare_exchange(&Owner.Event, m1, m0)") ... } }
which doesn't compile (object/long casts). This pass replaces each such add/remove body with
    Owner.m_Field = (T)Delegate.Combine(Owner.m_Field, value);     (Delegate.Remove for remove)
without the thread safety of the original loop. The same loop inlined into other methods (an
`obj.Event += handler` in a caller) is left alone.

    python -I tools/assess/bringup/eventfold.py <src dir> [--dry-run]
"""
import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from replace_body import body_span  # noqa: E402

ACCESSOR = re.compile(r'^(\s*)(add|remove)\s*$')
FIRST = re.compile(r'^\s*([\w\.<>, ]+?) action = ((?:[\w\.]+\.)?m?_?\w+);\s*$')


def fold(text):
    lines = text.splitlines(keepends=True)
    out, n, i = [], 0, 0
    pos = 0
    offsets = []
    for line in lines:
        offsets.append(pos)
        pos += len(line)
    result = text
    edits = []
    for i, line in enumerate(lines):
        m = ACCESSOR.match(line)
        if not m:
            continue
        try:
            open_at, close_at = body_span(text, offsets[i])
        except ValueError:
            continue
        body = text[open_at:close_at + 1]
        if '__atomic_compare_exchange(&' not in body or ('Delegate.Combine' not in body and 'Delegate.Remove' not in body):
            continue
        first = next((FIRST.match(l) for l in body.splitlines() if FIRST.match(l)), None)
        if not first:
            continue
        typ, field = first.groups()
        op = 'Combine' if m.group(2) == 'add' else 'Remove'
        ind = m.group(1)
        new = '{\n%s\t%s = (%s)Delegate.%s(%s, value);\n%s}' % (ind, field, typ, op, field, ind)
        edits.append((open_at, close_at + 1, new))
    for a, b, new in reversed(edits):
        result = result[:a] + new + result[b:]
    return result, len(edits)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src', type=Path)
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()
    total = 0
    for f in sorted(a.src.rglob('*.cs')):
        text = f.read_text(encoding='utf-8-sig')
        if '__atomic_compare_exchange(&' not in text:
            continue
        new, n = fold(text)
        if n:
            total += n
            print('%s: %d accessors' % (f.relative_to(a.src).as_posix(), n))
            if not a.dry_run:
                if 'using System;' not in new:
                    new = 'using System;\n' + new
                f.write_text(new, encoding='utf-8')
    print('total: %d accessors' % total)


if __name__ == '__main__':
    main()
