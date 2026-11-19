"""Check what the pipeline needs and print a checklist with the command that fixes each gap.

    uv run tools/setup.py doctor

Exits 1 if something required is missing. Changes nothing.

tools/setup.py installs what these checks look for; the paths, versions and download pins are here.
From huntdown-vita's doctor (MIT, same author), with the Unity editor, its license check and GOG left
out: the PS Vita app is built without the editor (tools/builder), and the game is Steam's IL2CPP build,
whose code comes from Rewind.
"""

import argparse
import importlib.util
import os
import re
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path

import progress
from common import (
    ASSETRIPPER,
    CECIL,
    CSC,
    DOTNET10,
    DOTNET10_VERSION,
    EXTERNAL,
    GAME_EXE,
    GAME_NAME,
    REWIND_COMMIT,
    REWIND_EXE,
    REWIND_SIGNATURES,
    REWIND_STAMP,
    ROOT,
    SOURCE_VERSION,
    STEAM,
    STEAM_APP_ID,
    TARGET,
    WORK,
    default_game,
    editor_data,
    env,
    file_hash,
)
from rich.text import Text

SETUP = 'uv run tools/setup.py'

# Unity 2018.2.19f1's installers: Unity's CDN, except the PS Vita module, which Unity no longer serves (the
# archive.org copy is Unity's own signed installer; setup.py checks the hash and the signature). Nothing is
# installed from them: setup.py extracts the files below with 7-Zip.
UNITY_CDN = 'https://download.unity3d.com/download_unity/06990f28ba00'
INSTALLERS = {
    'editor': (
        f'{UNITY_CDN}/Windows64EditorInstaller/UnitySetup64-2018.2.19f1.exe',
        '4a2e95a7bd7b04cc95d3ce110e17775ba5d810b59c2c925ad6e34da4147d3f5e',
    ),
    'vita': (
        (
            'https://archive.org/download/unitypsvitasupport/'
            'UnitySetup-Playstation-Vita-Support-for-Editor-2018.2.19f1.exe'
        ),
        'e68ad5a92622c34abb0c535a6de166531a1e8a192944fd1a50fe8297f7427901',
    ),
}
# What setup.py extracts from the editor's and the PS Vita module's installers into
# tools/external/unity-<version>: what the pipeline and the editor-free build (tools/builder) read.
UNITY_FILES = {
    'editor': [
        r'Editor\Data\CGIncludes',
        r'Editor\Data\Managed',
        r'Editor\Data\MonoBleedingEdge\lib\mono\unityaot',
        r'Editor\Data\MonoBleedingEdge\lib\mono\unityjit',
        r'Editor\Data\MonoBleedingEdge\etc',
        r'Editor\Data\il2cpp',
        r'Editor\Data\Tools\MapFileParser',
        r'Editor\Data\Resources\unity_builtin_extra',
        r'Editor\Data\UnityExtensions\Unity',
    ],
    'vita': ['$INSTDIR$_59_'],
}
UNITY_FILES_VITA_FOLDER = '$INSTDIR$_59_'  # the PS Vita module's PlaybackEngines/PSP2Player in its installer
UNITY_FILES_GB = 3  # free space the extraction needs, the installers included
# 7-Zip reads NSIS installers; where none is installed, setup.py unpacks its package with 7zr.
SEVEN_ZIP = {
    '7zr': (
        'https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe',
        '256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282',
    ),
    'package': (
        'https://github.com/ip7z/7zip/releases/download/26.04/7z2604-x64.exe',
        'd54bf805f9f3704d1e8db2fa3498ae7ef2df0312b40b558e7c71c734430a665d',
    ),
}
# The builder's native packages (quicktex's encoders) are built with Visual Studio 2022 17.10 or later and
# crash in an older msvcp140.dll, such as the 2015 runtime (14.0.23026) the Unity 2018 installer brings.
VC_RUNTIME_MIN = (14, 40)
VC_REDIST_URL = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
# What the pipeline and the builder read from Unity's Editor/Data folder (common.editor_data).
EDITOR_DATA_CHECKED = [
    'CGIncludes/UnityCG.cginc',
    'Managed/UnityEngine/UnityEngine.CoreModule.dll',
    'MonoBleedingEdge/lib/mono/unityaot/mscorlib.dll',
    'MonoBleedingEdge/lib/mono/unityjit/mscorlib.dll',
    'il2cpp/build/UnityLinker.exe',
    'il2cpp/build/il2cpp.exe',
    'Tools/MapFileParser/MapFileParser.exe',
    'Resources/unity_builtin_extra',
    'UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll',
    'PlaybackEngines/PSP2Player/Managed',
    'PlaybackEngines/PSP2Player/PSP2Player_Il2cpp.self',
]

