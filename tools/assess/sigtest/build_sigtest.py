"""Build SigTest.cs into IL2CPP Windows x64 players with symbols, without the Unity editor, for Rewind signature
libraries.

    python -I tools/assess/sigtest/build_sigtest.py <unity Editor/Data> <work dir> [--msvc <toolset dir>] <config>...

Configs (the editor's names; "ReleasePlus" is what the editor passes to il2cpp for C++ Compiler Configuration
"Master": whole-program optimization, /GL and /LTCG):

    master        Master, incremental GC (time slice 3 ms)            - Terminator 2D's configuration
    master-size   Master, incremental GC, IL2CPP Code Generation "Optimize for size" (full generic sharing)
    release       Release, incremental GC
    master-nogc   Master, incremental GC off

SigTestTables.cs is generated into the work dir by gen_tables.py (big methods of array stores, see there).

Steps, as the editor's player build program does them (PlayerBuildProgramLibrary, read with ILSpy): compile
SigTest.cs with the editor's Roslyn against the unityaot-win32 class libraries and the IL2CPP player's engine
modules, strip with UnityLinker, then convert and compile with il2cpp.exe and its Bee build of libil2cpp, with
null checks and array bounds checks on as the editor sets them. Each config ends up as a folder Rewind reads like
a game: <work>/<config>/game/GameAssembly.dll, SigTest_Data/il2cpp_data/Metadata/global-metadata.dat and
SigTest_Data/Resources/unity_builtin_extra (the editor's copy, which carries the Unity version), plus
GameAssembly.pdb and symbols.tsv (tools/assess/pdbsyms.py) next to the folder.

The C++ compiler matters as much as the Unity version: signatures compare code bytes, and Terminator 2D was
built with MSVC 14.50 (cl 19.50.35729, from GameAssembly.dll's Rich header) where this machine has 14.44
installed. With --msvc <…/VC/Tools/MSVC/14.50.xxxxx> (a portable toolset), il2cpp builds as usual with the
installed one, then replay_msvc.py recompiles and relinks every C++ step of the same Bee build with the given
toolset, and that is the GameAssembly.dll/.pdb that is kept.

Two quirks of this machine are handled here. The shell sets NoDefaultCurrentDirectoryInExePath=1, and the
user's cmd AutoRun runs doskey; Bee runs every compile through "cmd.exe /c" with a PATH holding only MSVC and
the Windows SDK, so the AutoRun fails and cmd returns 1 although cl succeeded. il2cpp therefore runs with that
variable removed and with a no-op doskey.cmd in its working directory (the work dir). Nothing outside the work
dir is changed.
"""
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

CONFIGS = {
    'master': dict(configuration='ReleasePlus', gc=3, generics=None),
    'master-size': dict(configuration='ReleasePlus', gc=3, generics='EnableFullSharing'),
    'release': dict(configuration='Release', gc=3, generics=None),
    'master-nogc': dict(configuration='ReleasePlus', gc=0, generics=None),
}
HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent.parent


def run(command, cwd=None, env=None, log=None):
    print('>', ' '.join(str(c) for c in command[:3]), '...', flush=True)
    with open(log, 'w', encoding='utf-8') if log else open(os.devnull, 'w') as out:
        code = subprocess.run([str(c) for c in command], cwd=cwd, env=env, stdout=out if log else None,
                              stderr=subprocess.STDOUT if log else None).returncode
    if code != 0:
        sys.exit(f'exit {code}: {command[0]}' + (f' (log: {log})' if log else ''))


