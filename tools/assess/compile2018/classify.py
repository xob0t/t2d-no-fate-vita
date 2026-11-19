"""Classify the compile errors RoslynDiag wrote (one TSV per assembly) by CS code and by cause.

    python -I classify.py --apigap <dir with <asm>.tsv> --libsrc <dir>... [--top 20] [--out <tsv>]
                          <Assembly>=<diag.tsv> ...

--libsrc: source folders of the game's other assemblies (InControl, Steamworks), to tell their private
members from Unity's. Prints, per assembly and in total: errors by code; errors, sites (distinct source
lines), files and methods by cause, and how many of those methods have Rewind gap markers; the top
files by errors. --out writes every error with its cause.
"""
import argparse
import collections
import csv
import os
import re
import sys

csv.field_size_limit(1 << 30)
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from apigap_categories import category as apigap_category  # noqa: E402

# Errors the parser reports (the source is not C#), as opposed to binding and flow analysis.
SYNTAX = {
    'CS1001', 'CS1002', 'CS1003', 'CS1013', 'CS1022', 'CS1026', 'CS1031', 'CS1056', 'CS1513', 'CS1514',
    'CS1519', 'CS1520', 'CS1525', 'CS1526', 'CS1527', 'CS0116', 'CS0106', 'CS8641', 'CS9348', 'CS8803',
    'CS8805', 'CS8802', 'CS1003',
}
FLOW = {'CS0165', 'CS0177', 'CS0161', 'CS0163', 'CS0269', 'CS0170', 'CS0171', 'CS0188', 'CS8073'}
PRIMITIVE = {'int', 'uint', 'long', 'ulong', 'short', 'ushort', 'byte', 'sbyte', 'char', 'bool', 'float',
             'double', 'decimal', 'nint', 'nuint', 'IntPtr', 'UIntPtr', 'System.IntPtr', 'System.UIntPtr'}

CAUSES = [
    ('marker', 'Rewind markers: a value from __Rewind.Opaque (typed long) used as an object, array or exception'),
    ('ctor-call', 'decompiler artifact: explicit constructor call (x..ctor(), base..ctor() mid-body)'),
    ('names', 'decompiler artifact: compiler-generated <> names (closures, lambdas, backing fields) and what they break'),
    ('arrayinit', 'decompiler artifact: array-initializer data (<PrivateImplementationDetails>, __ldtoken)'),
    ('il-only', 'decompiler artifact: IL-only constructs (__ldftn, __ldvirtftn, function pointers)'),
    ('reinterpret', 'decompiler artifact: struct/integer reinterpretation ((int)this, (Vector2)(ulong)...)'),
    ('no-base', 'decompiler artifact: constructor without a base-constructor call'),
    ('ambiguous', 'decompiler artifact: ambiguous simple name (using directives)'),
    ('unsafe-barrier', 'inlined runtime: GC write barrier (Unsafe.AsPointer card marking)'),
    ('unsafe-other', 'inlined BCL internals rendered with Unsafe.* (Unsafe.As, AreSame, Add ...)'),
    ('engine-internal', 'inlined engine internals (U1: *_Injected, GetCachedPtr, m_Ptr, private fields)'),
    ('bcl-internal', 'inlined BCL internals (B1/B2: String._firstChar, Delegate fields, ThrowHelper ...)'),
    ('lib-internal', 'inlined private members of InControl/Steamworks'),
    ('public-api', 'public API missing in 2018.2 (U2/B3)'),
    ('enum-name', 'Unity 6 enum member names (RuntimePlatform.PS5, KeyCode.LeftMeta ...): constants in IL'),
    ('api-unmatched', 'missing API not in the apigap list'),
    ('stripped', 'members IL2CPP stripped (operator == without !=)'),
    ('flow', 'flow analysis (unassigned out/locals, missing return)'),
    ('other', 'other'),
]
CAUSE_NAMES = dict(CAUSES)


def load_apigap(folder):
    names = {}
    for f in sorted(os.listdir(folder)):
        if not f.endswith('.tsv'):
            continue
        with open(os.path.join(folder, f), encoding='utf-8') as fh:
            for r in csv.DictReader(fh, delimiter='\t'):
                cat = apigap_category(r)
                api = r['api']
                if '::' in api:
                    t, m = api.split('::', 1)
                    m = re.split(r'[(<: ]', m, maxsplit=1)[0]
                    keys = {m, re.sub(r'^[gs]et_', '', m)}
                    if m == '.ctor':
                        keys = {t.split('.')[-1].split('/')[-1] + '..ctor'}
                else:
                    keys = {api.split('.')[-1].split('/')[-1]}
                for k in keys:
                    names.setdefault(k, cat)
    return names


def lib_types(folders):
    types = set()
    for folder in folders:
        for root, _, files in os.walk(folder):
            types.update(f[:-3] for f in files if f.endswith('.cs'))
    return types


