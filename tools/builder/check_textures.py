"""Check the builder's textures against the editor's, byte for byte, on generated images.

    uv run tools/builder/check_textures.py reference    # make the reference with the editor (once, about 2 minutes)
    uv run tools/builder/check_textures.py --compare    # write the same textures with the builder and compare
    uv run tools/builder/check_textures.py --compare --project <project> --media <player Media> [--bundles <dir>]
    uv run tools/builder/check_textures.py --pages <repack.py --out> <repack.py --out --raw>

`reference` generates a small Unity project under work/builder/texture-reference/project: images made from a fixed
seed (no game content), each with the import settings of one case below, and builds it with the installed Unity
2018.2.19f1 editor and PS Vita module (texture_reference/TexRefBuild.cs): a PC Hosted player, whose resources.assets
and resources.assets.resS hold Assets/Resources/tex, and an AssetBundle of Assets/Bundled. The editor changes no
importer: the .meta files are the settings.

`--compare` pairs every Texture2D of the reference with its image by name, makes the object with textures.texture()
(appending to a .resS in the reference's path ID order, as build.py does) and compares the fields, the serialized
object bytes, the image data and the whole .resS. DXT data comes from another encoder and is reported, not
compared. Exit code 1 if anything that should match differs. --project/--media point it at another editor build,
the texture test's for one (tools/assess/vita-texture-test).

`--pages` imports every PNG page of tools/downgrade/atlas/repack.py with textures.texture() and compares the
texels with the raw page (--raw) of the same run: the two forms of the texture pages must hold the same texels.
"""

import argparse
import collections
import hashlib
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

import bundles
import numpy as np
import paths
import textures
import UnityPy
from check_imported import serialize
from PIL import Image

from assets import Asset

sys.path.insert(0, str(paths.ROOT / 'tools' / 'downgrade' / 'atlas'))
from repack import TEXTURE_META as ATLAS_META

HERE = Path(__file__).resolve().parent / 'texture_reference'
REFERENCE = paths.WORK / 'texture-reference'
PROJECT = REFERENCE / 'project'
BUILD = REFERENCE / 'build'
UNITY = Path(os.environ.get('LOCALAPPDATA', '')) / 'Unity' / 'Editors' / paths.TARGET / 'Editor' / 'Unity.exe'
TITLE_ID = 'TDRF00001'
BUNDLE = 'texref'

META = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  fileIDToRecycleName: {{}}
  externalObjects: {{}}
  serializedVersion: 7
  mipmaps:
    mipMapMode: 0
    enableMipMap: {mips}
    sRGBTexture: {srgb}
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
  isReadable: {readable}
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: {filter}
    aniso: {aniso}
    mipBias: {bias}
    wrapU: {wrap}
    wrapV: {wrap}
    wrapW: {wrap}
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: {ait}
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  platformSettings:
  - serializedVersion: 2
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 4096
    resizeAlgorithm: 0
    textureFormat: {default_format}
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
  - serializedVersion: 2
    buildTarget: PSP2
    maxTextureSize: 4096
    resizeAlgorithm: 0
    textureFormat: {psp2_format}
    textureCompression: 0
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: {psp2_overridden}
    androidETC2FallbackOverride: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID:
    vertices: []
    indices:
    edges: []
    weights: []
  spritePackingTag:
  pSDRemoveMatte: 0
  pSDShowRemoveMatteOption: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""
SETTINGS = {
    'mips': 0, 'srgb': 1, 'readable': 0, 'filter': 0, 'aniso': 1, 'bias': 0, 'wrap': 0, 'ait': 0,
    'default_format': -1, 'psp2_format': 13, 'psp2_overridden': 1,
}  # fmt: skip
RGBA4444 = 13

