"""Disassemble functions of the repro build by (part of) their symbol name, with rizin, for checking that the
native code shows the pattern an issue is about.

    python -I tools/assess/rewind-repros/disasm.py [--build <dir>] <name part>...

<dir> defaults to work/rewind-repros/build/master (GameAssembly.dll in game/, symbols.tsv next to it). Every code
symbol whose decorated name contains a given part is listed, then disassembled. Calls are annotated with the
callee's symbol from symbols.tsv.
"""
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent.parent
BASE = 0x180000000


def load_symbols(build):
    by_rva, names = {}, []
    for line in (build / 'symbols.tsv').read_text(encoding='utf-8').splitlines():
        rva, code, name = line.split('\t', 2)
        if code == '1':
            by_rva.setdefault(int(rva, 16), name)
            names.append((int(rva, 16), name))
    return by_rva, names


def disassemble(build, rva, by_rva):
    dll = build / 'game/GameAssembly.dll'
    text = subprocess.run(['rizin', '-q', '-e', 'scr.color=0', '-e', 'asm.bytes=false', '-e', 'asm.lines=false',
                           '-e', 'asm.comments=false', '-e', 'asm.var=false', '-c', f's {BASE + rva:#x}; af; pdr', str(dll)],
                          capture_output=True, text=True, errors='replace').stdout

    def name(m):
        target = int(m.group(1), 16) - BASE
        sym = by_rva.get(target)
        return m.group(0) + (f'    ; {sym}' if sym else '')
    return re.sub(r'(?:call|jmp)\s+(?:fcn\.|sym\.)?(?:0x)?([0-9a-fA-F]{9,})', name, text)


def main():
    argv = sys.argv[1:]
    build = REPO / 'work/rewind-repros/build/master'
    if '--build' in argv:
        at = argv.index('--build')
        build = Path(argv[at + 1])
        del argv[at:at + 2]
    if not argv:
        sys.exit(__doc__)
    by_rva, names = load_symbols(build)
    for part in argv:
        hits = [(rva, n) for rva, n in names if part in n]
        if not hits:
            print(f'; no symbol contains {part!r}')
        for rva, n in hits:
            print(f'; {n} @ {BASE + rva:#x}')
            print(disassemble(build, rva, by_rva))


if __name__ == '__main__':
    main()
