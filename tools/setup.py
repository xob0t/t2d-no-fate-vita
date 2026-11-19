"""Install what the pipeline needs, skipping what is already there, then check the result.

    uv run tools/setup.py                        # everything the PS Vita build needs
    uv run tools/setup.py --vita-sdk <your SDK archive or folder>
    uv run tools/setup.py doctor                 # the checklist alone
    uv run tools/setup.py download editor vita assetripper dotnet dotnet10 cecil --to <folder>

From huntdown-vita's setup (MIT, same author). It installs no Unity: it downloads the editor's and the
PS Vita module's installers and extracts what the pipeline and the PS Vita build (tools/builder) read,
about 1.2 GB (CGIncludes, the engine and class libraries, IL2CPP, the built-in resources, the PS Vita
player), into tools/external/unity-<version>, with 7-Zip (an installed one, or 7-Zip's own package from
GitHub). The Unity installers come from Unity's CDN, the PS Vita module from its archive.org copy.

Rewind, which recovers the game's code from its IL2CPP build, is this project's fork of it (its fixes are
commits there; see tools/rewind/README.md), cloned with Git at a pinned commit into tools/external/rewind
and built with the pinned .NET 10 SDK; a change of commit builds it again. REWIND_REPOSITORY (an
environment variable) clones from another URL or a local clone instead.

Every download is checked against a pinned hash (SHA-256; SHA-512 for the .NET SDKs, from Microsoft's
release metadata; SHA-1 for the PS Vita SDK's files, the only one archive.org lists), and the Unity
installers must carry Unity Technologies' Authenticode signature before anything is extracted from
them; Microsoft's Visual C++ Redistributable, which Microsoft updates in place, must carry Microsoft's.
With no PS Vita SDK on the machine, setup downloads SDK 3.570 from archive.org, extracts it to
C:\\vitasdk and patches it to 3.570.071; --vita-sdk takes your own copy instead. --dry-run reports what
would be done and changes nothing.
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / 'downgrade'))
import doctor
import lock
import progress
from common import (
    ASSETRIPPER,
    CECIL,
    CECIL_SHA256,
    CECIL_URL,
    CECIL_VERSION,
    DOTNET10,
    DOTNET10_VERSION,
    EXTERNAL,
    GAME_EXE,
    NUGET_SOURCE,
    REWIND,
    REWIND_COMMIT,
    REWIND_EXE,
    REWIND_REPOSITORY,
    REWIND_STAMP,
    ROOT,
    TARGET,
    WORK,
    default_game,
    download,
    finish_deleting,
    mono_cecil,
    replace_path,
    unity_files,
)
from rich.text import Text

DOWNLOADS = WORK / 'installers'
SETUP_LOCK_WAIT = 6 * 3600  # how long a setup waits for another job's pipeline (build.py's default)


class Setup:
    def __init__(self, args):
        self.args = args
        self.notes = []  # what the user still has to do

    def say(self, step, message):
        progress.console.print(Text.assemble((f'[{step}]', 'cyan'), f' {message}'))

    def confirm(self, question):
        if self.args.yes:
            return True
        if not sys.stdin.isatty():
            print(f'  {question} Not asked here: rerun with --yes to accept.')
            return False
        try:
            return input(f'  {question} [y/N] ').strip().lower() in ('y', 'yes')
        except EOFError:  # nothing to read the answer from (stdin is NUL, which counts as a terminal)
            print('\n  Not answered: rerun with --yes to accept.')
            return False

    # ------------------------------------------------------------ Python, AssetRipper, .NET, Mono.Cecil

    def python(self):
        uv = shutil.which('uv')
        if not uv:
            self.say('python', 'uv is not on PATH; install it (https://docs.astral.sh/uv/), then run uv sync')
            return
        if self.args.dry_run:
            self.say('python', 'would run uv sync')
            return
        subprocess.run([uv, 'sync'], cwd=ROOT, check=True)
        self.say('python', 'packages in sync with uv.lock')

    def assetripper(self):
        target = ASSETRIPPER.parent
        if ASSETRIPPER.exists():
            self.say('assetripper', f'already installed ({target})')
            return
        if self.args.dry_run:
            self.say('assetripper', f'would download AssetRipper 2.0.0 into {target}')
            return
        url, sha256 = doctor.ASSETRIPPER_ZIP
        with tempfile.TemporaryDirectory() as temp:
            archive = download(url, Path(temp) / 'AssetRipper_win_x64.zip', sha256)
            extract_zip(archive, target)
        if not ASSETRIPPER.exists():
            sys.exit(f'AssetRipper: the archive has no {ASSETRIPPER.name}')
        self.say('assetripper', f'installed into {target}')

    def dotnet(self, folder: Path, major: str, pin, what: str):
        """A .NET SDK from Microsoft's zip: .NET 8 for the builder's TypeModelGen, .NET 10 for Rewind."""
        check = doctor.check_dotnet(folder, major, what)
        if check.ok:
            self.say(f'dotnet{major}', f'already installed ({check.detail})')
            return
        url, sha512 = pin
        if self.args.dry_run:
            self.say(f'dotnet{major}', f'would download {url} and extract it into {folder}')
            return
        archive = download(url, DOWNLOADS / url.rsplit('/', 1)[1], sha512)
        partial = folder.with_name(folder.name + '.part')
        if partial.exists():
            shutil.rmtree(partial)
        with progress.step(f'Extracting {archive.name}'):
            extract_zip(archive, partial)
        if folder.exists():
            shutil.rmtree(folder)
        replace_path(partial, folder, attempts=20)  # a virus scanner can hold a folder just written
        if not self.args.keep_downloads:
            archive.unlink()
        self.say(f'dotnet{major}', f'.NET SDK installed into {folder}')

    def cecil(self):
        if CECIL.exists():
            self.say('cecil', f'Mono.Cecil {CECIL_VERSION} already installed ({CECIL.parent})')
        elif self.args.dry_run:
            self.say('cecil', f'would download Mono.Cecil {CECIL_VERSION} from NuGet into {CECIL.parent}')
        else:
            mono_cecil()
            self.say('cecil', f'Mono.Cecil {CECIL_VERSION} installed into {CECIL.parent}')

    # ------------------------------------------------------------ Rewind

    def rewind(self):
        """This project's fork of Rewind at REWIND_COMMIT, built with the .NET 10 SDK. An existing clone is
        pointed at REWIND_REPOSITORY, fetched and checked out at the commit, with local changes dropped."""
        key = doctor.rewind_build_key()
        if REWIND_EXE.exists() and doctor.read_text(REWIND_STAMP) == key:
            self.say('rewind', f'already built at {REWIND_COMMIT[:7]} ({REWIND_EXE.parent})')
            return
        if self.args.dry_run:
            self.say(
                'rewind',
                f'would clone {REWIND_REPOSITORY} at {REWIND_COMMIT[:7]} into {REWIND} '
                f'and build it with .NET {DOTNET10_VERSION}',
            )
            return
        git = shutil.which('git')
        if not git:
            sys.exit('Rewind: Git is not on PATH; install it (winget install Git.Git) and run setup again')
        if not (REWIND / '.git').exists():
            if REWIND.exists():
                shutil.rmtree(REWIND)
            with progress.step('Cloning Rewind'):
                subprocess.run([git, 'clone', '--quiet', REWIND_REPOSITORY, str(REWIND)], check=True)
        else:
            # A clone made before the switch to the fork (or from another REWIND_REPOSITORY) fetches from here now.
            subprocess.run([git, '-C', str(REWIND), 'remote', 'set-url', 'origin', REWIND_REPOSITORY], check=True)
            with progress.step('Fetching Rewind'):
                subprocess.run([git, '-C', str(REWIND), 'fetch', '--quiet', 'origin'], check=False)
        # The pinned commit, with nothing else in the working tree.
        subprocess.run([git, '-C', str(REWIND), 'checkout', '--quiet', '--force', REWIND_COMMIT], check=True)
        subprocess.run([git, '-C', str(REWIND), 'clean', '--quiet', '-fdx', '-e', 'bin/', '-e', 'obj/'], check=True)
        dotnet = DOTNET10 / 'dotnet.exe'
        project = REWIND / 'src' / 'Rewind.Cli' / 'Rewind.Cli.csproj'
        log_path = WORK / 'rewind-build.log'
        REWIND_STAMP.unlink(missing_ok=True)
        progress.run(
            'Building Rewind',
            [dotnet, 'build', '-c', 'Release', '--disable-build-servers', '--source', NUGET_SOURCE, project],
            log_path,
        )
        if not REWIND_EXE.exists():
            sys.exit(f'Rewind: the build wrote no {REWIND_EXE}; see {log_path}')
        REWIND_STAMP.write_text(key, encoding='utf-8')
        self.say('rewind', f'built at {REWIND_COMMIT[:7]} ({REWIND_EXE.parent})')

    # ------------------------------------------------------------ Unity's files

    def unity_files(self):
        """What the pipeline and the PS Vita build read from Unity's editor and PS Vita module installers,
        extracted with 7-Zip into tools/external/unity-<version>: nothing is installed or run."""
        check = doctor.check_editor_data()
        target = unity_files(TARGET)
        if check.ok and target.exists():
            self.say('unity-files', f'already in place ({check.detail})')
            return
        if self.args.dry_run:
            self.say(
                'unity-files',
                f'would download the Unity {TARGET} editor and PS Vita module installers and extract their files '
                f'into {target}',
            )
            return
        free = doctor.free_gb(EXTERNAL)
        if free < doctor.UNITY_FILES_GB:
            sys.exit(
                f"Unity's files: {free:.1f} GB free on {EXTERNAL.anchor}; they need about {doctor.UNITY_FILES_GB} GB"
            )
        seven = self.seven_zip_nsis()
        partial = target.with_name(target.name + '.part')
        if partial.exists():
            shutil.rmtree(partial)
        for module in ('editor', 'vita'):
            url, sha256 = doctor.INSTALLERS[module]
            installer = DOWNLOADS / url.rsplit('/', 1)[1]
            download(url, installer, sha256)
            verify_unity_signature(installer)
            out = partial if module == 'editor' else partial / 'vita'
            with progress.step(f'Extracting from {installer.name}'):
                # 7-Zip exits with 1 for a warning: it reads the installer's script but not its commands.
                code = subprocess.run(
                    [seven, 'x', '-y', '-bso0', '-bsp0', f'-o{out}', str(installer), *doctor.UNITY_FILES[module]],
                    check=False,
                ).returncode
            if code not in (0, 1):
                sys.exit(f'7-Zip exited with code {code} extracting {installer.name}')
            if not self.args.keep_downloads:
                installer.unlink()
        player = partial / 'Editor' / 'Data' / 'PlaybackEngines' / 'PSP2Player'
        player.parent.mkdir(parents=True, exist_ok=True)
        replace_path(partial / 'vita' / doctor.UNITY_FILES_VITA_FOLDER, player)
        shutil.rmtree(partial / 'vita')
        if target.exists():
            shutil.rmtree(target)
        replace_path(partial, target)
        check = doctor.check_editor_data()
        if not check.ok:
            sys.exit(f"Unity's files: {check.detail}")
        self.say('unity-files', f'extracted into {target}')

    def seven_zip_nsis(self):
        """A 7-Zip that reads NSIS installers: an installed one, or its package unpacked with 7zr into
        tools/external/7-zip."""
        installed = seven_zip()
        if installed and Path(installed).stem.lower() == '7z':  # 7za has no NSIS reader
            return installed
        local = EXTERNAL / '7-zip' / '7z.exe'
        if not local.exists():
            url, sha256 = doctor.SEVEN_ZIP['7zr']
            seven_zr = download(url, DOWNLOADS / '7zr.exe', sha256)
            url, sha256 = doctor.SEVEN_ZIP['package']
            package = download(url, DOWNLOADS / url.rsplit('/', 1)[1], sha256)
            self.say('unity-files', f'unpacking 7-Zip into {local.parent}')
            subprocess.run([str(seven_zr), 'x', '-y', '-bso0', '-bsp0', f'-o{local.parent}', str(package)], check=True)
            for path in (seven_zr, package):
                path.unlink()
        return str(local)

    def vc_runtime(self):
        """Microsoft's current Visual C++ Redistributable, when System32's msvcp140.dll is older than the
        builder's native packages need."""
        check = doctor.check_vc_runtime()
        if check.ok:
            self.say('vc-runtime', f'Visual C++ runtime {check.detail}')
            return
        if self.args.dry_run:
            self.say('vc-runtime', f'{check.detail}: would install {doctor.VC_REDIST_URL}')
            return
        installer = DOWNLOADS / 'vc_redist.x64.exe'
        download(doctor.VC_REDIST_URL, installer, None)
        status, subject = authenticode(installer)
        if not doctor.signer_is_microsoft(status, subject):
            sys.exit(
                f'{installer.name}: no valid Authenticode signature of Microsoft (status {status}, '
                f'signer {subject or "none"}). Not running it.'
            )
        print(f'  signature: {status}, {subject}')
        self.say('vc-runtime', f'{check.detail}: installing the current Visual C++ Redistributable')
        with progress.step('Installing the Visual C++ Redistributable'):
            code = run_elevated(installer, '/install /quiet /norestart')
        installer.unlink(missing_ok=True)
        # 3010: installed, a restart pending; 1638: a newer version is already installed.
        if code not in (0, 3010, 1638) or not doctor.check_vc_runtime().ok:
            sys.exit(f'{installer.name} exited with code {code}; {doctor.check_vc_runtime().detail}')
        self.say('vc-runtime', f'Visual C++ runtime {doctor.check_vc_runtime().detail}')

    # ------------------------------------------------------------ PS Vita SDK

    def vita_sdk(self):
        source = self.args.vita_sdk
        found = None
        if source:
            sdk = self.install_sdk(Path(source))
        else:
            found = next((c for c in doctor.sdk_candidates() if (c / doctor.SDK_TOOLS[0]).exists()), None)
            sdk = found or self.download_sdk()
        if sdk is None:
            if self.args.dry_run:
                self.say('vita-sdk', 'would set SCE_PSP2_SDK_DIR')
            return
        if not source and (found is None or found == doctor.VITA_SDK_DOWNLOADED):
            self.patch_sdk(sdk)
        self.validate_sdk(sdk)
        # An SDK found on the machine is offered; one passed with --vita-sdk or downloaded is what the user asked for.
        self.set_env('vita-sdk', 'SCE_PSP2_SDK_DIR', sdk, ask=found is not None)

    def download_sdk(self):
        """SDK 3.570 from archive.org, extracted as an archive passed with --vita-sdk is."""
        url, digest = doctor.VITA_SDK_ZIP
        archive = DOWNLOADS / url.rsplit('/', 1)[1]
        destination = Path(self.args.vita_sdk_dir or doctor.VITA_SDK_FOLDER)
        existing = doctor.find_sdk_root(destination) if destination.exists() else None
        if existing:  # extracted there by an earlier run with --vita-sdk-dir
            self.say('vita-sdk', f'already installed ({existing})')
            return existing
        if self.args.dry_run:
            self.say(
                'vita-sdk',
                f'no PS Vita SDK found; would download {url}, extract it to {destination} and patch it to '
                + doctor.format_sdk_version(doctor.VITA_SDK_PATCHED),
            )
            return None
        self.say('vita-sdk', 'no PS Vita SDK found; downloading SDK 3.570 (350 MB)')
        download(url, archive, digest)
        sdk = self.install_sdk(archive)
        if not self.args.keep_downloads:
            archive.unlink()
        return sdk

    def patch_sdk(self, sdk: Path):
        """Sony's patches over the downloaded SDK, up to VITA_SDK_PATCHED. Each zip holds sdk/..., the files
        it replaces."""
        if (doctor.sdk_version(sdk) or (0, 0, 0)) >= doctor.VITA_SDK_PATCHED or self.args.dry_run:
            return
        for url, digest in doctor.VITA_SDK_PATCHES:
            patch = download(url, DOWNLOADS / url.rsplit('/', 1)[1], digest)
            self.say('vita-sdk', f'applying {patch.name}')
            extract_zip(patch, sdk.parent)
            if not self.args.keep_downloads:
                patch.unlink()

    def install_sdk(self, source: Path):
        if not source.exists():
            sys.exit(f'--vita-sdk: {source} does not exist')
        if source.is_dir() and (
            not self.args.vita_sdk_dir or Path(self.args.vita_sdk_dir).resolve() == source.resolve()
        ):
            sdk = doctor.find_sdk_root(source)
            if sdk is None:
                sys.exit(f'--vita-sdk: no PS Vita SDK in {source} (looked for {doctor.SDK_TOOLS[0]})')
            self.say('vita-sdk', f'using the SDK in place: {sdk}')
            return sdk
        destination = Path(self.args.vita_sdk_dir or doctor.VITA_SDK_FOLDER)
        existing = doctor.find_sdk_root(destination) if destination.exists() else None
        if existing:
            self.say('vita-sdk', f'already installed ({existing})')
            return existing
        if self.args.dry_run:
            self.say('vita-sdk', f'would {"copy" if source.is_dir() else "extract"} {source} to {destination}')
            return None
        if source.is_dir():
            self.say('vita-sdk', f'copying {source} to {destination}')
            with progress.step(f'Copying {source.name}'):
                shutil.copytree(source, destination, dirs_exist_ok=True)
        else:
            command = extract_command(source, destination, seven_zip(), windows_tar())
            self.say('vita-sdk', f'extracting {source.name} to {destination}')
            destination.mkdir(parents=True, exist_ok=True)
            with progress.step(f'Extracting {source.name}'):
                if command is None:
                    extract_zip(source, destination)
                else:
                    subprocess.run(command, check=True)
        sdk = doctor.find_sdk_root(destination)
        if sdk is None:
            sys.exit(f'--vita-sdk: no PS Vita SDK in {destination} after extracting (looked for {doctor.SDK_TOOLS[0]})')
        return sdk

    def validate_sdk(self, sdk: Path):
        problem, shown = doctor.sdk_status(sdk)
        if problem:
            sys.exit(f'PS Vita SDK: {problem}')
        self.say('vita-sdk', f'{sdk}: version {shown}, tools present')

    # ------------------------------------------------------------ environment

    def set_env(self, step, name, value, ask=False):
        value = str(value)
        current, stale = doctor.env(name)
        if current and os.path.normcase(current.rstrip('\\')) == os.path.normcase(value.rstrip('\\')):
            self.say(step, f'{name} already set to {current}' + (' (open a new shell to see it)' if stale else ''))
            return
        if self.args.dry_run:
            self.say(step, f'would set {name}={value} for your user' + (f' (now {current})' if current else ''))
            return
        if ask and not self.confirm(f'Set {name}={value} for your user?'):
            self.notes.append(f'Set {name}: uv run tools/setup.py --yes ... or setx {name} "{value}"')
            return
        set_user_env(name, value)
        self.say(step, f'{name} set to {value} for your user; new shells see it')

    def game(self):
        if self.args.game:
            game = Path(self.args.game)
            if not (game / GAME_EXE).exists():
                sys.exit(f'--game: no {GAME_EXE} in {game}')
            self.set_env('game', 'T2D_GAME', game)
            return
        current, _ = doctor.env('T2D_GAME')
        if current:
            if (Path(current) / GAME_EXE).exists():
                self.say('game', f'already found: T2D_GAME={current}')
            else:
                self.say('game', f'T2D_GAME={current} has no {GAME_EXE}; pass --game <folder>')
                self.notes.append('Point T2D_GAME at the game: uv run tools/setup.py --game <folder>')
            return
        found = doctor.find_game()
        if found is None:
            self.say('game', 'Terminator 2D: NO FATE is in no Steam library; install it, or pass --game <folder>')
            self.notes.append('Install Terminator 2D: NO FATE, or: uv run tools/setup.py --game <folder>')
        elif os.path.normcase(str(found)) == os.path.normcase(str(default_game())):
            self.say('game', f'already found in {found}, where the pipeline looks for it')
        else:
            self.say('game', f'found in {found}')
            self.set_env('game', 'T2D_GAME', found, ask=True)

    def vita_host(self):
        host = self.args.vita_host
        if not re.fullmatch(r'[A-Za-z0-9.:-]+', host):
            sys.exit(f'--vita-host: {host!r} is not an address')
        self.set_env('vita-host', 'T2D_VITA_HOST', host)