# name: (width, height, image kind, settings): 'atlas' takes repack.py's .meta as it is.
CASES = {
    'atlas_1024': (1024, 1024, 'noise', 'atlas'),
    'atlas_1024_quantized': (1024, 1024, 'quantized', 'atlas'),
    'atlas_256x2048': (256, 2048, 'noise', 'atlas'),
    'mip_256': (256, 256, 'noise', {'mips': 1, 'filter': 1}),
    'mip_linear_256x64': (256, 64, 'noise', {'mips': 1, 'srgb': 0}),
    'mip_tiny_4': (4, 4, 'noise', {'mips': 1}),
    'mip_npot_96x40': (96, 40, 'noise', {'mips': 1}),
    'npot_300x200': (300, 200, 'noise', {}),
    'one_1x1': (1, 1, 'noise', {}),
    'one_3x1': (3, 1, 'noise', {}),
    'transparency_128': (128, 128, 'holes', {'ait': 1}),
    'readable_64': (64, 64, 'noise', {'readable': 1}),
    'defaults_64': (64, 64, 'noise', {'filter': -1, 'aniso': -1, 'bias': -100, 'wrap': -1}),
    'default_platform_64': (64, 64, 'noise', {'default_format': RGBA4444, 'psp2_format': -1, 'psp2_overridden': 0}),
    'opaque_32': (32, 32, 'opaque', {}),
    'rgba32_mip_128': (128, 128, 'noise', {'mips': 1, 'psp2_format': textures.RGBA32}),
    'rgb24_mip_64x32': (64, 32, 'opaque', {'mips': 1, 'psp2_format': textures.RGB24}),
    'rgba32_mip_npot_96x40': (96, 40, 'noise', {'mips': 1, 'psp2_format': textures.RGBA32}),
    'rgba32_mip_npot_7x5': (7, 5, 'noise', {'mips': 1, 'psp2_format': textures.RGBA32}),
    'rgba32_mip_npot_linear_13x11': (13, 11, 'noise', {'mips': 1, 'srgb': 0, 'psp2_format': textures.RGBA32}),
    'rgba32_mip_transparency_64': (64, 64, 'holes', {'mips': 1, 'ait': 1, 'psp2_format': textures.RGBA32}),
    'mip_transparency_64': (64, 64, 'holes', {'mips': 1, 'ait': 1}),
}
BUNDLED = {
    'b_atlas_512': (512, 512, 'quantized', 'atlas'),
    'b_mip_32x8': (32, 8, 'noise', {'mips': 1}),
    'b_one_1x1': (1, 1, 'noise', {}),
}


