"""Print a game method's native code next to its Rewind markers, for writing repairs.

    python -I listing.py --dll GameAssembly.dll --methods methods.tsv --usages usages.tsv
                         [--sig sig-apply.txt] [--markers markers.txt] [--dump dump.cs] <Type::method> [...]

methods.tsv and usages.tsv are the tables tools/assess/ghidra writes (dump_to_tables.py, il2cpp_usages.py);
sig-apply.txt is rewind-lab's sig-apply output, for the runtime helpers' names.
markers.txt is `Repairs.exe markers`' output for the same methods; each marker is printed once, before the
first native instruction it plausibly comes from (a call to the address it names, a vtable offset it names,
the GC flag's load), and the rest at the end.
--dump adds the method a vtable call goes to (`call [reg+0x228]` is slot 15), from the receiver's class.

Everything is read from the user's own files; nothing is written.
"""
import argparse
import bisect
import re
import struct
import sys

import capstone
import pefile

GC_FLAG = 0x1810CD090  # the incremental GC's flag the inlined write barrier tests (Steam build 24040403)


def load_methods(path):
    rows = []
    for line in open(path, encoding='utf-8'):
        f = line.rstrip('\n').split('\t')
        if len(f) > 1 and f[0].startswith('0x'):
            rows.append((int(f[0], 16), f[1]))
    rows.sort()
    return rows


def load_usages(path):
    out = {}
    for line in open(path, encoding='utf-8'):
        f = line.rstrip('\n').split('\t')
        if len(f) >= 4:
            out[int(f[0], 16)] = f[3]
    return out


def load_helpers(path):
    out = {}
    if not path:
        return out
    for line in open(path, encoding='utf-8'):
        m = re.match(r'\s+(\w+)\s+(0x[0-9A-F]+)\s', line)
        if m:
            out[int(m.group(2), 16)] = 'rt_' + m.group(1)
    return out


def load_markers(path, method):
    """Markers for one method from `Repairs markers` (lines: '<method>\t<index>\t<kind>\t<IL offset>\t<text>')."""
    out = []
    if not path:
        return out
    for line in open(path, encoding='utf-8'):
        f = line.rstrip('\n').split('\t')
        if len(f) >= 5 and f[0].split('(')[0].endswith(method):
            out.append(f[1:])
    return out


def vtable_slots(dump, cls):
    """slot -> method signature for a class and its bases, from Rewind's dump.cs."""
    if not dump:
        return {}
    src = open(dump, encoding='utf-8').read().split('\n')
    classes, cur, parent = {}, None, {}
    for i, line in enumerate(src):
        m = re.match(r'^\t*(?:public|internal|private|protected)?[\w\s]*(?:class|struct) (\S+)(?: : (\S+?)[,\s])?.*// TypeDefIndex', line)
        if m:
            cur = m.group(1)
            parent.setdefault(cur, (m.group(2) or '').split('.')[-1])
            continue
        m = re.search(r'Slot: (\d+)', line)
        if m and cur:
            sig = src[i + 1].strip()
            if sig.startswith('['):
                sig = src[i + 2].strip()
            classes.setdefault(cur, {}).setdefault(int(m.group(1)), sig)
    slots, c = {}, cls
    while c:
        for s, sig in classes.get(c, {}).items():
            slots.setdefault(s, c + ': ' + sig)
        c = parent.get(c)
    return slots


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dll', required=True)
    ap.add_argument('--methods', required=True)
    ap.add_argument('--usages', required=True)
    ap.add_argument('--sig')
    ap.add_argument('--markers')
    ap.add_argument('--dump')
    ap.add_argument('method', nargs='+')
    a = ap.parse_args()

    pe = pefile.PE(a.dll, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    methods = load_methods(a.methods)
    starts = [m[0] for m in methods]
    names = {}
    for va, name in methods:
        names.setdefault(va, name)
    usages = load_usages(a.usages)
    helpers = load_helpers(a.sig)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    def label(va):
        if va in names:
            return names[va]
        if va in helpers:
            return helpers[va]
        if va in usages:
            return usages[va]
        if va == GC_FLAG:
            return 'GC flag (write barrier)'
        return None

    for want in a.method:
        hits = [(va, n) for va, n in methods if n == want or n.endswith('.' + want) or n.endswith(want)]
        if not hits:
            print('no method ' + want)
            continue
        for va, name in hits:
            i = bisect.bisect_right(starts, va)
            end = starts[i] if i < len(starts) else va + 0x4000
            code = pe.get_data(va - base, end - va)
            cls = name.split('::')[0].split('.')[-1]
            slots = vtable_slots(a.dump, cls)
            markers = load_markers(a.markers, name.split('.')[-1] if '.' in name else name)
            shown = set()
            print('== %s at 0x%X (%d bytes)' % (name, va, end - va))

            def show_markers(pred):
                for mk in markers:
                    if mk[0] not in shown and pred(mk[3]):
                        shown.add(mk[0])
                        print('            >> marker %s %s at IL_%s: %s' % (mk[0], mk[1], mk[2], mk[3][:160]))

            prev = None
            for ins in md.disasm(code, va):
                note = []
                for op in ins.operands:
                    if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                        tgt = ins.address + ins.size + op.mem.disp
                        lab = label(tgt)
                        if lab:
                            note.append(lab)
                        elif ins.mnemonic.startswith(('movs', 'mova', 'movup', 'mulsd', 'addsd', 'subsd', 'divsd', 'comisd', 'ucomisd')):
                            note.append('%r' % struct.unpack('<d', pe.get_data(tgt - base, 8))[0])
                    elif op.type == capstone.x86.X86_OP_IMM and ins.mnemonic in ('call', 'jmp'):
                        lab = label(op.imm)
                        if lab:
                            note.append(lab)
                    elif op.type == capstone.x86.X86_OP_MEM and ins.mnemonic in ('call', 'jmp') and op.mem.disp >= 0x138:
                        slot = (op.mem.disp - 0x138) // 16
                        if (op.mem.disp - 0x138) % 16 == 0 and slot in slots:
                            note.append('slot %d %s' % (slot, slots[slot][:110]))
                text = ins.op_str
                # markers that name this instruction's call target, vtable offset or the GC flag
                if ins.mnemonic == 'call' and ins.operands and ins.operands[0].type == capstone.x86.X86_OP_IMM:
                    show_markers(lambda t, x=ins.operands[0].imm: ('0x%X' % x) in t)
                if ins.mnemonic == 'call' and ins.operands and ins.operands[0].type == capstone.x86.X86_OP_MEM:
                    show_markers(lambda t, d=ins.operands[0].mem.disp: ('+ %dL)' % d) in t)
                if 'rip' in text and any(n == 'GC flag (write barrier)' for n in note):
                    show_markers(lambda t: str(GC_FLAG) in t)
                print('  %X  %-7s %-44s%s' % (ins.address, ins.mnemonic, text, ('; ' + ' | '.join(note)) if note else ''))
                if ins.mnemonic == 'int3' and prev in ('ret', 'jmp', 'int3'):
                    break  # padding up to the next function
                prev = ins.mnemonic
            show_markers(lambda t: True)


if __name__ == '__main__':
    main()