# ---------------------------------------------------------------- helpers


def extract_zip(archive: Path, destination: Path):
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as handle:
        handle.extractall(destination)


def seven_zip():
    found = shutil.which('7z') or shutil.which('7za')
    if found:
        return found
    for variable in ('ProgramFiles', 'ProgramFiles(x86)'):
        candidate = Path(os.environ.get(variable, '')) / '7-Zip' / '7z.exe'
        if os.environ.get(variable) and candidate.exists():
            return str(candidate)
    return None


def windows_tar():
    """Windows' own tar (bsdtar), which also reads .7z and .rar; Git's GNU tar on PATH does not."""
    candidate = Path(os.environ.get('WINDIR', r'C:\Windows')) / 'System32' / 'tar.exe'
    return str(candidate) if candidate.exists() else None


def extract_command(archive: Path, destination: Path, seven, tar):
    """How to extract an archive: None for a .zip (Python's zipfile), else 7-Zip's or Windows' tar command."""
    if archive.suffix.lower() == '.zip':
        return None
    if seven:
        return [seven, 'x', '-y', '-bso0', '-bsp0', f'-o{destination}', str(archive)]  # quiet: under a step
    if tar:
        return [tar, '-xf', str(archive), '-C', str(destination)]
    sys.exit(
        f'No tool to extract {archive.name}: install 7-Zip (winget install 7zip.7zip) or extract it yourself '
        'and pass the folder'
    )


