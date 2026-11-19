"""The audio import settings for the PS Vita build of Terminator 2D: NO FATE (docs/audio.md).

The game ships 930 AudioClips, all Streaming, all stereo 44.1 kHz: 89 music clips and 6 ambiences as Ogg
Vorbis under Resources/audio/music, and 835 sound effects, voices and UI sounds as 16-bit WAV under
Resources/audio/sfx and Resources/audio/ui. AssetsEmbed.staticInit loads every one of them with
Resources.Load when the game starts and keeps them all, so every clip that is not streamed sits in main
memory for the whole game.

The rule, by the clip's folder (the folders match the code's audiotype table: music 0, ambience 1, sound 2,
Skynet 3):

- Music and ambience stream (HEVAG, stereo, 44.1 kHz), except clips shorter than STREAM_MIN_SECONDS (the
  boss intros, the stings, two short loops), which stay compressed in memory. On the PS Vita an AudioSource
  opens a streamed clip on the main thread when it plays it, about 35 ms each time (butcher-vita measured
  this), and the game starts a short intro and its loop in the same frame.
- Everything else (sound effects, voices, Skynet's lines, UI) is compressed in memory as HEVAG, the PS Vita's
  ADPCM, at 44.1 kHz. A clip whose two channels are the same, or whose difference is SIDE_DB_MONO or more
  below their sum, is imported as mono (forceToMono): half of the effects are, and the game pans them itself.
  Skynet's lines and the UI sounds are real stereo and stay stereo.
- preloadAudioData stays on for clips in memory (otherwise each loads on the main thread when it first plays),
  and off for streamed clips (STREAM_PRELOAD).

The settings go into the exported .meta files' defaultSettings, which the editor's PS Vita import and the
builder's tools/builder/audio.py both read. Unity 2018.2's PS Vita importer makes every clip HEVAG whatever
format is asked, except PCM (huntdown-vita found this, and the test in tools/downgrade/audio/vita-audio-test
checks it again for this game's clips).

    python -I tools/downgrade/audio/audio_rules.py [--export DIR] [--report DIR] [--apply PROJECT]

reads the export (default: work/assetripper/export-stubs/ExportedProject, in this checkout or the main one),
prints the inventory, the settings and the PS Vita sizes and memory, and writes report.json and clips.tsv
into --report (default work/audio). --apply writes the changed .meta files into a project folder.
From the pipeline: overlay = audio_rules.audio_overlay(exported_project) -> {Path('Assets/...meta'): bytes}.

Needs numpy (the mono test reads the WAV samples). Nothing here holds game content.
"""

import argparse
import collections
import json
import math
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

# AudioClipLoadType, AudioCompressionFormat and AudioSampleRateSetting as Unity 2018.2's AudioImporter numbers them.
DECOMPRESS_ON_LOAD, COMPRESSED_IN_MEMORY, STREAMING = 0, 1, 2
PCM, VORBIS, ADPCM, MP3, VAG, HEVAG, ATRAC9 = 0, 1, 2, 3, 4, 5, 9
PRESERVE_RATE, OPTIMIZE_RATE, OVERRIDE_RATE = 0, 1, 2

AUDIO = Path('Assets') / 'Resources' / 'audio'
# Music or ambience shorter than this stays in memory (seconds).
STREAM_MIN_SECONDS = 10.0
# A stereo clip whose side (L-R) is this far below its mid (L+R), in RMS, is imported as mono (dB).
SIDE_DB_MONO = -20.0
# Whether streamed clips preload; the device test measures what a loaded streamed clip costs with each.
STREAM_PRELOAD = False
# Sample rate for clips in memory: None keeps the source's 44.1 kHz.
MEMORY_RATE = 32000

# Sizes. HEVAG: 16 bytes per 28 samples per channel; Unity stores each clip as a one-sample FSB5 bank with a
# 0x60-byte header, padded to 32 bytes (huntdown-vita's tools/builder/fsb5.py).
HEVAG_FRAME, HEVAG_FRAME_BYTES, BANK_HEADER, BANK_ALIGN = 28, 16, 0x60, 32
# Main memory a loaded streamed clip costs before it plays, and a playing stream's buffers (bytes).
# Placeholders until the device test (docs/audio.md, "Measurements") replaces them.
STREAM_LOADED_BYTES = 200 * 1024
STREAM_PLAYING_BYTES = 64 * 1024

