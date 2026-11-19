"""Compile every keyword variant of the project's shaders for the PS Vita and Direct3D 11, without the editor.

    uv run tools/builder/check_shader_compile.py [shader names...]

The shaders are the ones shader_rules.py writes into the project (pipeline.py project --platform vita).
The PS Vita's programs go through psp2cgc as Unity 2018.2 runs it (psp2cgc.py); Direct3D 11's through
d3dcompiler_47.dll with the defines Unity's compiler sets for the platform, so a failure here is one the
editor's build would report, minutes earlier.
"""

import ctypes
import itertools
import shutil
import sys
import tempfile
from ctypes import wintypes
from pathlib import Path

import paths
import psp2cgc
from shaderlab import SHADER_NAME, pragmas, scan, unity_input

# D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY, for CG programs' sampler2D and tex2D; and the standard include
# handler, which looks for includes next to the source's name: CGIncludes is copied there.
D3D_FLAGS = 1 << 12
STANDARD_INCLUDE = ctypes.c_void_p(1)
D3D_PROFILES = {'vp': b'vs_4_0', 'fp': b'ps_4_0'}


class Macro(ctypes.Structure):
    _fields_ = [('Name', ctypes.c_char_p), ('Definition', ctypes.c_char_p)]


def blob_text(blob):
    """An ID3DBlob's bytes as text (GetBufferPointer and GetBufferSize, its vtable's slots 3 and 4)."""
    if not blob:
        return ''
    table = ctypes.cast(blob, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    pointer = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(table[3])(blob)
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(table[4])(blob)
    return ctypes.string_at(pointer, size).decode(errors='replace')


def compile_d3d11(compiler, source, name, stage, entry, target, keywords):
    """(compiled, the compiler's messages)."""
    defines = [
        'SHADER_API_D3D11', 'SHADER_API_DESKTOP', f'SHADER_TARGET={target.replace(".", "")}', 'UNITY_VERSION=201829',
        'SHADER_STAGE_VERTEX' if stage == 'vp' else 'SHADER_STAGE_FRAGMENT', *keywords,
    ]  # fmt: skip
    macros = (Macro * (len(defines) + 1))()
    for index, define in enumerate(defines):
        key, _, value = define.partition('=')
        macros[index] = Macro(key.encode(), (value or '1').encode())
    data = source.encode()
    code, errors = ctypes.c_void_p(), ctypes.c_void_p()
    result = compiler.D3DCompile(
        data, ctypes.c_size_t(len(data)), str(name).encode(), macros, STANDARD_INCLUDE, entry.encode(),
        D3D_PROFILES[stage], wintypes.UINT(D3D_FLAGS), wintypes.UINT(0), ctypes.byref(code), ctypes.byref(errors),
    )  # fmt: skip
    return result == 0, blob_text(errors.value).strip()


def variants(found):
    """Every keyword combination of a program's multi_compile lines."""
    lines = [[None if k in ('_', '__') else k for k in args] for name, args in found if name == 'multi_compile']
    return [[k for k in combination if k] for combination in itertools.product(*lines)]


def main():
    names = set(sys.argv[1:])
    shaders = []
    for path in sorted((paths.PROJECT / 'Assets').rglob('*.shader')):
        match = SHADER_NAME.search(path.read_text(encoding='utf-8', errors='replace'))
        if match and (not names or match.group(1) in names):
            shaders.append(path)
    if not shaders:
        sys.exit(f'No shaders in {paths.PROJECT / "Assets"}: run pipeline.py project --platform vita first')
    compiler = ctypes.WinDLL('d3dcompiler_47.dll')
    failures, count = [], 0
    with tempfile.TemporaryDirectory() as temp:
        includes = Path(temp)
        shutil.copytree(paths.CGINCLUDES, includes, dirs_exist_ok=True)
        for path in shaders:
            text = path.read_text(encoding='utf-8')
            programs, blocks = scan(text)
            for program in programs:
                found = pragmas(program.body)
                settings = dict(found)
                target = settings.get('target', ['2.5'])[0]
                source = unity_input(text, program, blocks)
                # Direct3D 11 gets Unity's input without the PS Vita's prelude.
                d3d_source = source.replace('#include "HLSLToPSP2.cginc"', '', 1)
                for keywords in variants(found):
                    for stage, pragma in (('vp', 'vertex'), ('fp', 'fragment')):
                        entry = settings[pragma][0]
                        label = f'{path.name} subshader {program.sub} pass {program.passi} {stage} {keywords}'
                        count += 1
                        result, log = psp2cgc.compile_shader(source, str(path.parent), stage, entry, target, keywords)
                        if result is None:
                            failures.append(f'{label}, PS Vita:\n{log}')
                        ok, log = compile_d3d11(
                            compiler, d3d_source, includes / path.name, stage, entry, target, keywords
                        )
                        if not ok:
                            failures.append(f'{label}, Direct3D 11:\n{log}')
    for failure in failures:
        print(failure)
    print(f'{len(shaders)} shaders, {count} programs compiled for both platforms: {len(failures)} failures')
    sys.exit(1 if failures else 0)


if __name__ == '__main__':
    main()
