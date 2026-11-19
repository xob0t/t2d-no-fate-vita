"""Reads the game's three custom shaders from its serialized files and writes, per shader, what
docs/shaders.md is checked against: the properties, tags, keywords and render state as stored, and
the D3D11 programs (DXBC) with their disassembly by Windows' own d3dcompiler_47.dll.

It only reads the game: pass the data folder or a copy of it. The output holds the game's compiled
programs, so it goes under work/ and is never committed.

    python -I tools/downgrade/shaders/extract.py "<game>/Terminator 2D NO FATE_Data" work/shaders/extract

Needs UnityPy (with lz4), which reads Unity 6's type trees."""

import ctypes
import json
import sys
from pathlib import Path

import lz4.block
import UnityPy

SHADERS = ('Unlit/CRTShader', 'Unlit/NormalShader', 'Unlit/ColorTransformShader')
D3D11 = 4  # ShaderCompilerPlatform.D3D11 in the platforms array
BLEND = ['Zero', 'One', 'DstColor', 'SrcColor', 'OneMinusDstColor', 'SrcAlpha', 'OneMinusSrcColor',
         'DstAlpha', 'OneMinusDstAlpha', 'SrcAlphaSaturate', 'OneMinusSrcAlpha']
ZTEST = ['Disabled', 'Never', 'Less', 'Equal', 'LEqual', 'Greater', 'NotEqual', 'GEqual', 'Always']
CULL = ['Off', 'Front', 'Back']


def disassemble(dxbc: bytes) -> str:
    """D3DDisassemble from the system's d3dcompiler_47.dll."""
    compiler = ctypes.WinDLL('d3dcompiler_47.dll')
    blob = ctypes.c_void_p()
    result = compiler.D3DDisassemble(dxbc, len(dxbc), 0, None, ctypes.byref(blob))
    if result != 0:
        raise OSError(f'D3DDisassemble failed: {result & 0xFFFFFFFF:#x}')
    vtable = ctypes.cast(ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
    pointer = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vtable[3])(blob)
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vtable[4])(blob)
    text = ctypes.string_at(pointer, size).decode('ascii', 'replace').rstrip('\0')
    ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtable[2])(blob)  # Release
    return text


def blob_entries(data: bytes):
    """The entries of a decompressed shader blob: count, then (offset, length, segment) per entry."""
    count = int.from_bytes(data[:4], 'little')
    entries = []
    for i in range(count):
        offset, length = (int.from_bytes(data[4 + 12 * i + k : 8 + 12 * i + k], 'little') for k in (0, 4))
        entries.append(data[offset : offset + length])
    return entries


def dxbc_in(entry: bytes):
    """The DXBC container inside a sub-program entry (Unity puts a header in front of it)."""
    start = entry.find(b'DXBC')
    if start < 0:
        return None
    size = int.from_bytes(entry[start + 24 : start + 28], 'little')
    return entry[start : start + size]


def state_summary(state):
    def val(field):
        return state[field]['val']

    blend = state['rtBlend0']
    return {
        'blend': [BLEND[int(blend[k]['val'])] for k in ('srcBlend', 'destBlend', 'srcBlendAlpha', 'destBlendAlpha')],
        'blendOp': [int(blend['blendOp']['val']), int(blend['blendOpAlpha']['val'])],
        'colorMask': int(blend['colMask']['val']),
        'separateBlend': state['rtSeparateBlend'],
        'zClip': bool(val('zClip')),
        'zTest': ZTEST[int(val('zTest'))],
        'zWrite': bool(val('zWrite')),
        'cull': CULL[int(val('culling'))],
        'offset': [val('offsetFactor'), val('offsetUnits')],
        'alphaToMask': bool(val('alphaToMask')),
        'stencilRef': val('stencilRef'),
        'fogMode': state['fogMode'],
        'lighting': state['lighting'],
        'tags': state['m_Tags']['tags'],
    }


def main(data_folder: Path, out: Path):
    out.mkdir(parents=True, exist_ok=True)
    found = set()
    for path in sorted(data_folder.glob('*.assets')):
        env = UnityPy.load(str(path))
        for obj in env.objects:
            if obj.type.name != 'Shader':
                continue
            tree = obj.read_typetree()
            form = tree['m_ParsedForm']
            name = form['m_Name']
            if name not in SHADERS:
                continue
            found.add(name)
            stem = out / name.split('/')[-1]
            platform = tree['platforms'].index(D3D11)
            if tree['platforms'] != [D3D11]:
                print(f'{name}: platforms {tree["platforms"]}, only D3D11 is read')
            start = tree['offsets'][platform][0]
            blob = bytes(tree['compressedBlob'][start : start + tree['compressedLengths'][platform][0]])
            entries = blob_entries(lz4.block.decompress(blob, uncompressed_size=tree['decompressedLengths'][platform][0]))
            info = {
                'name': name,
                'file': path.name,
                'pathId': obj.path_id,
                'properties': [
                    {'name': p['m_Name'], 'description': p['m_Description'], 'type': p['m_Type'],
                     'default': [p[f'm_DefValue[{i}]'] for i in range(4)], 'texture': p['m_DefTexture']['m_DefaultName']}
                    for p in form['m_PropInfo']['m_Props']
                ],
                'keywords': form['m_KeywordNames'],
                'subShaders': [],
            }
            for s, sub in enumerate(form['m_SubShaders']):
                passes = []
                for p, shader_pass in enumerate(sub['m_Passes']):
                    programs = {}
                    for stage in ('progVertex', 'progFragment'):
                        variants = [v for group in shader_pass[stage]['m_PlayerSubPrograms'] for v in group]
                        programs[stage] = []
                        for v in variants:
                            dxbc = dxbc_in(entries[v['m_BlobIndex']])
                            label = f'{stem.name}.s{s}p{p}.{stage[4:].lower()}.b{v["m_BlobIndex"]}'
                            (out / f'{label}.dxbc').write_bytes(dxbc)
                            (out / f'{label}.asm').write_text(disassemble(dxbc), encoding='utf-8')
                            programs[stage].append({'blob': v['m_BlobIndex'], 'keywords': v['m_KeywordIndices'],
                                                    'gpuProgramType': v['m_GpuProgramType'], 'file': label})
                    passes.append({'state': state_summary(shader_pass['m_State']), 'programs': programs})
                info['subShaders'].append({'tags': sub['m_Tags']['tags'], 'lod': sub['m_LOD'], 'passes': passes})
            stem.with_suffix('.json').write_text(json.dumps(info, indent=2), encoding='utf-8')
            print(f'{name}: {path.name} path id {obj.path_id}, {len(entries)} blob entries')
    missing = set(SHADERS) - found
    if missing:
        sys.exit(f'not found: {sorted(missing)}')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(Path(sys.argv[1]), Path(sys.argv[2]))
