"""Fold inlined BCL constructors back into C# (a source rule for compile2018's pass-2 C#).

  - new ReadOnlyCollection<T>(list): IL2CPP inlines it as an uninitialized object whose private `list`
    field is written; Rewind writes
        R r = (ReadOnlyCollection<T>)FormatterServices.GetUninitializedObject(typeof(ReadOnlyCollection<T>));
        ... r.list = src;
    which becomes `r = new ReadOnlyCollection<T>(src);` at the store (the declaration stays).
  - System.ExceptionArgument.<name> (internal in 2018.2's mscorlib) becomes the string "<name>".
  - RuntimeHelpers.InitializeArray(a, __Rewind.FieldToken("<hash>")) becomes __Rewind.InitArray(a, "<hash>")
    (the probe stub copies the field's bytes, FieldData's dump; the source has no way to carry them).
  - __store2(&x.F, V), a 2-byte store across two bool fields (Rewind can't split it), becomes
    x.F = (V & 255) != 0; x.G = (V >> 8) != 0; where G is the bool field declared right after F (the
    declaration order is the layout); left alone unless every declaration of F is followed by the same bool field.
  - `0 > d.FindEntry(k)` / `0 <= d.FindEntry(k)` (Dictionary.ContainsKey inlined) become !d.ContainsKey(k) /
    d.ContainsKey(k).
  - RuntimePlatform.PS5, which 2018.2's enum lacks, becomes (RuntimePlatform)44, its Unity 6 value.
  - Unboxing recovered as a load at offset 16: `(x is int) ? Opaque<int>("__load4((m + 16L))")` becomes
    `(x is int) ? (int)x`, and a load of a named local becomes System.Convert.ToInt32/ToByte(local).

    python -I tools/assess/bringup/bclfold.py <src dir> [--dry-run]
"""
import argparse
import re
import sys
from pathlib import Path

DECL = re.compile(r'\b(\w+) = \((ReadOnlyCollection<[^;]*?>)\)FormatterServices\.GetUninitializedObject\(typeof\(\2\)\);')
EXARG = re.compile(r'System\.ExceptionArgument\.(\w+)')
FIELD = re.compile(r'^\t\t(?![^\n]*\bstatic\b)(?:(?:public|private|internal|protected|readonly)\s+)*([\w\.<>\[\]]+) (\w+)(?: = [^;]*)?;\s*$', re.M)
STORE2 = re.compile(r'__Rewind\.Effect\("__store2\(&([\w\.]+)\.(\w+), (\d+)\)"\);')
INIT = re.compile(r'RuntimeHelpers\.InitializeArray\(([^;]*?), __Rewind\.FieldToken\("([0-9A-F]+)"\)\)')
FIND_NEG = re.compile(r'0 > ([\w\.]+)\.FindEntry\(([^()]*)\)')
FIND_POS = re.compile(r'0 <= ([\w\.]+)\.FindEntry\(([^()]*)\)')
PS5 = re.compile(r'\bRuntimePlatform\.PS5\b')
# Unboxing recovered as a load at offset 16 (the x64 object header): guarded by `is int`, or on a named
# local (not Rewind's m/t temporaries).
UNBOX_GUARDED = re.compile(r'\((\w+) is int\) \? __Rewind\.Opaque<int>\("__load4\(\(\w+ \+ 16L\)\)"\)')
UNBOX_NAMED = re.compile(r'__Rewind\.Opaque<(int|byte)>\("__load[14]\(\((?![mt]\d+ )([A-Za-z_]\w*) \+ 16L\)\)"\)')
UNBOX_TYPES = {'int': 'Int32', 'byte': 'Byte'}
# Unity 6's internal Texture2D(w, h, format, mipCount, linear, nativeTex, createUninitialized,
# MipmapLimitDescriptor) constructor, which the public one calls (inlined): 2018.2's public one.
TEX2D = re.compile(r'new Texture2D\(([^,()]+), ([^,()]+), ([\w\.]+), ([^,()]+), ([^,()]+), \(IntPtr\)\(void\*\)0L, false, default\(Compat2018\.MipmapLimitDescriptor\)\)')


def bool_pairs(files):
    """field name -> the field declared after it, when every declaration of that name is a bool followed
    by a bool field of the same name."""
    nxt = {}
    for f in files:
        fields = FIELD.findall(f.read_text(encoding='utf-8-sig'))
        for (t1, n1), (t2, n2) in zip(fields, fields[1:] + [('', '')]):
            nxt.setdefault(n1, set()).add(n2 if t1 == 'bool' and t2 == 'bool' else None)
    return {k: next(iter(v)) for k, v in nxt.items() if len(v) == 1 and None not in v}


def fold(text, pairs=None):
    n = 0
    for m in list(DECL.finditer(text)):
        var, typ = m.groups()
        store = re.compile(r'\b%s\.list = ([^;]+);' % re.escape(var))
        text, k = store.subn(lambda s: '%s = new %s(%s);' % (var, typ, s.group(1)), text, count=1)
        n += k
    text, k = EXARG.subn(lambda m: '"%s"' % m.group(1), text)
    text, i = INIT.subn(lambda m: '__Rewind.InitArray((System.Array)(%s), "%s")' % m.groups(), text)
    text, f1 = FIND_NEG.subn(lambda m: '!%s.ContainsKey(%s)' % m.groups(), text)
    text, f2 = FIND_POS.subn(lambda m: '%s.ContainsKey(%s)' % m.groups(), text)
    text, ps5 = PS5.subn('((RuntimePlatform)44)', text)
    text, u1 = UNBOX_GUARDED.subn(lambda m: '(%s is int) ? (int)%s' % (m.group(1), m.group(1)), text)
    text, u2 = UNBOX_NAMED.subn(lambda m: 'System.Convert.To%s(%s)' % (UNBOX_TYPES[m.group(1)], m.group(2)), text)
    text, t2d = TEX2D.subn(lambda m: 'new Texture2D(%s, %s, %s, (%s) > 1, %s)' % m.groups(), text)
    i += f1 + f2 + ps5 + u1 + u2 + t2d
    s2 = 0
    if pairs:
        def two(m):
            obj, f, v = m.group(1), m.group(2), int(m.group(3))
            if f not in pairs:
                return m.group(0)
            return '%s.%s = %s; %s.%s = %s;' % (obj, f, 'true' if v & 255 else 'false', obj, pairs[f], 'true' if v >> 8 else 'false')
        text, s2 = STORE2.subn(two, text)
    return text, n, k + i + s2


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src', type=Path)
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()
    tn = tk = 0
    files = sorted(a.src.rglob('*.cs'))
    pairs = bool_pairs(files)
    for f in files:
        text = f.read_text(encoding='utf-8-sig')
        new, n, k = fold(text, pairs)
        if n or k:
            tn += n
            tk += k
            if not a.dry_run:
                f.write_text(new, encoding='utf-8')
    print('total: %d ReadOnlyCollection constructions, %d ExceptionArgument names, InitializeArray calls and bool-pair stores' % (tn, tk))


if __name__ == '__main__':
    sys.exit(main())
