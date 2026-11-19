"""Read each `new T[n]` of the given methods from the native code, for the repair engine.

    python -I array_facts.py --dll GameAssembly.dll --methods methods.tsv --usages usages.tsv
                             [--alloc 0x1800E2FD0] --out facts.tsv [--from-rules rules/Initializers.cs] [<Type::method> ...]

IL2CPP allocates an array with Array::NewSpecific(klass, n): rcx is the array class, loaded from its
metadata-usage slot just before (`mov rcx, [rip+TI_int[]]`), edx the length. Rewind leaves the call
unidentified with the class as `__klass()`, so the element type is lost; the length it keeps.
This reads both per call, in address order (the order of Rewind's markers in straight-line
initializers), and writes one line per call: method, index, call address, array class, length
(empty when not a constant). The engine (Engine.NewArraysFromNative) checks each against its marker
and fails on any mismatch in count or length.

The facts are the user's, made at build time from their GameAssembly.dll and written under work/;
the repository holds only this reader. usages.tsv is tools/assess/ghidra/il2cpp_usages.py's table.
"""
import argparse
import bisect

import capstone
import pefile


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dll', required=True)
    ap.add_argument('--methods', required=True)
    ap.add_argument('--usages', required=True)
    ap.add_argument('--alloc', default='0x1800E2FD0')
    ap.add_argument('--out', required=True)
    ap.add_argument('--from-rules', help='take the methods from a rules file (rules/Initializers.cs)')
    ap.add_argument('method', nargs='*')
    a = ap.parse_args()
    if a.from_rules:
        import re
        a.method += [m.split('(')[0] for m in re.findall(r'\{ "([^"]+::[^"]+)", "[0-9a-f]{8}" \}', open(a.from_rules, encoding='utf-8').read())]
    alloc = int(a.alloc, 16)

    pe = pefile.PE(a.dll, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    methods = []
    for line in open(a.methods, encoding='utf-8'):
        f = line.rstrip('\n').split('\t')
        if len(f) > 1 and f[0].startswith('0x'):
            methods.append((int(f[0], 16), f[1]))
    methods.sort()
    starts = [m[0] for m in methods]
    labels = {}
    for line in open(a.usages, encoding='utf-8'):
        f = line.rstrip('\n').split('\t')
        if len(f) >= 4 and f[3].startswith('TI_'):
            labels[int(f[0], 16)] = f[3][3:]
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    out = open(a.out, 'w', encoding='utf-8', newline='\n')
    for want in a.method:
        hits = [(va, n) for va, n in methods if n == want or n.endswith('.' + want)]
        if len(hits) != 1:
            raise SystemExit('%s: %d methods match' % (want, len(hits)))
        va, name = hits[0]
        i = bisect.bisect_right(starts, va)
        end = starts[i] if i < len(starts) else va + 0x100000
        rcx, edx, n = None, None, 0
        for ins in md.disasm(pe.get_data(va - base, end - va), va):
            ops = ins.operands
            if ins.mnemonic == 'mov' and len(ops) == 2 and ops[0].type == capstone.x86.X86_OP_REG:
                dst = ins.reg_name(ops[0].reg)
                if dst == 'rcx':
                    rcx = None
                    if ops[1].type == capstone.x86.X86_OP_MEM and ops[1].mem.base == capstone.x86.X86_REG_RIP:
                        rcx = labels.get(ins.address + ins.size + ops[1].mem.disp)
                elif dst in ('edx', 'rdx'):
                    edx = ops[1].imm if ops[1].type == capstone.x86.X86_OP_IMM else None
            elif ins.mnemonic == 'call':
                if ops[0].type == capstone.x86.X86_OP_IMM and ops[0].imm == alloc:
                    out.write('%s\t%d\t0x%X\t%s\t%s\n' % (name, n, ins.address, rcx or '', '' if edx is None else edx))
                    n += 1
                rcx, edx = None, None  # volatile across calls
            else:
                # any other write to rcx/edx forgets what they held
                for op in ops[:1]:
                    if op.type == capstone.x86.X86_OP_REG and ins.reg_name(op.reg) in ('rcx', 'ecx', 'rdx', 'edx', 'dl', 'cl'):
                        if ins.mnemonic not in ('cmp', 'test'):
                            if ins.reg_name(op.reg) in ('rcx', 'ecx', 'cl'):
                                rcx = None
                            else:
                                edx = None
        print('%s: %d allocations' % (name, n))


if __name__ == '__main__':
    main()
