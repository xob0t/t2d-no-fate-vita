"""Check repaired methods: no markers left, and ILVerify's errors before and after.

    python -I verify.py --ilverify ilverify.exe --repairs Repairs.exe --refs <Rewind assemblies dir>
                        <before Assembly-CSharp.dll> <after Assembly-CSharp.dll> <Type::method> [...]

Per method: markers before and after (Repairs markers), and ILVerify's errors split into gone (the
repair removed them), kept (they were there before: Rewind's other problems, such as inlined engine
plumbing, which other rules handle) and new (the repair's own: the run fails).
Exit code 1 if any method has a marker left or a new error.
"""
import argparse
import os
import re
import subprocess
import sys


def ilverify(exe, dll, refs, names):
    args = [exe, dll]
    for f in sorted(os.listdir(refs)):
        if f.endswith('.dll') and f != os.path.basename(dll):
            args += ['-r', os.path.join(refs, f)]
    for n in names:
        args += ['--include', re.escape(n.split('::')[-1].split('(')[0])]
    out = subprocess.run(args, capture_output=True, text=True).stdout
    errors = {}
    for line in out.splitlines():
        m = re.search(r' : ([^\]]+)\]\[offset 0x([0-9A-F]+)\](.*)', line)
        if m:
            # offsets move when IL is replaced: an error is its message and its place among the method's errors
            errors.setdefault(m.group(1), []).append(re.sub(r'\s+', ' ', m.group(3)).strip())
    return errors


def markers(exe, dll, name):
    out = subprocess.run([exe, 'markers', dll, name.split('(')[0]], capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l.split('\t')[1:2] != ['-'] and l.startswith(name.split('(')[0])]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ilverify', required=True)
    ap.add_argument('--repairs', required=True)
    ap.add_argument('--refs', required=True)
    ap.add_argument('before')
    ap.add_argument('after')
    ap.add_argument('method', nargs='+')
    a = ap.parse_args()
    a.repairs, a.ilverify = os.path.abspath(a.repairs), os.path.abspath(a.ilverify)
    eb = ilverify(a.ilverify, a.before, a.refs, a.method)
    ea = ilverify(a.ilverify, a.after, a.refs, a.method)
    bad = 0
    for name in a.method:
        key = name.split('(')[0]
        before = [e for k, v in eb.items() if k.startswith(key + '(') for e in v]
        after = [e for k, v in ea.items() if k.startswith(key + '(') for e in v]
        new = list(after)
        kept = []
        for e in before:
            if e in new:
                new.remove(e)
                kept.append(e)
        gone = len(before) - len(kept)
        mb, ma = len(markers(a.repairs, a.before, name)), len(markers(a.repairs, a.after, name))
        status = 'ok' if ma == 0 and not new else 'FAIL'
        bad += status != 'ok'
        print('%-4s %s: markers %d -> %d; ILVerify errors %d -> %d (gone %d, kept %d, new %d)'
              % (status, name, mb, ma, len(before), len(after), gone, len(kept), len(new)))
        for e in kept:
            print('       kept: ' + e[:150])
        for e in new:
            print('       NEW:  ' + e[:150])
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
