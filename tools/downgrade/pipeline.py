"""Translate the Steam release of Terminator 2D: NO FATE (Unity 6000.0, IL2CPP) into a Unity 2018.2 project
and a PS Vita player.

    uv run tools/downgrade/pipeline.py recover

Stages, each runnable alone:
  recover  Rewind recovers the game's assemblies from GameAssembly.dll and global-metadata.dat into
           work/recover/assemblies, with the signature library for the game's Unity
           (tools/rewind/signatures). Assemblies only, no C# source.
  rip      AssetRipper exports the game as a 2018.2 project into work/rip-<version>, from a Mono-layout
           copy of the game in work/rip-input (its data files, Rewind's assemblies in Managed/), so the
           export keeps the method bodies.
  code     Rewrite Rewind's assemblies for the target (DowngradeAssemblies.cs, T2DCompat) into
           work/assemblies-<version>[-vita]; prints what does not resolve and the methods Rewind left
           gaps in.
  project  Assemble work/t2d-<version>[-vita]: the export with the rewritten assemblies, the settings rules
           (asset_rules.py), the shaders from tools/downgrade/shaders over AssetRipper's dummies.
  build    Build the PS Vita player into builds/t2d-<version>-vita without running Unity (tools/builder).
  package  Turn the build into builds/t2d-<version>-vita.vpk, with the LiveArea art (livearea.py).

T2D_* environment variables switch parts of the stages on and off: common.BUILD_SWITCHES lists them.

The game installation is only read: no stage runs its executables or writes into its folder. Outputs go to
work/, builds/ and artifacts/. From huntdown-vita's and butcher-vita's pipelines (MIT, same author): rip,
the code stage's tools, project's sync, package and the lock check are theirs; recover is new.
"""

import argparse
import filecmp
import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from collections import Counter
from pathlib import Path

import progress
from asset_rules import (
    COMPAT_GUID,
    exported_plugins_excluded,
    lighting_overlay,
    link_xml,
    settings_overlay,
    shader_overlay,
    time_overlay,
)
from common import (
    ARTIFACTS,
    ASSETRIPPER,
    CECIL,
    CSC,
    DEFAULT_GAME,
    GAME_EXE,
    GAME_NAME,
    HERE,
    LINK,
    REWIND_EXE,
    REWIND_SIGNATURES,
    REWIND_STAMP,
    ROOT,
    TARGET,
    VITA_HOST,
    WORK,
    build_folder,
    build_switches,
    describe_switches,
    editor_data,
    file_hash,
    game_data,
    log,
    meta_of,
    mono_cecil,
    project_folder,
    replace_path,
    require_complete,
    rewritten_folder,
    run,
    stable_meta,
    walk,
)

# ---------------------------------------------------------------- recover

RECOVER = WORK / 'recover'
RECOVERED = RECOVER / 'assemblies'  # what Rewind writes under its output folder
RECOVER_STAMP = RECOVER / 't2d-recover.txt'
METADATA = Path('il2cpp_data') / 'Metadata' / 'global-metadata.dat'
# The game's assemblies the port carries; the rest of Rewind's 44 (mscorlib, the engine modules) come from
# Unity 2018.2.
GAME_ASSEMBLIES = ['Assembly-CSharp', 'InControl', 'Steamworks']


def recover_stamp(game: Path):
    """What Rewind's output depends on: the game's code and metadata, Rewind's build and the signatures."""
    lines = [
        f'binary {file_hash(game / "GameAssembly.dll")}',
        f'metadata {file_hash(game_data(game) / METADATA)}',
        (REWIND_STAMP.read_text(encoding='utf-8') if REWIND_STAMP.exists() else 'no Rewind build').strip(),
        f'signatures {file_hash(REWIND_SIGNATURES) if REWIND_SIGNATURES.exists() else "Rewind own"}',
    ]
    return hashlib.sha256('\n'.join(lines).encode()).hexdigest()


def recover_is_current(game: Path):
    return RECOVER_STAMP.exists() and RECOVER_STAMP.read_text(encoding='utf-8').strip() == recover_stamp(game)


def recover(game: Path):
    """Rewind's decompile of the game, assemblies only, into work/recover. Its log names the signature library
    and counts the bodies it recovered completely."""
    if not REWIND_EXE.exists():
        sys.exit(f'No Rewind at {REWIND_EXE}: run uv run tools/setup.py')
    stamp = recover_stamp(game)
    if RECOVER.exists():
        with progress.step('Removing the last recovery'):
            shutil.rmtree(RECOVER)
    RECOVER.mkdir(parents=True)
    signatures = ['--signatures', REWIND_SIGNATURES] if REWIND_SIGNATURES.exists() else []
    if not signatures:
        log(f'WARNING: no {REWIND_SIGNATURES.name}; Rewind uses the nearest of its own libraries, which recovers less')
    log_path = ARTIFACTS / 'rewind.log'
    started = time.time()
    progress.run(
        'Rewind: recovering the code',
        [REWIND_EXE, 'decompile', game, '-o', RECOVER, '--no-source', *signatures],
        log_path,
    )
    missing = [name for name in GAME_ASSEMBLIES if not (RECOVERED / f'{name}.dll').exists()]
    if missing:
        sys.exit(f'Rewind wrote no {", ".join(missing)} into {RECOVERED}; see {log_path}')
    text = log_path.read_text(encoding='utf-8', errors='replace')
    for line in text.splitlines():
        if line.startswith(('runtime:', 'method bodies:', 'generic definitions:', 'note:')):
            log(f'Rewind {line.strip()}')
    RECOVER_STAMP.write_text(stamp + '\n', encoding='utf-8')
    log(f'Recovered {len(list(RECOVERED.glob("*.dll")))} assemblies in {time.time() - started:.0f} s ({RECOVERED})')


# ---------------------------------------------------------------- rip

RIP_SETTINGS = {
    'DefaultVersion': '0.0.0a0',
    'BundledAssetsExportMode': 'DirectExport',
    'ScriptContentLevel': 'Level2',
    'AudioExportFormat': 'Default',
    'ImageExportFormat': 'Png',
    'LightmapTextureExportFormat': 'Yaml',
    'SpriteExportMode': 'Native',
    'ShaderExportMode': 'Dummy',
    'TextExportMode': 'Parse',
    'ScriptLanguageVersion': 'AutoSafe',
    'ScriptExportMode': 'DllExportWithoutRenaming',
    'PreferOriginalTextureExtension': '',
}
RIP_STAMP = 't2d-rip.txt'
# The Mono-layout copy of the game AssetRipper reads. Its path stays the same from run to run: the export's
# .meta GUIDs depend on what AssetRipper loads (an export from another layout changed 986 of 1,001 of them).
RIP_INPUT = WORK / 'rip-input' / GAME_NAME
# The game's data folder holds the IL2CPP build's code and native plugins too, which the Mono layout has no use for.
RIP_INPUT_SKIPPED = {'il2cpp_data', 'Plugins'}
# AssetRipper's server gets STARTUP_POLLS tries, POLL_SECONDS apart, to answer (about a minute);
# loading the game or exporting the project is one request that can take up to REQUEST_SECONDS.
STARTUP_POLLS, POLL_SECONDS = 120, 0.5
REQUEST_SECONDS = 3600
# Its log's line for each asset exported: "ExportProgress : (1810/27794) Exporting 'SneakTalkR'".
EXPORT_COUNT = r'^ExportProgress : \((\d+)/(\d+)\) (?:Exporting )?'


