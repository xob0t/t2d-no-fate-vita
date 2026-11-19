"""LZ4 as Unity 2018.2 compresses: LZ4 r131's HC compressor.

    compress(raw, level=16)   # the bytes Unity writes: a bundle block or blocks info (16), a shader blob (9)

The lz4 package bundles LZ4 1.9, whose HC compressor matches few of Unity's blocks; r131 matches them
all: every block and the blocks info of the AssetBundles at its top level (16), every shader blob of
unity_builtin_extra at its default (9). The first use downloads r131's four source files (pinned) and
builds them with gcc into work/builder/lz4-r131.dll. Without gcc it falls back to the lz4 package:
the output decompresses to the same bytes, but isn't Unity's byte for byte.
"""

import ctypes
import shutil
from functools import cache

import lz4.block
from paths import WORK, common, progress

SOURCE = 'https://raw.githubusercontent.com/lz4/lz4/r131/lib/'
FILES = {
    'lz4.c': '08213978424040ecdd0ce3bc8668cab3fd8f12cfddbda45bd9e372a10c16e495',
    'lz4.h': 'ee1cc5ab90016e75d62cfc21c42f029f7f495de5cc37b72eca20414b7e32562e',
    'lz4hc.c': 'f47d006f914a5a90cf28dc1c6beb7fbecd58cea128dd21a5901a47df9865128a',
    'lz4hc.h': '06482c03dd67b87abb610edbb238b0b413551eb15522c399c339808b9bd0159e',
}
FOLDER = WORK / 'lz4-r131'
LIBRARY = WORK / 'lz4-r131.dll'


@cache
def library():
    """r131's compressor, or None (no gcc: the lz4 package's then)."""
    if not LIBRARY.exists():
        gcc = shutil.which('gcc')
        if gcc is None:
            common.log("WARNING: no gcc, so LZ4 comes from the lz4 package: valid, but not Unity's bytes")
            return None
        for name, digest in FILES.items():
            common.download(SOURCE + name, FOLDER / name, digest)
        sources = [str(FOLDER / 'lz4.c'), str(FOLDER / 'lz4hc.c')]
        part = LIBRARY.with_name(LIBRARY.name + '.part')
        progress.run('Building LZ4 r131', [gcc, '-O2', '-shared', '-o', part, *sources], WORK / 'lz4-r131.log')
        part.replace(LIBRARY)
    lib = ctypes.CDLL(str(LIBRARY))
    lib.LZ4_compress_HC.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_int, ctypes.c_int]
    lib.LZ4_compress_HC.restype = ctypes.c_int
    return lib


def compress(raw, level=16):
    """LZ4HC of raw (no size prefix) at r131's level."""
    lib = library()
    if lib is None:
        return lz4.block.compress(raw, mode='high_compression', compression=min(level, 12), store_size=False)
    out = ctypes.create_string_buffer(len(raw) + len(raw) // 255 + 16)
    size = lib.LZ4_compress_HC(raw, out, len(raw), len(out), level)
    if size <= 0:
        raise ValueError('LZ4 r131 could not compress the block')
    return out.raw[:size]
