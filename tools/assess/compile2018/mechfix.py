"""Mechanical text rewrites of Rewind's C# so it gets closer to compiling against Unity 2018.2. For
measuring only: nothing here repairs game logic, and code it touches is not a working build.

    python -I mechfix.py text <src dir> <out dir> [--libs InControl,Steamworks]
    python -I mechfix.py diag <src dir> <diag tsv>...      (in place)

`text` copies <src dir> to <out dir> and applies the rules below to every .cs file, then prints how
often each fired. `diag` applies the rules that need Roslyn's binding (RoslynDiag TSVs of the same
sources): a value from __Rewind.Opaque (typed long) used as an object, array or exception.

Rules (text):
  names      compiler-generated names made legal: CS$<>8__locals1 -> CS__8__locals1, <>c -> __c,
             <Name>b__1_0 -> _Name_b__1_0, <Name>k__BackingField -> _Name_k__BackingField
  arrayinit  __ldtoken(<PrivateImplementationDetails>.HASH) -> __Rewind.FieldToken("HASH") (the data
             is not in the source), <PrivateImplementationDetails>.ComputeStringHash -> __Rewind.ComputeStringHash
  ftn        __ldftn(M) / __ldvirtftn(M) / (UIntPtr/*delegate*<...>*/)(&M) -> __Rewind.Ftn("M")
  newobj     T x = (T)FormatterServices.GetUninitializedObject(typeof(T)); x..ctor(a);  ->  T x = new T(a);
  basector   base..ctor(a); in a constructor body -> : base(a) on the constructor (logs when statements
             came before it, since that reorders them)
  objctor    ((T)numN)..ctor(a); on an opaque value -> numN = __Rewind.Ref(new T(a));
  barrier    the GC write barrier blocks IL2CPP inlined (if (GC flag) { card-table compare-exchange loop })
             are deleted
  redirect   members 2018.2 lacks, sent to the Compat2018 shim (see Compat2018.cs)
  libpublic  in --libs sources, private/protected/internal fields and nested types made public (other
             game assemblies read them: IL2CPP inlined their accessors)
  stripped   operator == without != (IL2CPP stripped the unused one): != added
"""
import collections
import csv
import os
import re
import shutil
import sys

csv.field_size_limit(1 << 30)
COUNTS = collections.Counter()
LOG = []


def sub(rule, pattern, repl, text, flags=0):
    new, n = re.subn(pattern, repl, text, flags=flags)
    COUNTS[rule] += n
    return new


# ---------------------------------------------------------------- names

def fix_names(t):
    t = sub('names', r'CS\$<>(\d+)__locals(\d+)', r'CS__\1__locals\2', t)
    t = sub('names', r'(?<![\w>])<>(\w+)', r'__\1', t)
    t = sub('names', r'(?<![\w>])<(\w+)>([a-zA-Z])__(\w*)', r'_\1_\2__\3', t)
    return t


# ---------------------------------------------------------------- arrayinit, ftn

def fix_il_only(t):
    t = sub('arrayinit', r'__ldtoken\(<PrivateImplementationDetails>\.(\w+)\)', r'__Rewind.FieldToken("\1")', t)
    t = sub('arrayinit', r'<PrivateImplementationDetails>\.ComputeStringHash\(', '__Rewind.ComputeStringHash(', t)
    t = sub('ftn', r'\(IntPtr\)(?:\(void\*\))?(?:\(ulong\))?\(UIntPtr/\*delegate\*<[^*]*>\*/\)\(&([\w.]+)\)', r'__Rewind.Ftn("\1")', t)
    t = sub('ftn', r'\(UIntPtr/\*delegate\*<[^*]*>\*/\)\(&([\w.]+)\)', r'(ulong)__Rewind.Ftn("\1")', t)
    t = sub('ftn', r'__ld(?:virt)?ftn\(([^()]*)\)', r'__Rewind.Ftn("\1")', t)
    return t


# ---------------------------------------------------------------- constructors

CTOR_DECL = re.compile(r'^(\s*)((?:public|private|protected|internal)(?:\s+(?:unsafe|static))*\s+)?(\w+)\((.*)\)\s*$')


def balanced_args(s, start):
    """s[start] == '('; return index of the matching ')'."""
    depth = 0
    for i in range(start, len(s)):
        c = s[i]
        if c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return i
    return -1