def rip_input(game: Path):
    """The game as a Mono build: its data files, copied (unchanged ones are left as they are), Rewind's
    assemblies in Managed/ and an empty executable. AssetRipper then reads the recovered assemblies, with
    their method bodies, instead of writing Cpp2IL's stubs."""
    if not RECOVERED.exists():
        sys.exit(f'No recovered assemblies in {RECOVERED}: run pipeline.py recover first')
    data = RIP_INPUT / f'{GAME_NAME}_Data'
    source = game_data(game)
    overlay = {Path('Managed') / dll.name: dll for dll in RECOVERED.glob('*.dll')}
    excluded = {relative for relative in walk(source) if relative.parts[0] in RIP_INPUT_SKIPPED}
    with progress.step('Copying the game into the Mono layout') as step:
        sync_tree(source, data, overlay, excluded, step=step)
    exe = RIP_INPUT / GAME_EXE
    if not exe.exists():
        exe.write_bytes(b'')
    return RIP_INPUT


def rip_stamp(game: Path, version: str):
    """What an export depends on: the game's data files (names, sizes, dates), the recovered assemblies,
    AssetRipper's executable and the export settings."""
    exe = ASSETRIPPER
    data = game_data(game)
    files = sorted(p for p in data.rglob('*') if p.is_file() and p.relative_to(data).parts[0] not in RIP_INPUT_SKIPPED)
    lines = [f'{p.relative_to(game).as_posix()} {p.stat().st_size} {p.stat().st_mtime_ns}' for p in files]
    lines += [f'Managed/{p.name} {file_hash(p)}' for p in sorted(RECOVERED.glob('*.dll'))]
    lines.append(f'{exe.name} {exe.stat().st_size} {exe.stat().st_mtime_ns}' if exe.exists() else 'no AssetRipper')
    lines.append(repr(sorted({**RIP_SETTINGS, 'TargetVersion': version}.items())))
    return hashlib.sha256('\n'.join(lines).encode()).hexdigest()


def rip_is_current(game: Path, out: Path, version: str):
    stamp = out / RIP_STAMP
    return stamp.exists() and stamp.read_text(encoding='utf-8').strip() == rip_stamp(game, version)


def rip(game: Path, out: Path, version: str):
    exe = ASSETRIPPER
    if not exe.exists():
        sys.exit(f'No AssetRipper at {exe}: run uv run tools/setup.py')
    stamp = rip_stamp(game, version)
    folder = rip_input(game)
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    with open(ARTIFACTS / 'assetripper.log', 'w', encoding='utf-8') as log_file:
        # AssetRipper writes temp/ beside its executable; it runs from its own folder.
        server = subprocess.Popen(
            [str(exe), '--headless', '--port', str(port), '--log-path', str(ARTIFACTS / 'assetripper-file.log')],
            stdout=log_file,
            stderr=subprocess.STDOUT,
            cwd=exe.parent,
        )
        try:
            with progress.step('AssetRipper: starting'):
                for _ in range(STARTUP_POLLS):
                    try:
                        urllib.request.urlopen(base + '/', timeout=2)
                        break
                    except OSError:
                        time.sleep(POLL_SECONDS)
                else:
                    raise RuntimeError('AssetRipper did not start')
            post(base, '/Settings/Update', {**RIP_SETTINGS, 'TargetVersion': version})
            settings = urllib.request.urlopen(base + '/Settings/Edit').read().decode()
            if f'name="TargetVersion" value="{version}"' not in settings:
                raise RuntimeError('AssetRipper rejected the target version setting')
            if out.exists():
                with progress.step('Removing the last export'):
                    shutil.rmtree(out)
            log(f'Loading {folder}')
            with progress.following('AssetRipper: loading', ARTIFACTS / 'assetripper.log'):
                post(base, '/LoadFolder', {'path': str(folder)})
            log(f'Exporting to {out}')
            with progress.following('AssetRipper: exporting', ARTIFACTS / 'assetripper.log', pattern=EXPORT_COUNT):
                post(base, '/Export/UnityProject', {'path': str(out)})
            project_version = out / 'ExportedProject' / 'ProjectSettings' / 'ProjectVersion.txt'
            if not project_version.exists():
                raise RuntimeError('AssetRipper export produced no project; see ' + str(ARTIFACTS / 'assetripper.log'))
            log('Exported project: ' + project_version.read_text().strip())
            (out / RIP_STAMP).write_text(stamp + '\n', encoding='utf-8')
        finally:
            server.kill()


def post(base, path, fields):
    """POST a form to AssetRipper's server and wait for it to finish the request."""
    data = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(base + path, data=data, method='POST')
    with urllib.request.urlopen(request, timeout=REQUEST_SECONDS) as response:
        response.read()


# ---------------------------------------------------------------- code

# Engine assemblies of each target platform. Vita modules can be empty shells (UnityEngine.VideoModule), so
# the game is rewritten against them; types they lack go to the shim like any other missing API.
PLATFORM_ENGINE = {'vita': Path('PlaybackEngines') / 'PSP2Player' / 'Managed'}


class EditorPaths:
    """Unity's folders the code stage compiles, rewrites and links against."""

    def __init__(self, version, platform='windows'):
        self.data = editor_data(version)
        self.bcl = self.data / 'MonoBleedingEdge' / 'lib' / 'mono' / 'unityaot'  # the unityaot class libraries
        self.engine = self.data / 'Managed' / 'UnityEngine'  # the editor's UnityEngine and its modules
        self.target_engine = self.data / PLATFORM_ENGINE[platform] if platform in PLATFORM_ENGINE else self.engine
        self.gui = self.data / 'UnityExtensions' / 'Unity' / 'GUISystem'  # uGUI


def engine_library(version, out: Path, sources, modules=(), defines=()):
    """Compile a C# 5 library from source files against Unity's unityaot class libraries, UnityEngine's core
    module and the given UnityEngine modules (e.g. 'AudioModule')."""
    paths = EditorPaths(version)
    bcl, engine = paths.bcl, paths.engine
    compile_csharp(
        [
            CSC,
            '/nologo',
            '/noconfig',
            '/target:library',
            '/nostdlib+',
            '/unsafe',
            *(f'/define:{name}' for name in defines),
            *(f'/r:{bcl / name}' for name in ('mscorlib.dll', 'System.dll', 'System.Core.dll')),
            *(f'/r:{engine / f"UnityEngine.{name}.dll"}' for name in ('CoreModule', *modules)),
            f'/r:{engine / "UnityEngine.dll"}',
            f'/r:{paths.gui / "UnityEngine.UI.dll"}',
            f'/out:{out}',
            *sources,
        ]
    )


