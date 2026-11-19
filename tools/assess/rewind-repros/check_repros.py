"""Regression test for the Rewind issues in docs/rewind-issues.md: decompile the repro game (Repros.cs, built by
build_repros.py) with a given Rewind build and print PASS (fixed) or FAIL (still reproduces) per repro.

    python -I tools/assess/rewind-repros/check_repros.py <rewind> [--out <dir>] [--sig <file>] [--work <dir>]
                                                         [--report <md>] [--only <issue>...]

<rewind>   rewind.exe, or the folder holding it (<worktree>/src/Rewind.Cli/bin/Release/net10.0).
--out      where Rewind writes (default: work/rewind-repros/check/<worktree name>, e.g. check/numeric for
           tools/external/rewind-wt/numeric/...).
--sig      signature library (default: tools/rewind/signatures/unity-6000.0.60f1-windows-x64.sig).
--work     the repro build's folder (default: work/rewind-repros/build). It is built the first time (about 10
           to 20 minutes) and reused while Repros.cs and the build scripts don't change.
--report   also write a Markdown report: per repro the expected C#, Rewind's C#, its IL where a check reads
           IL, and an excerpt of the native code (rizin; see disasm.py).
--only     run only these issues (e.g. --only 13 2).

The exit code is the number of failing checks. Checks named "control" are variants that Rewind already recovers
correctly (they guard against regressions); all others fail on upstream main (ea8e305) and pass once fixed. Checks read the shape of Rewind's C# (and, for a few, its IL
through RewindStats, built into work/bin/RewindStats if missing): a fix that recovers the code another correct way
may need its check adjusted. Each repro's expected C# is in REPROS below and in Repros.cs.
"""
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent.parent
sys.path.insert(0, str(HERE))
from build_repros import build  # noqa: E402

DOTNET = REPO / 'tools/external/dotnet10/dotnet.exe'
STATS = REPO / 'work/bin/RewindStats/RewindStats.dll'
DEFAULT_SIG = REPO / 'tools/rewind/signatures/unity-6000.0.60f1-windows-x64.sig'
MARKERS = ('__Rewind.Opaque', '__Rewind.Effect')


class Repro:
    """One repro: its issue, the member in Rewind's C# (type file and a string on the member's first line), the
    native symbol's prefix, the expected C#, the check (on the C#, or on the IL when il='check'), and a regex
    selecting the native lines to show (None: no native excerpt)."""

    def __init__(self, issue, name, type_name, member, symbol, expected, check, native=None, il=None):
        self.issue, self.name, self.type_name, self.member = issue, name, type_name, member
        self.symbol, self.expected, self.check, self.native, self.il = symbol, expected, check, native, il


def no(*parts):
    """Passes when none of the parts appear."""
    def f(text):
        found = [p for p in parts if p in text]
        return (not found, f'still has {found[0]!r}' if found else '')
    return f


def all_of(*checks):
    def f(text):
        for c in checks:
            ok, why = c(text)
            if not ok:
                return False, why
        return True, ''
    return f


def has(*parts):
    def f(text):
        missing = [p for p in parts if p not in text]
        return (not missing, f'no {missing[0]!r}' if missing else '')
    return f


def matches(pattern):
    def f(text):
        return (re.search(pattern, text) is not None, f'no match for /{pattern}/')
    return f


def not_silent(text):
    """A __Rewind.Note that says parts weren't recovered needs a marker in the body."""
    if 'were not recovered' in text and not any(m in text for m in MARKERS):
        return False, 'a Note says parts were not recovered, but there is no Opaque/Effect marker'
    return True, ''


