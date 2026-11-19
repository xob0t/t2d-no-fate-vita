"""Checks the texture rules on Rewind's Assembly-CSharp outside the pipeline: compiles atlas/compat/Atlas.cs against
Unity 2018.2's engine and unityaot class libraries with Roslyn (-nostdlib, C# 5, as T2DCompat is), compiles
RuleCheck.cs with atlas/rewriter/AtlasPatches.cs and the rewriter's Stack.cs with the .NET Framework's csc and
Mono.Cecil 0.10 (as the pipeline compiles the rewriter), and runs it (see RuleCheck.cs).

Usage: python -I check_rules.py --asm <Rewind out>/assemblies/Assembly-CSharp.dll --work <scratch dir>
                                [--external <tools/external>] [--unity <Unity 2018.2.19f1 Editor/Data>]
"""
import argparse
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ATLAS = os.path.dirname(HERE)
DOWNGRADE = os.path.dirname(ATLAS)
ROOT = os.path.dirname(os.path.dirname(DOWNGRADE))


def main_checkout():
    """The repository's main checkout (where setup put tools/external), seen from a worktree."""
    try:
        common = subprocess.run(['git', '-C', ROOT, 'rev-parse', '--git-common-dir'], capture_output=True, text=True,
                                check=True).stdout.strip()
        return os.path.dirname(os.path.abspath(os.path.join(ROOT, common)))
    except (OSError, subprocess.CalledProcessError):
        return ROOT


def first(*paths):
    for p in paths:
        if p and os.path.exists(p):
            return p
    raise SystemExit('none of these exist: %s' % (paths,))


def run(cmd):
    print('>', ' '.join(os.path.basename(c) if i == 0 else c for i, c in enumerate(map(str, cmd)))[:300], flush=True)
    r = subprocess.run([str(c) for c in cmd], capture_output=True, text=True)
    out = '\n'.join(l for l in (r.stdout + r.stderr).splitlines()
                    if l.strip() and not l.startswith(('Microsoft (R)', 'Copyright', 'This compiler', 'for C# 5', 'version')))
    if out:
        print(out)
    if r.returncode:
        raise SystemExit('exit %d' % r.returncode)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--asm', required=True)
    ap.add_argument('--work', required=True)
    ap.add_argument('--external')
    ap.add_argument('--unity')
    a = ap.parse_args()
    ext = a.external or first(os.path.join(ROOT, 'tools', 'external'), os.path.join(main_checkout(), 'tools', 'external'))
    unity = a.unity or first(os.path.join(ext, 'unity-2018.2.19f1', 'Editor', 'Data'),
                             os.path.join(os.environ.get('USERPROFILE', ''), '.t3', 'projects', 'huntdown-vita', 'tools',
                                          'external', 'unity-2018.2.19f1', 'Editor', 'Data'))
    dotnet = os.path.join(ext, 'dotnet10', 'dotnet.exe')
    roslyn = first(*[os.path.join(ext, 'dotnet10', 'sdk', v, 'Roslyn', 'bincore', 'csc.dll')
                     for v in sorted(os.listdir(os.path.join(ext, 'dotnet10', 'sdk')), reverse=True)])
    cecil = first(os.path.join(ext, 'mono.cecil-0.10.0-beta6', 'Mono.Cecil.dll'),
                  os.path.join(os.environ.get('USERPROFILE', ''), '.t3', 'projects', 'butcher-vita', 'tools', 'external',
                               'mono.cecil-0.10.0-beta6', 'Mono.Cecil.dll'))
    csc = os.path.join(os.environ['WINDIR'], 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe')
    aot = os.path.join(unity, 'MonoBleedingEdge', 'lib', 'mono', 'unityaot')
    engine = os.path.join(unity, 'Managed', 'UnityEngine')
    os.makedirs(a.work, exist_ok=True)
    atlas_dll = os.path.join(a.work, 'Atlas.dll')
    run([dotnet, roslyn, '-nologo', '-noconfig', '-nostdlib', '-langversion:5', '-target:library', '-warnaserror+',
         *['-r:' + os.path.join(aot, n) for n in ('mscorlib.dll', 'System.dll', 'System.Core.dll')],
         '-r:' + os.path.join(engine, 'UnityEngine.CoreModule.dll'), '-out:' + atlas_dll,
         os.path.join(ATLAS, 'compat', 'Atlas.cs')])
    exe = os.path.join(a.work, 'RuleCheck.exe')
    import shutil
    shutil.copy2(cecil, os.path.join(a.work, 'Mono.Cecil.dll'))
    run([csc, '-nologo', '-r:' + cecil, '-out:' + exe, os.path.join(HERE, 'RuleCheck.cs'),
         os.path.join(ATLAS, 'rewriter', 'AtlasPatches.cs'), os.path.join(DOWNGRADE, 'rewriter', 'Stack.cs')])
    r = subprocess.run([exe, a.asm, atlas_dll, os.path.join(a.work, 'Assembly-CSharp.atlas.dll')])
    sys.exit(r.returncode)


if __name__ == '__main__':
    main()