def compile_csharp(command: list):
    """Run csc, unless its output is there from the same command line, sources and references
    (<output>.stamp). The output is made reproducible (reproducible_assembly)."""
    out = Path(next(str(arg)[len('/out:') :] for arg in command if str(arg).startswith('/out:')))
    digest = hashlib.sha256()
    for arg in map(str, command):
        digest.update(arg.encode() + bytes(1))  # a separator
        path = Path(arg.removeprefix('/r:'))
        if arg.endswith('.cs'):
            digest.update(path.read_bytes())
        elif arg.startswith('/r:') and path.exists():
            digest.update(f'{path.stat().st_size} {path.stat().st_mtime_ns}'.encode())
    stamp = out.with_name(out.name + '.stamp')
    if out.exists() and stamp.exists() and stamp.read_text() == digest.hexdigest():
        return
    stamp.unlink(missing_ok=True)
    run(f'Compiling {out.name}', command, f'csc-{out.stem}')
    reproducible_assembly(out)
    stamp.write_text(digest.hexdigest())


PRIVATE_DETAILS = b'<PrivateImplementationDetails>{'


def reproducible_assembly(path: Path):
    """The .NET Framework's csc (C# 5) has no /deterministic: each compile of the same sources writes a new
    PE time stamp, module version ID (MVID) and GUID in the name of its <PrivateImplementationDetails>{...}
    class. They are set here from a hash of the rest of the file, as Roslyn's /deterministic does, so the
    same sources give the same bytes."""
    data = bytearray(path.read_bytes())

    def u16(at):
        return int.from_bytes(data[at : at + 2], 'little')

    def u32(at):
        return int.from_bytes(data[at : at + 4], 'little')

    coff = u32(0x3C) + 4
    optional = coff + 20
    directories = optional + (96 if u16(optional) == 0x10B else 112)
    sections = [optional + u16(coff + 16) + 40 * i for i in range(u16(coff + 2))]

    def offset(rva):
        for s in sections:
            if u32(s + 12) <= rva < u32(s + 12) + max(u32(s + 8), u32(s + 16)):
                return rva - u32(s + 12) + u32(s + 20)
        sys.exit(f'{path}: RVA {rva:#x} is in no section')

    cli = offset(u32(directories + 14 * 8))
    metadata = offset(u32(cli + 8))
    at = metadata + 16 + u32(metadata + 12)  # past the version string: flags, then the stream count
    streams, count = {}, u16(at + 2)
    at += 4
    for _ in range(count):
        start, size = u32(at), u32(at + 4)
        end = data.index(0, at + 8)
        streams[data[at + 8 : end].decode()] = (metadata + start, size)
        at = at + 8 + ((end - at - 8) // 4 + 1) * 4  # the name with its 0, padded to 4 bytes
    mvid = streams['#GUID'][0]  # the module's, the heap's first (the only one csc writes)
    strings_at, strings_size = streams['#Strings']
    details = data.find(PRIVATE_DETAILS, strings_at, strings_at + strings_size)
    details = details + len(PRIVATE_DETAILS) if details >= 0 else None
    data[coff + 4 : coff + 8] = bytes(4)
    data[mvid : mvid + 16] = bytes(16)
    if details:
        data[details : details + 36] = b'0' * 36
    digest = hashlib.sha256(data).digest()
    data[coff + 4 : coff + 8] = (int.from_bytes(digest[16:20], 'little') & 0x7FFFFFFF).to_bytes(4, 'little')
    data[mvid : mvid + 16] = digest[:16]
    if details:
        hexits = digest[16:32].hex().upper()
        name = f'{hexits[:8]}-{hexits[8:12]}-{hexits[12:16]}-{hexits[16:20]}-{hexits[20:32]}'
        data[details : details + 36] = name.encode()
    assert len(data) == path.stat().st_size
    path.write_bytes(data)


# T2DCompat: this pipeline's own sources, then the platform layer in compat/*.cs (written apart; every .cs
# file there is compiled, in name order). The engine modules it may use.
COMPAT_FOLDER = HERE / 'compat'


def compat_sources():
    # atlas/compat: the texture pages' runtime (docs/textures.md); spritebatch/compat: the sprite renderer
    # (docs/drawing.md); audio/compat: the sound loads (docs/audio.md).
    return [
        HERE / 'Compat2018.cs',
        HERE / 'Unity6Shims.cs',
        *sorted(COMPAT_FOLDER.glob('*.cs')),
        *sorted((HERE / 'atlas' / 'compat').glob('*.cs')),
        *sorted((HERE / 'spritebatch' / 'compat').glob('*.cs')),
        *sorted((HERE / 'audio' / 'compat').glob('*.cs')),
    ]


COMPAT_MODULES = [
    'AudioModule',
    'ImageConversionModule',
    'IMGUIModule',
    'TextRenderingModule',
    'UIModule',
    'UnityWebRequestModule',
    'UnityWebRequestAudioModule',
    'JSONSerializeModule',
]
REWRITER_SOURCES = [
    HERE / 'DowngradeAssemblies.cs',
    *(HERE / 'rewriter' / f'{name}.cs' for name in ('Redirect', 'Runtime', 'RewindGaps', 'Stack', 'GapTyping', 'SplitMethods', 'MenuCursor', 'MenuStrings', 'Trace')),
    # The platform layer's pass and its rules (docs/platform.md); compat_sources() takes only compat/*.cs.
    *(COMPAT_FOLDER / 'rewriter' / f'{name}.cs' for name in ('PlatformRules', 'PlatformPasses')),
    HERE / 'atlas' / 'rewriter' / 'AtlasPatches.cs',  # the texture rules R1-R6 (docs/textures.md)
    HERE / 'spritebatch' / 'rewriter' / 'SpriteBatchPatches.cs',  # the sprite renderer's rules R7-R9 (docs/drawing.md)
    HERE / 'audio' / 'rewriter' / 'AudioPatches.cs',  # the sound loading rules A1 to A3 (docs/audio.md)
    # The repair engine and its rules (docs/repairs.md), which Rewrite() runs first. RepairsMain.cs is the
    # standalone authoring driver, with a Main of its own.
    *(HERE / 'repairs' / f'{name}.cs' for name in ('Engine', 'Edit', 'IL', 'Support', 'Expr', 'KnownBits', 'Patterns', 'RewriterHook')),
    *sorted((HERE / 'repairs' / 'rules').glob('*.cs')),
    # The fold-back passes (foldback/README.md): inlined engine bindings and class library internals back to 2018.2's API.
    *sorted((HERE / 'foldback').glob('*.cs')),
]
# Files in the rewriter's folders that are not rewriter parts.
NOT_REWRITER_PARTS = {HERE / 'repairs' / 'RepairsMain.cs'}


def check_parts():
    """Stops if a rewriter part is missing from REWRITER_SOURCES: the compile would leave it out without a word."""
    parts = [*(HERE / 'rewriter').glob('*.cs'), *(HERE / 'repairs').glob('*.cs')]
    unlisted = sorted(p.name for p in parts if p not in REWRITER_SOURCES and p not in NOT_REWRITER_PARTS)
    if unlisted:
        sys.exit(f'sources missing from REWRITER_SOURCES: {", ".join(unlisted)}')


def compat_path(platform):
    """T2DCompat, compiled per platform."""
    return WORK / 'bin' / platform / 'T2DCompat.dll'


def compile_tools(version, platform):
    check_parts()
    bin_dir = WORK / 'bin'
    bin_dir.mkdir(parents=True, exist_ok=True)
    cecil = bin_dir / CECIL.name  # beside the tools, which load it at run time
    if not cecil.exists() or cecil.read_bytes() != mono_cecil().read_bytes():
        shutil.copy2(mono_cecil(), cecil)
    compat = compat_path(platform)
    compat.parent.mkdir(parents=True, exist_ok=True)
    # T2D_VITA: for T2DCompat code that differs on the PS Vita's player.
    engine_library(version, compat, compat_sources(), COMPAT_MODULES, ['T2D_VITA'] if platform == 'vita' else [])
    rewriter = bin_dir / 'DowngradeAssemblies.exe'
    compile_csharp([CSC, '/nologo', f'/r:{cecil}', f'/out:{rewriter}', *REWRITER_SOURCES])
    compile_csharp([CSC, '/nologo', f'/r:{cecil}', f'/out:{bin_dir / "StubAssembly.exe"}', HERE / 'StubAssembly.cs'])
    paths = EditorPaths(version, platform)
    return compat, rewriter, [paths.bcl, paths.target_engine, paths.gui]


# Managed wrappers of native libraries the PS Vita lacks; their methods become no-ops returning defaults, so
# the game's own "not available" paths run instead. Steamworks.NET calls steam_api64.dll. Unused while the roots
# leave Steamworks out (below); kept for a release whose code the platform layer doesn't cover.
STUBBED_ON = {'vita': ['Steamworks'], 'windows': ['Steamworks']}
# The roots: the game's code. InControl and Steamworks.NET are left out ("-"): the rewriter sends their types to
# T2DCompat's (compat/InControl.cs, the input API the game uses over Unity's Input; compat/Steamworks.cs, the types
# left once the platform pass has taken Steam out of the game's code). docs/platform.md.
ROOTS = ['Assembly-CSharp', '-InControl', '-Steamworks']
# The rewriter's lines that are problems: what doesn't resolve, rules that matched nothing, roots not found.
PROBLEMS = ('UNRESOLVED', 'NO MATCH', 'MISSING')
CODE_REPORT = 'code-report.txt'
# The rewriter's exit code, beside the assemblies it wrote: build checks it before the builder's long data step.
REWRITER_EXIT = 'rewriter-exit.txt'


def rewrite_assemblies(recovered: Path, out: Path, version: str, platform: str, roots=ROOTS):
    """Rewrite the roots among Rewind's assemblies, and what they reference there, into out. Returns the
    rewriter's exit code: 0, or 3 with UNRESOLVED references, 4 with rules that matched nothing. Writes a
    report of the problems and of Rewind's gaps to artifacts/downgrade/code-report.txt."""
    compat, rewriter, references = compile_tools(version, platform)
    if out.exists():
        shutil.rmtree(out)
    log_path = ARTIFACTS / f'rewrite-{version}-{platform}.log'
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    command = [rewriter, recovered, out, compat, ','.join(roots), *references]
    switches = build_switches(platform, ('code',))
    log('Code switches: ' + describe_switches(switches))
    env = {**os.environ, **switches, 'T2D_API_VERSION': version}
    code = progress.run("Rewriting the game's assemblies", command, log_path, env=env, check=False)
    output = log_path.read_text(encoding='utf-8', errors='replace')
    if code not in (0, 3, 4):
        sys.exit(f'The rewriter failed (exit code {code}); see {log_path}')
    for name in STUBBED_ON.get(platform, []):
        target = out / f'{name}.dll'
        if target.exists():
            stubbed = target.with_name(target.stem + '.stub.dll')
            run(f'Stubbing {target.stem}', [WORK / 'bin' / 'StubAssembly.exe', target, stubbed])
            stubbed.replace(target)
    code_report(output, out, platform)
    (out / REWRITER_EXIT).write_text(f'{code}\n', encoding='utf-8')
    # A repair rule that no longer fits Rewind's output (drift, a stale rule) is never tolerated, unlike the
    # references that don't resolve yet: it would hide among them (docs/repairs.md, "Drift policy").
    repair_errors = [line for line in output.splitlines() if line.startswith('REPAIR ERROR')]
    if repair_errors:
        for line in repair_errors[:20]:
            log(line)
        sys.exit(f'{len(repair_errors)} repair errors (REPAIR ERROR lines); see {log_path} and docs/repairs.md')
    return code


def rewriter_exit(version: str, platform: str):
    """The last code stage's exit code (REWRITER_EXIT), or None when there is none."""
    path = rewritten_folder(version, platform) / REWRITER_EXIT
    return int(path.read_text(encoding='utf-8')) if path.exists() else None


def code_report(output: str, out: Path, platform: str):
    """The rewriter's problems and Rewind's gaps, summed up in the log and listed in CODE_REPORT."""
    lines = output.splitlines()
    problems = [line for line in lines if line.startswith(PROBLEMS)]
    gaps = [line.split(' ', 4) for line in lines if line.startswith('REWIND GAP ')]
    notes = [line for line in lines if line.startswith('REWIND NOTE ')]
    stubbed = set(STUBBED_ON.get(platform, []))
    gap_count = Counter(g[2] for g in gaps if g[2] not in stubbed)
    sites = Counter()
    for g in gaps:
        if g[2] not in stubbed:
            sites[g[2]] += int(g[4].rsplit('sites=', 1)[1])
    # "UNRESOLVED <assembly>: <what> ...": counted by assembly and what (type, member, field, verify ...).
    kinds = Counter(
        ' '.join([line.split()[1].rstrip(':'), line.split(':', 1)[1].split()[0]]) if ':' in line else line.split()[0]
        for line in problems
    )
    report = [
        f'Rewritten into {out}: {", ".join(sorted(p.name for p in out.glob("*.dll")))}',
        '',
        f'{len(problems)} problems from the rewriter (by kind: '
        + ', '.join(f'{k} {n}' for k, n in kinds.most_common())
        + ')',
        *problems,
        '',
        'Methods with a Rewind marker left, which throw when they reach it (stubbed assemblies left out):',
        *(f'  {name}: {gap_count[name]} methods, {sites[name]} marker sites' for name in sorted(gap_count)),
        *(f'  {" ".join(g[2:])}' for g in gaps if g[2] not in stubbed),
        '',
        f'{len(notes)} methods Rewind notes as incomplete without a marker (docs/rewind-issues.md, issue 6):',
        *(f'  {line[len("REWIND NOTE ") :]}' for line in notes),
    ]
    (ARTIFACTS / CODE_REPORT).write_text('\n'.join(report) + '\n', encoding='utf-8')
    log(f'Rewrote {len(list(out.glob("*.dll")))} assemblies; {len(problems)} problems')
    for kind, count in kinds.most_common():
        log(f'  {count:5} {kind}')
    for name in sorted(gap_count):
        log(f'  Rewind gaps in {name}: {gap_count[name]} methods, {sites[name]} marker sites')
    log(f'Report: {ARTIFACTS / CODE_REPORT}')


# ---------------------------------------------------------------- project


PLUGIN_META = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  serializedVersion: 2
  externalObjects: {{}}
  iconMap: {{}}
  executionOrder: {{}}
  isPreloaded: 0
  isOverridable: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def refuse_links(path: Path, root: Path | None = None):
    """Stop unless no folder from the workspace root (ROOT) down to path, path included, is a link or
    junction. The mirror writes and deletes under path: through work/rip-input/<game> as a junction to the
    installed game, say, it deleted the game's il2cpp_data and Plugins and wrote Managed/ into it. A path
    outside the workspace is checked from its drive's root."""
    root = Path(os.path.abspath(ROOT if root is None else root))
    path = Path(os.path.abspath(path))
    try:
        parts = path.relative_to(root).parts
        current = root
    except ValueError:
        current = Path(path.anchor)
        parts = path.relative_to(current).parts
    for part in parts:
        current = current / part
        if current.is_symlink() or current.is_junction():
            raise SystemExit(
                f'{current} is a link; writing to {path} would write and delete in its target. Remove the link first.'
            )


def inside(path: Path, folder: Path):
    """Whether path is folder or under it, links followed (resolve)."""
    path, folder = path.resolve(), folder.resolve()
    return path == folder or folder in path.parents


def sync_tree(source: Path, destination: Path, overlay: dict, excluded: set, known: dict | None = None, step=None):
    """Mirror source into destination with generated files from overlay replacing or adding paths,
    and excluded source paths left out. An overlay value is the file's bytes, or a Path to copy it from.
    Unchanged files keep their timestamps so an import cache stays valid between runs. A .meta
    file the editor generated (for a folder or file that has none in the source) stays, so its GUID
    doesn't change on every run. known: copy_if_changed's record, kept by the caller between runs.
    step: a progress step, its bar set to the files to go through and advanced per file."""
    refuse_links(destination)
    if inside(destination, source) or inside(source, destination):
        raise SystemExit(f'{destination} and {source} overlap; the mirror would write into its own source.')
    destination.mkdir(parents=True, exist_ok=True)
    present = walk(destination, links=True)  # listed once: one stat per file was most of the time
    # A link or junction in the destination is removed, never gone into: its target isn't the mirror's.
    for relative in [relative for relative, stat in present.items() if stat == LINK]:
        path = destination / relative
        try:
            path.unlink()  # a junction, or a link to a file
        except OSError:
            os.rmdir(path)  # a link to a folder, which rmdir removes without going into it
        del present[relative]
    known = {} if known is None else known
    wanted = set()
    files = walk(source)
    if step:
        step.update(0, total=len(files) + len(overlay))
    for relative, stat in files.items():
        if step:
            step.advance()
        if relative in excluded or relative in overlay:
            continue
        wanted.add(relative)
        if stat is None:
            if relative not in present:
                (destination / relative).mkdir(parents=True, exist_ok=True)
        elif not unchanged(source / relative, destination / relative, stat, present.get(relative)):
            (destination / relative).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source / relative, destination / relative)
    for relative, content in overlay.items():
        if step:
            step.advance()
        if isinstance(content, Path):
            copy_if_changed(content, destination / relative, known, relative.as_posix())
        elif not (present.get(relative) and present[relative][0] == len(content)):
            write(destination / relative, content)  # new, or another size: no need to read it
        else:
            write_if_changed(destination / relative, content)
        wanted.add(relative)
        wanted.update(relative.parents)
    for relative in sorted(present, reverse=True):
        if relative in wanted or (relative.suffix == '.meta' and relative.with_suffix('') in wanted):
            continue
        path = destination / relative
        shutil.rmtree(path) if present[relative] is None else path.unlink()
    for key in [key for key in known if Path(key) not in overlay]:
        del known[key]