def main():
    argv = sys.argv[1:]
    msvc = None
    if '--msvc' in argv:
        at = argv.index('--msvc')
        msvc = Path(argv[at + 1]).resolve()
        del argv[at:at + 2]
    if len(argv) < 3 or any(c not in CONFIGS for c in argv[2:]):
        sys.exit(__doc__)
    data = Path(argv[0]).resolve()
    work = Path(argv[1]).resolve()
    work.mkdir(parents=True, exist_ok=True)
    bcl = data / 'MonoBleedingEdge/lib/mono/unityaot-win32'
    engines = data / 'PlaybackEngines/windowsstandalonesupport'
    managed_engine = engines / 'Variations/il2cpp/Managed'
    variation = engines / 'Variations/win64_player_nondevelopment_il2cpp'
    deploy = data / 'il2cpp/build/deploy'

    managed = work / 'managed'
    managed.mkdir(exist_ok=True)
    run([sys.executable, '-I', HERE / 'gen_tables.py', work / 'SigTestTables.cs'])
    run([data / 'NetCoreRuntime/dotnet.exe', data / 'DotNetSdkRoslyn/csc.dll', '-nologo', '-nostdlib', '-noconfig',
         '-unsafe', '-optimize+', '-debug:portable', '-langversion:9', '-target:library',
         f'-out:{managed / "SigTest.dll"}', f'-r:{bcl / "mscorlib.dll"}', f'-r:{bcl / "System.dll"}',
         f'-r:{bcl / "System.Core.dll"}', f'-r:{bcl / "Facades/netstandard.dll"}',
         f'-r:{managed_engine / "UnityEngine.CoreModule.dll"}', f'-r:{managed_engine / "UnityEngine.InputLegacyModule.dll"}',
         HERE / 'SigTest.cs', work / 'SigTestTables.cs'])
    linked = work / 'linked'
    shutil.rmtree(linked, ignore_errors=True)
    run([deploy / 'UnityLinker.exe', f'--search-directory={managed},{managed_engine},{bcl},{bcl / "Facades"}',
         f'--out={linked}', f'--include-unity-root-assembly={managed / "SigTest.dll"}', '--rule-set=Minimal',
         '--core-action=link', '--dotnetprofile=unityaot-win32', '--dotnetruntime=il2cpp', '--platform=WindowsDesktop',
         '--architecture=x64', f'--descriptor-directory={data / "il2cpp/LinkerDescriptors"}', '--strip-security',
         '--i18n=none', '--silent'], log=work / 'unitylinker.log')

    (work / 'doskey.cmd').write_bytes(b'@rem No-op stand-in for doskey, which the cmd AutoRun calls (see build_sigtest.py).\r\n@exit /b 0\r\n')
    env = {k: v for k, v in os.environ.items() if k.lower() != 'nodefaultcurrentdirectoryinexepath'}
    for name in argv[2:]:
        config = CONFIGS[name]
        out = work / name
        shutil.rmtree(out, ignore_errors=True)
        out.mkdir()
        command = [deploy / 'il2cpp.exe', '--convert-to-cpp', '--compile-cpp', f'--directory={linked}',
                   f'--generatedcppdir={out / "cpp"}', f'--data-folder={out / "Data"}', f'--symbols-folder={out / "symbols"}',
                   f'--outputpath={out / "build/GameAssembly.dll"}', f'--cachedirectory={out / "cache"}',
                   '--platform=WindowsDesktop', '--architecture=x64', f'--configuration={config["configuration"]}',
                   f'--baselib-directory={variation}', f'--incremental-g-c-time-slice={config["gc"]}',
                   '--emit-null-checks', '--enable-array-bounds-check', '--dotnetprofile=unityaot-win32']
        if config['generics']:
            command.append(f'--generics-option={config["generics"]}')
        run(command, cwd=work, env=env, log=out / 'il2cpp.log')
        built = out / 'build'
        if msvc:
            dag = json.load(open(out / 'cache/buildstate/bee.dag.json', encoding='utf-8'))
            link = next(n for n in dag['Nodes'] if n.get('Annotation', '').startswith('Link_Win'))
            installed = re.match(r'"?(.+?)[\\/]bin[\\/]Host', link['Action']).group(1)
            run([sys.executable, '-I', HERE / 'replay_msvc.py', out / 'cache', installed, msvc], log=out / 'replay.log')
            built = Path(next(o for o in link['Outputs'] if o.endswith('.dll'))).parent
        game = out / 'game'
        (game / 'SigTest_Data/il2cpp_data/Metadata').mkdir(parents=True)
        (game / 'SigTest_Data/Resources').mkdir(parents=True)
        shutil.copy2(built / 'GameAssembly.dll', game)
        shutil.copy2(out / 'Data/Metadata/global-metadata.dat', game / 'SigTest_Data/il2cpp_data/Metadata')
        # Only for the Unity version Rewind reads. The player's "unity default resources" is stamped 6000.0.51f1
        # and Rewind would prefer it, so it is left out; the editor's unity_builtin_extra carries 6000.0.60f1.
        shutil.copy2(data / 'Resources/unity_builtin_extra', game / 'SigTest_Data/Resources')
        shutil.copy2(built / 'GameAssembly.pdb', out)
        run([sys.executable, '-I', REPO / 'tools/assess/pdbsyms.py', out / 'GameAssembly.pdb', out / 'symbols.tsv'])
        shutil.rmtree(out / 'cache')  # several GB of objects
        print(f'{name}: {out / "game"}', flush=True)


if __name__ == '__main__':
    main()