ASSETRIPPER_ZIP = (
    'https://github.com/AssetRipper/AssetRipper/releases/download/2.0.0/AssetRipper_win_x64.zip',
    '9a7ef0e7c5c3ea5b90b4e6d855e2d98d5f7ec8c3f9e26fccbc194c6a7b01baf7',
)
# The .NET SDKs, from Microsoft's zips, pinned to the SHA-512 in Microsoft's release metadata
# (builds.dotnet.microsoft.com/dotnet/release-metadata/<major>/releases.json): .NET 8 for the builder's
# TypeModelGen, .NET 10 for Rewind.
DOTNET_VERSION = '8.0.425'
DOTNET_ZIP = (
    f'https://builds.dotnet.microsoft.com/dotnet/Sdk/{DOTNET_VERSION}/dotnet-sdk-{DOTNET_VERSION}-win-x64.zip',
    'f0b6f15bf6f1a0507205c0cb102ab99e1dee875c4682c8ed94665be1d580186a06b21455e83b3a01a0ff7f4cd887b67420f2e2fe09ed985534a4cea488ae1af9',
)
DOTNET = EXTERNAL / 'dotnet'
DOTNET10_ZIP = (
    f'https://builds.dotnet.microsoft.com/dotnet/Sdk/{DOTNET10_VERSION}/dotnet-sdk-{DOTNET10_VERSION}-win-x64.zip',
    '330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee',
)

GAME_DATA = f'{GAME_NAME}_Data'
METADATA = Path(GAME_DATA) / 'il2cpp_data' / 'Metadata' / 'global-metadata.dat'
# The game's code the rules are written for, by the SHA-256 of GameAssembly.dll and of global-metadata.dat
# (an IL2CPP build has no Assembly-CSharp.dll): the Steam release, build 24040403 (depot 1718464, manifest
# 503866610356912072), Unity 6000.0.60f1.
GAME_CODE = {
    (
        '2d7f522d1035eb181dc4161b20b8ce5c822805f8217f0b6826e0802907892c8c',
        'fed567b826eff1310181c843ac25b449cc8119709329f5cedc177640521022be',
    ): 'Steam build 24040403',
}

# Where setup.py extracts an SDK archive by default; the SDK is its sdk folder.
VITA_SDK_FOLDER = Path(r'C:\vitasdk')
# The PS Vita SDK files the PS Vita build runs, under SCE_PSP2_SDK_DIR.
SDK_TOOLS = (
    'host_tools/build/bin/psp2snc.exe',
    'host_tools/build/bin/psp2ld.exe',
    'host_tools/build/bin/arm-eabi-as.exe',
    'host_tools/build/bin/psp2snarl.exe',
    'host_tools/build/bin/psp2bin.exe',
    'host_tools/bin/psp2psarc.exe',
)
SDK_VERSION_FILE = 'target/include/sdk_version.h'
MIN_SDK = (3, 570)
# What setup.py downloads when the machine has no SDK: SDK 3.570 from archive.org's copy of the DevNet
# release, pinned to the SHA-1 in the item's metadata (archive.org lists no SHA-256).
VITA_SDK_ITEM = 'https://archive.org/download/psvita-sdk-3.570-devnet-full/SDK%20Software'
VITA_SDK_ZIP = (f'{VITA_SDK_ITEM}/PSVITA.zip', 'fbb3a9dfa24bc886eff02805e3005abdec755cb8')
VITA_SDK_DOWNLOADED = VITA_SDK_FOLDER / 'PSVITA' / 'sdk'  # where VITA_SDK_ZIP's SDK lands
# That SDK is 3.570.011. Sony's patches bring it to 3.570.071, applied over it in this order: the shader
# compiler psp2cgc (3.570.021), then libc (3.570.071).
VITA_SDK_PATCHED = (3, 570, 71)
_PATCHES = f'{VITA_SDK_ITEM}/Tools/Plugins%20-%20Addons/SDK%20Patches/%5BSDK%5D%20SDK%203.570%20Patch%20-%20'
VITA_SDK_PATCHES = (
    (f'{_PATCHES}psp2cgc-3.570.021/SDK-3_570_021-patch.zip', '5ecc5d6cefbeaa527a139a331d65d048d2238185'),
    (f'{_PATCHES}libc-3.570.071/SDK-3_570_071-P03460462-patch.zip', 'bd76d99979ba2d4881b533ca20c48a12edc01a2d'),
)

