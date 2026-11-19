"""Find the shape of Rewind issue 31 in the game's native code: a block of one register update that some branch
targets and that falls through into a join, right after an unconditional jump to that same join. MSVC duplicates
a loop's `j++` into both paths that way, and Rewind keeps only the copy on the path with the jump.

    uv run --with capstone --with pefile python -I joins.py --dll GameAssembly.dll --methods methods.tsv [--filter s]

Prints one line a candidate: the method, the block's address and instruction. Reads the user's own files only.
"""
import argparse
import bisect

import capstone
import pefile


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dll', required=True)
    ap.add_argument('--methods', required=True)
    ap.add_argument('--filter', default='com.bitmapbureau')
    a = ap.parse_args()
    pe = pefile.PE(a.dll, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    methods = []
    for line in open(a.methods, encoding='utf-8'):
        f = line.rstrip('\n').split('\t')
        if len(f) > 1 and f[0].startswith('0x'):
            methods.append((int(f[0], 16), f[1]))
    methods.sort()
    starts = [m[0] for m in methods]
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    seen = set()
    for i, (va, name) in enumerate(methods):
        if a.filter not in name or va in seen:
            continue
        seen.add(va)
        end = next((s for s in starts[i + 1:] if s > va), va + 0x4000)
        size = min(end - va, 0x20000)
        data = pe.get_data(va - base, size)
        ins = list(md.disasm(data, va))
        targets = set()
        for x in ins:
            if x.mnemonic.startswith('j') and x.op_str.startswith('0x'):
                targets.add(int(x.op_str, 16))
        for k in range(1, len(ins) - 1):
            prev, cur, nxt = ins[k - 1], ins[k], ins[k + 1]
            if prev.mnemonic != 'jmp' or not prev.op_str.startswith('0x') or int(prev.op_str, 16) != nxt.address:
                continue
            if cur.address not in targets:
                continue
            if cur.mnemonic not in ('inc', 'dec', 'add', 'sub', 'lea', 'mov', 'xor'):
                continue
            print(f'{name}\t0x{cur.address:X}\t{cur.mnemonic} {cur.op_str}\tthen {nxt.mnemonic} {nxt.op_str}')


if __name__ == '__main__':
    main()
