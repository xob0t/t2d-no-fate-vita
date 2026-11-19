"""Fit the importer's resampling kernel to its own output, and write it for the builder.

    uv run --with scipy tools/downgrade/audio/importer-reference/fit_resampler.py

Reads work/audio-importer-reference/reference.pkl (make.py): the PCM clips the editor resampled from 44.1 kHz to
32 kHz, the only conversion the audio rule asks for. Sample j of n_out from n is a sum of the source's samples
around position j * n / n_out, weighted by a kernel of their distance t from it; the kernel is the same for every
output sample (the editor's output fits it to about 1.5 LSB rms), and it is no windowed sinc of any usual window
(the best ones leave 29 dB). So the kernel is fitted as a table, h(|t|) at TABLE_STEP source samples apart out to
REACH, linearly interpolated, by least squares on the stereo probes' interior; the edges, the mono probes and a
probe that clips are held out, and the builder's --compare checks them all (tools/builder/check_audio.py).

Writes tools/builder/audio_kernel.py, which tools/builder/audio.py reads. The table is a measurement of Unity's
filter on synthetic signals, nothing of the game.
"""

import io
import pickle
import struct
import sys
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
REFERENCE = ROOT / 'work' / 'audio-importer-reference' / 'reference.pkl'
OUT = ROOT / 'tools' / 'builder' / 'audio_kernel.py'
# Linear, in range: loud_pcm_32000 is left out, as its filtered peaks pass full scale and the editor's clipping
# there is not the kernel's.
TRAIN = ['noise_pcm_32000', 'impulse_pcm_32000', 'sweep_pcm_32000', 'tones_pcm_32000']
TABLE_STEP = 1 / 16  # source samples between table entries
REACH = 40  # source samples either side
EDGE = 64  # output samples left out of the fit at each end


def source(probe):
    with wave.open(io.BytesIO(probe['wav'])) as w:
        data = np.frombuffer(w.readframes(w.getnframes()), '<i2')
        return data.reshape(-1, w.getnchannels()).T.astype(np.float64)


def output(probe):
    bank = probe['bank']
    sample_headers, data_size = struct.unpack_from('<I', bank, 12)[0], struct.unpack_from('<I', bank, 20)[0]
    word = struct.unpack_from('<Q', bank, 0x3C)[0]
    channels, samples = 2 if (word >> 5) & 1 else 1, word >> 34
    start = 0x3C + sample_headers
    pcm = np.frombuffer(bank[start : start + samples * 2 * channels], '<i2')
    assert samples * 2 * channels <= data_size
    return pcm.reshape(-1, channels).T.astype(np.float64)


def rows(a, m, js):
    """Sparse rows of the design matrix: (output index, table column, weight) for outputs js."""
    channels, n = a.shape
    step = n / m
    pos = js * step
    whole = np.floor(pos).astype(np.int64)
    out_r, out_c, out_v = [], [], []
    for offset in range(-REACH - 1, REACH + 2):
        i = whole + offset
        t = np.abs(i - pos)
        inside = (t < REACH) & (i >= 0) & (i < n)
        u = t / TABLE_STEP
        k = np.minimum(np.floor(u).astype(np.int64), int(REACH / TABLE_STEP))  # outside: weight 0
        f = u - k
        for c in range(channels):
            r = c * len(js) + np.arange(len(js))
            x = a[c, np.clip(i, 0, n - 1)] * inside
            out_r += [r, r]
            out_c += [k, k + 1]
            out_v += [x * (1 - f), x * f]
    return np.concatenate(out_r), np.concatenate(out_c), np.concatenate(out_v)


def main():
    from scipy.sparse import coo_matrix
    from scipy.sparse.linalg import lsqr

    probes = pickle.loads(REFERENCE.read_bytes())  # noqa: S301 (make.py's own file under work/)
    columns = int(REACH / TABLE_STEP) + 2
    blocks, targets = [], []
    for name in TRAIN:
        a, o = source(probes[name]), output(probes[name])
        js = np.arange(EDGE, o.shape[1] - EDGE)
        r, c, v = rows(a, o.shape[1], js)
        blocks.append(coo_matrix((v, (r, c)), shape=(a.shape[0] * len(js), columns)).tocsr())
        y = o[:, js].reshape(-1)
        targets.append(y + 0.5 * np.sign(y))  # the editor truncates; fit the value before it
    from scipy.sparse import vstack

    design, y = vstack(blocks).tocsr(), np.concatenate(targets)
    table = lsqr(design, y, atol=1e-14, btol=1e-14, iter_lim=20000)[0]
    table[-2:] = 0
    residual = np.trunc(design @ table) - y + 0.5 * np.sign(y)
    print(f'fitted {columns} entries on {len(y)} samples: residual rms {np.sqrt((residual**2).mean()):.3f} LSB')
    values = ',\n    '.join(', '.join(f'{v:.9e}' for v in table[i : i + 6]) for i in range(0, columns, 6))
    OUT.write_text(
        '"""The resampling kernel of Unity 2018.2.19f1\'s audio importer, as fitted to its 44.1 kHz to 32 kHz output\n'
        'by tools/downgrade/audio/importer-reference/fit_resampler.py (generated; do not edit).\n\n'
        'KERNEL[k] is h(k * STEP), source samples from the output position; h is even and linear between entries.\n'
        '"""\n\n'
        f'SOURCE_RATE, OUTPUT_RATE = 44100, 32000\nSTEP = {TABLE_STEP!r}\nREACH = {REACH}\n'
        f'KERNEL = (\n    {values},\n)\n',
        encoding='utf-8',
    )
    print(f'wrote {OUT}')


if __name__ == '__main__':
    sys.exit(main())