KINDS = ('music', 'ambience', 'skynet', 'voice', 'sfx', 'ui')
GAME_TYPE = {'music': 0, 'ambience': 1, 'sfx': 2, 'voice': 2, 'ui': 2, 'skynet': 3}


def kind_of(relative):
    """The clip's use from its path under Resources/audio (lower case): music, ambience, skynet, voice, sfx, ui."""
    parts = relative.lower().split('/')
    if parts[0] == 'music':
        return 'ambience' if parts[1] == 'ambience' else 'music'
    if parts[0] == 'ui':
        return 'ui'
    if parts[:2] == ['sfx', 'voices']:
        return 'skynet' if len(parts) > 2 and parts[2] == 'skynet' else 'voice'
    if parts[0] == 'sfx':
        return 'sfx'
    raise ValueError(f'audio clip outside music, sfx and ui: {relative}')


@dataclass
class Clip:
    path: str  # relative to Assets/Resources/audio
    kind: str
    format: str  # wav or ogg
    channels: int
    rate: int
    frames: int
    source_bytes: int
    side_db: float | None  # None for Ogg (not decoded)

    @property
    def seconds(self):
        return self.frames / self.rate


@dataclass
class Settings:
    load_type: int
    compression: int
    mono: bool
    rate: int | None  # None: the source's
    preload: bool

    def channels(self, clip):
        return 1 if self.mono else clip.channels


def read_header(path: Path):
    """(format, channels, rate, frames, data offset, data size) of a WAV or Ogg Vorbis file."""
    data = path.read_bytes()
    if path.suffix.lower() == '.wav':
        fmt, chunk = data.find(b'fmt '), data.find(b'data')
        channels = int.from_bytes(data[fmt + 10 : fmt + 12], 'little')
        rate = int.from_bytes(data[fmt + 12 : fmt + 16], 'little')
        bits = int.from_bytes(data[fmt + 22 : fmt + 24], 'little')
        size = int.from_bytes(data[chunk + 4 : chunk + 8], 'little')
        return 'wav', channels, rate, size // (channels * bits // 8), chunk + 8, size, bits, data
    header, last = data.find(b'\x01vorbis'), data.rfind(b'OggS')
    channels, rate = data[header + 11], int.from_bytes(data[header + 12 : header + 16], 'little')
    frames = int.from_bytes(data[last + 6 : last + 14], 'little')
    return 'ogg', channels, rate, frames, 0, 0, 0, data


