"""Build the AudioClips and their .resource streams and compare them with the reference build.

    uv run tools/builder/check_audio.py [--every N] [--workers N] [--no-snr]
    uv run tools/builder/check_audio.py --compare [--every N] [--workers N] [--no-snr]

Clips pair with the reference by path ID (layout.py reproduces the reference order). Reports
per-field AudioClip matches, FSB5 header matches field by field, the stream layout (offsets and
file sizes) and the sound: SNR of our HEVAG and Unity's against the stretched source, and of ours
against Unity's, all decoded as the console does. Our .resource files go to work/builder/audio/.
--every N checks one clip in N (the stream layout then is not comparable and is skipped).

--compare checks the import settings the pipeline's audio rule uses (forceToMono, a sample rate
override; tools/downgrade/audio/audio_rules.py) against what Unity 2018.2.19f1's PS Vita importer
made of them, without a reference build of the whole game:

- the importer reference (tools/downgrade/audio/importer-reference/make.py): synthetic clips in PCM
  and HEVAG, mono and stereo, at several rates. The PCM banks hold the importer's converted samples,
  so the mono mix and the resampler compare sample for sample; every field and bank header byte
  (but FMOD's content hash) must match;
- the audio test's editor build of the game's 930 clips (tools/downgrade/audio/vita-audio-test,
  work/vita-audio-test/build and project in this checkout or the main one): every AudioClip field
  and bank header byte (but the content hash), and the sound against Unity's.

HEVAG data is never byte for byte Unity's (hevag.py's encoder is not Sony's), so --compare reports
its SNR. Exit code 1 if anything that should match differs.
"""

import argparse
import collections
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import audio
import fsb5
import hevag
import numpy as np
import paths
import UnityPy
from layout import Layout
from placement import first_path_id

OUT = paths.WORK / 'audio'
FILES = ['resources.assets', 'sharedassets1.assets']


def reference_clips(file_name):
    env = UnityPy.load(str(paths.REFERENCE_MEDIA / file_name))
    sf = next(iter(env.files.values()))
    return {pid: o.read_typetree() for pid, o in sf.objects.items() if o.class_id == 83}


def frame_layout(data, channels, frames):
    """Whether the frames carry Unity's flags (6 first, 3 last, 2 between) and zeros follow."""
    f = np.frombuffer(data[: frames * 16 * channels], np.uint8).reshape(frames, channels, 16)
    expected = np.full(frames, hevag.MIDDLE)
    expected[-1], expected[0] = hevag.LAST, hevag.FIRST
    return bool(((f[..., 1] & 0x0F) == expected[:, None]).all() and not any(data[frames * 16 * channels :]))