def cases_reach(bodies):
    """Passes when every case value's label (or "default:", for a value with no label) leads, directly or through
    "goto case", to its body's call first."""
    def f(text):
        for call, values in bodies.items():
            for v in values:
                seen, label = set(), v
                while True:
                    m = re.search(rf'\bcase {label}:', text) or (label == v and re.search(r'\bdefault:', text))
                    if not m:
                        return False, f'no "case {label}:" and no default'
                    nxt = re.search(r'\b(Walk|Climb|Swim)\(|goto case (-?\d+);', text[m.end():])
                    if nxt is None:
                        return False, f'case {v} reaches no body'
                    if nxt.group(1):
                        if nxt.group(1) != call:
                            return False, f'case {v} reaches {nxt.group(1)}, not {call}'
                        break
                    label = nxt.group(2)
                    if label in seen:
                        return False, f'case {v}: goto loop'
                    seen.add(label)
        return True, ''
    return f


def il_calls_base_ctor(base):
    def f(il):
        if f'call System.Void {base}::.ctor()' in il:
            return True, ''
        return False, f'the constructor does not call {base}::.ctor()'
    return f


REPROS = [
    Repro('13', 'negated double as a stack argument (silent)', 'Issue13', 'void Issue13_NegatedStackArgument(',
          'Issue13_Issue13_NegatedStackArgument',
          'double vy = Mode == 8 ? 0.0 : -(Rng.NextDouble() * 6.0 + 2.0);\nSpawned[i & 7] = new Particle(57, 0, true, vx, vy, 0.0, i);',
          all_of(no('Expected F8, but got I4', 'DoubleToInt64Bits', '__bits_to_double'), matches(r'-\s*\(|= -|\(-')),
          native=r'xorps|Particle__ctor'),
    Repro('13', 'negated double as a register argument (opaque)', 'Issue13', 'void Issue13_NegatedRegisterArgument(',
          'Issue13_Issue13_NegatedRegisterArgument', 'Sink.TakeDouble(-Math.Sin(angle));',
          all_of(no('__bits_to_double', 'DoubleToInt64Bits'), matches(r'TakeDouble\(-')), native=r'.'),
    Repro('13', 'negated field as a register argument (opaque)', 'Issue13', 'void Issue13_NegatedFieldArgument(',
          'Issue13_Issue13_NegatedFieldArgument', 'Sink.TakeDoubleBool(-p.Vy, p.Animated);',
          all_of(no('__bits_to_double', 'DoubleToInt64Bits'), matches(r'TakeDoubleBool\(-')), native=r'.'),
    Repro('2', 'clamped double merged before a virtual call', 'Fader', 'void Issue2_ClampedAlpha(',
          'Fader_Issue2_ClampedAlpha', 'double a = GetAlpha();\nif (a < 1.0) { a += 0.05; SetAlpha(a > 1.0 ? 1.0 : a); }',
          no('__bits_to_double', 'DoubleToInt64Bits'), native=r'.'),
    Repro('2', 'double merged from two branches', 'Issue2', 'void Issue2_MergedArgument(',
          'Issue2_Issue2_MergedArgument', 'if (alive && p != null) { SaveX(p.Vx, false); y = p.Vy; } else { SaveX(0.0, false); y = 0.0; }\nSaveY(y, false);',
          no('__bits_to_double', 'DoubleToInt64Bits'), native=r'.'),
    Repro('19', 'Vector2 returned in RAX', 'Issue19', 'Vector2 Issue19_SnapVector2(',
          'Issue19_Issue19_SnapVector2', 'return new Vector2(Math.Sign(x), Math.Sign(y));  // and (0, sign y), (sign x, 0)',
          all_of(no('Expected VT, but got I8', 'SingleToInt32Bits', '(Vector2)('), has('new Vector2(')),
          native=r'shl\s+r..?, 0x20|or\s+r|movd|movq|ret'),
    Repro('19', 'control: a struct of our own returned in RAX', 'Issue19', 'FloatPair Issue19_OrderedPair(',
          'Issue19_Issue19_OrderedPair', 'return a > b ? new FloatPair(b, a) : new FloatPair(a, b);',
          all_of(no('Expected VT', 'SingleToInt32Bits'), has('new FloatPair(')), native=r'.'),
    Repro('8', 'UInt64.GetHashCode inlined, not truncated', 'ULongId', 'int GetHashCode(',
          'ULongId_GetHashCode', 'public override int GetHashCode() => Value.GetHashCode();',
          no('Expected I4, but got I8'), native=r'.'),
    Repro('8', 'int added to an inlined UInt64 hash', 'KeyChord', 'int GetHashCode(',
          'KeyChord_GetHashCode', 'public override int GetHashCode() => Keys.GetHashCode() + Count;',
          no('Expected I4, but got I8'), native=r'.'),
    Repro('7', 'address of an offset-0 field (this)', 'ULongId', 'string ToString(',
          'ULongId_ToString', 'public override string ToString() => Value.ToString();',
          all_of(no('(ulong)this', 'ulong*'), has('Value.ToString()')), native=r'.', il='show'),
    Repro('7', 'address of an offset-0 field (parameter)', 'Issue7', 'void Issue7_ParameterFieldToString(',
          'Issue7_Issue7_ParameterFieldToString', 'if (callback.Result == 1) Last = callback.GameId.ToString();',
          all_of(no('ulong*'), has('GameId.ToString()')), native=r'UInt64_ToString|lea\s+rcx', il='show'),
    Repro('16', 'two inherited bool fields set by one 2-byte store', 'FlagsDerived', 'void Issue16_SetInheritedBools(',
          'FlagsDerived_Issue16_SetInheritedBools', 'Kind = kind; Visible = true; CanBeHit = false;',
          all_of(no('__store2', 'spans fields'), has('Visible = true', 'CanBeHit = false')), native=r'.'),
    Repro('16', 'two inherited bools cleared by one 2-byte store', 'FlagsDerived', 'void Issue16_ClearInheritedBools(',
          'FlagsDerived_Issue16_ClearInheritedBools', 'CanBeHit = false; CanHitPlayer = false; Extra = 3;',
          all_of(no('__store2', 'spans fields'), has('CanBeHit = false', 'CanHitPlayer = false')), native=r'.'),
    Repro('16', 'four inherited bools set by one 4-byte store', 'FlagsDerived', 'void Issue16_SetFourInheritedBools(',
          'FlagsDerived_Issue16_SetFourInheritedBools', 'Important = false; Active = true; Visible = true; CanBeHit = false;',
          all_of(no('__store4', 'spans fields'), has('Active = true', 'Visible = true')), native=r'.'),
    Repro('16', 'control: two own bool fields, split correctly', 'Flags16', 'void Issue16_SetTwoBools(',
          'Flags16_Issue16_SetTwoBools', 'State = state; Visible = true; CanBeHit = false;',
          all_of(no('__store2', 'spans fields'), has('Visible = true', 'CanBeHit = false')), native=r'.'),
    Repro('17', 'control: calls on a static field\'s object, not merged (class-init checks)', 'Issue17', 'void Issue17_TailMergedStaticReceiver(',
          'Issue17_Issue17_TailMergedStaticReceiver', 'case 1: t = Mode != 8 ? Rng.Next(60, 180) : 120; ...\ncase 2: t = Mode != 8 ? Rng.Next(120, 180) : 150; ...',
          all_of(no('__call_indirect', '__objklass'), matches(r'\.Next\(')), native=r'call\s+qword|jmp|mov\s+r.., qword \[r..\]'),
    Repro('17', 'control: tail-merged call on this, class reloaded in the shared block', 'Walker', 'void Issue17_TailMergedThisCall(',
          'Walker_Issue17_TailMergedThisCall', 'ChangeAnim(7, 0, 4); ... ChangeAnim(8, 0, 4); ... ChangeAnim(Input == 0 ? 1 : 2, 0, 4);',
          all_of(no('__call_indirect', '__objklass'), has('ChangeAnim(')), native=r'call\s+qword|jmp\s+qword|jmp\s+r'),
    Repro('17', 'tail-merged call, static receiver without class init', 'Issue17b', 'int Issue17_TailMergedNoCctor(',
          'Issue17b_Issue17_TailMergedNoCctor', 'if (state == 1) t = mode != 8 ? RandomHolder.Rng.Next(60, 180) : 120;\nelse t = mode != 8 ? RandomHolder.Rng.Next(120, 180) : 150;\nreturn t + 1;',
          all_of(no('__call_indirect', '__objklass'), matches(r'\.Next\(')), native=r'call\s+qword|jmp|mov\s+r.., qword \[r..\]'),
    Repro('17', 'control: calls with arguments from fields, not merged', 'Issue17b', 'int Issue17_TailMergedFieldArgs(',
          'Issue17b_Issue17_TailMergedFieldArgs', 't = state == 1 ? RandomHolder.Rng.Next(RandomHolder.Low, 180) : state == 2 ? RandomHolder.Rng.Next(RandomHolder.High, 180) : 0;',
          all_of(no('__call_indirect', '__objklass'), matches(r'\.Next\(')), native=r'call\s+qword|jmp|mov\s+r.., qword \[r..\]'),
    Repro('17', 'control: call on this after a merge, class reloaded', 'Walker', 'void Issue17_MergedClassPointer(',
          'Walker_Issue17_MergedClassPointer', 'int row;\nif (Pressed(3)) { Vx -= 0.5; row = 7; } else { if (Pressed(2)) Vx += 0.5; row = 8; }\nChangeAnim(row, 0, 4);',
          all_of(no('__call_indirect', '__objklass'), has('ChangeAnim(')), native=r'call\s+qword|jmp\s+qword|mov\s+r.., qword \[r..\]'),
    Repro('17', 'control: tail call on this after a merge, class reloaded', 'Walker', 'void Issue17_MergedClassPointerTail(',
          'Walker_Issue17_MergedClassPointerTail', 'if (input == 0) { if (Pressed(1)) return; ChangeAnim(5, 0, 4); }\nelse { if (Pressed(input)) Frame = 0; ChangeAnim(44, 0, 4); }',
          all_of(no('__call_indirect', '__objklass'), has('ChangeAnim(')), native=r'call\s+qword|jmp\s+qword|mov\s+r.., qword \[r..\]'),
    Repro('18', 'call through an invoker specialized for one slot', 'Brawler', 'void Issue18_SpecializedInvoker(',
          'Brawler_Issue18_SpecializedInvoker', 'SetupHitbox(X + 8, Y, 32, 32, Grounded, Image != 230 ? Damage : Damage * 2, Facing, Facing * 2.0, Vy, Types, Sounds, true, Grounded);  // and 10 more',
          all_of(no('__native_', '__missing_argument', '__Rewind.Effect', '__Rewind.Opaque'), has('SetupHitbox(')),
          native=r'call\s+(fcn\.)?0x'),
    Repro('21', '13-argument virtual call: last 6 arguments not captured', 'Shooter', 'void Issue18_InvokerCallA(',
          'Shooter_Issue18_InvokerCallA', 'SetupImpact(X + 4.0, Y, 32, 32, KnockDown, damage, Facing, Facing * 2.0, Vy, Types, Sounds, true, KnockDown);',
          no('__missing_argument'), native=r'.'),
    Repro('6', 'typeof(T) passed to Marshal.SizeOf: noted, unmarked', 'Issue6', 'bool Issue6_TypeofSizeOf(',
          'Issue6_Issue6_TypeofSizeOf', 'int a = Marshal.SizeOf(typeof(PackSentinel));\nint b = Marshal.SizeOf(typeof(PackedRecord));\nreturn a == 16 && b == 16;',
          not_silent, native=r'call|0x20\]'),
    Repro('6', 'typeof(T) recovered (the cause of the unmarked gap)', 'Issue6', 'bool Issue6_TypeofSizeOf(',
          'Issue6_Issue6_TypeofSizeOf', 'Marshal.SizeOf(typeof(PackSentinel))',
          all_of(no('InternalBoxEnum', '.GetType()'), has('typeof(PackSentinel)')), native=None),
    Repro('11', 'MonoBehaviour constructor without its base call', 'EmptyBehaviour', '.ctor',
          'EmptyBehaviour__ctor', 'public EmptyBehaviour() : base() { }  // IL: ldarg.0; call MonoBehaviour::.ctor(); ret',
          il_calls_base_ctor('UnityEngine.MonoBehaviour'), native=r'.', il='check'),
    Repro('11', 'plain class constructor without its base call', 'EmptyDerived', '.ctor',
          'EmptyDerived__ctor', 'public EmptyDerived() : base() { }  // IL: ldarg.0; call EmptyBase::.ctor(); ret',
          il_calls_base_ctor('Repros.EmptyBase'), native=r'.', il='check'),
    Repro('ctor', 'control: inlined constructor of a sealed class, folded back', 'InlinedCtor', 'void InlinedCtor_NewTint(',
          'InlinedCtor_InlinedCtor_NewTint', 'Logo = new Tint(1, 1, 1, 0);',
          all_of(no('GetUninitializedObject'), has('new Tint(')), native=r'Object_New|New@Object|call'),
    Repro('ctor', 'inlined constructor calling virtual methods', 'InlinedCtor', 'void InlinedCtor_NewVirtualTint(',
          'InlinedCtor_InlinedCtor_NewVirtualTint', 'Fade = new VirtualTint(1, 1, 1, 0);',
          all_of(no('GetUninitializedObject'), has('new VirtualTint(')), native=r'New@Object|call'),
    Repro('ctor', 'inlined constructor calling virtual methods (local)', 'InlinedCtor', 'VirtualTint InlinedCtor_NewVirtualTintLocal(',
          'InlinedCtor_InlinedCtor_NewVirtualTintLocal', 'VirtualTint t = new VirtualTint(0, 0, 0, alpha);',
          all_of(no('GetUninitializedObject'), has('new VirtualTint(')), native=r'New@Object|call'),
    Repro('9', 'RuntimeHelpers.GetHashCode inlined', 'Issue9', 'int Issue9_IdentityHash(',
          'Issue9_Issue9_IdentityHash', 'return RuntimeHelpers.GetHashCode(o);',
          all_of(no('>> 3', '-1640531535'), has('GetHashCode(')), native=r'.'),
    Repro('9', 'Object.GetHashCode inlined (base call)', 'BindingLike', 'int GetHashCode(',
          'BindingLike_GetHashCode', 'public override int GetHashCode() => base.GetHashCode();',
          all_of(no('>> 3', '-1640531535'), has('GetHashCode(')), native=r'.'),
    Repro('10', '4-byte struct parameter used as an int', 'Matcher', 'Issue10_VendorID',
          'Matcher_set_Issue10_VendorID', 'set => VendorField = value;',
          all_of(no('(int)value'), has('VendorField = value')), native=r'.'),
    Repro('10', 'int returned as a 4-byte struct', 'Issue10', 'PipeHandle Issue10_IntAsStruct(',
          'Issue10_Issue10_IntAsStruct', 'return new PipeHandle(NextHandle());',
          all_of(no('Expected VT, but got I4', '(PipeHandle)'), has('new PipeHandle(')), native=r'.'),
    Repro('3', 'write barrier after a static field store', 'Registry', 'Registry Issue3_GetInstance(',
          'Registry_Issue3_GetInstance', 'if (s_instance == null) s_instance = new Registry();\nreturn s_instance;',
          all_of(no('__atomic_compare_exchange', '__statics()', '__load4('), has('return s_instance')),
          native=r'cmpxchg|0x1fffff|shr'),
    Repro('3', 'control: instance field store, barrier removed', 'Registry', 'void Issue3_SetInstanceField(',
          'Registry_Issue3_SetInstanceField', 'Item = item;',
          all_of(no('__atomic_compare_exchange'), has('Item = item')), native=r'cmpxchg'),
    Repro('1', 'new int[n] calls Array::NewSpecific directly', 'Issue1', 'int[] Issue1_NewIntArray(',
          'Issue1_Issue1_NewIntArray', 'return new int[n];',
          all_of(no('__native_', '__klass()'), has('new int[')), native=r'call|jmp'),
    Repro('1', 'new string[n] calls Array::NewSpecific directly', 'Issue1', 'string[] Issue1_NewStringArray(',
          'Issue1_Issue1_NewStringArray', 'string[] a = new string[n];\nif (n > 0) a[0] = "first";\nreturn a;',
          all_of(no('__native_', '__klass()'), has('new string[')), native=r'call|jmp'),
    Repro('4', 'signed modulo by a power of two', 'Issue4', 'int Issue4_Mod4(',
          'Issue4_Issue4_Mod4', 'return ticks % 4;', has('% 4'), native=r'.'),
    Repro('29', 'switch through a byte-indexed jump table', 'Issue29', 'void Issue29_ByteTable(',
          'Issue29_Issue29_ByteTable', 'switch (tile) { case 0: case 1: ... Walk(tile); break; case 3: ... Climb(tile); break; case 6: ... Swim(tile); break; }',
          all_of(no('__call_indirect', '__Rewind.Opaque', '__Rewind.Effect'),
                 cases_reach({'Walk': [0, 1, 2, 5, 8, 9, 13, 21, 22, 29], 'Climb': [3, 4, 10, 11, 17, 18, 25],
                              'Swim': [6, 7, 14, 15, 16, 23, 24, 28]})),
          native=r'movzx|cmp|jmp|lea\s+r..?, \[rip'),
    Repro('29', 'switch through a byte-indexed jump table, values from 400', 'Issue29', 'void Issue29_ByteTableFrom400(',
          'Issue29_Issue29_ByteTableFrom400', 'switch (id) { case 400: ... Walk(id); break; case 402: ... Climb(id); break; case 406: ... Swim(id); break; }',
          all_of(no('__call_indirect', '__Rewind.Opaque', '__Rewind.Effect'),
                 cases_reach({'Walk': [400, 401, 404, 405, 409, 412, 413, 420, 427], 'Climb': [402, 403, 410, 411, 418, 419, 426],
                              'Swim': [406, 407, 414, 415, 416, 422, 423, 429]})),
          native=r'movzx|cmp|add|sub|jmp|lea\s+r..?, \[rip'),
    Repro('30', 'control: zero stored after a barrier, after other stores (barrier inlined)', 'Issue30', 'void Issue30_ZeroAfterStores(',
          'Issue30_Issue30_ZeroAfterStores', 'o.A = a; ... o.D = d;\no.StopPoint = null;\no.Name = 231;\no.Type = 0;\no.Important = true;',
          all_of(no('__Rewind.Opaque', '__Rewind.Effect'), has('Type = 0;')), native=r'.'),
    Repro('30', 'control: zero stored after a barrier, in a loop (barrier inlined)', 'Issue30', 'void Issue30_ZeroInLoop(',
          'Issue30_Issue30_ZeroInLoop', 'Issue30 o = all[i];\no.StopPoint = null;\no.Type = 0;\no.Important = true;',
          all_of(no('__Rewind.Opaque', '__Rewind.Effect'), has('Type = 0;')), native=r'.'),
    Repro('30', 'control: double stored after a barrier, after other stores (barrier inlined)', 'Issue30', 'void Issue30_DoubleAfterStores(',
          'Issue30_Issue30_DoubleAfterStores', 'double top = y + offset;\nif (top <= o.Y) { o.Platform = platform; o.Grounded = true; o.Y = top - 1.0; }',
          all_of(no('__Rewind.Opaque', '__Rewind.Effect'), matches(r'Y = \(?(y \+ offset|\w+) - 1\.0')), native=r'.'),
    Repro('30', 'double kept in an xmm register across the write barrier, in a loop', 'Issue30', 'bool Issue30_DoubleInLoop(',
          'Issue30_Issue30_DoubleInLoop', 'if (o != null && top <= o.Y) { o.Platform = platform; o.Grounded = true; o.Y = top - 1.0; return true; }',
          all_of(no('__Rewind.Opaque', '__Rewind.Effect'), matches(r'Y = \(?(y \+ offset|\w+) - 1\.0')), native=r'.'),
    Repro('30', 'reference kept in r9 across the write barrier, in a loop', 'Issue30', 'bool Issue30_ReferenceInLoop(',
          'Issue30_Issue30_ReferenceInLoop', 'Issue30 s = sprites[i];\nif (s != null && s.Collider == null) { s.Collider = this; Cover = s; return true; }',
          all_of(no('__Rewind.Opaque', '__Rewind.Effect'), matches(r'Cover = (?!null)\w+;')), native=r'.'),
]