QUOTED = re.compile(r"'([^']*)'")


def api_names(msg):
    q = QUOTED.findall(msg)
    if 'does not contain a constructor' in msg and q:
        return [q[0].split('.')[-1] + '..ctor']
    if 'does not contain a definition for' in msg and len(q) >= 2:
        return [q[1], q[0].split('.')[-1]]
    if 'is inaccessible due to its protection level' in msg and q:
        parts = q[0].split('(')[0].split('.')
        return [parts[-1], parts[-2] if len(parts) > 1 else parts[-1]]
    if 'type or namespace name' in msg and q:
        return [q[0]]
    if 'cannot be assigned to -- it is read only' in msg and q:
        return [q[0].split('.')[-1]]
    if 'can only appear on the left hand side' in msg and q:
        return [q[0].split('.')[-1]]
    return [x.split('.')[-1] for x in q]


ENUMS = ('RuntimePlatform', 'KeyCode')
CASCADE = {'CS0103', 'CS0127', 'CS0029', 'CS0161', 'CS0119', 'CS1503', 'CS8370'}


def classify(r, line, api, libs, broken_types, broken_files):
    code, msg, text = r['code'], r['message'], r['text']
    if code == 'CS0155' or (code == 'CS0030' and msg.startswith("Cannot convert type 'long' to")
                            and msg.split("to '")[-1].rstrip("'") not in PRIMITIVE):
        return 'marker'
    # constructs mechfix.py introduced (pass 2), by the error's own span
    for key, cause in (('__Rewind.As<', 'marker'), ('__Rewind.Ref(', 'marker'), ('__Rewind.FieldToken', 'arrayinit'),
                       ('__Rewind.Ftn', 'il-only'), ('Compat2018.Injected', 'engine-internal'),
                       ('Compat2018.Engine', 'engine-internal'), ('Compat2018.BCL', 'bcl-internal'),
                       ('Compat2018.Unsafe', 'unsafe-other')):
        if key in text:
            return cause
    if 'PrivateImplementationDetails' in line or '__ldtoken' in line:
        return 'arrayinit'
    if '..ctor' in line and code != 'CS0030':
        return 'ctor-call'
    if r['file'] in broken_files and code in CASCADE:
        return 'names'
    if '__ldftn' in line or '__ldvirtftn' in line or code in ('CS8812', 'CS0214') or 'delegate*' in line:
        return 'il-only'
    if (code in SYNTAX or '<>' in line or 'CS$' in line or re.search(r'<\w+>[bk]__', line)
            or '<invalid-global-code>' in msg or code in ('CS8801', 'CS0101', 'CS0102', 'CS0548', 'CS0549',
                                                          'CS0694', 'CS0579', 'CS8422')):
        return 'names'
    if any(n in broken_types for n in QUOTED.findall(msg)) or (code == 'CS1061' and QUOTED.findall(msg)[1:2] and QUOTED.findall(msg)[1].split('.')[-1] in broken_types):
        return 'names'
    if code == 'CS0030':
        return 'reinterpret'
    if code == 'CS7036' and r['member'] == '.ctor':
        return 'no-base'
    if code == 'CS0104':
        return 'ambiguous'
    if code == 'CS0216':
        return 'stripped'
    if code in FLOW:
        return 'flow'
    if code == 'CS0117' and msg.split("'")[1] in ENUMS:
        return 'enum-name'
    if code == 'CS0246' and 'InspectorName' in msg:
        return 'public-api'
    if code in ('CS0122', 'CS0117', 'CS1061', 'CS0234', 'CS0246', 'CS1729', 'CS0200', 'CS0070', 'CS1540',
                'CS0426', 'CS0103'):
        names = api_names(msg)
        if 'Unsafe' in names[:1] or (code == 'CS0122' and 'Unsafe' in msg):
            return 'unsafe-barrier' if 'AsPointer' in line else 'unsafe-other'
        if code == 'CS1540':
            return 'bcl-internal'
        if code in ('CS0122', 'CS0070'):
            owner = re.sub(r'<[^>]*>', '', msg.split("'")[1]).split('.')[0] if "'" in msg else ''
            if owner in libs:
                return 'lib-internal'
            if owner in ('ThrowHelper', 'RuntimeType', 'Number', 'SpanHelpers', 'ExceptionArgument', 'InternalThread'):
                return 'bcl-internal'
        for n in names:
            cat = api.get(n)
            if cat:
                if cat.startswith('U1'):
                    return 'engine-internal'
                if cat in ('B1', 'B2', 'C'):
                    return 'bcl-internal'
                if cat in ('U2', 'B3', 'U3'):
                    return 'public-api'
        if code in ('CS0122', 'CS0070') or 'can only appear on the left' in msg:
            owner = re.sub(r'<[^>]*>', '', msg.split("'")[1]).split('.')[0] if "'" in msg else ''
            if owner in libs or any(n in libs for n in names):
                return 'lib-internal'
        if code != 'CS0103':
            return 'api-unmatched'
    if 'can only appear on the left' in msg:
        return 'lib-internal'
    return 'other'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--apigap', required=True)
    ap.add_argument('--libsrc', action='append', default=[])
    ap.add_argument('--top', type=int, default=20)
    ap.add_argument('--out')
    ap.add_argument('diags', nargs='+')
    a = ap.parse_args()
    api = load_apigap(a.apigap)
    libs = lib_types(a.libsrc)

    rows = []
    for spec in a.diags:
        asm, path = spec.split('=', 1)
        with open(path, encoding='utf-8') as fh:
            asm_rows = [dict(r, asm=asm) for r in csv.DictReader(fh, delimiter='\t', quoting=csv.QUOTE_NONE)]
        cache = {}
        for r in asm_rows:
            f = r['file']
            if f and f not in cache:
                with open(f, encoding='utf-8-sig', errors='replace') as fh:
                    cache[f] = fh.read().split('\n')
            r['_line'] = cache[f][int(r['line']) - 1] if f else ''
        # Files whose structure the parser lost: syntax errors other than on array-initializer lines.
        broken_files = {r['file'] for r in asm_rows
                        if r['code'] in SYNTAX and 'PrivateImplementationDetails' not in r['_line']}
        broken_types = {os.path.basename(f)[:-3] for f in broken_files}
        for r in asm_rows:
            r['cause'] = classify(r, r['_line'], api, libs, broken_types, broken_files)
            rows.append(r)
        # A constructor that calls base..ctor() mid-body also lacks the implicit base() call (CS7036).
        with_call = {(r['file'], r['type']) for r in asm_rows if r['cause'] == 'ctor-call' and r['member'] == '.ctor'}
        for r in asm_rows:
            if r['cause'] == 'no-base' and (r['file'], r['type']) in with_call:
                r['cause'] = 'ctor-call'

    def report(title, rs):
        print('== %s: %d errors, %d sites, %d files, %d methods' % (
            title, len(rs), len({(r['file'], r['line']) for r in rs}), len({r['file'] for r in rs}),
            len({(r['type'], r['member']) for r in rs if r['member']})))
        codes = collections.Counter(r['code'] for r in rs)
        print('  by code: ' + ', '.join('%s %d' % kv for kv in codes.most_common()))
        print('  %-15s %7s %6s %5s %8s %10s  %s' % ('cause', 'errors', 'sites', 'files', 'methods', 'gap meths', 'what'))
        for key, name in CAUSES:
            cr = [r for r in rs if r['cause'] == key]
            if not cr:
                continue
            methods = {(r['asm'], r['type'], r['member']) for r in cr if r['member']}
            gap = {(r['asm'], r['type'], r['member']) for r in cr if r['member'] and r['markers'] == 'gap'}
            print('  %-15s %7d %6d %5d %8d %10d  %s' % (
                key, len(cr), len({(r['file'], r['line']) for r in cr}), len({r['file'] for r in cr}),
                len(methods), len(gap), name))
        meths = {(r['asm'], r['type'], r['member']) for r in rs if r['member']}
        gapm = {(r['asm'], r['type'], r['member']) for r in rs if r['member'] and r['markers'] == 'gap'}
        clean = [r for r in rs if r['markers'] != 'gap']
        print('  methods with errors: %d, of them with gap markers %d, without %d; errors outside gap methods %d'
              % (len(meths), len(gapm), len(meths - gapm), len(clean)))

    for asm in dict.fromkeys(r['asm'] for r in rows):
        report(asm, [r for r in rows if r['asm'] == asm])
    report('all', rows)

    print('== top %d files by errors' % a.top)
    byfile = collections.Counter((r['asm'], r['file']) for r in rows)
    for (asm, f), n in byfile.most_common(a.top):
        causes = collections.Counter(r['cause'] for r in rows if r['file'] == f)
        rel = f.split(os.sep + asm + os.sep, 1)[-1] if (os.sep + asm + os.sep) in f else f
        print('  %6d  %s/%s  [%s]' % (n, asm, rel.replace(os.sep, '/'), ', '.join('%s %d' % kv for kv in causes.most_common(4))))

    if a.out:
        with open(a.out, 'w', encoding='utf-8', newline='') as fh:
            w = csv.writer(fh, delimiter='\t', lineterminator='\n')
            keys = ['asm', 'cause', 'code', 'file', 'line', 'col', 'type', 'member', 'markers', 'message', 'text']
            w.writerow(keys)
            for r in rows:
                w.writerow([r[k] for k in keys])


if __name__ == '__main__':
    main()