def fix_ctors(lines, path):
    out = list(lines)
    # inlinedctor: x = (T)FormatterServices.GetUninitializedObject(typeof(T)); then x..ctor(a). In Rewind's
    # IL the call is nearly always to a base class's constructor, T's own constructor having been inlined
    # (Assembly-CSharp: 142 of 154), which C# can't express; `new T(a)` would silently pick T's
    # constructor. Left as a placeholder call.
    uninit = set()
    for i, l in enumerate(out):
        m = re.search(r'(\w+) = \(([\w.]+)\)FormatterServices\.GetUninitializedObject\(typeof\(([\w.]+)\)\);', l)
        if m:
            uninit.add(m.group(1))
            continue
        m2 = re.match(r'^(\s*)(\w+)\.\.ctor\((.*)\);\s*$', l)
        if m2 and m2.group(2) in uninit:
            args = m2.group(3).strip()
            out[i] = '%s__Rewind.InlinedCtor(%s, %s);' % (
                m2.group(1), m2.group(2), 'new object[] { %s }' % args if args else 'new object[0]')
            COUNTS['inlinedctor'] += 1
    # basector
    i = 0
    while i < len(out):
        m = re.match(r'^(\s*)base\.\.ctor(\(.*\));\s*$', out[i])
        if m:
            args = m.group(2)
            # find the constructor declaration above: the nearest line matching CTOR_DECL followed by '{'
            j = i - 1
            while j >= 0 and not (CTOR_DECL.match(out[j]) and j + 1 < len(out) and out[j + 1].strip() == '{'):
                j -= 1
            if j >= 0:
                before = [l.strip() for l in out[j + 2:i]]
                real = [l for l in before if l and not l.startswith('//') and not l.startswith('__Rewind.Note(')]
                if real:
                    LOG.append('basector reorder %s:%d (%d statements before base ctor)' % (path, j + 1, len(real)))
                    COUNTS['basector-reordered'] += 1
                out[j] = out[j].rstrip() + ' : base' + args
                del out[i]
                COUNTS['basector'] += 1
                continue
        i += 1
    # objctor: ((T)numN)..ctor(args);
    for i, l in enumerate(out):
        m = re.match(r'^(\s*)\(\(([\w.<>\[\], ]+)\)(\w+)\)\.\.ctor(\(.*\));\s*$', l)
        if m:
            out[i] = '%s%s = __Rewind.Ref(new %s%s);' % (m.group(1), m.group(3), m.group(2), m.group(4))
            COUNTS['objctor'] += 1
    return out


# ---------------------------------------------------------------- write barriers

BARRIER_LINE = [
    re.compile(r'^\{$'), re.compile(r'^\}$'), re.compile(r'^do$'),
    re.compile(r'^int \w+ = \(int\)\(\(ulong\).*>> 12\) & 0x1FFFFF;$'),
    re.compile(r'^(ulong|long) \w+ = .*;$'),
    re.compile(r'^long \w+;$'),
    re.compile(r'^\w+ = __Rewind\.Opaque<long>\("__(load8|atomic_compare_exchange)\(.*\)"\);$'),
    re.compile(r'^while \(\w+ != \w+\);$'),
]


def block_end(lines, open_idx):
    depth = 0
    for k in range(open_idx, len(lines)):
        s = lines[k].strip()
        if s == '{':
            depth += 1
        elif s == '}':
            depth -= 1
            if depth == 0:
                return k
    return -1


def fix_barriers(lines):
    out = list(lines)
    i = 0
    while i < len(out):
        s = out[i].strip()
        if re.match(r'^if \((__Rewind\.Opaque<int>\("__load4\(\d+L\)"\)|\w+) != 0\)$', s) and i + 1 < len(out) and out[i + 1].strip() == '{':
            end = block_end(out, i + 1)
            body = [l.strip() for l in out[i + 1:end + 1]]
            if end > 0 and any('__atomic_compare_exchange' in l for l in body) and all(any(p.match(l) for p in BARRIER_LINE) for l in body if l):
                nxt = out[end + 1].strip() if end + 1 < len(out) else ''
                if not nxt.startswith('else'):
                    del out[i:end + 1]
                    COUNTS['barrier'] += 1
                    continue
        i += 1
    return out


# ---------------------------------------------------------------- redirects to the shim