def coarse(ns):
    """Whether a modification time is a whole millisecond: a file system that keeps coarse times (FAT's 2 s,
    exFAT's 10 ms, some network shares), where two versions of a file written in one tick have one time. On
    NTFS (100 ns) about one file in 10,000 has such a time by chance: none of the 2,069 export files and 24
    game files sampled (Nov 2026), so the byte compares this adds cost nothing there."""
    return ns % 10**6 == 0


def same_time(stat, other):
    """Whether two walk() entries are files of one size and modification time, to the nanosecond (copy2
    keeps both). By the second alone, a same-length edit within the second the last copy was made in
    was missed. A coarse time (coarse) can't tell two versions apart: never the same then, and the
    caller compares the bytes."""
    return other is not None and stat[0] == other[0] and stat[1] == other[1] and not coarse(stat[1])


def unchanged(source: Path, target: Path, stat, other):
    """Whether target, of walk() entry other, already has source's content: the same size and time, or
    the same size and second and the same bytes (a file system that keeps coarser times than the source's)."""
    if same_time(stat, other):
        return True
    if other is None or stat[0] != other[0] or stat[1] // 10**9 != other[1] // 10**9:
        return False
    return filecmp.cmp(source, target, shallow=False)


def copy_if_changed(source: Path, target: Path, known: dict | None = None, key: str = ''):
    """Copy source to target unless target has its size and modification time (as sync_tree mirrors),
    or the same bytes (known[key] then remembers the pair, so the next run doesn't compare them again)."""
    known = {} if known is None else known
    stat = source.stat()
    stat = (stat.st_size, stat.st_mtime_ns)
    other = target.stat() if target.exists() else None
    other = (other.st_size, other.st_mtime_ns) if other else None
    if same_time(stat, other):
        known.pop(key, None)
        return
    if other is not None and other[0] == stat[0]:
        pair = [*stat, other[1]]
        if known.get(key) == pair and not coarse(stat[1]):
            return
        if filecmp.cmp(source, target, shallow=False):
            known[key] = pair
            return
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def write(path: Path, content: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)