def side_db(data, offset, size, bits, channels):
    """Side (L-R) RMS against mid (L+R) RMS of 16-bit stereo PCM in dB; -inf for identical channels."""
    if channels != 2 or bits != 16:
        return None
    import numpy as np

    samples = np.frombuffer(data, '<i2', count=size // 2, offset=offset).reshape(-1, 2).astype(np.float64)
    side = samples[:, 0] - samples[:, 1]
    if not side.any():
        return -math.inf
    mid = samples[:, 0] + samples[:, 1]
    return float(10 * np.log10((side**2).mean() / max((mid**2).mean(), 1e-9)))


def scan(exported: Path):
    """Every clip under Assets/Resources/audio of the exported project, in path order."""
    root = exported / AUDIO
    clips = []
    for path in sorted(root.rglob('*')):
        if path.suffix.lower() not in ('.wav', '.ogg'):
            continue
        fmt, channels, rate, frames, offset, size, bits, data = read_header(path)
        relative = path.relative_to(root).as_posix()
        clips.append(
            Clip(
                relative,
                kind_of(relative),
                fmt,
                channels,
                rate,
                frames,
                len(data),
                side_db(data, offset, size, bits, channels) if fmt == 'wav' else None,
            )
        )
    return clips


def decide(clip: Clip) -> Settings:
    """The PS Vita import settings for a clip (the rule in the module's docstring)."""
    if clip.kind in ('music', 'ambience') and clip.seconds >= STREAM_MIN_SECONDS:
        return Settings(STREAMING, HEVAG, mono=False, rate=None, preload=STREAM_PRELOAD)
    mono = clip.channels == 2 and clip.side_db is not None and clip.side_db <= SIDE_DB_MONO
    return Settings(COMPRESSED_IN_MEMORY, HEVAG, mono=mono, rate=MEMORY_RATE, preload=True)


def vita_bytes(clip: Clip, settings: Settings):
    """The clip's FSB5 bank in the PS Vita build (bytes)."""
    rate = settings.rate or clip.rate
    frames = clip.frames * rate // clip.rate
    channels = settings.channels(clip)
    if settings.compression == PCM:
        data = frames * 2 * channels
    else:
        data = -(-frames // HEVAG_FRAME) * HEVAG_FRAME_BYTES * channels
    return -(-(BANK_HEADER + data) // BANK_ALIGN) * BANK_ALIGN


def resident_bytes(clip: Clip, settings: Settings):
    """Main memory the clip holds while loaded but not playing."""
    if settings.load_type == STREAMING:
        return STREAM_LOADED_BYTES
    return vita_bytes(clip, settings)


# defaultSettings as AssetRipper writes it for 2018.2 (serializedVersion 6).
DEFAULT_SETTINGS = re.compile(
    r'^(  defaultSettings:\n)((?:    \w+: [^\n]*\n)+)',
    re.MULTILINE,
)


def importer_text(text, settings: Settings, where='meta'):
    """The .meta text with the clip's settings in defaultSettings, forceToMono and preloadAudioData."""
    if 'AudioImporter:' not in text:
        raise ValueError(f'{where}: not an AudioImporter')
    match = DEFAULT_SETTINGS.search(text)
    if not match:
        raise ValueError(f'{where}: no defaultSettings block')
    values = dict(re.findall(r'^    (\w+): ([^\n]*)$', match[2], re.MULTILINE))
    values.update(
        loadType=str(settings.load_type),
        compressionFormat=str(settings.compression),
        sampleRateSetting=str(OVERRIDE_RATE if settings.rate else PRESERVE_RATE),
        sampleRateOverride=str(settings.rate or 44100),
    )
    block = ''.join(f'    {k}: {v}\n' for k, v in values.items())
    text = text[: match.start(2)] + block + text[match.end(2) :]
    for key, value in (
        ('forceToMono', int(settings.mono)),
        ('normalize', 0),  # a forced mono clip would be normalized after the downmix
        ('preloadAudioData', int(settings.preload)),
        ('loadInBackground', 0),
    ):
        text, count = re.subn(rf'^  {key}: \d+$', f'  {key}: {value}', text, count=1, flags=re.MULTILINE)
        if count != 1:
            raise ValueError(f'{where}: no {key}')
    return text


def audio_overlay(exported: Path, clips=None):
    """{Path('Assets/Resources/audio/...meta'): new .meta bytes} for every clip whose settings change."""
    clips = clips if clips is not None else scan(exported)
    overlay = {}
    for clip in clips:
        relative = AUDIO / (clip.path + '.meta')
        meta = exported / relative
        original = meta.read_text(encoding='utf-8')
        text = importer_text(original, decide(clip), str(meta))
        if text != original:
            overlay[relative] = text.encode('utf-8')
    return overlay


def report(clips):
    """Per kind and in total: clips, seconds, source size, and the PS Vita's settings, VPK size and memory."""
    rows = collections.OrderedDict()
    for kind in (*KINDS, 'total'):
        rows[kind] = collections.Counter()
    longest = collections.defaultdict(float)
    for clip in clips:
        s = decide(clip)
        size, resident = vita_bytes(clip, s), resident_bytes(clip, s)
        for kind in (clip.kind, 'total'):
            r = rows[kind]
            r['clips'] += 1
            r['seconds'] += clip.seconds
            r['source_bytes'] += clip.source_bytes
            r['pcm_bytes'] += clip.frames * 2 * clip.channels
            r['vita_bytes'] += size
            r['resident_bytes'] += resident
            r['streamed'] += s.load_type == STREAMING
            r['in_memory'] += s.load_type != STREAMING
            r['mono'] += s.mono
            r['in_memory_bytes'] += 0 if s.load_type == STREAMING else size
            longest[kind] = max(longest[kind], clip.seconds)
    return {k: dict(v, longest=round(longest[k], 1)) for k, v in rows.items() if v['clips']}


def mib(n):
    return f'{n / 2**20:.1f}'


def print_report(rows):
    head = ('kind', 'clips', 'minutes', 'longest s', 'source MiB', 'PCM MiB', 'streamed', 'in memory', 'mono',
            'VPK MiB', 'resident MiB')
    print(' | '.join(head))
    for kind, r in rows.items():
        print(' | '.join(str(x) for x in (
            kind, r['clips'], f'{r["seconds"] / 60:.1f}', r['longest'], mib(r['source_bytes']), mib(r['pcm_bytes']),
            r['streamed'], r['in_memory'], r['mono'], mib(r['vita_bytes']), mib(r['resident_bytes']),
        )))
    print(
        f'resident = clips in memory as HEVAG + {STREAM_LOADED_BYTES // 1024} KiB per loaded streamed clip '
        f'(placeholder); each playing stream adds about {STREAM_PLAYING_BYTES // 1024} KiB'
    )


def default_export():
    here = Path(__file__).resolve().parents[3]
    candidates = [here]
    try:
        common = subprocess.run(['git', '-C', str(here), 'rev-parse', '--git-common-dir'], capture_output=True,
                                text=True, check=True).stdout.strip()
        candidates.append(Path(common).resolve().parent)
    except (OSError, subprocess.CalledProcessError):
        pass
    for root in candidates:
        export = root / 'work' / 'assetripper' / 'export-stubs' / 'ExportedProject'
        if (export / AUDIO).is_dir():
            return export, root / 'work' / 'audio'
    sys.exit('no export found; pass --export')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--export', type=Path, help='the exported project (with Assets/)')
    parser.add_argument('--report', type=Path, help='where report.json and clips.tsv go')
    parser.add_argument('--apply', type=Path, help='write the changed .meta files into this project')
    args = parser.parse_args()
    export, out = default_export() if args.export is None else (args.export, None)
    out = args.report or out or Path('work') / 'audio'
    clips = scan(export)
    rows = report(clips)
    print_report(rows)
    out.mkdir(parents=True, exist_ok=True)
    with (out / 'clips.tsv').open('w', encoding='utf-8') as handle:
        handle.write('path\tkind\tformat\tchannels\trate\tseconds\tside_db\tload_type\tmono\trate_out\tvita_bytes\n')
        for c in clips:
            s = decide(c)
            side = '' if c.side_db is None else f'{c.side_db:.1f}'
            handle.write(f'{c.path}\t{c.kind}\t{c.format}\t{c.channels}\t{c.rate}\t{c.seconds:.3f}\t{side}\t'
                         f'{s.load_type}\t{int(s.mono)}\t{s.rate or c.rate}\t{vita_bytes(c, s)}\n')
    rule = {k: v for k, v in globals().items() if k in (
        'STREAM_MIN_SECONDS', 'SIDE_DB_MONO', 'STREAM_PRELOAD', 'MEMORY_RATE', 'STREAM_LOADED_BYTES',
        'STREAM_PLAYING_BYTES')}
    (out / 'report.json').write_text(json.dumps({'rule': rule, 'kinds': rows}, indent=1), encoding='utf-8')
    print(f'wrote {out / "clips.tsv"} and {out / "report.json"}')
    if args.apply:
        overlay = audio_overlay(export, clips)
        for relative, data in overlay.items():
            target = args.apply / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        print(f'applied {len(overlay)} .meta files to {args.apply}')


if __name__ == '__main__':
    main()
