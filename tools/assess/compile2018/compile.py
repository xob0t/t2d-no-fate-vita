"""Compile a folder of C# (Rewind's source for one assembly) with the .NET SDK's Roslyn against Unity
2018.2's unityaot class libraries and engine modules, with no Unity 6 references.

    python -I compile.py --name Assembly-CSharp --src <dir> [--src <dir|file> ...] [--ref <dll> ...]
                         --out <dir> [--langversion 7.3] [--unity <Editor/Data>] [--dotnet <dotnet.exe>]

Writes <out>/<name>.rsp, <out>/<name>.dll (if it compiles) and <out>/<name>.log (csc's output, one
diagnostic per line). Exit code: csc's.
"""
import argparse
import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
DEFAULT_UNITY = os.path.join(
    os.path.dirname(REPO), 'huntdown-vita', 'tools', 'external', 'unity-2018.2.19f1', 'Editor', 'Data'
)
DEFAULT_DOTNET = os.path.join(REPO, 'tools', 'external', 'dotnet10', 'dotnet.exe')


def unity_references(data):
    """unityaot's class libraries and Facades, and the engine modules (Managed/UnityEngine/*.dll, whose
    UnityEngine.dll only forwards; not the monolithic Managed/UnityEngine.dll, which would clash)."""
    aot = os.path.join(data, 'MonoBleedingEdge', 'lib', 'mono', 'unityaot')
    refs = sorted(glob.glob(os.path.join(aot, '*.dll')))
    refs += sorted(glob.glob(os.path.join(aot, 'Facades', '*.dll')))
    refs += sorted(glob.glob(os.path.join(data, 'Managed', 'UnityEngine', '*.dll')))
    return refs


def find_csc(dotnet):
    found = glob.glob(os.path.join(os.path.dirname(dotnet), 'sdk', '*', 'Roslyn', 'bincore', 'csc.dll'))
    if not found:
        sys.exit('no csc.dll under ' + os.path.dirname(dotnet))
    return sorted(found)[-1]


def sources(paths):
    for path in paths:
        if os.path.isfile(path):
            yield os.path.abspath(path)
        else:
            for root, _, files in os.walk(path):
                for f in sorted(files):
                    if f.endswith('.cs'):
                        yield os.path.abspath(os.path.join(root, f))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--name', required=True)
    ap.add_argument('--src', action='append', required=True)
    ap.add_argument('--ref', action='append', default=[])
    ap.add_argument('--out', required=True)
    ap.add_argument('--langversion', default='7.3')
    ap.add_argument('--unity', default=DEFAULT_UNITY)
    ap.add_argument('--dotnet', default=DEFAULT_DOTNET)
    ap.add_argument('--stub', action='store_true', help='add the __Rewind stub (RewindStub.cs)')
    a = ap.parse_args()

    os.makedirs(a.out, exist_ok=True)
    srcs = list(sources(a.src))
    if a.stub:
        srcs.append(os.path.join(HERE, 'RewindStub.cs'))
    # An explicit reference replaces a Unity one with the same file name.
    explicit = {os.path.basename(r).lower() for r in a.ref}
    refs = [r for r in unity_references(a.unity) if os.path.basename(r).lower() not in explicit]
    refs += [os.path.abspath(r) for r in a.ref]
    rsp = os.path.join(a.out, a.name + '.rsp')
    lines = [
        '-nologo', '-nostdlib+', '-unsafe+', '-target:library', '-deterministic+',
        '-langversion:' + a.langversion, '-warn:0', '-fullpaths',
        '-out:' + os.path.abspath(os.path.join(a.out, a.name + '.dll')),
    ]
    lines += ['-r:"%s"' % r for r in refs]
    lines += ['"%s"' % s for s in srcs]
    with open(rsp, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    log = os.path.join(a.out, a.name + '.log')
    with open(log, 'w', encoding='utf-8') as f:
        code = subprocess.call([a.dotnet, find_csc(a.dotnet), '-noconfig', '@' + rsp], stdout=f, stderr=subprocess.STDOUT)
    with open(log, encoding='utf-8', errors='replace') as f:
        errors = sum(1 for line in f if ': error ' in line)
    print('%s: csc exit %d, %d errors, %d sources; log %s' % (a.name, code, errors, len(srcs), log))
    return code


if __name__ == '__main__':
    sys.exit(main())
