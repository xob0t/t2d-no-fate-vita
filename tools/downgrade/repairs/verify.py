"""Check repaired methods: no markers left, and ILVerify's errors before and after.

    python -I verify.py --ilverify ilverify.exe --repairs Repairs.exe --refs <Rewind assemblies dir>
                        <before Assembly-CSharp.dll> <after Assembly-CSharp.dll> <Type::method> [...]

Per method: markers before and after (Repairs markers), and ILVerify's errors split into gone (the
repair removed them), kept (they were there before: Rewind's other problems, such as inlined engine
plumbing, which other rules handle) and new (the repair's own: the run fails).
Exit code 1 if any method has a marker left or a new error. The run stops with a message when ILVerify
or Repairs fails (an exit code without the output that explains it), rather than read that as no errors
or no markers.
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
    run = subprocess.run(args, capture_output=True, text=True, check=False)
    return parse_ilverify(run, args, dll)


# "[IL]: Error [StackUnexpected]: [<path> : Type::Method(...)][offset 0x0000000C] message", the offset missing for an
# error that has none (a type or a signature that doesn't load), and "[MD]: ..." for metadata.
ERROR = re.compile(r'^\[(\w+)\]: Error \[(\w+)\]: \[[^\]]*? : ([^\]]+?)\](?:\[offset 0x[0-9A-Fa-f]+\])?(.*)$')
# ILVerify's last line: "<n> Error(s) Verifying <path>", or "All Classes and Methods in <path> Verified."
SUMMARY = re.compile(r'^(?:(\d+) Error\(s\) Verifying |All Classes and Methods in )(.+?)(?: Verified\.)?$')


def parse_ilverify(run, args, dll):
    """{method: [error, ...]} from an ILVerify run. Accepted only when the run finished (its summary line, for this
    assembly) and every error it counted was read: before (Nov 2026) any non-zero exit passed once one error with an
    offset was read, so a run that stopped part way, or errors without an offset, went unseen."""
    errors, read = {}, 0
    summary = None
    for line in run.stdout.splitlines():
        line = line.rstrip()
        found = SUMMARY.match(line)
        if found and os.path.basename(found.group(2)).lower() == os.path.basename(dll).lower():
            summary = int(found.group(1) or 0)
            continue
        m = ERROR.match(line)
        if m:
            read += 1
            # offsets move when IL is replaced: an error is its message and its place among the method's errors
            message = re.sub(r'\s+', ' ', m.group(4)).strip()
            errors.setdefault(m.group(3), []).append(f'[{m.group(2)}] {message}')
        elif line.startswith('[') and ']: Error' in line:
            fail('ILVerify (an error this script cannot read)', args, run, line)
    if summary is None:
        fail('ILVerify (no summary line: it did not finish)', args, run)
    if read != summary:
        fail(f'ILVerify (it counted {summary} errors, {read} were read)', args, run)
    if (run.returncode != 0) != (summary > 0):
        fail('ILVerify (its exit code and its error count disagree)', args, run)
    return errors


def fail(tool, args, run, tail=None):
    tail = tail or '\n'.join((run.stderr or run.stdout or '').strip().splitlines()[-10:])
    sys.exit(f'{tool} failed (exit code {run.returncode}) on {" ".join(args[:2])}:\n{tail}')


def matches(key, name):
    """Whether a Repairs method key ("Type::m(Int32)") is the method given: the same key when it has a signature,
    else any overload of that name."""
    return key == name if '(' in name else key.split('(')[0] == name


def methods(exe, dll, name):
    """The methods of dll that name names (matches), from `Repairs methods`."""
    args = [exe, 'methods', dll, name.split('(')[0]]
    run = subprocess.run(args, capture_output=True, text=True, check=False)
    if run.returncode != 0:
        fail('Repairs methods', args, run)
    return [line for line in run.stdout.splitlines() if matches(line, name)]


def markers(exe, dll, name):
    args = [exe, 'markers', dll, name.split('(')[0]]
    run = subprocess.run(args, capture_output=True, text=True, check=False)
    if run.returncode != 0:  # no markers read is not no markers
        fail('Repairs markers', args, run)
    # before (Nov 2026) the key had to equal the name as given, so a name without its signature counted no markers
    return [l for l in run.stdout.splitlines() if l.split('	')[1:2] != ['-'] and matches(l.split('	')[0], name)]


def compare(before, after):
    """(kept, new) over ILVerify's errors per method key (an overload apart from the others): an error that moved
    from one overload to another is new in the second. Before (Nov 2026) the overloads' errors were pooled."""
    kept, new = [], []
    for key in sorted(set(before) | set(after)):
        left = list(before.get(key, []))
        for e in after.get(key, []):
            if e in left:
                left.remove(e)
                kept.append(e)
            else:
                new.append(f'{key}: {e}')
    return kept, new


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
        # A method neither assembly has (a misspelt name) has no errors and no markers: it must exist in both.
        missing = [path for path in (a.before, a.after) if not methods(a.repairs, path, name)]
        if missing:
            bad += 1
            print(f'FAIL {name}: no such method in {", ".join(missing)}')
            continue
        before = {k: v for k, v in eb.items() if k.startswith(key + '(')}
        after = {k: v for k, v in ea.items() if k.startswith(key + '(')}
        kept, new = compare(before, after)
        count_before, count_after = sum(map(len, before.values())), sum(map(len, after.values()))
        gone = count_before - len(kept)
        mb, ma = len(markers(a.repairs, a.before, name)), len(markers(a.repairs, a.after, name))
        status = 'ok' if ma == 0 and not new else 'FAIL'
        bad += status != 'ok'
        print('%-4s %s: markers %d -> %d; ILVerify errors %d -> %d (gone %d, kept %d, new %d)'
              % (status, name, mb, ma, count_before, count_after, gone, len(kept), len(new)))
        for e in kept:
            print('       kept: ' + e[:150])
        for e in new:
            print('       NEW:  ' + e[:150])
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
