"""Checks the sprite batch's rules on Rewind's Assembly-CSharp outside the pipeline: compiles
spritebatch/compat/SpriteBatch.cs with atlas/compat/Atlas.cs against Unity 2018.2's engine and unityaot class
libraries with Roslyn (-nostdlib, C# 5, as T2DCompat is), compiles SpriteCheck.cs with
spritebatch/rewriter/SpriteBatchPatches.cs, atlas/rewriter/AtlasPatches.cs and the rewriter's Stack.cs with the .NET
Framework's csc and Mono.Cecil 0.10 (as the pipeline compiles the rewriter), and runs it (see SpriteCheck.cs).

Usage: python -I check_rules.py --asm <Rewind out>/assemblies/Assembly-CSharp.dll --work <scratch dir>
                                [--external <tools/external>] [--unity <Unity 2018.2.19f1 Editor/Data>]
"""

import argparse
import importlib.util
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SPRITEBATCH = os.path.dirname(HERE)
DOWNGRADE = os.path.dirname(SPRITEBATCH)
ATLAS = os.path.join(DOWNGRADE, 'atlas')

# atlas/check/check_rules.py's helpers (same file name, so loaded by path)
_spec = importlib.util.spec_from_file_location('atlas_check_rules', os.path.join(ATLAS, 'check', 'check_rules.py'))
_atlas = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_atlas)
ROOT, first, main_checkout, run = _atlas.ROOT, _atlas.first, _atlas.main_checkout, _atlas.run


def tools(external=None, unity=None):
    """The compilers and references, found as atlas/check/check_rules.py finds them."""
    ext = external or first(os.path.join(ROOT, 'tools', 'external'), os.path.join(main_checkout(), 'tools', 'external'))
    home = os.environ.get('USERPROFILE', '')
    unity = unity or first(
        os.path.join(ext, 'unity-2018.2.19f1', 'Editor', 'Data'),
        os.path.join(
            home, '.t3', 'projects', 'huntdown-vita', 'tools', 'external', 'unity-2018.2.19f1', 'Editor', 'Data'
        ),
    )
    sdk = os.path.join(ext, 'dotnet10', 'sdk')
    return {
        'dotnet': os.path.join(ext, 'dotnet10', 'dotnet.exe'),
        'roslyn': first(
            *[os.path.join(sdk, v, 'Roslyn', 'bincore', 'csc.dll') for v in sorted(os.listdir(sdk), reverse=True)]
        ),
        'cecil': first(
            os.path.join(ext, 'mono.cecil-0.10.0-beta6', 'Mono.Cecil.dll'),
            os.path.join(
                home,
                '.t3',
                'projects',
                'butcher-vita',
                'tools',
                'external',
                'mono.cecil-0.10.0-beta6',
                'Mono.Cecil.dll',
            ),
        ),
        'csc': os.path.join(os.environ['WINDIR'], 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
        'aot': os.path.join(unity, 'MonoBleedingEdge', 'lib', 'mono', 'unityaot'),
        'engine': os.path.join(unity, 'Managed', 'UnityEngine'),
    }


def compile_compat(t, out):
    """SpriteBatch.cs and Atlas.cs as T2DCompat compiles them: C# 5, -nostdlib, Unity 2018.2's references."""
    run(
        [
            t['dotnet'],
            t['roslyn'],
            '-nologo',
            '-noconfig',
            '-nostdlib',
            '-langversion:5',
            '-target:library',
            '-warnaserror+',
            *['-r:' + os.path.join(t['aot'], n) for n in ('mscorlib.dll', 'System.dll', 'System.Core.dll')],
            '-r:' + os.path.join(t['engine'], 'UnityEngine.CoreModule.dll'),
            '-out:' + out,
            os.path.join(ATLAS, 'compat', 'Atlas.cs'),
            os.path.join(SPRITEBATCH, 'compat', 'SpriteBatch.cs'),
        ]
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--asm', required=True)
    ap.add_argument('--work', required=True)
    ap.add_argument('--external')
    ap.add_argument('--unity')
    a = ap.parse_args()
    t = tools(a.external, a.unity)
    os.makedirs(a.work, exist_ok=True)
    compat_dll = os.path.join(a.work, 'SpriteCompat.dll')
    compile_compat(t, compat_dll)
    exe = os.path.join(a.work, 'SpriteCheck.exe')
    shutil.copy2(t['cecil'], os.path.join(a.work, 'Mono.Cecil.dll'))
    run(
        [
            t['csc'],
            '-nologo',
            '-r:' + t['cecil'],
            '-out:' + exe,
            os.path.join(HERE, 'SpriteCheck.cs'),
            os.path.join(SPRITEBATCH, 'rewriter', 'SpriteBatchPatches.cs'),
            os.path.join(ATLAS, 'rewriter', 'AtlasPatches.cs'),
            os.path.join(DOWNGRADE, 'rewriter', 'Stack.cs'),
        ]
    )
    r = subprocess.run([exe, a.asm, compat_dll, os.path.join(a.work, 'Assembly-CSharp.spritebatch.dll')], check=False)
    sys.exit(r.returncode)


if __name__ == '__main__':
    main()
