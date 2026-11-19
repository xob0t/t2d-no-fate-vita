"""Checks the sound loading rules A1 to A3 (audio/rewriter/AudioPatches.cs) with Mono.Cecil: compiles AudioCheck.cs
with the rule files (AudioPatches.cs, atlas/rewriter/AtlasPatches.cs, whose R3 shares AssetsEmbed.staticInit) and the
rewriter's Stack.cs with the .NET Framework's csc and Mono.Cecil 0.10, as the pipeline compiles the rewriter, and runs
it (see AudioCheck.cs) on Rewind's Assembly-CSharp and on the code stage's written Recovered.T2D.

    uv run tools/downgrade/pipeline.py code          # first: writes Recovered.T2D and T2DCompat
    python -I tools/downgrade/audio/check/check_rules.py [--recovered DIR] [--written DLL] [--compat DLL]

Defaults are the pipeline's: work/recover/assemblies, work/assemblies-2018.2.19f1-vita/Recovered.T2D.dll and
work/bin/vita/T2DCompat.dll. Output: work/audio-check (the checker and the changed IL, audio-rules.il.txt).
"""

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DOWNGRADE = HERE.parents[1]
ROOT = DOWNGRADE.parents[1]
WORK = ROOT / 'work'
CECIL = ROOT / 'tools' / 'external' / 'mono.cecil-0.10.0-beta6' / 'Mono.Cecil.dll'
CSC = Path(os.environ.get('WINDIR', r'C:\Windows')) / 'Microsoft.NET' / 'Framework64' / 'v4.0.30319' / 'csc.exe'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--recovered', type=Path, default=WORK / 'recover' / 'assemblies')
    parser.add_argument('--written', type=Path, default=WORK / 'assemblies-2018.2.19f1-vita' / 'Recovered.T2D.dll')
    parser.add_argument('--compat', type=Path, default=WORK / 'bin' / 'vita' / 'T2DCompat.dll')
    parser.add_argument('--out', type=Path, default=WORK / 'audio-check')
    args = parser.parse_args()
    for path in (args.recovered / 'Assembly-CSharp.dll', args.written, args.compat, CECIL):
        if not path.exists():
            sys.exit(f'missing {path} (run pipeline.py code first)')
    args.out.mkdir(parents=True, exist_ok=True)
    shutil.copy2(CECIL, args.out / 'Mono.Cecil.dll')
    exe = args.out / 'AudioCheck.exe'
    sources = [
        HERE / 'AudioCheck.cs',
        DOWNGRADE / 'audio' / 'rewriter' / 'AudioPatches.cs',
        DOWNGRADE / 'atlas' / 'rewriter' / 'AtlasPatches.cs',
        DOWNGRADE / 'rewriter' / 'Stack.cs',
    ]
    compiled = subprocess.run(
        [str(CSC), '/nologo', f'/r:{CECIL}', f'/out:{exe}', *map(str, sources)],
        capture_output=True,
        text=True,
        check=False,
    )
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        sys.exit('the checker did not compile')
    result = subprocess.run(
        [str(exe), str(args.recovered / 'Assembly-CSharp.dll'), str(args.written), str(args.compat), str(args.out)],
        check=False,
    )
    print(f'IL: {args.out / "audio-rules.il.txt"}')
    sys.exit(result.returncode)


if __name__ == '__main__':
    main()