class Output:
    """Rewind's C# for the Repros assembly, its IL (RewindStats) and its recovery report."""

    def __init__(self, out):
        self.files = {p.stem: p.read_text(encoding='utf-8') for p in (out / 'src/Repros').rglob('*.cs')}
        self.out = out
        self._il = None

    def method(self, type_name, member):
        if type_name not in self.files:
            raise KeyError(f'no C# file for type {type_name}')
        lines = self.files[type_name].splitlines()
        for i, line in enumerate(lines):
            if member in line and not line.strip().startswith('//'):
                body, depth, opened = [], 0, False
                for l in lines[i:]:
                    body.append(l)
                    depth += l.count('{') - l.count('}')
                    opened = opened or '{' in l
                    if (opened and depth <= 0) or (not opened and l.rstrip().endswith(';')):
                        break
                return dedent(body)
        raise KeyError(f'{type_name}: no member matching {member!r}')

    def il(self, type_name, member):
        if self._il is None:
            if not STATS.exists():
                subprocess.run([str(DOTNET), 'build', '-c', 'Release', str(REPO / 'tools/assess/RewindStats'), '-o',
                                str(STATS.parent), '--source', 'https://api.nuget.org/v3/index.json'],
                               check=True, capture_output=True)
            text = subprocess.run([str(DOTNET), str(STATS), 'il', str(self.out / 'assemblies'), 'Repros', 'Repros.'],
                                  capture_output=True, text=True, check=True).stdout
            self._il = {}
            current = None
            for line in text.splitlines():
                if not line.startswith(' '):
                    current = line
                    self._il[current] = []
                elif current:
                    self._il[current].append(line)
        name = member.split('(')[0].split()[-1]
        for head, lines in self._il.items():
            if f'Repros.{type_name}::{name}(' in head:
                return head + '\n' + '\n'.join(lines)
        raise KeyError(f'no IL for {type_name}::{name}')