def authenticode(path: Path):
    """(Status, signer subject) from PowerShell's Get-AuthenticodeSignature."""
    script = (
        '$s = Get-AuthenticodeSignature -LiteralPath $env:SETUP_SIGNED_FILE; '
        '$s.Status.ToString(); if ($s.SignerCertificate) { $s.SignerCertificate.Subject }'
    )
    with progress.step(f'Checking the signature of {path.name}'):
        result = subprocess.run(
            ['powershell', '-NoProfile', '-NonInteractive', '-Command', script],
            capture_output=True,
            text=True,
            env={**os.environ, 'SETUP_SIGNED_FILE': str(path)},
            check=False,
        )
    lines = result.stdout.strip().splitlines()
    return (lines[0].strip() if lines else 'unknown'), (lines[1].strip() if len(lines) > 1 else '')


def verify_unity_signature(path: Path):
    status, subject = authenticode(path)
    if not doctor.signer_is_unity(status, subject):
        sys.exit(
            f'{path.name}: no valid Authenticode signature of Unity Technologies (status {status}, '
            f'signer {subject or "none"}). Not running it.'
        )
    print(f'  signature: {status}, {subject}')


def run_elevated(program, arguments: str):
    print('  Windows asks for administrator rights for this installer.', flush=True)
    script = (
        '$p = Start-Process -FilePath $env:SETUP_PROGRAM -ArgumentList $env:SETUP_ARGUMENTS -Verb RunAs '
        '-Wait -PassThru; exit $p.ExitCode'
    )
    return subprocess.run(
        ['powershell', '-NoProfile', '-NonInteractive', '-Command', script],
        env={**os.environ, 'SETUP_PROGRAM': str(program), 'SETUP_ARGUMENTS': arguments},
        check=False,
    ).returncode


