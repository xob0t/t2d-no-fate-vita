"""Build Repros.cs into an IL2CPP Master player for Windows x64 (Unity 6000.0.60f1, MSVC 14.50), without the
Unity editor, the same way tools/assess/sigtest/build_sigtest.py builds its test project.

    python -I tools/assess/rewind-repros/build_repros.py [--work <dir>] [--force]

Defaults: Unity in tools/external/unity-6000.0.60f1, MSVC 14.50 in tools/external/msvc-14.50, output in
work/rewind-repros/build. The result is a folder Rewind reads like a game, <work>/master/game, plus
GameAssembly.pdb, symbols.tsv (public symbols, tools/assess/pdbsyms.py) and cpp/ (the generated C++) next to it.

The build is cached: <work>/master/stamp.txt holds a hash of Repros.cs and of the build scripts, and when it
matches the build is skipped. --force rebuilds anyway. A build takes about 8 minutes (il2cpp with the installed
MSVC, then every C++ step again with 14.50; see replay_msvc.py).

Steps: Roslyn compiles Repros.cs against the unityaot-win32 class libraries and the IL2CPP player's engine
modules; UnityLinker strips it; il2cpp converts and compiles it with --configuration=ReleasePlus (the editor's
"Master": /GL and /LTCG), incremental GC, null checks and array bounds checks on; replay_msvc.py redoes the C++
build with MSVC 14.50, the compiler the game where these issues were found was built with.
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent.parent
SIGTEST = REPO / 'tools/assess/sigtest'
sys.path.insert(0, str(SIGTEST))
from build_sigtest import CONFIGS, run  # noqa: E402

UNITY_DATA = REPO / 'tools/external/unity-6000.0.60f1/Editor/Data'
MSVC_ROOT = REPO / 'tools/external/msvc-14.50'
CONFIG = 'master'


def find_msvc():
    hits = sorted(MSVC_ROOT.glob('**/VC/Tools/MSVC/14.50.*/bin'))
    if not hits:
        sys.exit(f'no MSVC 14.50 toolset under {MSVC_ROOT}')
    return hits[-1].parent


def input_hash():
    h = hashlib.sha256()
    for f in [HERE / 'Repros.cs', Path(__file__).resolve(), SIGTEST / 'build_sigtest.py', SIGTEST / 'replay_msvc.py']:
        h.update(f.name.encode() + b'\0' + f.read_bytes())
    return h.hexdigest()


def build(work, force=False):
    """Build (or reuse) the repro game; return <work>/master."""
    work = Path(work).resolve()
    out = work / CONFIG
    stamp = out / 'stamp.txt'
    digest = input_hash()
    if not force and stamp.exists() and stamp.read_text().strip() == digest and (out / 'game/GameAssembly.dll').exists():
        print(f'cached: {out / "game"}', flush=True)
        return out
    msvc = find_msvc()
    data = UNITY_DATA
    work.mkdir(parents=True, exist_ok=True)
    bcl = data / 'MonoBleedingEdge/lib/mono/unityaot-win32'
    engines = data / 'PlaybackEngines/windowsstandalonesupport'
    managed_engine = engines / 'Variations/il2cpp/Managed'
    variation = engines / 'Variations/win64_player_nondevelopment_il2cpp'
    deploy = data / 'il2cpp/build/deploy'

    managed = work / 'managed'
    shutil.rmtree(managed, ignore_errors=True)
    managed.mkdir()
    run([data / 'NetCoreRuntime/dotnet.exe', data / 'DotNetSdkRoslyn/csc.dll', '-nologo', '-nostdlib', '-noconfig',
         '-unsafe', '-optimize+', '-debug:portable', '-langversion:9', '-target:library',
         f'-out:{managed / "Repros.dll"}', f'-r:{bcl / "mscorlib.dll"}', f'-r:{bcl / "System.dll"}',
         f'-r:{bcl / "System.Core.dll"}', f'-r:{bcl / "Facades/netstandard.dll"}',
         f'-r:{managed_engine / "UnityEngine.CoreModule.dll"}', HERE / 'Repros.cs'])
    linked = work / 'linked'
    shutil.rmtree(linked, ignore_errors=True)
    run([deploy / 'UnityLinker.exe', f'--search-directory={managed},{managed_engine},{bcl},{bcl / "Facades"}',
         f'--out={linked}', f'--include-unity-root-assembly={managed / "Repros.dll"}', '--rule-set=Minimal',
         '--core-action=link', '--dotnetprofile=unityaot-win32', '--dotnetruntime=il2cpp', '--platform=WindowsDesktop',
         '--architecture=x64', f'--descriptor-directory={data / "il2cpp/LinkerDescriptors"}', '--strip-security',
         '--i18n=none', '--silent'], log=work / 'unitylinker.log')

    # See build_sigtest.py: a no-op doskey.cmd for the cmd AutoRun, and NoDefaultCurrentDirectoryInExePath removed.
    (work / 'doskey.cmd').write_bytes(b'@rem No-op stand-in for doskey, which the cmd AutoRun calls (see build_sigtest.py).\r\n@exit /b 0\r\n')
    env = {k: v for k, v in os.environ.items() if k.lower() != 'nodefaultcurrentdirectoryinexepath'}
    config = CONFIGS[CONFIG]
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir()
    command = [deploy / 'il2cpp.exe', '--convert-to-cpp', '--compile-cpp', f'--directory={linked}',
               f'--generatedcppdir={out / "cpp"}', f'--data-folder={out / "Data"}', f'--symbols-folder={out / "symbols"}',
               f'--outputpath={out / "build/GameAssembly.dll"}', f'--cachedirectory={out / "cache"}',
               '--platform=WindowsDesktop', '--architecture=x64', f'--configuration={config["configuration"]}',
               f'--baselib-directory={variation}', f'--incremental-g-c-time-slice={config["gc"]}',
               '--emit-null-checks', '--enable-array-bounds-check', '--dotnetprofile=unityaot-win32']
    run(command, cwd=work, env=env, log=out / 'il2cpp.log')
    dag = json.load(open(out / 'cache/buildstate/bee.dag.json', encoding='utf-8'))
    link = next(n for n in dag['Nodes'] if n.get('Annotation', '').startswith('Link_Win'))
    installed = re.match(r'"?(.+?)[\\/]bin[\\/]Host', link['Action']).group(1)
    run([sys.executable, '-I', SIGTEST / 'replay_msvc.py', out / 'cache', installed, msvc], log=out / 'replay.log')
    built = Path(next(o for o in link['Outputs'] if o.endswith('.dll'))).parent

    game = out / 'game'
    (game / 'Repros_Data/il2cpp_data/Metadata').mkdir(parents=True)
    (game / 'Repros_Data/Resources').mkdir(parents=True)
    shutil.copy2(built / 'GameAssembly.dll', game)
    shutil.copy2(out / 'Data/Metadata/global-metadata.dat', game / 'Repros_Data/il2cpp_data/Metadata')
    # Only for the Unity version Rewind reads (see build_sigtest.py).
    shutil.copy2(data / 'Resources/unity_builtin_extra', game / 'Repros_Data/Resources')
    shutil.copy2(built / 'GameAssembly.pdb', out)
    run([sys.executable, '-I', REPO / 'tools/assess/pdbsyms.py', out / 'GameAssembly.pdb', out / 'symbols.tsv'])
    shutil.rmtree(out / 'cache')  # several GB of objects
    stamp.write_text(digest + '\n')
    print(f'built: {game}', flush=True)
    return out


def main():
    argv = sys.argv[1:]
    force = '--force' in argv
    if force:
        argv.remove('--force')
    work = REPO / 'work/rewind-repros/build'
    if '--work' in argv:
        at = argv.index('--work')
        work = Path(argv[at + 1])
        del argv[at:at + 2]
    if argv:
        sys.exit(__doc__)
    build(work, force)


if __name__ == '__main__':
    main()