# Free space a first build needs on the repository's drive, in GB: Rewind's output (0.1), the
# Mono-layout copy of the game AssetRipper reads (0.6) and its export (0.6), the PS Vita project (0.7),
# the builder's work files and the build with its VPK (about 2), with room to spare.
FIRST_BUILD_GB = 6
REBUILD_GB = 3


@dataclass
class Check:
    name: str
    ok: bool
    detail: str = ''
    fix: str = ''
    required: bool = True


# ---------------------------------------------------------------- pure helpers


def parse_sdk_version(text):
    """(3, 570, 71) from sdk_version.h's '#define SCE_PSP2_SDK_VERSION 0x03570071', or None."""
    match = re.search(r'#define\s+SCE_PSP2_SDK_VERSION\s+0x([0-9A-Fa-f]{8})\b', text)
    if not match:
        return None
    digits = match.group(1)
    return int(digits[0:2]), int(digits[2:5]), int(digits[5:8])


def format_sdk_version(version):
    return f'{version[0]}.{version[1]:03d}.{version[2]:03d}'


def parse_library_folders(text):
    """The library paths in Steam's steamapps/libraryfolders.vdf (both the old and the new format)."""
    paths = [m.group(1) for m in re.finditer(r'"path"\s+"((?:[^"\\]|\\.)*)"', text)]
    if not paths:  # before 2021: "1" "D:\\SteamLibrary"
        paths = [m.group(1) for m in re.finditer(r'^\s*"\d+"\s+"((?:[^"\\]|\\.)*)"\s*$', text, re.MULTILINE)]
    return [p.replace('\\\\', '\\') for p in paths]


def game_candidates(steam_roots, read_text):
    """Where the game can be: every Steam library of every Steam install, in order, without repeats.
    read_text(path) returns a file's text or None."""
    found = []
    for root in steam_roots:
        libraries = [root]
        text = read_text(Path(root) / 'steamapps' / 'libraryfolders.vdf')
        if text:
            libraries += parse_library_folders(text)
        for library in libraries:
            candidate = Path(library) / 'steamapps' / 'common' / GAME_NAME
            if all(os.path.normcase(str(candidate)) != os.path.normcase(str(f)) for f in found):
                found.append(candidate)
    return found


def unity_version(header: bytes):
    """The Unity version in the header of a serialized file such as globalgamemanagers."""
    match = re.search(rb'(\d{4}\.\d+\.\d+[abfp]\d+)\0', header[:512])
    return match.group(1).decode() if match else None


def signer_is_unity(status, subject):
    """Get-AuthenticodeSignature's Status and SignerCertificate.Subject of a Unity installer."""
    return (
        status == 'Valid'
        and re.search(r'(^|,\s*)O=Unity Technologies( ApS| SF)?\s*(,|$)', subject, re.IGNORECASE) is not None
    )


def signer_is_microsoft(status, subject):
    """Get-AuthenticodeSignature's Status and SignerCertificate.Subject of a Microsoft installer."""
    microsoft = re.search(r'(^|,\s*)O=Microsoft Corporation\s*(,|$)', subject, re.IGNORECASE)
    return status == 'Valid' and microsoft is not None


def missing(root: Path, relatives):
    return [r for r in relatives if not (root / r).exists()]


def find_sdk_root(folder: Path, depth=4):
    """The folder holding host_tools/ and target/ (what SCE_PSP2_SDK_DIR names) in or below folder."""
    folder = Path(folder)
    if (folder / SDK_TOOLS[0]).exists():
        return folder
    if depth == 0 or not folder.is_dir():
        return None
    try:
        children = sorted(p for p in folder.iterdir() if p.is_dir())
    except OSError:
        return None
    for child in children:
        found = find_sdk_root(child, depth - 1)
        if found:
            return found
    return None


STATUS_STYLES = {'ok': 'green', 'MISSING': 'bold red', 'optional': 'yellow'}