def set_user_env(name, value):
    """Set a variable for the user (HKCU\\Environment) and tell running programs, so new shells,
    Git Bash included, see it."""
    import winreg

    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Environment', 0, winreg.KEY_SET_VALUE) as key:
        winreg.SetValueEx(key, name, 0, winreg.REG_SZ, value)
    os.environ[name] = value
    environment_changed()


def environment_changed():
    import ctypes

    result = ctypes.c_ulong()
    ctypes.windll.user32.SendMessageTimeoutW(0xFFFF, 0x001A, 0, 'Environment', 0x0002, 5000, ctypes.byref(result))


DOWNLOADABLE = [*doctor.INSTALLERS, 'assetripper', 'dotnet', 'dotnet10', 'cecil']


def download_only(args):
    """Download and verify installers into a folder without installing anything."""
    folder = Path(args.to)
    for item in args.items:
        if item == 'dotnet':
            url, digest = doctor.DOTNET_ZIP  # SHA-512
        elif item == 'dotnet10':
            url, digest = doctor.DOTNET10_ZIP  # SHA-512
        elif item == 'assetripper':
            url, digest = doctor.ASSETRIPPER_ZIP
        elif item == 'cecil':
            url, digest = CECIL_URL, CECIL_SHA256
        else:
            url, digest = doctor.INSTALLERS[item]
        path = download(url, folder / url.rsplit('/', 1)[1], digest)
        print(f'{item}: {path} ({"SHA-512" if len(digest) == 128 else "SHA-256"} matches)')
        if item in doctor.INSTALLERS:
            verify_unity_signature(path)