REDIRECTS = [
    # BCL members 2018.2 lacks or keeps private (B1/B2/B3)
    (r'\bBitConverter\.SingleToInt32Bits\(', 'Compat2018.BCL.SingleToInt32Bits('),
    (r'(?<![\w.])(?:System\.)?(?:Runtime\.)?(?:CompilerServices\.)?Unsafe\.', 'Compat2018.Unsafe.'),
    (r'("(?:[^"\\]|\\.)*"|(?<![\w.])\w+(?:\.\w+)*)\._firstChar\b', r'Compat2018.BCL.FirstChar(\1)'),
    (r'(?<![\w.])(?:System\.)?SpanHelpers\.', 'Compat2018.BCL.'),
    (r'(?<![\w.])UnityEngine\.Bindings\.ThrowHelper\.', 'Compat2018.Engine.'),
    (r'(?<![\w.])UnityEngine\.Bindings\.Unmarshal\.', 'Compat2018.Engine.'),
    (r'(?<![\w.])(?:System\.)?ThrowHelper\.', 'Compat2018.BCL.'),
    (r'\(\(Delegate\)(\w+)\)\.(method_ptr|method_code|m_target|invoke_impl|extra_arg|method)\b', r'Compat2018.BCL.Delegate_\2(\1)'),
    (r'(?<![\w.])(\w+)\.m_Ptr\b', r'Compat2018.Engine.Ptr(\1)'),
    (r'(?<![\w.])(\w+(?:\.\w+)*|\(\([\w.]+\)\w+\))\.GetCachedPtr\(\)', r'Compat2018.Engine.GetCachedPtr(\1)'),
    (r'(?<![\w.])GetCachedPtr\(\)', r'Compat2018.Engine.GetCachedPtr(this)'),
    (r'\(\(object\)([\w.]+)\)\.MemberwiseClone\(\)', r'Compat2018.BCL.MemberwiseClone(\1)'),
    # engine bindings: Type.Member_Injected( -> Compat2018.Injected.Type_Member_Injected(
    (r'(?<![\w.])(?:UnityEngine\.)?(?:\w+\.)*?(\w+)\.(\w+_Injected)\(', r'Compat2018.Injected.\1_\2('),
    # public Unity 6 API (U2)
    (r'(?<![\w.])(?:UnityEngine\.)?(?:Rendering\.)?MeshUpdateFlags\b', 'Compat2018.MeshUpdateFlags'),
    (r'(?<![\w.])(?:UnityEngine\.)?MipmapLimitDescriptor\b', 'Compat2018.MipmapLimitDescriptor'),
    (r'(?<![\w.])InspectorName\b', 'Compat2018.InspectorName'),
]


def fix_redirects(t):
    for pat, repl in REDIRECTS:
        t = sub('redirect', pat, repl, t)
    if 'RecalculateNormals(Compat2018.' in t:  # the shim's extension overload
        t = 'using Compat2018;\n' + t
    return t


# ---------------------------------------------------------------- libraries

PUBLIC_VIRTUALS = set()  # names of public abstract/virtual members in the --libs sources


def fix_lib(t):
    # an override of a member IL2CPP's metadata shows as public in the base (CS0507)
    t = sub('libpublic', r'\binternal override ([\w.<>\[\]]+) (\w+)\b',
            lambda m: ('public' if m.group(2) in PUBLIC_VIRTUALS else 'internal') + ' override %s %s' % (m.group(1), m.group(2)), t)
    t = sub('libpublic', r'^(\s*)(?:private|protected internal|protected|internal)(\s+(?:static\s+|readonly\s+|volatile\s+)*[\w.<>\[\], ]+\s+\w+(?:\s*=(?!>)\s*[^;]*)?;\s*)$',
            r'\1public\2', t, flags=re.M)
    t = sub('libpublic', r'^(\s*)(?:private|protected internal|protected|internal)(\s+(?:static\s+|sealed\s+|abstract\s+)*(?:class|struct|enum)\s)',
            r'\1public\2', t, flags=re.M)
    return t


PAIRS = {'==': '!=', '!=': '==', '<': '>', '>': '<', '<=': '>=', '>=': '<='}


def fix_stripped(t):
    """IL2CPP strips members nothing calls; C# requires some in pairs, and interface members."""
    for m in list(re.finditer(r'public static bool operator (==|!=|<=|>=|<|>)\((\w+) (\w+), \2 (\w+)\)', t)):
        op, typ = m.group(1), m.group(2)
        other = PAIRS[op]
        if re.search(r'operator ' + re.escape(other) + r'\(' + typ + r'\b', t):
            continue
        if op in ('==', '!='):
            body = 'return !(x %s y);' % op
        else:
            body = 'return y %s x;' % op  # a < b  <=>  b > a
        t = t.replace(m.group(0), 'public static bool operator %s(%s x, %s y) { %s }\n\n\t\t%s'
                      % (other, typ, typ, body, m.group(0)), 1)
        COUNTS['stripped'] += 1
    # iterator classes whose IEnumerator.Reset was stripped
    for m in list(re.finditer(r'class _\w+_d__\d+ : [^\n]*\bIEnumerator\b[^\n]*\n(\s*)\{', t)):
        start = m.end()
        if 'Reset()' not in t[start:start + 20000].split('\n' + m.group(1) + '}')[0]:
            t = t[:start] + '\n%s\tvoid System.Collections.IEnumerator.Reset() { throw new System.NotSupportedException(); }\n' % m.group(1) + t[start:]
            COUNTS['stripped'] += 1
    return t