def write_if_changed(path: Path, content: bytes):
    if path.exists() and path.read_bytes() == content:
        return
    write(path, content)


# The renamed game assembly keeps its original's .meta, so the scene's script references stay valid. The
# rewriter (DowngradeAssemblies.cs) and T2DCompat (Compat2018.cs) name it too: keep them in step.
ASSEMBLY_NAMES = {'Recovered.T2D': 'Assembly-CSharp'}


def assembly_overlay(exported: Path, version: str, platform: str):
    """The rewritten game assemblies with T2DCompat in Assets/Plugins, in place of the exported plugins
    (asset_rules.exported_plugins_excluded). Returns (overlay, the exported files they replace, the
    assemblies). The rewriter reads Rewind's assemblies, the same the export was made from; the export
    gives the .meta files, whose GUIDs the scene references."""
    plugins = exported / 'Assets' / 'Plugins'
    relative = Path('Assets') / 'Plugins'
    original_meta = {dll.stem: meta_of(dll).read_bytes() for dll in plugins.glob('*.dll') if meta_of(dll).exists()}
    excluded = exported_plugins_excluded(exported)
    rewritten = rewritten_folder(version, platform)
    code = rewrite_assemblies(RECOVERED, rewritten, version, platform)
    if code:
        log(
            f'WARNING: the rewriter exited {code}: the game assemblies do not resolve against {version} yet '
            f'({ARTIFACTS / CODE_REPORT}). The project gets them as they are.'
        )
    if not any(rewritten.glob('*.dll')):
        sys.exit(f'The rewriter wrote no assemblies into {rewritten}; see {ARTIFACTS}')
    compat = compat_path(platform)
    overlay = {}
    for dll in [*rewritten.glob('*.dll'), compat]:
        if dll == compat:
            meta = PLUGIN_META.format(guid=COMPAT_GUID).encode()
        else:
            source = ASSEMBLY_NAMES.get(dll.stem, dll.stem)
            meta = (
                original_meta.get(source)
                or PLUGIN_META.format(guid=hashlib.md5(dll.stem.encode()).hexdigest()).encode()
            )
        overlay[relative / dll.name] = dll.read_bytes()
        overlay[meta_of(relative / dll.name)] = meta
    log('Plugins: ' + ', '.join(sorted(p.name for p in overlay if p.suffix == '.dll')))
    return overlay, excluded, [*rewritten.glob('*.dll'), compat]


