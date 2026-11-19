"""AudioClips as Unity's PS Vita build makes them: the release object and its FSB5 bank.

On the PS Vita Unity's importer encodes every clip as HEVAG (the console's ADPCM), whatever
compression the importer settings ask for (the pipeline's audio_overlay in
tools/downgrade/asset_rules.py asks for MP3, which records as HEVAG too), so all 9089 clips of the
reference build are HEVAG FSB5 banks and none is Vorbis or PCM. The importer:

- decodes the source (.ogg or .wav) at its own rate and channel count;
- with forceToMono, mixes the channels down to their mean, truncated to 16 bits (convert);
- with a sample rate override, resamples to that rate (resample): n samples become
  n * rate_out // rate, sample j taken at j * n / n_out with the editor's own filter, measured for
  44.1 kHz to 32 kHz (the only conversion the builder makes);
- time-stretches it to whole 28-sample frames (hevag.stretch) instead of padding it;
- encodes each channel as HEVAG, frames of the channels side by side (hevag.encode);
- stores it in a one-sample FSB5 bank (fsb5.bank) at the clip's rate; a rate outside FMOD's
  table goes in a frequency chunk.

The AudioClip keeps the converted clip's sample count in m_Length (seconds); m_Frequency is its
rate, or for a rate outside FMOD's table the next rate up in it, and then m_Length counts the
samples the clip would have at that rate, n * rate_out // rate.

The mono mix and the lengths are Unity's exactly; the resampler is close to Unity's but not the
same (tools/builder/check_audio.py --compare measures both against the editor's own output, and
docs/audio.md has the numbers). The HEVAG encoder is not Unity's (Sony's vagconv2, closed; see
hevag.py), so no HEVAG bank is byte for byte the editor's.

Banks go to the .resource file of the clip's serialized file, in path ID order, each starting on
a 32-byte boundary (every bank's size is a multiple of 32, so they follow each other with no gap).

    entries = build(assets)                       # [(fields, bank)], in the order given
    data = resource_stream(entries, 'sharedassets1.resource')   # fills m_Resource

or, inside build.py, Build.IMPORTED[83] = audio.imported, which leaves each file's stream in
build.streams['<file>.resource'].

Encoded clips are cached in work/builder/audio-cache, keyed by the source file's bytes, the clip's
name, its importer settings and this code (CACHE_FILES): a build whose audio didn't change encodes
nothing.

Reading .ogg needs soundfile (libsndfile).
"""

import contextlib
import functools
import hashlib
import json
import os
import pickle
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import audio_kernel
import fsb5
import hevag
import numpy as np
import unityyaml
from paths import PROJECT, WORK, common, progress

PCM_FORMAT, HEVAG_FORMAT = 0, 5  # AudioCompressionFormat
PSP2_GROUP = '17'  # BuildTargetGroup.PSP2, the key of the importer's platform override
PRESERVE_RATE, OVERRIDE_RATE = '0', '2'  # AudioSampleRateSetting (1, Optimize, is not supported)
CACHE = WORK / 'audio-cache'  # encoded clips (build_jobs)
JOB_FRAMES = 1 << 20  # channel frames a worker encodes at once
# Processes that encode the clips. More than 4 hardly help: on an 8-core, 16-thread Ryzen 7 5800X3D
# the game's 9089 clips took 528 s with 1, 242 s with 2, 144 s with 4, 122 s with 8 and 123 s with
# 13. The largest job (MusCh9GeneralNoDrums, 5 minutes of stereo, and the clip grouped with it)
# allocated up to 0.6 GB by tracemalloc; 1 GB a worker leaves room. The reserve is for the system
# and the build process, which grew by 1 GB after the audio step started.
MAX_WORKERS = 4
MEMORY_PER_WORKER_GB = 1.0
MEMORY_RESERVE_GB = 2


