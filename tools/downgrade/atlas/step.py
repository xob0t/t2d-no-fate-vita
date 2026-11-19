# ruff: noqa: UP031
"""The pipeline's texture step: pipeline.asset_overlay calls atlas_overlay, which runs repack.py when its inputs
changed and returns the pages and tables as overlay entries for Assets/Resources/atlas, with the exported texture
packs left out of the project (docs/textures.md).

Inputs: Rewind's Assembly-CSharp.dll (the recover stage's, not the rewritten one: the rules replace the very tables
repack.py reads) and the export's Resources/texturepack/TexturePackN.bytes. Output: work/atlas (the files, a stamp)
and artifacts/downgrade/atlas (report.json, memory.md).
"""

import hashlib
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RESOURCES = Path('Assets') / 'Resources'
PACKS = RESOURCES / 'texturepack'
STAMP = 't2d-atlas.txt'


def _stamp(assembly: Path, packs: Path, page: int, raw: bool):
    digest = hashlib.sha256()
    for path in [assembly, *sorted(packs.glob('TexturePack*.bytes')), HERE / 'scenes.json', *sorted(HERE.glob('*.py'))]:
        digest.update(path.name.encode() + b'\0')
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    digest.update(('%d %d' % (page, raw)).encode())
    return digest.hexdigest()


def atlas_overlay(exported: Path, assembly: Path, work: Path, report: Path, page=1024, raw=True, log=print):
    """(overlay, excluded): overlay maps Assets/Resources/atlas/<file> to the file in work/atlas/atlas; excluded
    lists the export's texture packs (and their folder), which the game no longer loads.
    raw: pages as raw RGBA 4444 TextAssets (repack.py --raw), the default while tools/builder imports textures
    only as RGB24, RGBA32, DXT1 and DXT5; False: PNGs with RGBA 4444 import settings."""
    packs = exported / PACKS
    if not packs.is_dir():
        sys.exit(f'No texture packs in {packs}: run pipeline.py rip first')
    out = work / 'atlas'
    stamp = work / STAMP
    current = _stamp(assembly, packs, page, raw)
    if not (stamp.exists() and stamp.read_text() == current and (out / 'frames.bytes').exists()):
        log('Repacking the texture packs into pages (tools/downgrade/atlas/repack.py)')
        command = [
            sys.executable,
            '-I',
            str(HERE / 'repack.py'),
            '--asm',
            str(assembly),
            '--packs',
            str(packs),
            '--out',
            str(out),
            '--report',
            str(report),
            '--page',
            str(page),
        ] + (['--raw'] if raw else [])
        code = subprocess.run(command, check=False).returncode
        if code:
            sys.exit(f'The texture repacker failed (exit code {code}); see {report / "report.json"}')
        stamp.write_text(current)
    overlay = {RESOURCES / 'atlas' / f.name: f for f in sorted(out.iterdir()) if f.is_file()}
    folder_guid = hashlib.md5(b'folder:Assets/Resources/atlas').hexdigest()
    overlay[RESOURCES / 'atlas.meta'] = (
        f'fileFormatVersion: 2\nguid: {folder_guid}\nfolderAsset: yes\nDefaultImporter:\n  userData:\n'.encode()
    )
    excluded = {PACKS, PACKS.with_name(PACKS.name + '.meta')}
    excluded |= {PACKS / f.name for f in packs.iterdir()}
    return overlay, excluded