def asset_overlay(exported: Path, platform: str, assemblies: list):
    """The rules for the exported assets and settings (asset_rules.py), in order. Returns (overlay,
    exported paths left out)."""
    overlay, excluded = {}, set()
    overlay.update(settings_overlay(exported, platform))
    overlay.update(time_overlay(exported, RIP_INPUT / f'{GAME_NAME}_Data'))
    lighting, lighting_excluded = lighting_overlay(exported)
    overlay.update(lighting)
    excluded |= lighting_excluded
    overlay.update(shader_overlay(exported))
    # The sound's import settings (docs/audio.md): by module name, as tools/builder has a module named audio.
    sys.path.insert(0, str(HERE / 'audio'))
    from audio_rules import audio_overlay

    sounds = audio_overlay(exported)
    log(f'Sound: import settings of {len(sounds)} clips (audio/audio_rules.py)')
    overlay.update(sounds)
    # The texture pages in place of the twelve packs (docs/textures.md).
    from atlas.step import atlas_overlay

    raw = build_switches(platform, ('project',))['T2D_ATLAS_PAGES'] == 'raw'
    log('Texture pages: ' + describe_switches({'T2D_ATLAS_PAGES': 'raw' if raw else 'png'}))
    atlas, atlas_excluded = atlas_overlay(
        exported, RECOVERED / 'Assembly-CSharp.dll', WORK / 'atlas', ARTIFACTS / 'atlas', raw=raw, log=log
    )
    overlay.update(atlas)
    excluded |= atlas_excluded
    overlay[Path('Assets') / 'link.xml'] = link_xml(assemblies, platform).encode()
    overlay[Path('Assets') / 'link.xml.meta'] = stable_meta('asset', 'link.xml', 'TextScriptImporter').encode()
    return overlay, excluded


SYNC_RECORD = '.t2d-sync.json'  # in the project's folder, outside Assets: the editor ignores it


