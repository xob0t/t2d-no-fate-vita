"""Fold Rewind's unrecovered array allocations back into C# (a source rule for compile2018's pass-2 C#).

IL2CPP's `new T[n]` is a direct call to Array::NewSpecific, which Rewind leaves as
    long numX = __Rewind.Opaque<long>("__native_0x1800E2FD0(__klass(), <n>L[, ...])");
followed by the element stores it couldn't type and, later, the typed use
    field = __Rewind.As<T[]>(numX);
This pass rewrites such a window when it holds no other allocation:
    object numX__arr = new T[<n>];                      (the allocation)
    ((T[])numX__arr)[i] = v;                            (__ArraySetAt(t, i, v); __storeN((t + off), v) when
                                                         N is T's size; off - 32 is the byte offset on x64)
    __Rewind.InitArray((Array)numX__arr, "<hash>");     (RuntimeHelpers.InitializeArray with a field token)
    ((U)numX__arr)                                       (every __Rewind.As<U>(numX))
A store whose value it can't express stays a marker, and a window where numX has another use is left
alone. Element types are those of the typing As<T[]>.

    python -I tools/assess/bringup/arrayfold.py <src dir> [--dry-run]
Prints counts per file and in total.
"""
import argparse
import re
import sys
from pathlib import Path

ALLOC = re.compile(r'^(\s*)long (num\d*) = __Rewind\.Opaque<long>\("__native_0x1800E2FD0\(__klass\(\), (\d+L|conv\.[iu][48]\([A-Za-z_][\w\.]*(?:\(\))?\))(?:, [^"]*)?\)"\);\s*$')


def size_expr(n):
    """C# for the size text of an allocation marker: 37L -> 37, conv.u8(X.get_Length()) -> (int)X.Length."""
    if n.endswith('L'):
        return n[:-1]
    inner = n[n.index('(') + 1:-1]
    inner = re.sub(r'\.get_(\w+)\(\)$', r'.\1', inner)
    return '(int)' + inner
SETAT = re.compile(r'^(\s*)__Rewind\.Effect\("__ArraySetAt\(t\d+, (\d+)L, (.*)\)"\);\s*$')
STORE = re.compile(r'^(\s*)__Rewind\.Effect\("__store(\d)\(\(t\d+ \+ (\d+)L\), (-?\d+|(?![tmo]\d+\b)[A-Za-z_][\w\.]*)L?\)"\);\s*$')
INIT = re.compile(r'^(\s*)RuntimeHelpers\.InitializeArray\(__Rewind\.As<Array>\((num\d+)\), __Rewind\.FieldToken\("([0-9A-F]+)"\)\);\s*$')
SIZES = {'int': 4, 'uint': 4, 'short': 2, 'ushort': 2, 'byte': 1, 'sbyte': 1, 'bool': 1, 'long': 8, 'ulong': 8,
         'char': 2}
SIMPLE = re.compile(r'^[A-Za-z_][\w\.]*$')
WINDOW = 5000  # lines; the window also ends at the method's end
MEMBER = re.compile(r'^\t\t(public|private|internal|protected|static|unsafe)')


def value(text, elem):
    """C# for a marker's value text, or None."""
    t = text.strip()
    if t.startswith('\\"') and t.endswith('\\"'):
        return '"' + t[2:-2] + '"'
    m = re.fullmatch(r'(-?\d+)L?', t)
    if m:
        return '(%s)(%s)' % (elem, m.group(1)) if elem in SIZES else None
    m = re.fullmatch(r'conv\.[iu][1248]\(([^()]*)\)', t)
    if m and SIMPLE.match(m.group(1)):
        return '(%s)(%s)' % (elem, m.group(1))
    m = re.fullmatch(r'([A-Za-z_][\w\.]*)\(([^()]*)\)', t)
    if (m and not re.search(r'\b[tmo]\d+\b|\blocal\d+\b|__|[\\]', m.group(2))
            and not m.group(1).startswith(('conv.', 'box', '__'))):
        return '(%s)(%s)' % (elem, t)
    if SIMPLE.match(t) and t not in ('null',) and not re.match(r'^(?:[tmo]\d+|local\d+)$', t):
        return '(%s)(%s)' % (elem, t)
    if t == 'null':
        return 'null'
    return None