def dedent(lines):
    indent = min((len(l) - len(l.lstrip('\t')) for l in lines if l.strip()), default=0)
    return '\n'.join(l[indent:] for l in lines)


def run_rewind(rewind, game, out, sig):
    exe = Path(rewind)
    if exe.is_dir():
        exe = exe / 'rewind.exe'
    if not exe.exists():
        sys.exit(f'no rewind.exe at {exe}')
    out.mkdir(parents=True, exist_ok=True)
    log = out / 'decompile.log'
    with open(log, 'w', encoding='utf-8') as handle:
        code = subprocess.run([str(exe), 'decompile', str(game), '-o', str(out), '--signatures', str(sig),
                               '--assembly', 'Repros'], stdout=handle, stderr=subprocess.STDOUT).returncode
    if code != 0:
        sys.exit(f'rewind exited {code}, see {log}')
    return exe


def native_excerpt(build_dir, symbol, focus, limit=60):
    import disasm
    by_rva, names = disasm.load_symbols(build_dir)
    hits = [(rva, n) for rva, n in names if n.startswith(symbol + '_m') or n == symbol]
    if not hits:
        return f'(no symbol {symbol})'
    rva, name = hits[0]
    text = disasm.disassemble(build_dir, rva, by_rva)
    lines = [l for l in text.splitlines() if l.strip() and not l.startswith('|')]
    if len(lines) > limit and focus:
        keep = set()
        for i, l in enumerate(lines):
            if re.search(focus, l):
                keep.update(range(max(1, i - 3), min(len(lines), i + 4)))
        keep.add(0)
        picked, last = [], -1
        for i in sorted(keep):
            if last >= 0 and i != last + 1:
                picked.append('...')
            picked.append(lines[i])
            last = i
        lines = picked[:limit] + (['...'] if len(picked) > limit else [])
    return f'; {name} @ {0x180000000 + rva:#x}\n' + '\n'.join(lines)