def image(kind, width, height, seed):
    """Generated RGBA pixels, rows from the top: noise with every alpha, see-through holes, or opaque."""
    rng = np.random.default_rng(seed)
    rgba = rng.integers(0, 256, (height, width, 4), dtype=np.uint8)
    if kind == 'quantized':  # as repack.py writes its PNG pages: 4-bit values times 17
        rgba = rgba // 17 * 17
    elif kind == 'holes':
        y, x = np.mgrid[:height, :width]
        rgba[..., 3] = np.where((x // 16 + y // 16) % 3 == 0, 0, rgba[..., 3] | 128)
    elif kind == 'opaque':
        rgba[..., 3] = 255
    return rgba


def meta_text(name, settings):
    guid = hashlib.md5(f't2d-texref/{name}'.encode()).hexdigest()
    if settings == 'atlas':
        return ATLAS_META.format(guid=guid)
    return META.format(guid=guid, **{**SETTINGS, **settings})


def make_project():
    if PROJECT.exists():
        shutil.rmtree(PROJECT)
    assets = PROJECT / 'Assets'
    for folder, cases in (('Resources/tex', CASES), ('Bundled', BUNDLED)):
        (assets / folder).mkdir(parents=True)
        for seed, (name, (w, h, kind, settings)) in enumerate(sorted(cases.items())):
            Image.fromarray(image(kind, w, h, seed + (100 if folder == 'Bundled' else 0)), 'RGBA').save(
                assets / folder / f'{name}.png'
            )
            (assets / folder / f'{name}.png.meta').write_text(meta_text(name, settings), newline='\n')
    (assets / 'Editor').mkdir()
    shutil.copy2(HERE / 'TexRefBuild.cs', assets / 'Editor' / 'TexRefBuild.cs')
    (PROJECT / 'Packages').mkdir()
    (PROJECT / 'Packages' / 'manifest.json').write_text('{\n  "dependencies": {\n  }\n}\n')


def build_reference():
    if not UNITY.exists():
        sys.exit(f'No Unity {paths.TARGET} editor at {UNITY}')
    make_project()
    if BUILD.exists():
        shutil.rmtree(BUILD)
    log = REFERENCE / 'build.log'
    started = time.time()
    command = [
        str(UNITY), '-batchmode', '-nographics', '-buildTarget', 'PSP2', '-projectPath', str(PROJECT),
        '-executeMethod', 'TexRefBuild.Build', '-buildOutput', str(BUILD), '-vitaTitleId', TITLE_ID,
        '-logFile', str(log),
    ]  # fmt: skip
    result = subprocess.run(command, check=False)
    lines = log.read_text(encoding='utf-8', errors='replace').splitlines() if log.exists() else []
    for line in lines:
        if line.startswith(('TEXREF_', 'error CS')):
            print(line)
    # Unity 2018.2 exits with 1 after EditorApplication.Exit(0) in batch mode, so the log and the files decide.
    print(f'Unity exited {result.returncode} after {time.time() - started:.0f} s; log {log}')
    built = 'TEXREF_BUILD Succeeded errors=0' in lines and (BUILD / 'bundles' / BUNDLE).exists()
    return 0 if built and (BUILD / 'player' / 'Media' / 'resources.assets.resS').exists() else 1


def project_assets(project):
    """Texture assets of a project by name: Asset records textures.texture() takes."""
    found = {}
    for meta in sorted((project / 'Assets').rglob('*.png.meta')):
        source = meta.with_suffix('')
        guid = next(line.split()[1] for line in meta.read_text().splitlines() if line.startswith('guid:'))
        rel = source.relative_to(project).as_posix()
        found[source.stem] = Asset(guid, rel, '.png', str(meta), classes={textures.TEXTURE_FILE_ID: 28})
    return found


def compare_file(label, serialized, folder, inside, project, totals, problems):
    """The Texture2D objects of one serialized file (a path, or a name in `inside`, a bundle's files), each made
    again with textures.texture() in path ID order, and the .resS they make as a whole."""
    env = UnityPy.load(str(serialized) if inside is None else inside[serialized])
    sf = next(iter(env.files.values()))
    found = project_assets(project)
    streams = {}
    for pid in sorted(sf.objects):
        obj = sf.objects[pid]
        if obj.type.name != 'Texture2D':
            continue
        theirs = obj.read_typetree()
        name, sd, fmt = theirs['m_Name'], theirs['m_StreamData'], theirs['m_TextureFormat']
        asset = found.get(name)
        if asset is None:
            totals['differ'] += 1
            problems.append(f'{label} {name}: no image of that name in {project}')
            continue
        stream_name = sd['path'] or f'{label}.resS'
        stream = streams.setdefault(stream_name, textures.Stream(stream_name, bytearray()))
        try:
            mine = textures.texture(asset, label, stream, project=project)
        except NotImplementedError as error:
            totals['not supported'] += 1
            print(f'  {name:30} not supported: {str(error).rsplit(": ", 1)[-1]}')
            if sd['path']:  # the editor's bytes in its place, so the textures after it compare
                stream.add(stream_bytes(sd, folder, inside))
            continue
        o = mine['m_StreamData']
        ours = bytes(mine['image data']) or bytes(stream.data[o['offset'] : o['offset'] + o['size']])
        ref = bytes(theirs['image data']) or stream_bytes(sd, folder, inside)
        compressed = fmt in (textures.DXT1, textures.DXT5)
        skip = {'image data'} | ({'m_StreamData'} if compressed else set())
        fields = [k for k in theirs if k not in skip and mine.get(k) != theirs[k]]
        if compressed:  # another encoder: the sizes match, the bytes don't
            same_object, data = None, f'DXT from another encoder, {len(ours)} vs {len(ref)} bytes'
            ok = not fields and len(ours) == len(ref)
            totals['DXT'] += 1
        else:
            same_object = serialize(mine, 28) == obj.get_raw_data()
            data = first_difference(ours, ref)
            ok = not fields and same_object and data == 'identical'
            totals['identical' if ok else 'differ'] += 1
        print(
            f'  {name:30} format {fmt:2} {theirs["m_Width"]}x{theirs["m_Height"]} mips {theirs["m_MipCount"]}: '
            f'fields {"same" if not fields else "differ"}, '
            f'object {"-" if same_object is None else "identical" if same_object else "differs"}, data {data}'
        )
        if not ok:
            problems.append(f'{label} {name}: fields {fields}, object {same_object}, data {data}')
            problems += [f'    {k}: ours {str(mine.get(k))[:100]} | editor {str(theirs[k])[:100]}' for k in fields]
    for stream_name, stream in streams.items():
        if not stream.data:
            continue
        ours = bundles.stream_file('.resS', stream.data) if inside is not None else bytes(stream.data)
        verdict = first_difference(ours, stream_bytes({'path': stream_name, 'offset': 0, 'size': None}, folder, inside))
        if totals['DXT'] and verdict != 'identical':
            verdict += ' (with DXT data from another encoder)'
        elif verdict != 'identical':
            totals['differ'] += 1
            problems.append(f'{stream_name}: {verdict}')
        print(f'  {stream_name}: {len(ours)} bytes, {verdict}')


def stream_bytes(sd, folder, inside):
    if sd['path'].startswith('archive:'):
        data = inside[sd['path'].rsplit('/', 1)[1]]
    else:
        data = (folder / sd['path']).read_bytes()
    end = None if sd['size'] is None else sd['offset'] + sd['size']
    return data[sd['offset'] : end]


def first_difference(ours, ref):
    if ours == ref:
        return 'identical'
    at = next((i for i, (a, b) in enumerate(zip(ours, ref, strict=False)) if a != b), min(len(ours), len(ref)))
    return f'differs at byte {at} (sizes {len(ours)} vs editor {len(ref)})'


def compare(project, media, bundles_dir):
    totals, problems = collections.Counter(), []
    print(f'{media / "resources.assets"}')
    compare_file('resources.assets', media / 'resources.assets', media, None, project, totals, problems)
    for path in sorted(bundles_dir.iterdir()) if bundles_dir and bundles_dir.is_dir() else []:
        if path.suffix or path.name == bundles_dir.name:
            continue  # the manifests
        inside = dict(bundles.read_unityfs(path.read_bytes()))
        serialized = next(n for n in inside if '.' not in n)
        print(f'{path} ({serialized})')
        compare_file(f'archive:/{serialized}/{serialized}', serialized, path.parent, inside, project, totals, problems)
    print(
        f'{totals["identical"]} identical, {totals["differ"]} differ, {totals["DXT"]} DXT (sizes and fields only), '
        f'{totals["not supported"]} not supported'
    )
    for p in problems:
        print(p)
    return 1 if totals['differ'] else 0


def compare_pages(png_dir, raw_dir):
    """Each PNG page as the builder imports it against the raw page: the same RGBA 4444 texels."""
    pages = sorted(png_dir.glob('p[0-9][0-9][0-9].png'))
    if not pages:
        sys.exit(f'No PNG pages in {png_dir}')
    found = project_assets_in(png_dir)
    differ = []
    for page in pages:
        stream = textures.Stream('pages.resS', bytearray())
        made = textures.texture(found[page.stem], 'pages', stream, project=png_dir)
        raw = (raw_dir / f'{page.stem}.bytes').read_bytes()
        if made['m_TextureFormat'] != textures.RGBA4444 or bytes(stream.data) != raw:
            differ.append(page.stem)
    same = len(pages) - len(differ)
    print(f'{len(pages)} pages: {same} identical to their raw page, {len(differ)} differ {differ[:10]}')
    return 1 if differ else 0


def project_assets_in(folder):
    """Asset records for the PNGs of a folder that is not a project (paths relative to the folder)."""
    found = {}
    for meta in sorted(folder.glob('*.png.meta')):
        guid = next(line.split()[1] for line in meta.read_text().splitlines() if line.startswith('guid:'))
        source = meta.with_suffix('')
        found[source.stem] = Asset(guid, source.name, '.png', str(meta), classes={textures.TEXTURE_FILE_ID: 28})
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('action', nargs='?', choices=['reference'])
    parser.add_argument('--compare', action='store_true')
    parser.add_argument('--project', type=Path, default=PROJECT)
    parser.add_argument('--media', type=Path, default=BUILD / 'player' / 'Media')
    parser.add_argument('--bundles', type=Path, default=None, help="default: the generated reference's bundles")
    parser.add_argument('--pages', type=Path, nargs=2, metavar=('PNG_DIR', 'RAW_DIR'))
    args = parser.parse_args()
    if args.pages:
        return compare_pages(*args.pages)
    if args.action == 'reference':
        code = build_reference()
        if code or not args.compare:
            return code
    if not args.compare:
        parser.error('reference and/or --compare')
    bundles_dir = args.bundles or (BUILD / 'bundles' if args.project == PROJECT else None)
    return compare(args.project, args.media, bundles_dir)


if __name__ == '__main__':
    sys.exit(main())
