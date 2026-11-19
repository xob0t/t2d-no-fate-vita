"""What the PS Vita build made of the audio test's clips: each AudioClip's load type, compression format, rate,
channels and the codec, rate and channels of its FSB5 bank, summed by group.

    uv run --with unitypy==1.25.3 python -I tools/downgrade/audio/vita-audio-test/check_build.py [build folder]

Default build folder: work/vita-audio-test/build. Prints every probe clip, and a summary of the others.
"""

import collections
import struct
import sys
from pathlib import Path

import UnityPy

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import audio_rules as ar  # noqa: E402

FORMATS = {0: 'PCM', 1: 'Vorbis', 2: 'ADPCM', 3: 'MP3', 4: 'VAG', 5: 'HEVAG', 6: 'XMA', 7: 'AAC', 8: 'GCADPCM', 9: 'ATRAC9'}
LOAD = {0: 'decompress', 1: 'compressed', 2: 'streaming'}
CODECS = {1: 'PCM8', 2: 'PCM16', 6: 'GCADPCM', 7: 'IMAADPCM', 8: 'VAG', 9: 'HEVAG', 11: 'MPEG', 13: 'AT9', 15: 'Vorbis'}
RATES = [4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000]


def bank_info(bank: bytes):
    """(codec, rate, channels, samples) from a one-sample FSB5 bank."""
    if bank[:4] != b'FSB5':
        return ('not FSB5', 0, 0, 0)
    codec = struct.unpack_from('<I', bank, 0x18)[0]
    word = struct.unpack_from('<Q', bank, 0x3C)[0]
    has_chunks, rate_code = word & 1, (word >> 1) & 0xF
    channels = 2 if (word >> 5) & 1 else 1
    samples = word >> 34
    rate = RATES[rate_code] if rate_code < len(RATES) else 0
    offset = 0x3C + 8
    while has_chunks:
        chunk = struct.unpack_from('<I', bank, offset)[0]
        has_chunks, size, kind = chunk & 1, (chunk >> 1) & 0xFFFFFF, chunk >> 25
        if kind == 2:
            rate = struct.unpack_from('<I', bank, offset + 4)[0]
        elif kind == 1:
            channels = bank[offset + 4]
        offset += 4 + size
    return (CODECS.get(codec, f'codec{codec}'), rate, channels, samples)


def main():
    build = Path(sys.argv[1]) if len(sys.argv) > 1 else ar.default_export()[1].parent / 'vita-audio-test' / 'build'
    media = build / 'Media'
    env = UnityPy.load(str(media))
    group = collections.defaultdict(collections.Counter)
    size = collections.Counter()
    for obj in env.objects:
        if obj.type.name != 'AudioClip':
            continue
        clip = obj.read()
        res = clip.m_Resource
        source = media / Path(res.m_Source).name
        bank = b''
        if source.exists():
            with source.open('rb') as handle:
                handle.seek(res.m_Offset)
                bank = handle.read(min(res.m_Size, 0x200))
        codec, rate, channels, samples = bank_info(bank) if bank else ('no data', 0, 0, 0)
        line = (f'{clip.m_Name}: {LOAD.get(clip.m_LoadType)} {FORMATS.get(clip.m_CompressionFormat)} {clip.m_Frequency} Hz '
                f'{clip.m_Channels} ch preload={clip.m_PreloadAudioData} | bank {codec} {rate} Hz {channels} ch '
                f'{samples} samples {res.m_Size} bytes in {Path(res.m_Source).name}')
        name = clip.m_Name
        if name.startswith(('fx_', 'amb_')):
            print('PROBE', line)
            continue
        key = f'{LOAD.get(clip.m_LoadType)} {codec} {rate} Hz {channels} ch'
        kind = 'copy' if name.startswith('s') and name[1:].isdigit() else 'game'
        group[kind][key] += 1
        size[kind, key] += res.m_Size
    for kind, counts in group.items():
        for key, n in sorted(counts.items()):
            print(f'{kind}: {n} clips {key}: {size[kind, key] / 2**20:.1f} MiB')


if __name__ == '__main__':
    main()