def settings(meta):
    """The importer settings for the PS Vita: the PSP2 override if there is one, else the defaults."""
    importer = meta['AudioImporter']
    overrides = importer.get('platformSettingOverrides') or {}
    chosen = overrides.get(PSP2_GROUP) or importer['defaultSettings']
    keys = ('loadType', 'sampleRateSetting', 'sampleRateOverride', 'conversionMode', 'compressionFormat')
    out = {k: chosen.get(k, '0') for k in keys}
    for k in ('forceToMono', 'normalize', 'preloadAudioData', 'loadInBackground', 'ambisonic', '3D'):
        out[k] = importer.get(k, '0')
    if out['sampleRateSetting'] not in (PRESERVE_RATE, OVERRIDE_RATE):
        # Optimize Sample Rate picks a rate from the content; no reference shows how.
        raise NotImplementedError(f'AudioImporter sampleRateSetting {out["sampleRateSetting"]}')
    if out['forceToMono'] != '0' and out['normalize'] != '0':
        # Normalize scales the mono mix; no reference shows by how much.
        raise NotImplementedError('AudioImporter forceToMono with normalize')
    return out


def meta(asset):
    # AssetDB stores meta_path as a str, which unityyaml.meta can't open.
    return unityyaml.meta(Path(asset.meta_path))


def read(path):
    """(samples as (channels, n) float32 in the 16-bit range, rate)."""
    import soundfile

    data, rate = soundfile.read(str(path), dtype='float32', always_2d=True)
    return np.ascontiguousarray(data.T) * np.float32(32768), rate


# The resampler: sample j of n_out from n is sum_i x[i] * h(i - j * n / n_out), samples past either
# end 0, truncated to 16 bits. h is the editor's own kernel for 44.1 kHz to 32 kHz, fitted to its
# output as a table (audio_kernel.py, from tools/downgrade/audio/importer-reference/fit_resampler.py):
# no windowed sinc matches it. That is the only conversion the audio rule asks for, and the only one
# the builder does: the same kernel scaled to other ratios came within 11 to 29 dB of the editor's,
# so another ratio stops the build until its kernel is measured the same way.
RESAMPLE_PHASES = 1024  # the kernel is tabulated at this many fractional positions and interpolated
RESAMPLE_BLOCK = 1 << 16  # output samples computed at once


def check_rate(rate, importer):
    """Stops on a sample rate conversion the builder can't make as the editor does."""
    out = output_rate(rate, importer)
    if out != rate and (rate, out) != (audio_kernel.SOURCE_RATE, audio_kernel.OUTPUT_RATE):
        raise NotImplementedError(
            f'AudioImporter sample rate {rate} Hz to {out} Hz: only {audio_kernel.SOURCE_RATE} to '
            f'{audio_kernel.OUTPUT_RATE} Hz is measured (tools/downgrade/audio/importer-reference)'
        )
    return out


def _kernel(t):
    """h at distance t, in source samples."""
    table = np.asarray(audio_kernel.KERNEL)
    k = np.abs(t) / audio_kernel.STEP
    i = np.minimum(np.floor(k).astype(np.int64), len(table) - 2)
    f = k - i
    return np.where(np.abs(t) < audio_kernel.REACH, table[i] * (1 - f) + table[i + 1] * f, 0)


def resample(pcm, n_out):
    """(channels, n) samples to (channels, n_out), as the importer's sample rate override does from
    44.1 kHz to 32 kHz; floats in the 16-bit range, truncated."""
    channels, n = pcm.shape
    reach = int(np.ceil(audio_kernel.REACH))
    taps = np.arange(-reach + 1, reach + 1)  # i - floor(position)
    # table[q, k] = h(taps[k] - q / RESAMPLE_PHASES), one row more for the interpolation
    phases = np.arange(RESAMPLE_PHASES + 1) / RESAMPLE_PHASES
    table = _kernel(taps[None, :] - phases[:, None])
    padded = np.concatenate([np.zeros((channels, reach)), pcm, np.zeros((channels, reach + 1))], 1)
    out = np.empty((channels, n_out), np.float64)
    for a in range(0, n_out, RESAMPLE_BLOCK):
        j = np.arange(a, min(n_out, a + RESAMPLE_BLOCK), dtype=np.int64)
        whole, rest = np.divmod(j * n, n_out)  # position j * n / n_out, exactly
        q = rest * RESAMPLE_PHASES / n_out
        qi = np.floor(q).astype(np.int64)
        f = (q - qi)[:, None]
        weights = table[qi] * (1 - f) + table[qi + 1] * f
        index = whole[:, None] + taps[None, :] + reach
        for c in range(channels):
            out[c, a : a + len(j)] = np.einsum('jk,jk->j', padded[c][index], weights)
    return np.clip(np.trunc(out), -32768, 32767).astype(np.float32)


