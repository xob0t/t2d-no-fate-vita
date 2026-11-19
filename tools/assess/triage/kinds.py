"""Classifies Rewind gap marker sites by repair pattern.

classify(kind, text, ctx) -> pattern key. `kind` is 'Opaque' or 'Effect', `text` the marker's text, `ctx`
the texts of the method's other markers (for the boxing block, whose `__undefined()` sites are only
recognisable next to their `__Memcpy`). The keys and their descriptions are in PATTERNS.
Nothing here names a game method; the shapes are IL2CPP's and Rewind's.
"""
import re

# key: (title, scope, how it could be fixed)
#   scope 'general' = one pattern / Rewind fix covers every site; 'per-method' = each site needs its own facts
PATTERNS = {
    'box':        ('Inlined Int32 boxing into object[] (Memcpy from the frame + 3 __undefined)', 'general',
                   'pattern: the block is `box Int32` of the value already in the local; or Rewind: recognise il2cpp Box'),
    'barrier':    ('GC write barrier left in (GC flag load, card-table load, card CAS)', 'general',
                   'pattern: widen Patterns.cs barrier pattern to address-in-local / static / element shapes (the 126 psp2snc errors)'),
    'event':      ('Inlined event add/remove (interlocked compare-exchange on a delegate field)', 'general',
                   'pattern: eventfold (Delegate.Combine/Remove), as the probe did by source rule'),
    'string':     ('Inlined String indexer / compare (String._firstChar arithmetic)', 'general',
                   'pattern: fold to String.get_Chars / String.CompareOrdinal (bclfold has the shape)'),
    'unbox':      ('Unboxing or field read on an untyped object at +16 (haxe Dynamic)', 'general',
                   'pattern: `unbox.any T` / castclass when the offset is 16 and the type follows from the use'),
    'untyped':    ('Field access through an untyped reference (no field at offset N of object)', 'per-method',
                   'rule: castclass to the class whose field sits at that offset (field-offset table)'),
    'missingarg': ('Call argument not captured (stack arguments of a many-argument call)', 'general',
                   'Rewind fix: issue 21 (capture stack arguments); else invoker decoder per call'),
    'invoker':    ('Unidentified invoker thunk call (__native_0x18000xxxx)', 'per-method',
                   'tool: invoker decoder maps registers/stack to the target method; then 1 line per site'),
    'tailmerge':  ('Indirect / tail-merged virtual call (__call_indirect, __objklass)', 'per-method',
                   'Rewind fix: issue 17 this-form; else rule: call slot N on this'),
    'rtti':       ('Runtime type data used as a value (__vtable, __klass, __fieldinfo, IsAssignableFrom)', 'per-method',
                   'rule: isinst / castclass / ldtoken'),
    'statics':    ('Statics pointer reused as a value (__statics())', 'general',
                   'Rewind fix: issue 3 remainder (statics reused across a removed barrier); else ldsfld'),
    'smallstore': ('Store spanning adjacent fields (__store1/2/4)', 'general',
                   'pattern: split by the field-offset table (patch 0007 covers own-class fields)'),
    'rawstore':   ('Store or load through an untyped address (__store8, __load8 of a local)', 'per-method',
                   'rule: stfld/stelem with the field or element named'),
    'switch':     ('Native switch table read from an absolute address', 'per-method',
                   'generator: read the jump table from GameAssembly.dll (as atlas-usage native_dispatch.py does)'),
    'unreachable':('Unreachable / no-return marker after a call (__unreachable, __noreturn)', 'general',
                   'pattern: delete (the path after a throw helper)'),
    'interface':  ('Interface invoker not translated (__InterfaceInvoker)', 'per-method',
                   'rule: callvirt the interface method'),
    'native':     ('Other unidentified runtime function or intrinsic', 'per-method',
                   'rule after reading the native code'),
    'exception':  ('Exception-stack / thread-static / native-code runtime data', 'per-method',
                   'rule after reading the native code'),
    'undefined':  ('Value Rewind lost (__undefined) outside a boxing block', 'per-method',
                   'rule after reading the native code'),
}

GC_FLAG = '6460067984L'   # the incremental GC's flag (0x1810CD090), as Rewind prints it
# a native switch's tables: an index byte from one absolute table, scaled into a second (case values or offsets)
SWITCH = re.compile(r'^__load4\(\(\((conv\.u8\(__load1\(.*\d{9,}L\)\)\)|\w+) \* 4L\) \+ \d{9,}L\)\)$')
CARD = re.compile(r'^__load8\(\(\(?\d+L \+ \(t\d+ \* 8L\)\)( \+ \d+L\))?\)?$')


def head(text):
    m = re.match(r'[^(]*', text)
    return m.group(0) if m else text


def classify(kind, text, ctx=()):
    h = head(text)
    if h == '__Memcpy' and '__frame()' in text:
        return 'box'
    if text == '__undefined()':
        return 'box' if any(head(c) == '__Memcpy' and '__frame()' in c for c in ctx) else 'undefined'
    if text == f'__load4({GC_FLAG})' or CARD.match(text) or (h == '__atomic_compare_exchange' and '>>> 12' in text):
        return 'barrier'
    if h == '__atomic_compare_exchange':
        return 'event'
    if '_firstChar' in text:
        return 'string'
    if h.startswith('__load') and re.search(r'__reference_arithmetic\(\(\w[\w.\[\]]* \+ 16L\)\)', text):
        return 'unbox'
    if h.startswith('__load') and '__reference_arithmetic' in text:
        return 'untyped'
    if h == '__missing_argument':
        return 'missingarg'
    if h.startswith('__native_0x18000') or h == '__native_0x180003':
        return 'invoker'
    if h.startswith('__native_0x1800030') or h.startswith('__native_0x1800031') or h.startswith('__native_0x1800032'):
        return 'invoker'
    if h in ('__call_indirect', '__objklass'):
        return 'tailmerge'
    if h in ('__vtable', '__fieldinfo', '__klass', '__IsAssignableFrom'):
        return 'rtti'
    if h == '__statics':
        return 'statics'
    if SWITCH.match(text):
        return 'switch'
    if h in ('__store1', '__store2', '__store4') and re.match(r'^__store\d\(\(\w+ \+ \d+L\), -?\d+\)$', text):
        return 'smallstore'
    if h.startswith('__store') or h.startswith('__load') or h == '__address':
        return 'rawstore'
    if h in ('__unreachable', '__noreturn'):
        return 'unreachable'
    if h == '__InterfaceInvoker':
        return 'interface'
    if h in ('__ExceptionStackPush', '__ExceptionStackPop', '__ThreadStaticData', '__nativecode'):
        return 'exception'
    return 'native'


# My estimate of the one-off cost of each general rule (hours: write, check on every site, ILVerify). No per-method
# time is counted for the sites it clears. docs/repairs.md measured only the builder pieces (SkipBarrier 10 min,
# Expr.cs 40 min, the initializer reader and recipe 40 min), so these are guesses at a few times that.
RULE_HOURS = {
    'box': 3.0, 'barrier': 3.0, 'string': 3.0, 'missingarg': 6.0, 'statics': 4.0, 'unreachable': 0.5,
    'unbox': 2.0, 'smallstore': 1.0, 'event': 1.0,
}