def text_pass(src, out, libs):
    if os.path.exists(out):
        shutil.rmtree(out)
    shutil.copytree(src, out)
    for lib in libs:
        for root, _, files in os.walk(os.path.join(out, lib)):
            for f in files:
                if f.endswith('.cs'):
                    with open(os.path.join(root, f), encoding='utf-8-sig') as fh:
                        PUBLIC_VIRTUALS.update(re.findall(r'public (?:abstract|virtual) [\w.<>\[\]]+ (\w+)', fh.read()))
    for root, _, files in os.walk(out):
        for f in files:
            if not f.endswith('.cs'):
                continue
            path = os.path.join(root, f)
            with open(path, encoding='utf-8-sig') as fh:
                t = fh.read()
            orig = t
            t = fix_names(t)
            t = fix_il_only(t)
            lines = fix_ctors(t.split('\n'), path)
            lines = fix_barriers(lines)
            t = '\n'.join(lines)
            t = fix_redirects(t)
            t = fix_stripped(t)
            rel = os.path.relpath(path, out).split(os.sep)[0]
            if rel in libs:
                t = fix_lib(t)
            if t != orig:
                with open(path, 'w', encoding='utf-8') as fh:
                    fh.write(t)


# ---------------------------------------------------------------- diag-driven pass

PRIMITIVE = {'int', 'uint', 'long', 'ulong', 'short', 'ushort', 'byte', 'sbyte', 'char', 'bool', 'float',
             'double', 'decimal', 'IntPtr', 'UIntPtr', 'System.IntPtr', 'System.UIntPtr'}


def diag_pass(diags):
    edits = collections.defaultdict(list)  # file -> [(line, col, old, new)]
    for path in diags:
        with open(path, encoding='utf-8') as fh:
            for r in csv.DictReader(fh, delimiter='\t', quoting=csv.QUOTE_NONE):
                code, msg, text = r['code'], r['message'], r['text']
                if code == 'CS0030' and msg.startswith("Cannot convert type 'long' to '"):
                    target = msg.split("to '", 1)[1].rstrip("'")
                    if target in PRIMITIVE:
                        continue
                    m = re.match(r'^\(([^()]+)\)(.+)$', text)
                    if m:
                        edits[r['file']].append((int(r['line']), int(r['col']), text, '__Rewind.As<%s>(%s)' % (m.group(1), m.group(2))))
                        COUNTS['marker-cast'] += 1
                elif code == 'CS0155' and text and re.match(r'^[\w.]+$', text):
                    edits[r['file']].append((int(r['line']), int(r['col']), text, '__Rewind.As<System.Exception>(%s)' % text))
                    COUNTS['marker-throw'] += 1
    for f, es in edits.items():
        with open(f, encoding='utf-8-sig') as fh:
            lines = fh.read().split('\n')
        for line, col, old, new in sorted(set(es), key=lambda e: (e[0], e[1]), reverse=True):
            s = lines[line - 1]
            if s[col - 1:col - 1 + len(old)] == old:
                lines[line - 1] = s[:col - 1] + new + s[col - 1 + len(old):]
            else:
                COUNTS['diag-miss'] += 1
        with open(f, 'w', encoding='utf-8') as fh:
            fh.write('\n'.join(lines))


def main():
    if len(sys.argv) >= 4 and sys.argv[1] == 'text':
        libs = set()
        if '--libs' in sys.argv:
            libs = set(sys.argv[sys.argv.index('--libs') + 1].split(','))
        text_pass(sys.argv[2], sys.argv[3], libs)
    elif len(sys.argv) >= 4 and sys.argv[1] == 'diag':
        diag_pass(sys.argv[3:])
    else:
        sys.exit(__doc__)
    for k, v in sorted(COUNTS.items()):
        print('%-20s %6d' % (k, v))
    for l in LOG:
        print(l)


if __name__ == '__main__':
    main()