def quality(items):
    """[(source path, frames, channels, ours, theirs)] -> [(ours/source, unity/source, ours/unity) dB]."""
    sources = []
    for path, frames, _, _, _ in items:
        pcm, _ = audio.read(path)
        sources.append(hevag.stretch(pcm, frames))
    ours = hevag.decode([(o, ch) for _, _, ch, o, _ in items])
    theirs = hevag.decode([(t, ch) for _, _, ch, _, t in items])
    return [(hevag.snr(s, o), hevag.snr(s, t), hevag.snr(t, o)) for s, o, t in zip(sources, ours, theirs, strict=True)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--every', type=int, default=1)
    parser.add_argument('--workers', type=int, help="default: the build's (audio.default_workers)")
    parser.add_argument('--no-snr', action='store_true')
    parser.add_argument('--show', type=int, default=3)
    parser.add_argument('--compare', action='store_true', help='against the importer reference and the audio test')
    parser.add_argument('--importer-reference', type=Path, help='its reference.pkl (default: found under work/)')
    parser.add_argument('--test-build', type=Path, help='the audio test: work/vita-audio-test (default: found)')
    args = parser.parse_args()
    if args.compare:
        return compare(args)
    layout = Layout()
    OUT.mkdir(parents=True, exist_ok=True)
    pairs = []  # (file, path ID, asset)
    for f in FILES:
        start = first_path_id(f)
        for i, (g, _, cid) in enumerate(layout.objects(f)):
            if cid == 83:
                pairs.append((f, start + i, layout.db.by_guid[g]))
    pairs = pairs[:: args.every]
    print(f'{len(pairs)} clips')
    args.workers = args.workers or audio.default_workers(len(pairs))

    started = time.time()
    built = audio.build([a for _, _, a in pairs], args.workers)
    elapsed = time.time() - started
    seconds = sum(f['m_Length'] for f, _ in built)
    print(f'built in {elapsed:.1f} s on up to {args.workers} workers ({seconds / 3600:.2f} hours of audio)')

    totals, examples = collections.Counter(), collections.defaultdict(list)

    def note(key, ok, example):
        totals[(key, ok)] += 1
        if not ok and len(examples[key]) < args.show:
            examples[key].append(example)

    jobs = []
    for f in FILES:
        refs = reference_clips(f)
        mine = [(pid, a, entry) for (ff, pid, a), entry in zip(pairs, built, strict=True) if ff == f]
        source = audio.resource_name(f)
        stream = audio.resource_stream([e for _, _, e in mine], source)
        (OUT / source).write_bytes(stream)
        reference_stream = paths.REFERENCE_MEDIA / source
        if args.every == 1:
            size = reference_stream.stat().st_size
            print(f'{source}: {len(stream)} bytes, reference {size}: {"same" if len(stream) == size else "differs"}')
        with open(reference_stream, 'rb') as handle:
            for pid, a, (fields, bank) in mine:
                theirs = refs[pid]
                for k, v in fields.items():
                    if k == 'm_Resource':
                        for kk in ('m_Source', 'm_Size') + (('m_Offset',) if args.every == 1 else ()):
                            note(f'{k}.{kk}', v[kk] == theirs[k][kk], (a.path, v[kk], theirs[k][kk]))
                    else:
                        note(k, v == theirs[k], (a.path, v, theirs[k]))
                r = theirs['m_Resource']
                handle.seek(r['m_Offset'])
                ref_bank = handle.read(r['m_Size'])
                ours, ref = fsb5.parse(bank), fsb5.parse(ref_bank)
                for k in ('codec', 'sample_headers', 'names', 'data_size', 'name_hash', 'word', 'chunks'):
                    note(f'fsb {k}', ours[k] == ref[k], (a.path, ours[k], ref[k]))
                note('fsb content_hash (expected to differ)', ours['content_hash'] == ref['content_hash'], (a.path,))
                head = ours['header'][:0x2C] + ours['header'][0x3C:]
                note(
                    'fsb header bytes but the content hash',
                    head == ref['header'][:0x2C] + ref['header'][0x3C:],
                    (a.path,),
                )
                note('fsb size', len(bank) == len(ref_bank), (a.path, len(bank), len(ref_bank)))
                frames = ours['samples'] // hevag.FRAME
                ch = ours['channels']
                note('fsb frame flags and padding', frame_layout(ours['data'], ch, frames), (a.path,))
                note('reference frame flags and padding', frame_layout(ref['data'], ch, frames), (a.path,))
                data, ref_data = ours['data'][: frames * 16 * ch], ref['data'][: frames * 16 * ch]
                note('HEVAG bytes identical', data == ref_data, (a.path,))
                jobs.append((a.path, paths.PROJECT / a.path, frames, ch, data, ref_data))

    for (key, ok), v in sorted(totals.items()):
        if ok:
            bad = totals[(key, False)]
            print(f'  {key:42} {v:5} same' + (f', {bad} differ' if bad else ''))
    for (key, ok), v in sorted(totals.items()):
        if not ok and not totals[(key, True)]:
            print(f'  {key:42} {v:5} differ')
    for key, ex in examples.items():
        for e in ex:
            print(f'    {key}: {e}')

    if args.no_snr:
        return 0
    started = time.time()
    groups = [jobs[i :: args.workers * 4] for i in range(args.workers * 4)]
    results = {}
    with audio.one_blas_thread(), ProcessPoolExecutor(args.workers) as pool:
        for group, out in zip(groups, pool.map(quality, [[j[1:] for j in g] for g in groups]), strict=True):
            for j, q in zip(group, out, strict=True):
                results[j[0]] = q
    snr = np.array(list(results.values()))
    finite = np.where(np.isfinite(snr), snr, np.nan)
    print(f'SNR over {len(snr)} clips, dB (decoded in {time.time() - started:.1f} s):')
    for i, name in enumerate(('ours vs source', 'Unity vs source', 'ours vs Unity')):
        col = finite[:, i]
        p = np.nanpercentile(col, [1, 10, 50, 90])
        spread = f'p1 {p[0]:6.2f}  p10 {p[1]:6.2f}  median {p[2]:6.2f}  p90 {p[3]:6.2f}'
        print(f'  {name:16} mean {np.nanmean(col):6.2f}  {spread}')
    better = np.sum(finite[:, 0] >= finite[:, 1])
    print(f'  ours at least as close to the source as Unity on {better} of {len(snr)} clips')
    worst = sorted(results.items(), key=lambda kv: kv[1][2])[: args.show]
    for path, q in worst:
        print(f'  lowest ours vs Unity: {path}: {q[2]:.2f} dB (ours/source {q[0]:.2f}, Unity/source {q[1]:.2f})')
    return 0


# ---------------------------------------------------------------- --compare

FIELDS = (
    'm_Name',
    'm_LoadType',
    'm_Channels',
    'm_Frequency',
    'm_BitsPerSample',
    'm_Length',
    'm_IsTrackerFormat',
    'm_Ambisonic',
    'm_SubsoundIndex',
    'm_PreloadAudioData',
    'm_LoadInBackground',
    'm_Legacy3D',
    'm_CompressionFormat',
)


def checkouts():
    """This checkout and the main one (a worktree's work/ may not have the references)."""
    import subprocess

    found = [paths.ROOT]
    try:
        common = subprocess.run(
            ['git', '-C', str(paths.ROOT), 'rev-parse', '--git-common-dir'], capture_output=True, text=True, check=True
        ).stdout.strip()
        main = (paths.ROOT / common).resolve().parent
        if main != paths.ROOT:
            found.append(main)
    except (OSError, subprocess.CalledProcessError):
        pass
    return found


def first_existing(given, relative):
    if given:
        return given
    return next((root / relative for root in checkouts() if (root / relative).exists()), None)


def bank_header_without_hash(bank):
    """The bank's header and sample header, but FMOD's content hash (fsb5.py: not reproducible)."""
    return bank[:0x2C] + bank[0x3C : fsb5.HEADER + fsb5.parse(bank)['sample_headers']]


def compare_clip(label, ours, theirs, totals, examples):
    """Fields and bank of one clip against Unity's, counted in totals; returns both banks parsed."""
    fields, bank = ours
    their_fields, their_bank = theirs

    def note(key, ok, example):
        totals[key, ok] += 1
        if not ok and len(examples[key]) < 3:
            examples[key].append((label, *example))

    for k in FIELDS:
        note(k, fields[k] == their_fields[k], (fields[k], their_fields[k]))
    size = their_fields['m_Resource']['m_Size']
    note('m_Resource.m_Size', len(bank) == size, (len(bank), size))
    a, b = fsb5.parse(bank), fsb5.parse(their_bank)
    note('bank header but the content hash', bank_header_without_hash(bank) == bank_header_without_hash(their_bank), ())
    return a, b


def converted_source(path, importer, frames):
    """The source as our importer feeds the encoder: converted and stretched to whole frames."""
    pcm, _ = audio.convert(*audio.read(path), importer)
    return hevag.stretch(pcm, frames)


def hevag_quality(items):
    """[(source path, importer, frames, channels, ours, theirs)] -> [(ours/source, Unity/source, ours/Unity) dB]."""
    sources = [converted_source(p, imp, fr) for p, imp, fr, _, _, _ in items]
    ours = hevag.decode([(o, ch) for _, _, _, ch, o, _ in items])
    theirs = hevag.decode([(t, ch) for _, _, _, ch, _, t in items])
    return [(hevag.snr(s, o), hevag.snr(s, t), hevag.snr(t, o)) for s, o, t in zip(sources, ours, theirs, strict=True)]


def print_totals(title, totals, examples, expected_to_differ=()):
    """Prints the counts; returns how many of what should match differ."""
    print(title)
    bad = 0
    for key in sorted({k for k, _ in totals}):
        same, differ = totals[key, True], totals[key, False]
        expected = key in expected_to_differ
        bad += 0 if expected else differ
        print(
            f'  {key:36} {same:5} same'
            + (f', {differ} differ' if differ else '')
            + (' (expected)' if expected and differ else '')
        )
        for e in examples.get(key, []):
            print(f'      {e}')
    return bad


def snr_summary(title, snr):
    snr = np.where(np.isfinite(snr), snr, np.nan)
    print(title)
    for i, name in enumerate(('ours vs source', 'Unity vs source', 'ours vs Unity')):
        p = np.nanpercentile(snr[:, i], [1, 10, 50])
        print(
            f'    {name:16} mean {np.nanmean(snr[:, i]):6.2f}  p1 {p[0]:6.2f}  p10 {p[1]:6.2f}  median {p[2]:6.2f} dB'
        )


def compare_probes(reference_pkl, workers):
    """The importer reference's synthetic clips: PCM sample for sample, HEVAG by SNR."""
    import pickle

    probes = pickle.loads(reference_pkl.read_bytes())
    folder = reference_pkl.parent / 'project' / 'Assets' / 'probe'
    names, jobs = [], []
    for n in sorted(probes):
        try:
            importer = audio.settings(audio.unityyaml.meta(folder / f'{n}.wav.meta'))
            audio.check_rate(probe_rate(probes[n]), importer)
            jobs.append((n, folder / f'{n}.wav', importer))
            names.append(n)
        except NotImplementedError as e:
            print(f'  skipped {n}: the builder refuses it ({e})')
    built = audio.build_jobs(jobs, workers, cache=False)
    totals, examples = collections.Counter(), collections.defaultdict(list)
    rows, hevag_items = [], []
    for name, job, ours in zip(names, jobs, built, strict=True):
        a, b = compare_clip(name, ours, (probes[name]['fields'], probes[name]['bank']), totals, examples)
        resampled = audio.output_rate(probe_rate(probes[name]), job[2]) != probe_rate(probes[name])
        if b['codec'] == fsb5.CODEC_PCM16:
            n, ch = b['samples'], b['channels']
            x = np.frombuffer(a['data'][: n * 2 * ch], '<i2').astype(np.int64)
            y = np.frombuffer(b['data'][: n * 2 * ch], '<i2').astype(np.int64)
            identical = bool(np.array_equal(x, y))
            key = 'PCM data identical, resampled' if resampled else 'PCM data identical, not resampled'
            totals[key, identical] += 1
            rows.append((name, identical, int(np.abs(x - y).max()), hevag.snr(y, x)))
        else:
            frames = b['samples'] // hevag.FRAME
            ch = b['channels']
            ours_data, their_data = a['data'][: frames * 16 * ch], b['data'][: frames * 16 * ch]
            totals['HEVAG data identical', ours_data == their_data] += 1
            hevag_items.append((job[1], job[2], frames, ch, ours_data, their_data))
    expected = ('PCM data identical, resampled', 'HEVAG data identical')
    bad = print_totals(f'Importer reference: {len(names)} clips ({reference_pkl})', totals, examples, expected)
    print("  PCM clips, our conversion against the importer's:")
    for name, identical, worst, snr in rows:
        print(f'    {name:28} ' + ('identical' if identical else f'max |diff| {worst:5}, SNR {snr:6.2f} dB'))
    if hevag_items:
        print('  HEVAG clips:')
        for (path, *_), q in zip(hevag_items, hevag_quality(hevag_items), strict=True):
            print(
                f'    {path.stem:28} SNR ours/source {q[0]:6.2f}, Unity/source {q[1]:6.2f}, ours/Unity {q[2]:6.2f} dB'
            )
    return bad


def probe_rate(probe):
    import io
    import wave

    with wave.open(io.BytesIO(probe['wav'])) as w:
        return w.getframerate()


def test_clips(test):
    """{container path: (fields, bank)} of the audio test's build."""
    media = test / 'build' / 'Media'
    env = UnityPy.load(str(media))
    manager = next(o for o in env.objects if o.type.name == 'ResourceManager').read_typetree()
    by_id = {ptr['m_PathID']: path for path, ptr in manager['m_Container']}
    clips, streams = {}, {}
    for obj in env.objects:
        if obj.type.name != 'AudioClip' or obj.path_id not in by_id:
            continue
        tree = obj.read_typetree()
        resource = tree['m_Resource']
        name = Path(resource['m_Source']).name
        if name not in streams:
            streams[name] = (media / name).read_bytes()
        clips[by_id[obj.path_id]] = (
            tree,
            streams[name][resource['m_Offset'] : resource['m_Offset'] + resource['m_Size']],
        )
    return clips


def compare_game(test, every, workers, snr):
    """The audio test's editor build of the game's clips, imported with audio_rules.py's settings."""
    clips = test_clips(test)
    resources = test / 'project' / 'Assets' / 'Resources'
    selected = sorted(p for p in clips if p.startswith('audio/'))[::every]
    jobs = []
    for p in selected:
        # Container paths are lower case; the clip is named after its file, as the builder names it (audio.clip_name).
        folder, name = (resources / p).parent, p.rsplit('/', 1)[-1]
        source = next(f for f in folder.iterdir() if f.stem.lower() == name and f.suffix.lower() in ('.wav', '.ogg'))
        jobs.append((source.stem, source, audio.settings(audio.unityyaml.meta(Path(f'{source}.meta')))))
    started = time.time()
    built = audio.build_jobs(jobs, workers)
    print(f'built {len(jobs)} clips in {time.time() - started:.1f} s')
    totals, examples = collections.Counter(), collections.defaultdict(list)
    groups = collections.defaultdict(list)
    for p, job, ours in zip(selected, jobs, built, strict=True):
        a, b = compare_clip(p, ours, clips[p], totals, examples)
        frames = b['samples'] // hevag.FRAME
        ch = b['channels']
        data, theirs = a['data'][: frames * 16 * ch], b['data'][: frames * 16 * ch]
        totals['HEVAG data identical', data == theirs] += 1
        rate = int(job[2]['sampleRateOverride']) if job[2]['sampleRateSetting'] == audio.OVERRIDE_RATE else 0
        kind = ('mono' if job[2]['forceToMono'] != '0' else 'stereo') + (f', {rate} Hz' if rate else ', source rate')
        groups[kind].append((job[1], job[2], frames, ch, data, theirs))
    bad = print_totals(
        f'Audio test build: {len(jobs)} game clips ({test})', totals, examples, ('HEVAG data identical',)
    )
    if snr:
        with audio.one_blas_thread(), ProcessPoolExecutor(workers or audio.default_workers(16)) as pool:
            for kind, group in sorted(groups.items()):
                chunks = [c for c in (group[i::16] for i in range(16)) if c]
                quality = [q for out in pool.map(hevag_quality, chunks) for q in out]
                snr_summary(f'  {kind}: {len(group)} clips, HEVAG SNR', np.array(quality))
    return bad


def compare(args):
    reference = first_existing(args.importer_reference, Path('work') / 'audio-importer-reference' / 'reference.pkl')
    test = first_existing(args.test_build, Path('work') / 'vita-audio-test' / 'build' / 'Media')
    test = test.parents[1] if test and args.test_build is None else test
    if reference is None and test is None:
        sys.exit('no importer reference (tools/downgrade/audio/importer-reference/make.py) and no audio test build')
    bad = 0
    if reference:
        bad += compare_probes(reference, args.workers)
    else:
        print('no importer reference: run tools/downgrade/audio/importer-reference/make.py')
    if test:
        bad += compare_game(test, args.every, args.workers, not args.no_snr)
    else:
        print('no audio test build: run tools/downgrade/audio/vita-audio-test/build.py')
    print('compare: ' + ('everything that should match matches' if bad == 0 else f'{bad} mismatches'))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
