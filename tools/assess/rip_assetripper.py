"""Export a Unity game folder with AssetRipper 2.0.0 headless, as huntdown-vita's `rip` stage does
(tools/downgrade/pipeline.py there): start the GUI exe with --headless on a free port, POST the
settings form to /Settings/Update, POST /LoadFolder, POST /Export/UnityProject, then kill it.

usage: python -I tools/assess/rip_assetripper.py <game folder> <output folder> <log file> [target version]
"""

import socket
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'tools' / 'external' / 'assetripper-2.0.0' / 'AssetRipper.GUI.Free.exe'

# huntdown-vita's RIP_SETTINGS, unchanged.
SETTINGS = {
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


def post(base, path, fields):
    data = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(base + path, data=data, method='POST')
    with urllib.request.urlopen(request, timeout=3600) as response:
        return response.read()


def main():
    game, out, log_path = (Path(a).resolve() for a in sys.argv[1:4])
    version = sys.argv[4] if len(sys.argv) > 4 else '2018.2.19f1'
    if out.exists():
        sys.exit(f'{out} exists; remove it first')
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    log_path.parent.mkdir(parents=True, exist_ok=True)
    file_log = log_path.with_name(log_path.stem + '-file.log')
    # AssetRipper writes temp/ beside its exe; run it from its own folder.
    with open(log_path, 'w', encoding='utf-8') as log_file:
        server = subprocess.Popen(
            [str(EXE), '--headless', '--port', str(port), '--log-path', str(file_log)],
            stdout=log_file, stderr=subprocess.STDOUT, cwd=EXE.parent,
        )
        try:
            for _ in range(120):
                try:
                    urllib.request.urlopen(base + '/', timeout=2)
                    break
                except OSError:
                    time.sleep(0.5)
            else:
                raise RuntimeError('AssetRipper did not start')
            post(base, '/Settings/Update', {**SETTINGS, 'TargetVersion': version})
            page = urllib.request.urlopen(base + '/Settings/Edit').read().decode()
            (log_path.with_name(log_path.stem + '-settings.html')).write_text(page, encoding='utf-8')
            if f'name="TargetVersion" value="{version}"' not in page:
                raise RuntimeError('AssetRipper rejected the target version setting')
            t0 = time.time()
            post(base, '/LoadFolder', {'path': str(game)})
            t1 = time.time()
            print(f'load {t1 - t0:.1f}s', flush=True)
            post(base, '/Export/UnityProject', {'path': str(out)})
            t2 = time.time()
            print(f'export {t2 - t1:.1f}s total {t2 - t0:.1f}s', flush=True)
        finally:
            server.kill()
    pv = out / 'ExportedProject' / 'ProjectSettings' / 'ProjectVersion.txt'
    print(pv.read_text().strip() if pv.exists() else 'no ProjectVersion.txt: export produced no project')


if __name__ == '__main__':
    main()