def fold(lines):
    out = list(lines)
    stats = {'allocs': 0, 'stores': 0, 'stores_left': 0, 'inits': 0, 'skipped': 0}
    i = 0
    while i < len(out):
        m = ALLOC.match(out[i])
        if not m:
            i += 1
            continue
        indent, var, n = m.groups()
        n = size_expr(n)
        typing = re.compile(r'__Rewind\.As<([\w\.]+(?:<[^<>]*>)?(?:\[\])*)\[\]>\(%s\)' % var)
        end = None
        for j in range(i + 1, min(len(out), i + WINDOW)):
            if ALLOC.match(out[j]) or MEMBER.match(out[j]):
                break
            if typing.search(out[j]):
                end = j
                break
        if end is None:
            stats['skipped'] += 1
            i += 1
            continue
        elem = typing.search(out[end]).group(1)
        arr = var + '__arr'
        # every other use of var in the method must be an As<...>(var)
        uses = re.compile(r'\b%s\b' % var)
        k = end + 1
        ok = True
        while k < len(out) and not re.match(r'^\t\t(public|private|internal|protected|static|unsafe)', out[k]):
            rest = re.sub(r'__Rewind\.As<[^()]*>\(%s\)' % var, '', out[k])
            if uses.search(rest):
                ok = False
                break
            k += 1
        if not ok:
            stats['skipped'] += 1
            i += 1
            continue
        base, ranks = re.match(r'^(.*?)((?:\[\])*)$', elem).groups()
        out[i] = '%sobject %s = new %s[%s]%s;\n' % (indent, arr, base, n, ranks)  # new int[20][] for int[]
        for j in range(i + 1, end + 1):
            line = out[j]
            s = SETAT.match(line)
            st = STORE.match(line)
            ini = INIT.match(line)
            if s:
                v = value(s.group(3), elem)
                if v is None and re.fullmatch(r'o\d+', s.group(3).strip()):
                    # the object constructed on the line before
                    prev = re.match(r'^\s*[\w\.<>\[\]]+ (\w+) = new ', out[j - 1])
                    if prev:
                        v = '(%s)(%s)' % (elem, prev.group(1))
                box = re.fullmatch(r'box<(\w+)>\(local\d+\)', s.group(3).strip())
                if v is None and box:
                    # IL2CPP boxes the local the line before assigned: `numK = 250; ...box<int>(localN)`
                    prev = re.match(r'^\s*(?:[\w\.<>\[\]]+ )?(\w+) = [^;=]+;\s*$', out[j - 1])
                    if prev:
                        v = '(%s)(object)(%s)(%s)' % (elem, box.group(1), prev.group(1))
                if v is None:
                    stats['stores_left'] += 1
                else:
                    out[j] = '%s((%s[])%s)[%s] = %s;\n' % (s.group(1), elem, arr, s.group(2), v)
                    stats['stores'] += 1
            elif st:
                size, off, v = int(st.group(2)), int(st.group(3)), st.group(4)
                if SIZES.get(elem) == size and off >= 32 and (off - 32) % size == 0:
                    out[j] = '%s((%s[])%s)[%d] = (%s)(%s);\n' % (st.group(1), elem, arr, (off - 32) // size, elem, v)
                    stats['stores'] += 1
                elif elem == 'double' and size == 8 and off >= 32 and (off - 32) % 8 == 0 and re.fullmatch(r'-?\d+', v):
                    # a double stored as its bits
                    out[j] = '%s((double[])%s)[%d] = BitConverter.Int64BitsToDouble(%sL);\n' % (st.group(1), arr, (off - 32) // 8, v)
                    stats['stores'] += 1
                else:
                    stats['stores_left'] += 1
            elif ini and ini.group(2) == var:
                out[j] = '%s__Rewind.InitArray((Array)%s, "%s");\n' % (ini.group(1), arr, ini.group(3))
                stats['inits'] += 1
        for j in range(i + 1, k):
            out[j] = re.sub(r'__Rewind\.As<([^()]*)>\(%s\)' % var, r'((\1)%s)' % arr, out[j])
        stats['allocs'] += 1
        i = end + 1
    return out, stats


OBJNEW = re.compile(r'^(\s*)long (num\d*) = __Rewind\.Opaque<long>\("__native_0x1800E3F30\(__klass\(\)(?:, t\d+)?\)"\);\s*$')


def fold_objnew(lines):
    """0x1800E3F30 is the runtime's object allocation (Object::New) for a class whose constructor
    IL2CPP elided; Rewind leaves it unidentified. `long numX = ...0x1800E3F30(__klass())` typed later by
    As<T>(numX) becomes `object numX__obj = FormatterServices.GetUninitializedObject(typeof(T))`, and
    every As<U>(numX) in the method a cast of it."""
    out = list(lines)
    n = 0
    for i, line in enumerate(out):
        m = OBJNEW.match(line)
        if not m:
            continue
        indent, var = m.groups()
        end = next((j for j in range(i + 1, min(len(out), i + WINDOW)) if MEMBER.match(out[j])), len(out))
        t = next((re.search(r'__Rewind\.As<([\w\.]+)>\(%s\)' % var, out[j]) for j in range(i + 1, end)
                  if re.search(r'__Rewind\.As<([\w\.]+)>\(%s\)' % var, out[j])), None)
        if not t:
            continue
        if any(re.search(r'\b%s\b' % var, re.sub(r'__Rewind\.As<[^()]*>\(%s\)' % var, '', out[j])) for j in range(i + 1, end)):
            continue  # another use (an inlined constructor's `numX = __Rewind.Ref(new T())`, a store): left alone
        out[i] = '%sobject %s__obj = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(%s));\n' % (
            indent, var, t.group(1))
        for j in range(i + 1, end):
            out[j] = re.sub(r'__Rewind\.As<([^()]*)>\(%s\)' % var, r'((\1)%s__obj)' % var, out[j])
        n += 1
    return out, n


NSTORE = re.compile(r'^(\s*)__Rewind\.Effect\("__store(\d)\(\(t(\d+) \+ (\d+)L\), (t\d+|-?\d+|(?![tmo]\d+\b)[A-Za-z_][\w\.]*)L?\)"\);\s*$')
NSETAT = re.compile(r'^(\s*)__Rewind\.Effect\("__ArraySetAt\(t(\d+), (\d+)L, (.*)\)"\);\s*$')


def fold_nested(lines):
    """Nested allocations (arrays of arrays) left by fold(): inside one method, Rewind's temporary tK is
    the K-th allocation, and a parent stores a child with __store8((tP + 32 + 8i), tK). Applied to a
    method only when every allocation in it is folded (the numbering then holds for all of it) and
    its root allocations are typed by an As<T[]>."""
    out = list(lines)
    stats = {'nested_methods': 0, 'nested_allocs': 0, 'nested_stores': 0}
    starts = [i for i, l in enumerate(out) if MEMBER.match(l)] + [len(out)]
    for a, b in zip(starts, starts[1:]):
        allocs = [(i, ALLOC.match(out[i])) for i in range(a, b) if ALLOC.match(out[i])]
        if len(allocs) < 2:
            continue
        if any('__native_0x1800E2FD0' in out[i] for i in range(a, b) if not ALLOC.match(out[i])):
            continue  # an allocation of another form: the numbering is unknown
        if not any((NSTORE.match(out[i]) or NSETAT.match(out[i])) and re.search(r', t\d+\)"\);', out[i]) for i in range(a, b)):
            continue  # nothing nested: fold() handles it
        var = [m.group(2) for _, m in allocs]
        size = [m.group(3) for _, m in allocs]
        types = {}
        for k, v in enumerate(var):
            t = re.search(r'__Rewind\.As<([\w\.]+(?:<[^<>]*>)?(?:\[\])*)\[\]>\(%s\)' % v, ''.join(out[a:b]))
            if t:
                types[k] = t.group(1)
        # children's element types from their parents' stores
        edits, ok = {}, True
        for i in range(a, b):
            m = NSTORE.match(out[i])
            s = NSETAT.match(out[i])
            pc = None
            if m and m.group(5).startswith('t'):
                pc = int(m.group(3)), int(m.group(5)[1:])
            elif s and re.fullmatch(r't\d+', s.group(4).strip()):
                pc = int(s.group(2)), int(s.group(4).strip()[1:])
            if pc:
                p, c = pc
                if c >= len(var):
                    continue  # a temporary that isn't an allocation: its store stays a marker
                if p >= len(var) or p not in types or not types[p].endswith('[]'):
                    ok = False
                    break
                types.setdefault(c, types[p][:-2])
        if not ok or len(types) != len(var):
            continue
        # the numbering check: allocation K's temporary tK is first used after allocation K's line
        for k, (line_k, _) in enumerate(allocs):
            first = next((i for i in range(a, b) if re.search(r'\bt%d\b' % k, out[i]) and '__Rewind.' in out[i]), None)
            if first is not None and first < line_k:
                ok = False
                break
        if not ok:
            continue
        for k, (i, m) in enumerate(allocs):
            n = m.group(3)
            n = size_expr(n)
            base, ranks = re.match(r'^(.*?)((?:\[\])*)$', types[k]).groups()
            edits[i] = '%sobject %s__arr = new %s[%s]%s;\n' % (m.group(1), var[k], base, n, ranks)
        left = 0
        for i in range(a, b):
            m = NSTORE.match(out[i])
            s = NSETAT.match(out[i])
            if m and (int(m.group(3)) >= len(var) or (m.group(5).startswith('t') and int(m.group(5)[1:]) >= len(var))):
                left += 1
            elif s and int(s.group(2)) >= len(var):
                left += 1
            elif s and re.fullmatch(r't\d+', s.group(4).strip()) and int(s.group(4).strip()[1:]) >= len(var):
                # a temporary that is the C# array literal declared by the statement just before
                name = None
                for j in range(i - 1, max(a, i - 2000), -1):
                    if '__Rewind.' in out[j] or j in edits:
                        break
                    d = re.match(r'^\s*[\w\.<>\[\]]+ (\w+) = new [\w\.<>]+\[', out[j])
                    if d:
                        name = d.group(1)
                        break
                p = int(s.group(2))
                if name:
                    edits[i] = '%s((%s[])%s__arr)[%s] = %s;\n' % (s.group(1), types[p], var[p], s.group(3), name)
                    stats['nested_stores'] += 1
                else:
                    left += 1
            elif m:
                p, off, v = int(m.group(3)), int(m.group(4)), m.group(5)
                elem, sz = types[p], int(m.group(2))
                if v.startswith('t'):
                    val = '(%s)%s__arr' % (elem, var[int(v[1:])])
                    esz = 8
                else:
                    val = '(%s)(%s)' % (elem, v)
                    esz = SIZES.get(elem)
                if esz == sz and off >= 32 and (off - 32) % sz == 0:
                    edits[i] = '%s((%s[])%s__arr)[%d] = %s;\n' % (m.group(1), elem, var[p], (off - 32) // sz, val)
                    stats['nested_stores'] += 1
                else:
                    left += 1
            elif s:
                p, idx, v = int(s.group(2)), s.group(3), s.group(4).strip()
                if re.fullmatch(r't\d+', v) and int(v[1:]) < len(var):
                    val = '(%s)%s__arr' % (types[p], var[int(v[1:])])
                else:
                    val = value(v, types[p])
                if val is None:
                    left += 1
                else:
                    edits[i] = '%s((%s[])%s__arr)[%s] = %s;\n' % (s.group(1), types[p], var[p], idx, val)
                    stats['nested_stores'] += 1
        for i, line in edits.items():
            out[i] = line
        for i in range(a, b):
            for v in var:
                out[i] = re.sub(r'__Rewind\.As<([^()]*)>\(%s\)' % v, r'((\1)%s__arr)' % v, out[i])
        stats['nested_methods'] += 1
        stats['nested_allocs'] += len(var)
    return out, stats


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src', type=Path)
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()
    total = {}
    for f in sorted(a.src.rglob('*.cs')):
        text = f.read_text(encoding='utf-8-sig')
        if '__native_0x1800E2FD0' not in text and '__native_0x1800E3F30' not in text:
            continue
        lines = text.splitlines(keepends=True)
        lines, objnew = fold_objnew(lines)
        out, nested = fold_nested(lines)
        nested['objnew'] = objnew
        out, stats = fold(out)
        stats.update(nested)
        for key, v in stats.items():
            total[key] = total.get(key, 0) + v
        if stats['allocs'] or nested['nested_allocs'] or objnew:
            print('%s: %s' % (f.relative_to(a.src).as_posix(), ' '.join('%s=%d' % kv for kv in stats.items())))
            if not a.dry_run:
                f.write_text(''.join(out), encoding='utf-8')
    print('total: ' + ' '.join('%s=%d' % kv for kv in total.items()))


if __name__ == '__main__':
    sys.exit(main())