def output_rate(rate, importer):
    """The clip's rate after the import: the source's, or the override."""
    return int(importer['sampleRateOverride']) if importer['sampleRateSetting'] == OVERRIDE_RATE else rate


def convert(pcm, rate, importer):
    """The importer's conversion of the decoded source before it encodes: forceToMono mixes the
    channels down to their mean, truncated, as the editor's PCM output shows; a sample rate override
    resamples to n * rate_out // rate samples. Returns (pcm, rate)."""
    if importer['forceToMono'] != '0' and pcm.shape[0] > 1:
        pcm = np.trunc(pcm.mean(0, keepdims=True, dtype=np.float64)).astype(np.float32)
    out = check_rate(rate, importer)
    if out != rate:
        pcm = resample(pcm, pcm.shape[1] * out // rate)
    return pcm, out


def frequency(rate):
    """The AudioClip's m_Frequency: the source's rate, or the next one up in FMOD's table."""
    if rate in fsb5.RATES:
        return rate
    return next((r for r in fsb5.RATES if r > rate), rate)


def length(samples, rate):
    out = frequency(rate)
    if out != rate:
        samples = samples * out // rate
    return float(np.float32(samples / out))


def is_pcm(importer):
    """Whether a clip stays PCM: the importer's compressionFormat 0, which the port gives the clips the game
    reads samples from (AudioClip.GetData; tools/downgrade/asset_rules.py, READ_CLIPS). Every other
    format the PS Vita's importer records as HEVAG."""
    return int(importer.get('compressionFormat', HEVAG_FORMAT)) == PCM_FORMAT


def fields(name, importer, channels, rate, samples):
    """The AudioClip release dict; m_Resource gets its source and offset from resource_stream."""
    return {
        'm_Name': name,
        'm_LoadType': int(importer['loadType']),
        'm_Channels': channels,
        'm_Frequency': frequency(rate),
        'm_BitsPerSample': 16,
        'm_Length': length(samples, rate),
        'm_IsTrackerFormat': False,
        'm_Ambisonic': importer['ambisonic'] != '0',
        'm_SubsoundIndex': 0,
        'm_PreloadAudioData': importer['preloadAudioData'] != '0',
        'm_LoadInBackground': importer['loadInBackground'] != '0',
        'm_Legacy3D': importer['3D'] != '0',
        'm_Resource': {'m_Source': '', 'm_Offset': 0, 'm_Size': 0},
        'm_CompressionFormat': PCM_FORMAT if is_pcm(importer) else HEVAG_FORMAT,
    }


def clip_name(asset):
    return asset.path.rsplit('/', 1)[-1].rsplit('.', 1)[0]


def _make(jobs):
    """[(name, source path, importer settings)] -> [(fields, bank)], in the jobs' order; the HEVAG clips
    encode together."""
    made = [None] * len(jobs)
    shapes, stretched = [], []
    for i, (name, path, importer) in enumerate(jobs):
        pcm, rate = convert(*read(path), importer)
        channels, samples = pcm.shape
        if is_pcm(importer):
            # 16-bit samples, channels interleaved, as many as the source has: no stretching.
            data = np.clip(np.round(pcm), -32768, 32767).astype('<i2').T.tobytes()
            bank = fsb5.bank(name, rate, channels, samples, data, codec=fsb5.CODEC_PCM16)
            made[i] = (fields(name, importer, channels, rate, samples), bank)
            continue
        frames = hevag.frames_for(samples)
        shapes.append((i, name, importer, channels, rate, samples, frames))
        stretched.append(hevag.stretch(pcm, frames))
    encoded = hevag.encode(stretched) if stretched else []
    for (i, name, importer, channels, rate, samples, frames), data in zip(shapes, encoded, strict=True):
        bank = fsb5.bank(name, rate, channels, frames * hevag.FRAME, data)
        made[i] = (fields(name, importer, channels, rate, samples), bank)
    return made


def default_workers(jobs):
    """How many processes encode the audio: MAX_WORKERS, or fewer where there are fewer hardware
    threads or jobs, or where the free memory holds fewer (MEMORY_PER_WORKER_GB each after
    MEMORY_RESERVE_GB). One per thread ran a user's machine out of memory."""
    _, available = common.memory_gb()
    by_memory = int((available - MEMORY_RESERVE_GB) // MEMORY_PER_WORKER_GB)
    return max(1, min(MAX_WORKERS, os.cpu_count() or 1, jobs, by_memory))


@contextlib.contextmanager
def one_blas_thread():
    """Processes started inside load numpy with one OpenBLAS thread. By default OpenBLAS starts one
    per hardware thread, and splits the encoder's matrix products over them: the sums then add up in
    another order, and the HEVAG bytes depend on the thread count (12 clips gave 5 different
    encodings with 1, 2, 4, 8 and 16 threads, at the same SNR). Each thread also commits about
    32 MB, 0.5 GB a process with 16, which Windows counts against its memory. This fixes the thread
    count only: OpenBLAS picks its kernels by CPU, and those differ too (Haswell, Sandybridge and
    Nehalem kernels gave three different encodings)."""
    old = os.environ.get('OPENBLAS_NUM_THREADS')
    os.environ['OPENBLAS_NUM_THREADS'] = '1'
    try:
        yield
    finally:
        if old is None:
            del os.environ['OPENBLAS_NUM_THREADS']
        else:
            os.environ['OPENBLAS_NUM_THREADS'] = old


def build(assets, workers=None, cache=True):
    """[(fields, bank)] for the audio assets, in their order (build_jobs)."""
    return build_jobs([(clip_name(a), PROJECT / a.path, settings(meta(a))) for a in assets], workers, cache)


# What the cache key covers of the code: these files' bytes (the conversion, the encoder, the bank).
CACHE_FILES = ('audio.py', 'audio_kernel.py', 'hevag.py', 'fsb5.py')
CACHE_FORMAT = 1


@functools.cache
def _code_hash():
    here = Path(__file__).resolve().parent
    digest = hashlib.sha256(str(CACHE_FORMAT).encode())
    for name in CACHE_FILES:
        digest.update((here / name).read_bytes())
    return digest.hexdigest()


def cache_key(name, path, importer):
    """The clip's cache key: its source's bytes, its name (the bank holds a hash of it), its importer
    settings and the code that makes it."""
    digest = hashlib.sha256(_code_hash().encode())
    digest.update(hashlib.sha256(Path(path).read_bytes()).digest())
    digest.update(json.dumps([name, sorted(importer.items())]).encode())
    return digest.hexdigest()


def _cache_path(key):
    return CACHE / key[:2] / f'{key}.pickle'


def _cache_get(key):
    try:
        return pickle.loads(_cache_path(key).read_bytes())
    except (OSError, pickle.UnpicklingError, EOFError):
        return None


def _cache_put(key, entry):
    path = _cache_path(key)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(f'.{os.getpid()}.tmp')
    temporary.write_bytes(pickle.dumps(entry, protocol=pickle.HIGHEST_PROTOCOL))
    temporary.replace(path)


def build_jobs(jobs, workers=None, cache=True):
    """[(fields, bank)] for [(name, source path, importer settings)], in their order, encoded in
    `workers` processes at once (default: default_workers()). Clips in the cache (CACHE) are taken
    from it; the others are encoded and put in it. Always in processes started with one OpenBLAS
    thread, so the bytes don't depend on the thread count, the free memory or the number of
    workers; they can still differ between CPU families (one_blas_thread)."""
    import soundfile

    out = [None] * len(jobs)
    keys = [cache_key(*job) for job in jobs] if cache else [None] * len(jobs)
    todo, sizes = [], {}
    for i, (job, key) in enumerate(zip(jobs, keys, strict=True)):
        hit = _cache_get(key) if key else None
        if hit is not None:
            out[i] = hit
            continue
        todo.append(i)
        info = soundfile.info(str(job[1]))
        importer = job[2]
        frames = info.frames * check_rate(info.samplerate, importer) // info.samplerate
        channels = 1 if importer['forceToMono'] != '0' else info.channels
        sizes[i] = hevag.frames_for(frames) * channels
    if cache:
        progress.console.print(
            f'Audio: {len(jobs) - len(todo)} of {len(jobs)} clips from the cache, {len(todo)} to encode'
        )
    if not todo:
        return out
    groups, group, total = [], [], 0
    for i in sorted(todo, key=lambda i: -sizes[i]):
        group.append(i)
        total += sizes[i]
        if total >= JOB_FRAMES:
            groups.append(group)
            group, total = [], 0
    if group:
        groups.append(group)
    workers = workers or default_workers(len(groups))
    with (
        one_blas_thread(),
        ProcessPoolExecutor(workers) as pool,
        # Counted in frames: the long clips go first, a few to a group, so clips done would say little.
        progress.step(
            f'Encoding audio ({workers} worker{"s" if workers > 1 else ""})',
            total=sum(sizes.values()),
            eta=True,
            count=None,
        ) as step,
    ):
        futures = {pool.submit(_make, [jobs[i] for i in g]): g for g in groups}
        done = 0
        step.note(f'0/{len(todo)} clips')
        try:
            for future in as_completed(futures):
                g = futures[future]
                for i, result in zip(g, future.result(), strict=True):
                    out[i] = result
                    if keys[i]:
                        _cache_put(keys[i], result)
                step.advance(sum(sizes[i] for i in g))
                done += len(g)
                step.note(f'{done}/{len(todo)} clips')
        except BaseException:  # Ctrl+C or a failed group: the groups not started yet are dropped
            pool.shutdown(cancel_futures=True)
            raise
    return out


def resource_name(file_name):
    """The stream file next to a serialized file: sharedassets1.assets -> sharedassets1.resource."""
    return file_name.rsplit('.', 1)[0] + '.resource' if file_name.endswith('.assets') else file_name + '.resource'


def append(stream, fields, bank, source):
    """Put a bank on the end of a .resource stream (a bytearray) at the next 32-byte boundary and
    point the clip's m_Resource at it."""
    stream += bytes(fsb5.align(len(stream)) - len(stream))
    fields['m_Resource'] = {'m_Source': source, 'm_Offset': len(stream), 'm_Size': len(bank)}
    stream += bank


def resource_stream(entries, source):
    """Lay out the banks of one serialized file's clips (in path ID order) in its .resource file,
    filling each clip's m_Resource; returns the file's bytes."""
    out = bytearray()
    for fields, bank in entries:
        append(out, fields, bank, source)
    return bytes(out)


def imported(builder, file_name, guid, file_id, asset):
    """Build.IMPORTED[83]: the AudioClip's (data, tree). Its bank goes on the end of
    builder.streams['<file>.resource'] (a bytearray per stream file, made here if build.py has
    none), for the writer to save next to the serialized file. Objects come in path ID order,
    which is the stream's order. The first clip of a file encodes all of the file's clips at once."""
    cache = builder.__dict__.setdefault('audio_clips', {})
    if file_name not in cache:
        guids = [g for g, _, cid in builder.objects(file_name) if cid == 83]
        cache[file_name] = dict(zip(guids, build([builder.layout.db.by_guid[g] for g in guids]), strict=True))
    fields, bank = cache[file_name].pop(guid)
    source = resource_name(file_name)
    streams = builder.__dict__.setdefault('streams', {})
    append(streams.setdefault(source, bytearray()), fields, bank, source)
    return fields, None