def main():
    if sys.argv[1:2] == ['doctor']:
        sys.exit(doctor.main(sys.argv[2:]))
    if sys.argv[1:2] == ['download']:
        parser = argparse.ArgumentParser(prog='setup.py download', description=download_only.__doc__)
        parser.add_argument('items', nargs='+', choices=DOWNLOADABLE)
        parser.add_argument('--to', required=True, help='the folder to download into')
        download_only(parser.parse_args(sys.argv[2:]))
        return
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--vita-sdk', metavar='PATH', help='your own PS Vita SDK: an archive or a folder')
    parser.add_argument(
        '--vita-sdk-dir',
        metavar='DIR',
        help=f'where to extract (or copy) the SDK; default {doctor.VITA_SDK_FOLDER} for an archive, '
        'in place for a folder',
    )
    parser.add_argument('--vita-host', metavar='ADDRESS', help="set T2D_VITA_HOST, the console's address")
    parser.add_argument('--game', metavar='FOLDER', help='set T2D_GAME to this copy of the game')
    parser.add_argument('--yes', '-y', action='store_true', help='accept the offers (set variables)')
    parser.add_argument('--dry-run', action='store_true', help='report what would be done, change nothing')
    parser.add_argument(
        '--keep-downloads', action='store_true', help=f'keep the installers and the SDK archive in {DOWNLOADS}'
    )
    parser.add_argument('--then-build', action='store_true', help=argparse.SUPPRESS)  # build.py builds next
    args = parser.parse_args()
    setup = Setup(args)
    # The changes under the vita-build lock (build.py runs setup inside its own): two setups at once both cloned or
    # checked out Rewind, and a setup changed tools/external under a running pipeline.
    holder = f'setup.py {os.getpid()}: setup'
    locking = not args.dry_run and not lock.ours('vita-build')
    if locking:
        WORK.mkdir(parents=True, exist_ok=True)
    if locking and lock.take('vita-build', holder, SETUP_LOCK_WAIT, process=True) != 0:
        sys.exit(f'setup: another job held the vita-build lock for {SETUP_LOCK_WAIT} s; try again later')
    try:
        if not args.dry_run:  # what an interrupted extraction or delete left, so nothing half there is trusted
            finish_deleting(EXTERNAL, WORK)
        setup.python()
        setup.unity_files()
        setup.vc_runtime()
        setup.dotnet(DOTNET10, '10', doctor.DOTNET10_ZIP, 'Rewind')
        setup.rewind()
        setup.assetripper()
        setup.dotnet(doctor.DOTNET, '8', doctor.DOTNET_ZIP, "the builder's TypeModelGen")
        setup.cecil()
        setup.game()
        setup.vita_sdk()
        if args.vita_host:
            setup.vita_host()
    finally:
        if locking:
            lock.release('vita-build', holder)
    print()
    if args.dry_run:
        print('Dry run: nothing was changed.')
        return
    if DOWNLOADS.exists() and not any(DOWNLOADS.iterdir()):
        DOWNLOADS.rmdir()
    code = doctor.main([])
    for note in setup.notes:
        print('To do: ' + note)
    if code == 0 and not setup.notes and not args.then_build:
        print('Next: uv run build.py')
    sys.exit(code or (1 if args.then_build and setup.notes else 0))  # build.py stops for the to-dos


if __name__ == '__main__':
    main()