def assemble(rip_dir: Path, project: Path, version: str, platform: str):
    exported = rip_dir / 'ExportedProject'
    if not exported.exists():
        sys.exit(f'No export in {rip_dir}: run pipeline.py rip first')
    plugins, plugin_files, assemblies = assembly_overlay(exported, version, platform)
    assets, excluded = asset_overlay(exported, platform, assemblies)
    overlay = {**plugins, **assets}
    excluded |= plugin_files
    log(f'Synchronizing {project}')
    record = project / SYNC_RECORD
    # The record as the mirror's own files: no link on the way to it, nor it a link (written through, it would
    # overwrite the link's target), and written whole (a temp file renamed over it; a rename replaces a link
    # rather than writing through it).
    refuse_links(record)
    try:
        known = json.loads(record.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        known = {}
    with progress.step(f'Synchronizing {project.name}') as step:
        for part in ('Assets', 'ProjectSettings', 'Packages'):
            step.note(part)
            sync_tree(
                exported / part,
                project / part,
                {k.relative_to(part): v for k, v in overlay.items() if k.parts[0] == part},
                {k.relative_to(part) for k in excluded if k.parts[0] == part},
                known.setdefault(part, {}),
                step,
            )
    record.parent.mkdir(parents=True, exist_ok=True)
    refuse_links(record)
    temporary = record.with_name(record.name + '.part')
    temporary.unlink(missing_ok=True)  # a link left there is removed, not written through
    temporary.write_text(json.dumps(known, sort_keys=True), encoding='utf-8')
    replace_path(temporary, record)


# ---------------------------------------------------------------- build (the builder's helpers)

# The PS Vita native plugins the player loads from Media/Plugins for [DllImport("<name>")], built by
# native/<name>/build.py. None yet.
NATIVE_PLUGINS = {}


def native_plugins(output: Path):
    folder = output / 'Media' / 'Plugins'
    for name, files in NATIVE_PLUGINS.items():
        folder.mkdir(parents=True, exist_ok=True)
        script = HERE / 'native' / name / 'build.py'
        progress.run(f'Native plugin {name}', [sys.executable, script], ARTIFACTS / 'tools' / f'native-{name}.log')
        for file in files:
            shutil.copy2(WORK / 'native' / name / file, folder / file)


# The PS Vita player's graphics jobs: the 2018.2 editor has no setting for them on the PS Vita, but its
# player reads gfx-enable-gfx-jobs from Media/boot.config at start (measured in Crow Country's port: 24.9 ->
# 22.1 ms of main thread in a busy scene). T2D_GFX_JOBS=on (the default) writes gfx-enable-gfx-jobs=1.
def boot_config(output: Path):
    path = output / 'Media' / 'boot.config'
    if not path.exists():
        sys.exit(f'No {path}: the PS Vita build is incomplete, and its graphics jobs would be off')
    lines = path.read_text(encoding='utf-8').splitlines()
    lines = [line for line in lines if not line.startswith('gfx-enable-gfx-jobs=')]
    jobs = build_switches('vita', ('player',))['T2D_GFX_JOBS'] == 'on'
    if jobs:
        lines.insert(0, 'gfx-enable-gfx-jobs=1')
    path.write_bytes(('\n'.join(lines) + '\n').encode('utf-8'))
    log(f'Graphics jobs {"on" if jobs else "off"} in {path}')


def reproducible_player_data(data: Path):
    """What Unity's build writes differently into globalgamemanagers on every build, set in place so two
    builds of one tree give the same bytes: the [RuntimeInitializeOnLoadMethod] methods sorted (T2DCompat's run
    first), MonoManager's scripts and ResourceManager's dependencies sorted by object, and the build GUID
    derived from the content. data: the player's data folder (the PS Vita's Media)."""
    import UnityPy

    path = data / 'globalgamemanagers'
    content = bytearray(path.read_bytes())
    env = UnityPy.load(bytes(content))
    objects = {o.type.name: o for o in env.objects}

    written = {}  # each object's bytes as last written

    def patch(obj, tree):
        start = obj.byte_start
        old = written.setdefault(obj.path_id, obj.get_raw_data())
        if content[start : start + len(old)] != old:
            sys.exit(f'{path}: {obj.type.name} is not where UnityPy reads it')
        new = obj.save_typetree(tree)
        if len(new) != len(old):
            sys.exit(f'{path}: {obj.type.name} would change size ({len(old)} -> {len(new)} bytes)')
        content[start : start + len(new)] = new
        written[obj.path_id] = new

    manager = objects['RuntimeInitializeOnLoadManager']
    tree = manager.read_typetree()
    assemblies, namespaces = tree['m_AssemblyNames'], tree['m_NamespaceNames']
    classes, methods = tree['m_ClassInfos'], tree['m_ClassMethodInfos']
    if tree['m_BeforeUnityMethodExecutionOrders'] or tree['m_AfterUnityMethodExecutionOrders']:
        sys.exit(f'{path}: Unity classes among the RuntimeInitializeOnLoadMethods; sort them too')

    def class_key(c):
        return assemblies[c['m_AssemblyNameIndex']], namespaces[c['m_NamespaceIndex']], c['m_ClassName']

    before = {id(methods[i]) for i in tree['m_BeforeMethodExecutionOrders']}
    after = {id(methods[i]) for i in tree['m_AfterMethodExecutionOrders']}
    new_assemblies, new_namespaces = sorted(assemblies), sorted(namespaces)
    new_classes, new_methods, was = [], [], []
    for index, c in enumerate(sorted(classes, key=class_key)):
        first = c['m_MethodIndex']
        own = sorted(methods[first : first + c['m_NumOfMethods']], key=lambda m: m['m_MethodName'])
        new_classes.append(
            {
                **c,
                'm_AssemblyNameIndex': new_assemblies.index(assemblies[c['m_AssemblyNameIndex']]),
                'm_NamespaceIndex': new_namespaces.index(namespaces[c['m_NamespaceIndex']]),
                'm_MethodIndex': len(new_methods),
            }
        )
        new_methods += [{**m, 'm_ClassIndex': index} for m in own]
        was += [id(m) for m in own]
    if sorted(was) != sorted(map(id, methods)):
        sys.exit(f'{path}: RuntimeInitializeOnLoadManager lists methods outside their classes; sort it by hand')
    tree.update(
        m_AssemblyNames=new_assemblies,
        m_NamespaceNames=new_namespaces,
        m_ClassInfos=new_classes,
        m_ClassMethodInfos=new_methods,
        m_BeforeMethodExecutionOrders=[i for i, m in enumerate(was) if m in before],
        m_AfterMethodExecutionOrders=[i for i, m in enumerate(was) if m in after],
    )
    patch(manager, tree)

    mono = objects['MonoManager']
    tree = mono.read_typetree()
    tree['m_Scripts'] = sorted(tree['m_Scripts'], key=lambda s: (s['m_FileID'], s['m_PathID']))
    patch(mono, tree)

    resources = objects['ResourceManager']
    tree = resources.read_typetree()
    tree['m_DependentAssets'] = sorted(
        tree['m_DependentAssets'], key=lambda d: (d['m_Object']['m_FileID'], d['m_Object']['m_PathID'])
    )
    patch(resources, tree)

    settings = objects['BuildSettings']
    tree = settings.read_typetree()
    tree['buildGUID'] = dict.fromkeys(tree['buildGUID'], 0)
    patch(settings, tree)
    digest = hashlib.sha256(content)
    for file in sorted(data.iterdir()):
        if file.is_file() and file.name.startswith(('level', 'sharedassets', 'resources.assets')):
            digest.update(file.name.encode() + bytes(1) + file_hash(file).encode())
    words = digest.digest()[:16]
    tree['buildGUID'] = {
        key: int.from_bytes(words[4 * i : 4 * i + 4], 'little') for i, key in enumerate(tree['buildGUID'])
    }
    patch(settings, tree)
    path.write_bytes(content)
    log(f'{path}: RuntimeInitializeOnLoadMethods sorted ({len(new_methods)}), build GUID {words.hex()}')


# ---------------------------------------------------------------- package

VITA_TITLE_ID = 'TNOF00001'  # tools/builder/editor_settings.py writes the content ID with it
VITA_FTP = (VITA_HOST, 1337)  # VitaShell's or vitacompanion's FTP server
VITA_COMMANDS = (VITA_HOST, 1338)  # vitacompanion's command server


def vita_app_files(build: Path):
    """Files of the installed app for a PS Vita build, as (path in the app, local path or bytes): the
    player becomes eboot.bin with the trial flag cleared, the LiveArea art replaces Unity's placeholders,
    host-only files are left out (The Drifter's port's packaging)."""
    import livearea

    selfs = list(build.glob('*.self'))
    if len(selfs) != 1:
        sys.exit(f'expected one .self in {build}, found {[p.name for p in selfs]}')
    player = bytearray(selfs[0].read_bytes())
    player[0x80] = 0  # trial flag, as the community PSVita Unity Utilities do
    marker = player.find(b'trial.png')
    if marker >= 0:
        player[marker] = 0  # no trial watermark
    patcher_usse(player)
    yield 'eboot.bin', bytes(player)
    with progress.step('Making the LiveArea art'):  # the store images, fetched once, and the PNGs
        art = livearea.generate()
    skip = {'SymbolFiles', 'configuration.psp2path'}
    for path in sorted(build.rglob('*')):
        relative = path.relative_to(build)
        if path.is_dir() or relative.parts[0] in skip or path.suffix in ('.self', '.bat', '.part'):
            continue
        name = relative.as_posix()
        yield name, art / VITA_ARTWORK[name] if name in VITA_ARTWORK else path


# The USSE memory of GXM's shader patcher. Unity 2018.2.19f1's PS Vita player allocates a fixed 512 KB from
# g_ShaderPatcherCombinedUsseSize (an initialized global of its data segment) and frees a shader's patched
# programs only when the shader is unloaded; when it is used up the player draws with a fragment program it
# never set and crashes (huntdown-vita, after 2 h of play). The player is a plain fself, so the global's
# initial value is patched in eboot.bin.
VITA_PATCHER_USSE_OFFSET = 0x15D9164  # g_ShaderPatcherCombinedUsseSize in PSP2Player_Il2cpp.self, 2018.2.19f1
VITA_PATCHER_USSE_UNITY = 0x80000  # its value, and that of g_ShaderPatcherBufferSize just before it
VITA_PATCHER_USSE_BYTES = 0x100000


def patcher_usse(player: bytearray):
    """Sets the shader patcher's USSE memory in the player (eboot.bin) to VITA_PATCHER_USSE_BYTES."""
    at = VITA_PATCHER_USSE_OFFSET

    def word(offset):
        return int.from_bytes(player[offset : offset + 4], 'little')

    if word(at - 4) != VITA_PATCHER_USSE_UNITY or word(at) not in (VITA_PATCHER_USSE_UNITY, VITA_PATCHER_USSE_BYTES):
        sys.exit(
            f"The player has {word(at - 4):#x} {word(at):#x} where 2018.2.19f1 keeps the shader patcher's "
            f'buffer and USSE sizes ({at - 4:#x}): another player; find g_ShaderPatcherCombinedUsseSize again'
        )
    player[at : at + 4] = VITA_PATCHER_USSE_BYTES.to_bytes(4, 'little')


# LiveArea images from livearea.py, in place of Unity's placeholders.
VITA_ARTWORK = {
    'sce_sys/icon0.png': 'icon0.png',
    'sce_sys/pic0.png': 'pic0.png',
    'sce_sys/livearea/contents/bg0.png': 'bg0.png',
    'sce_sys/livearea/contents/default_gate.png': 'gate.png',
}
PACKAGE_CHUNK = 1 << 22
VPK_DATE = (2024, 1, 1, 0, 0, 0)  # every file's date in the VPK


def package_vita(build: Path) -> Path:
    """Turn a PS Vita build into an installable VPK."""
    import zipfile

    if not any(build.glob('*.self')):
        sys.exit(f'No PS Vita build in {build}; build it first: pipeline.py build')
    if not (build / 'Media' / 'Modules' / 'il2CppAssemblies.suprx').exists():
        sys.exit(f'{build} has no code module (Media/Modules/il2CppAssemblies.suprx): its build stopped before IL2CPP')
    require_complete(build)
    vpk = build.with_name(build.name + '.vpk')  # with_suffix would cut the version at its dots
    files = list(vita_app_files(build))
    total = sum(len(source) if isinstance(source, bytes) else source.stat().st_size for _, source in files)
    with (
        zipfile.ZipFile(vpk, 'w', zipfile.ZIP_DEFLATED) as archive,
        progress.step('Packaging the VPK', total=total, eta=True, count='MB') as step,
    ):
        for name, source in files:
            step.note(name)
            # The same headers for every file, whatever the time of the build or the files' dates and
            # attributes: two packagings of one build give the same VPK.
            info = zipfile.ZipInfo(name, date_time=VPK_DATE)
            info.compress_type = archive.compression
            info.external_attr = 0o100644 << 16
            if isinstance(source, bytes):
                archive.writestr(info, source)
                step.advance(len(source))
                continue
            with source.open('rb') as src, archive.open(info, 'w') as dest:
                while chunk := src.read(PACKAGE_CHUNK):
                    dest.write(chunk)
                    step.advance(len(chunk))
    log(f'Packaged {vpk} ({vpk.stat().st_size / 2**20:.0f} MB)')
    return vpk


# ---------------------------------------------------------------- locks


def writes_vita_build(stage):
    """Whether the stage writes builds/t2d-<version>-vita (or its VPK), which a console sync reads under
    vita-build as a reader."""
    return stage in ('build', 'package')


def check_lock(stage=None):
    """Every stage writes in work/, builds/ or artifacts/, which other jobs share: one pipeline.py at a
    time, under lock.py run (which sets T2D_LOCKS). Without it, stop while another run holds a build
    lock, and warn otherwise. The PS Vita stages need vita-build."""
    import lock

    command = 'uv run tools/downgrade/lock.py run vita-build "<you>: <purpose>" --wait 1800 -- '
    command += 'uv run tools/downgrade/pipeline.py ...'
    here = set(lock.held_here())
    if here & set(lock.BUILD_LOCKS):
        if writes_vita_build(stage) and 'vita-build' not in here:
            sys.exit(f'{stage} writes the PS Vita build: run it under vita-build: {command}')
        return
    held = [name for name in lock.BUILD_LOCKS if lock.holder_of(name) and not lock.stale(name)]
    holders = ', '.join(f'{name} ({lock.holder_of(name)})' for name in held)
    if any(lock.process_of(name) for name in held):
        sys.exit(f'Held by another run: {holders}. Wait for it: {command}')
    if held:
        log(f'WARNING: {holders} held; unless that is this job, run: {command}')
    else:
        log('Note: not run under lock.py run, so another job could start a pipeline at the same time')


STAGES = ['recover', 'rip', 'code', 'project', 'build', 'package']


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('stage', choices=STAGES)
    parser.add_argument('--game', type=Path, default=DEFAULT_GAME)
    parser.add_argument(
        '--anyway',
        action='store_true',
        help="build: run the builder even though the code stage left references that don't resolve (UnityLinker "
        'then stops at the first, after the data step)',
    )
    args = parser.parse_args()
    platform, version = 'vita', TARGET
    build_switches(platform)  # stops on a value a switch doesn't take, before any stage starts
    check_lock(args.stage)
    if not (args.game / 'GameAssembly.dll').exists() or not (game_data(args.game) / METADATA).exists():
        sys.exit(f'{args.game} is not the IL2CPP build of the game (no GameAssembly.dll or {METADATA.as_posix()}).')
    rip_dir = WORK / f'rip-{version}'
    project = project_folder(version, platform)
    started = time.time()
    if args.stage == 'recover':
        recover(args.game)
    if args.stage == 'rip':
        rip(args.game, rip_dir, version)
    if args.stage == 'code':
        out = rewritten_folder(version, platform)
        code = rewrite_assemblies(RECOVERED, out, version, platform)
        sys.exit(code)
    if args.stage == 'project':
        assemble(rip_dir, project, version, platform)
    if args.stage == 'build':
        status = rewriter_exit(version, platform)
        if status and not args.anyway:
            sys.exit(
                f"The code stage left references that don't resolve against {version} (rewriter exit {status}; "
                f'{ARTIFACTS / CODE_REPORT}). UnityLinker stops at the first of them, after the builder has written '
                'the data (about 18 minutes). Repair them first, or pass --anyway to build the data regardless.'
            )
        sys.path.insert(0, str(HERE.parent / 'builder'))
        import make

        make.make(build_folder('vita', version=version))
    if args.stage == 'package':
        package_vita(build_folder('vita', version=version))
    log(f'{args.stage} took {time.time() - started:.0f} s')


if __name__ == '__main__':
    main()