def main():
    argv = sys.argv[1:]
    opts = {}
    for flag in ('--out', '--work', '--sig', '--report'):
        if flag in argv:
            at = argv.index(flag)
            opts[flag] = Path(argv[at + 1])
            del argv[at:at + 2]
    only = None
    if '--only' in argv:
        at = argv.index('--only')
        only = set(argv[at + 1:])
        del argv[at:]
    if len(argv) != 1:
        sys.exit(__doc__)
    rewind = Path(argv[0]).resolve()
    built = build(opts.get('--work', REPO / 'work/rewind-repros/build'))
    tree = next((p.name for p in rewind.parents if p.parent.name == 'rewind-wt'), None)
    tree = tree or next((p.name for p in rewind.parents if (p / '.git').exists()), 'rewind')
    out = opts.get('--out', REPO / 'work/rewind-repros/check' / tree).resolve()
    sig = opts.get('--sig', DEFAULT_SIG)
    run_rewind(rewind, built / 'game', out, sig)
    output = Output(out)
    failed = ran = 0
    summary = ['| Issue | Repro | Method | Result |', '| --- | --- | --- | --- |']
    report =[f'# Rewind repro results\n\nRewind: `{rewind}`  \nSignatures: `{Path(sig).name}`  \n'
              f'Repro build: `{built / "game"}`  \n\n'] if '--report' in opts else None
    for r in REPROS:
        if only and r.issue not in only:
            continue
        ran += 1
        text = il = ''
        try:
            try:
                text = output.method(r.type_name, r.member)
            except KeyError:
                if r.il != 'check':
                    raise
                text = '(not shown in the C#)'
            il = output.il(r.type_name, r.member) if r.il else ''
            passed, detail = r.check(il if r.il == 'check' else text)
        except KeyError as e:
            passed, detail = False, f'missing: {e}'
        failed += not passed
        verdict = 'PASS' if passed else 'FAIL'
        print(f'{verdict}  {r.issue:<5} {r.name}' + (f'\n            {detail}' if detail else ''), flush=True)
        summary.append(f'| {r.issue} | {r.name} | `{r.type_name}` `{r.member.strip("(").split()[-1]}` | {verdict} |')
        if report is not None:
            report.append(f'## {r.issue}: {r.name} ({verdict})\n\nRepro: `{r.type_name}` `{r.member.strip("(")}`'
                          f'{" " + detail if detail else ""}\n\nExpected:\n\n```csharp\n{r.expected}\n```\n\n'
                          f'Rewind:\n\n```csharp\n{text}\n```\n\n')
            if il:
                report.append(f'Rewind IL:\n\n```\n{il}\n```\n\n')
            if r.native:
                report.append(f'Native:\n\n```\n{native_excerpt(built, r.symbol, r.native)}\n```\n\n')
    print(f'{ran - failed} passed, {failed} failed; Rewind output in {out}')
    if report is not None:
        report.insert(1, 'PASS means fixed, FAIL means the issue still reproduces.\n\n' + '\n'.join(summary) + '\n\n')
        Path(opts['--report']).write_text(''.join(report), encoding='utf-8')
        print(f'report: {opts["--report"]}')
    sys.exit(failed)


if __name__ == '__main__':
    main()