def summarize(checks):
    """The checklist as lines (Text, each status in its colour), and the number of required items that
    failed."""
    lines, failed = [], 0
    width = max((len(c.name) for c in checks), default=0)
    for check in checks:
        status = 'ok' if check.ok else ('MISSING' if check.required else 'optional')
        failed += not check.ok and check.required
        line = Text(f'  [{status:^8}] {check.name:<{width}}  {check.detail}'.rstrip())
        line.stylize(STATUS_STYLES[status], 2, 12)
        lines.append(line)
        if not check.ok and check.fix:
            lines.append(Text(f'  {"":10} {"":<{width}}  fix: {check.fix}'))
    return lines, failed


# ---------------------------------------------------------------- the machine


def steam_roots():
    import winreg

    roots = [str(STEAM)]
    for key, path, name in (
        (winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath'),
        (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'),
        (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Valve\Steam', 'InstallPath'),
    ):
        try:
            with winreg.OpenKey(key, path) as handle:
                roots.append(str(Path(winreg.QueryValueEx(handle, name)[0])))
        except OSError:
            pass
    return roots


def read_text(path: Path):
    try:
        return path.read_text(encoding='utf-8', errors='replace')
    except OSError:
        return None


def find_game():
    """The game's folder in the first Steam library that has it, or None."""
    return next((c for c in game_candidates(steam_roots(), read_text) if (c / GAME_EXE).exists()), None)


def store_build(game: Path):
    """The install's Steam build ('Steam build 24040403', from its library's appmanifest), or None."""
    manifest = read_text(game.parent.parent / f'appmanifest_{STEAM_APP_ID}.acf') or ''
    match = re.search(r'"buildid"\s+"(\d+)"', manifest)
    return f'Steam build {match[1]}' if match else None


def game_release(game: Path):
    """The release GAME_CODE names for the game's GameAssembly.dll and global-metadata.dat, or None."""
    try:
        key = (file_hash(game / 'GameAssembly.dll'), file_hash(game / METADATA))
    except OSError:
        return None
    return GAME_CODE.get(key)


def sdk_version(sdk: Path):
    text = read_text(sdk / SDK_VERSION_FILE)
    return parse_sdk_version(text) if text else None


def sdk_status(sdk: Path):
    """(what is wrong with the SDK in a folder or None, its version as shown)."""
    gaps = missing(sdk, SDK_TOOLS)
    version = sdk_version(sdk)
    shown = format_sdk_version(version) if version else f'unknown, no {SDK_VERSION_FILE}'
    if gaps:
        return f'{sdk} lacks ' + ', '.join(Path(g).name for g in gaps), shown
    if version and version[:2] < MIN_SDK:
        return f'{sdk}: version {shown}; Unity needs {MIN_SDK[0]}.{MIN_SDK[1]} or later', shown
    return None, shown


def sdk_candidates():
    value, _ = env('SCE_PSP2_SDK_DIR')
    candidates = [Path(value)] if value else []
    candidates += [VITA_SDK_FOLDER / 'sdk', VITA_SDK_DOWNLOADED]
    for variable in ('ProgramFiles(x86)', 'ProgramFiles'):
        if os.environ.get(variable):
            candidates += sorted((Path(os.environ[variable]) / 'SCE' / 'PSP2 SDKs').glob('*'), reverse=True)
    return candidates


def dotnet_sdks(folder: Path):
    """The SDK versions in a .NET folder."""
    sdk = folder / 'sdk'
    return sorted(p.name for p in sdk.iterdir() if p.is_dir()) if sdk.is_dir() else []


def free_gb(path: Path):
    while not path.exists():
        path = path.parent
    return shutil.disk_usage(path).free / 2**30


def rewind_build_key():
    """What Rewind's build depends on: the pinned commit of the fork and the .NET SDK."""
    return f'commit {REWIND_COMMIT}\ndotnet {DOTNET10_VERSION}\n'


# ---------------------------------------------------------------- checks


PYTHON_MODULES = {
    'numpy': 'numpy',
    'PIL': 'Pillow',
    'UnityPy': 'UnityPy',
    'yaml': 'PyYAML',
    'dnfile': 'dnfile',
    'quicktex': 'quicktex',
    'truststore': 'truststore',
}


def check_python():
    for module, package in PYTHON_MODULES.items():
        if importlib.util.find_spec(module) is None:
            return Check('Python packages', False, f'no {package} (module {module!r})', 'uv sync')
    return Check('Python packages', True, ', '.join(PYTHON_MODULES.values()))


def check_csc():
    return Check(
        '.NET Framework C# compiler',
        CSC.exists(),
        str(CSC),
        'part of Windows (.NET Framework 4.8); turn it on in Windows features',
    )


def check_git():
    found = shutil.which('git')
    return Check('Git', bool(found), found or 'not on PATH (setup.py clones Rewind with it)', 'winget install Git.Git')


def check_game(game: Path):
    name = 'Terminator 2D: NO FATE'
    fix = f'{SETUP} --game <the folder with {GAME_EXE}>'
    if not (game / GAME_EXE).exists():
        found = find_game()
        hint = f'; found in {found}' if found and found != game else ''
        return Check(name, False, f'no {GAME_EXE} in {game}{hint}', fix)
    try:
        with (game / GAME_DATA / 'globalgamemanagers').open('rb') as handle:
            version = unity_version(handle.read(512))
    except OSError:
        version = None
    if version != SOURCE_VERSION:
        return Check(name, False, f'{game}: built with Unity {version or "?"}, the rules expect {SOURCE_VERSION}', fix)
    if not (game / 'GameAssembly.dll').exists() or not (game / METADATA).exists():
        return Check(name, False, f'{game}: no GameAssembly.dll or {METADATA.as_posix()} (not an IL2CPP build)', fix)
    release = game_release(game)
    if release is None:
        build = store_build(game)
        return Check(
            name,
            False,
            f'{game} ({f"{build}, " if build else ""}Unity {version}): another release than the rules were written '
            f'for ({", ".join(GAME_CODE.values())}); Rewind may recover it, but nothing checked it',
            'update the game, or have Steam verify its files',
            required=False,
        )
    return Check(name, True, f'{game} ({release}, Unity {version})')


def check_rewind():
    fix = SETUP
    if not REWIND_EXE.exists():
        return Check('Rewind', False, f'not built ({REWIND_EXE})', fix)
    stamp = read_text(REWIND_STAMP)
    if stamp != rewind_build_key():
        return Check('Rewind', False, 'built from another commit; setup.py builds it again', fix)
    return Check('Rewind', True, f'{REWIND_COMMIT[:7]} ({REWIND_EXE.parent})')


def check_signatures():
    return Check(
        f'Rewind signatures for {SOURCE_VERSION}',
        REWIND_SIGNATURES.exists(),
        str(REWIND_SIGNATURES.relative_to(ROOT))
        if REWIND_SIGNATURES.exists()
        else "not in the repository; recover falls back on Rewind's nearest library, which recovers less",
        required=False,
    )


def check_assetripper():
    return Check('AssetRipper 2.0.0', ASSETRIPPER.exists(), str(ASSETRIPPER.parent), SETUP)


def check_dotnet(folder: Path, major: str, what: str):
    sdks = [v for v in dotnet_sdks(folder) if v.startswith(major + '.')]
    ok = (folder / 'dotnet.exe').exists() and bool(sdks)
    return Check(
        f'.NET {major} SDK',
        ok,
        f'{folder} (SDK {", ".join(sdks)})' if ok else f'no .NET {major} SDK in {folder} ({what})',
        SETUP,
    )


def check_cecil():
    return Check(
        'Mono.Cecil',
        CECIL.exists(),
        str(CECIL.parent) if CECIL.exists() else 'not yet: pipeline.py fetches it from NuGet on first use',
        SETUP,
        required=False,
    )


def check_editor_data():
    """The Unity files the pipeline and the builder read, extracted from Unity's installers by setup.py."""
    data = editor_data(TARGET)
    fix = f'{SETUP} (extracts them from the Unity {TARGET} installers; installs nothing)'
    if not data.exists():
        return Check("Unity's files", False, f'no {data}', fix)
    gaps = missing(data, EDITOR_DATA_CHECKED)
    return Check("Unity's files", not gaps, f'{data} lacks ' + ', '.join(gaps) if gaps else str(data), fix)


def file_version(path: Path):
    """(major, minor, build, revision) from a DLL's version resource, or None."""
    import ctypes
    from ctypes import wintypes

    version = ctypes.WinDLL('version')
    size = version.GetFileVersionInfoSizeW(str(path), None)
    if not size:
        return None
    data = ctypes.create_string_buffer(size)
    if not version.GetFileVersionInfoW(str(path), 0, size, data):
        return None
    info, length = ctypes.c_void_p(), wintypes.UINT()
    if not version.VerQueryValueW(data, '\\', ctypes.byref(info), ctypes.byref(length)):
        return None
    fixed = ctypes.cast(info, ctypes.POINTER(wintypes.DWORD * 4)).contents  # signature, struct version, MS, LS
    return fixed[2] >> 16, fixed[2] & 0xFFFF, fixed[3] >> 16, fixed[3] & 0xFFFF


def vc_runtime():
    """The version of the 64-bit Visual C++ runtime (System32's msvcp140.dll), or None."""
    path = Path(os.environ.get('SYSTEMROOT', r'C:\Windows')) / 'System32' / 'msvcp140.dll'
    return file_version(path) if path.exists() else None


def check_vc_runtime():
    found = vc_runtime()
    shown = '.'.join(map(str, found)) if found else None
    ok = bool(found) and found[:2] >= VC_RUNTIME_MIN
    return Check(
        'Visual C++ runtime',
        ok,
        f'{shown} (msvcp140.dll)'
        if ok
        else f'{shown or "none"}; the builder needs {".".join(map(str, VC_RUNTIME_MIN))} or later',
        f'{SETUP} (installs the current Visual C++ Redistributable)',
    )


def check_vita_sdk():
    """The PS Vita SDK, through the variable the builder reads."""
    fix = f'{SETUP} (downloads the SDK), or --vita-sdk <your SDK archive or folder>'
    sdk, stale = env('SCE_PSP2_SDK_DIR')
    if not sdk:
        found = next((c for c in sdk_candidates() if (c / SDK_TOOLS[0]).exists()), None)
        return Check(
            'PS Vita SDK',
            False,
            'SCE_PSP2_SDK_DIR is not set' + (f'; an SDK is in {found}' if found else ''),
            f'{SETUP} --vita-sdk "{found}"' if found else fix,
        )
    problem, shown = sdk_status(Path(sdk))
    if problem:
        return Check('PS Vita SDK', False, problem, fix)
    return Check(
        'PS Vita SDK',
        True,
        f'{sdk} (version {shown})' + (' - set for new shells; this one does not see it' if stale else ''),
    )


VITA_HOST_VARIABLES = ('T2D_VITA_HOST', 'BUTCHER_VITA_HOST', 'HUNT_VITA_HOST', 'CROW_VITA_HOST', 'DRIFTER_VITA_HOST')


def check_vita_host():
    name, value = next(((n, env(n)[0]) for n in VITA_HOST_VARIABLES if env(n)[0]), (None, None))
    return Check(
        'Console address',
        bool(value),
        f'{name}={value}' if value else 'T2D_VITA_HOST is not set (only the console tools need it)',
        f"{SETUP} --vita-host <the console's IP address>",
        required=False,
    )


def check_space():
    from common import project_folder

    need = REBUILD_GB if project_folder(TARGET, 'vita').exists() else FIRST_BUILD_GB
    free = free_gb(WORK)
    return Check(
        'Free disk space',
        free >= need,
        f'{free:.0f} GB free on {ROOT.anchor}; a build from here needs about {need} GB',
        f'free space on {ROOT.anchor} or move the repository to a larger drive',
    )


def checks_for(game: Path):
    return [
        check_python(),
        check_csc(),
        check_git(),
        check_game(game),
        check_dotnet(DOTNET10, '10', 'Rewind'),
        check_rewind(),
        check_signatures(),
        check_assetripper(),
        check_dotnet(DOTNET, '8', "the builder's TypeModelGen"),
        check_cecil(),
        check_editor_data(),
        check_vc_runtime(),
        check_vita_sdk(),
        check_vita_host(),
        check_space(),
    ]


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog='setup.py doctor', description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.parse_args(argv)
    lines, failed = summarize(checks_for(default_game()))
    for line in [Text('Requirements for the PS Vita build:'), *lines]:
        progress.console.print(line)
    if failed:
        progress.console.print(Text(f'{failed} required item(s) missing.', 'bold red'))
        return 1
    progress.console.print(Text('Everything required is in place.', 'bold green'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
