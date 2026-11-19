"""The shader test: are tools/downgrade/shaders' three shaders the game's, on Direct3D 11 and the PS Vita?

    python -I tools/downgrade/shaders/test/run.py project     # inputs, cases and the Unity project
    python -I tools/downgrade/shaders/test/run.py render      # the editor renders every case (Direct3D 11)
    python -I tools/downgrade/shaders/test/run.py reference   # the same cases from the originals' DXBC, in NumPy
    python -I tools/downgrade/shaders/test/run.py compare [renders]  # renders against the reference
    python -I tools/downgrade/shaders/test/run.py sheet [renders]    # a side-by-side crop as compare-<renders>.png
    python -I tools/downgrade/shaders/test/run.py vita        # PS Vita player (psp2 shader compile), VPK, GXP cost
    python -I tools/downgrade/shaders/test/run.py all         # project, render, reference, compare, vita

Everything goes to work/shaders/test: project/ (Unity 2018.2.19f1, the shaders copied into Resources),
inputs/, reference/, d3d11/ (the editor's renders), vita-build/, t2d-shader-test.vpk, gxp/ (the PS Vita
programs from the build, with psp2shaderperf's report), and report-<renders>.txt and diff images.
device.py runs the VPK on the console and fetches its renders to vita/.

The reference is a NumPy port of the DXBC the game ships (work/shaders/extract, from extract.py), one line
per instruction in float32, so it checks the new shaders against the originals and not against themselves.
The inputs are synthetic, except "pixelart", a 398x224 crop of the game's TexturePack0 from
work/assetripper/export-stubs (game content: it stays in work/).
"""

import argparse
import os
import re
import shutil
import struct
import subprocess
import sys
import time
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
SHADERS = HERE.parent
ROOT = HERE.parents[3]
WORK = ROOT / 'work' / 'shaders' / 'test'
PROJECT = WORK / 'project'
INPUTS = WORK / 'inputs'
REFERENCE = WORK / 'reference'
VITA_BUILD = WORK / 'vita-build'
VPK = WORK / 't2d-shader-test.vpk'
GXP = WORK / 'gxp'
UNITY = Path(os.environ['LOCALAPPDATA']) / 'Unity' / 'Editors' / '2018.2.19f1' / 'Editor' / 'Unity.exe'
SDK = Path(os.environ.get('SCE_PSP2_SDK_DIR') or r'C:\vitasdk\sdk')
CLOCK = Path.home() / '.t3' / 'projects' / 'butcher-vita' / 'work' / 'native' / 'clock'

def main_checkout():
    """The repository's main checkout (work/ is per checkout; a worktree's has no AssetRipper export)."""
    result = subprocess.run(['git', '-C', str(ROOT), 'rev-parse', '--git-common-dir'], capture_output=True, text=True, check=False)
    common = Path(result.stdout.strip()) if result.returncode == 0 else ROOT / '.git'
    return (common if common.is_absolute() else ROOT / common).resolve().parent


ATLAS = next(
    (path for base in (ROOT, main_checkout())
     if (path := base / 'work' / 'assetripper' / 'export-stubs' / 'ExportedProject' / 'Assets' / 'Resources' / 'texturepack' / 'TexturePack0.bytes').exists()),
    ROOT / 'work' / 'assetripper' / 'export-stubs',
)  # fmt: skip
TITLE_ID = 'TDSH00001'
SHADER_FILES = ['CRTShader.shader', 'NormalShader.shader', 'ColorTransformShader.shader']
VPK_DATE = (2024, 1, 1, 0, 0, 0)
F = np.float32


def log(message):
    print(time.strftime('[%H:%M:%S] ') + message, flush=True)


def write_if_changed(path: Path, data: bytes):
    if path.exists() and path.read_bytes() == data:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


# Inputs and cases.


def make_inputs():
    """{name: RGBA uint8 array, top row first}."""
    rng = np.random.default_rng(2018)
    w, h = 398, 224
    # synthetic: flat blocks, one-pixel lines and dots, gradients and noise, opaque.
    img = np.zeros((h, w, 4), np.uint8)
    img[..., 3] = 255
    for _ in range(60):
        x, y = rng.integers(0, w - 8), rng.integers(0, h - 8)
        bw, bh = rng.integers(2, 40), rng.integers(2, 30)
        img[y : y + bh, x : x + bw, :3] = rng.integers(0, 256, 3)
    for x in range(0, w, 13):
        img[:, x, :3] = (255, 255, 255) if x % 2 else (255, 0, 0)
    for y in range(0, h, 11):
        img[y, :, :3] = (0, 255, 0) if y % 2 else (0, 0, 255)
    img[150:200, 20:220, :3] = np.linspace(0, 255, 200).astype(np.uint8)[None, :, None]
    img[150:200, 230:390, :3] = rng.integers(0, 256, (50, 160, 3))
    dots = rng.integers(0, [w, h], (200, 2))
    img[dots[:, 1], dots[:, 0], :3] = 255
    img[0:4, :, :3] = 255  # bright top and bottom edges, which the clamped taps read
    img[-3:, :, :3] = (255, 200, 0)
    inputs = {'synthetic': img}
    # rgba: noise with every alpha from 0 to 255, for the blended shaders.
    rgba = rng.integers(0, 256, (h, w, 4)).astype(np.uint8)
    rgba[:, :256, 3] = np.arange(256, dtype=np.uint8)[None, :]
    rgba[:20, :, 3] = 0
    rgba[20:40, :, 3] = 255
    inputs['rgba'] = rgba
    # pixelart: a 398x224 crop of the game's first atlas over black, opaque.
    if ATLAS.is_file():
        with Image.open(ATLAS) as atlas:
            crop = np.asarray(atlas.convert('RGBA').crop((2680, 950, 2680 + w, 950 + h)), np.uint8).astype(np.float32)
        art = np.empty((h, w, 4), np.uint8)
        art[..., :3] = np.round(crop[..., :3] * crop[..., 3:] / 255).astype(np.uint8)
        art[..., 3] = 255
        inputs['pixelart'] = art
    else:
        log(f'no {ATLAS}: pixelart is a second synthetic input')
        inputs['pixelart'] = np.ascontiguousarray(img[:, ::-1])
    return inputs


def case_lines():
    """cases.txt: name shader fast input filter width height clear(4) color(4) coloff(4) outputsize(2)."""
    lines = []

    def add(name, shader, fast, source, filt, size, clear=(0, 0, 0, 0), color=(1, 1, 1, 1), coloff=(0, 0, 0, 0), output=None):
        output = output or size
        values = [*clear, *color, *coloff, *output]
        lines.append(' '.join([name, shader, '1' if fast else '0', source, filt, str(size[0]), str(size[1])] + [repr(float(v)) for v in values]))

    for size in ((960, 544), (967, 544), (1920, 1080), (398, 224)):
        for source in ('pixelart', 'synthetic'):
            for fast in (False, True):
                # The fast variant needs a bilinear source (CRTShader.shader); the faithful one samples texel centres.
                add(f'crt{"fast" if fast else ""}_{source}_{size[0]}x{size[1]}', 'Unlit/CRTShader', fast, source, 'bilinear' if fast else 'point', size)
    add('crt_synthetic_960x544_bilinear', 'Unlit/CRTShader', False, 'synthetic', 'bilinear', (960, 544))
    background = (0.2, 0.4, 0.6, 0.4)  # all exact in 8 bits (0.5 clears to 127 on Direct3D 11)
    add('normal_rgba', 'Unlit/NormalShader', False, 'rgba', 'point', (398, 224), background)
    add('normal_pixelart', 'Unlit/NormalShader', False, 'pixelart', 'point', (398, 224), background)
    for name, color, coloff in (
        ('identity', (1, 1, 1, 1), (0, 0, 0, 0)),
        ('tint', (1, 0.5, 0.25, 0.75), (0.1, -0.2, 0.3, 0)),
        ('fill', (0, 0, 0, 0), (0.8, 0.3, 0.1, 1)),  # BitmapData.fillRect
        ('flash', (1, 1, 1, 1), (1, 1, 1, 0)),  # an offset of 255: the hit flash
        ('overflow', (1, 1, 1, 1), (0.5, -0.5, 0, 0.6)),  # alpha past 1
    ):
        add(f'ct_{name}', 'Unlit/ColorTransformShader', False, 'rgba', 'point', (398, 224), background, color, coloff)
    return lines


def parse_cases(text):
    cases = []
    for line in text.splitlines():
        if not line.strip() or line.startswith('#'):
            continue
        f = line.split()
        n = [float(v) for v in f[7:]]
        cases.append({
            'name': f[0], 'shader': f[1], 'fast': f[2] == '1', 'input': f[3], 'filter': f[4],
            'size': (int(f[5]), int(f[6])), 'clear': n[0:4], 'color': n[4:8], 'coloff': n[8:12], 'output': n[12:14],
        })  # fmt: skip
    return cases


def project():
    INPUTS.mkdir(parents=True, exist_ok=True)
    assets = PROJECT / 'Assets'
    for name, img in make_inputs().items():
        path = INPUTS / f'{name}.png'
        Image.fromarray(img, 'RGBA').save(path)
        write_if_changed(assets / 'Resources' / 'inputs' / f'{name}.bytes', path.read_bytes())
    cases = '\n'.join(case_lines()) + '\n'
    write_if_changed(INPUTS / 'cases.txt', cases.encode())
    write_if_changed(assets / 'Resources' / 'cases.txt', cases.encode())
    for name in SHADER_FILES:
        write_if_changed(assets / 'Resources' / 'shaders' / name, (SHADERS / name).read_bytes())
    write_if_changed(assets / 'ShaderTest.cs', (HERE / 'unity' / 'ShaderTest.cs').read_bytes())
    write_if_changed(assets / 'Editor' / 'ShaderTestBuild.cs', (HERE / 'unity' / 'ShaderTestBuild.cs').read_bytes())
    # Only built-in modules: the editor would fetch the default packages over the network.
    modules = ['imageconversion', 'imgui', 'jsonserialize']
    manifest = ',\n'.join(f'    "com.unity.modules.{m}": "1.0.0"' for m in modules)
    write_if_changed(PROJECT / 'Packages' / 'manifest.json', ('{\n  "dependencies": {\n' + manifest + '\n  }\n}\n').encode())
    log(f'project {PROJECT}: {len(case_lines())} cases')


def unity(args, log_file, keep=('T2DSH', 'error CS', 'Shader error', 'Shader warning')):
    started = time.time()
    result = subprocess.run([str(UNITY), '-batchmode', '-projectPath', str(PROJECT), '-logFile', str(log_file), *args], check=False)
    lines = log_file.read_text(encoding='utf-8', errors='replace').splitlines() if log_file.exists() else []
    for line in lines:
        if line.startswith(keep) or 'Exception' in line[:80] or 'Shader error' in line or 'Shader warning' in line:
            print('   ' + line)
    log(f'Unity exited {result.returncode} after {time.time() - started:.0f}s; log {log_file}')
    return result.returncode


def render():
    out = WORK / 'd3d11'
    if out.exists():
        shutil.rmtree(out)
    code = unity(['-executeMethod', 'ShaderTestBuild.RenderD3D11', '-outputFolder', str(out)], WORK / 'render-d3d11.log')
    if code != 0:
        sys.exit('render failed')


# The reference: the original DXBC programs in float32.


def load(path):
    """RGBA float32 in [0, 1], bottom row first (texture rows: v = 0 at row 0)."""
    with Image.open(path) as image:
        return np.ascontiguousarray(np.asarray(image.convert('RGBA'), np.uint8)[::-1]).astype(F) / F(255)


def sample(tex, u, v, filt):
    """tex2D with clamp addressing: point takes the texel the coordinate falls in, bilinear blends four."""
    h, w = tex.shape[:2]
    if filt == 'point':
        x = np.clip(np.floor(u * F(w)).astype(np.int64), 0, w - 1)
        y = np.clip(np.floor(v * F(h)).astype(np.int64), 0, h - 1)
        return tex[y, x]
    fx, fy = u * F(w) - F(0.5), v * F(h) - F(0.5)
    x0, y0 = np.floor(fx), np.floor(fy)
    ax, ay = (fx - x0)[..., None], (fy - y0)[..., None]
    x0, y0 = x0.astype(np.int64), y0.astype(np.int64)

    def t(x, y):
        return tex[np.clip(y, 0, h - 1), np.clip(x, 0, w - 1)]

    return (t(x0, y0) * (1 - ax) + t(x0 + 1, y0) * ax) * (1 - ay) + (t(x0, y0 + 1) * (1 - ax) + t(x0 + 1, y0 + 1) * ax) * ay


def crt_dxbc(tex, u, v, output, filt):
    """CRTShader's ps_4_0, instruction by instruction (work/shaders/extract/CRTShader.s0p0.fragment.b3.asm)."""
    ow, oh = F(output[0]), F(output[1])
    r0x, r0y = F(224.0) / oh, F(398.0) / ow  # div r0.xy, (224, 398), cb0[4].yx
    r0z, r0w = v * oh, u * ow  # mul r0.zw, v0.yyyx, cb0[4].yyyx
    r1x = np.floor(r0z * r0x + F(-0.5)) + F(0.5)  # mad, round_ni, add
    r1y = np.floor(r0w * r0y + F(-1.5)) + F(0.5)
    dy = r0z * r0x - r1x  # mad r0.xy, r0.zwzz, r0.xyxx, -r1.xyxx
    dx = r0w * r0y - r1y
    phase = r0w * F(0.333333343)
    phase = phase - np.floor(phase)  # frc
    first, second = phase < F(0.333333343), phase < F(0.666666687)
    w0 = np.exp2(dx * dx * F(-4.0))  # mul, mul, exp
    tx, ty = F(0.00251256279), F(0.00446428591)
    t2, t3, t5 = F(0.00502512557), F(0.00753768813), F(0.00251256279)

    def s(a, b):
        return sample(tex, a, b, filt)[..., :3]

    y0 = r1x * ty
    y1 = r1x * ty + ty
    x0 = r1y * tx
    x1, x2, x3 = r1y * tx + t5, r1y * tx + t2, r1y * tx + t3
    w1 = np.exp2((dx - F(1)) ** 2 * F(-4.0))
    w2 = np.exp2((dx - F(2)) ** 2 * F(-4.0))
    w3 = np.exp2((dx - F(3)) ** 2 * F(-4.0))
    W = lambda a: a[..., None]  # noqa: E731
    row1 = s(x1, y1) * W(w1)
    row1 = s(x0, y1) * W(w0) + row1
    row1 = s(x2, y1) * W(w2) + row1
    row1 = s(x3, y1) * W(w3) + row1
    beam1 = np.cos(np.minimum(dy * F(-0.7) + F(0.7), F(0.5)) * F(6.28318548)) * F(0.5) + F(0.5)
    beam0 = np.cos(np.minimum(dy * F(0.7), F(0.5)) * F(6.28318548)) * F(0.5) + F(0.5)
    norm = F(1.0) / (((w0 + w1) + w2) + w3)
    beam0, beam1 = beam0 * norm, beam1 * norm
    row1 = W(beam1) * row1
    row0 = W(w1) * s(x1, y0)
    row0 = s(x0, y0) * W(w0) + row0
    row0 = s(x2, y0) * W(w2) + row0
    row0 = s(x3, y0) * W(w3) + row0
    c = row0 * W(beam0) + row1
    mask = np.where(W(first), F([1.0, 0.5, 0.5]), np.where(W(second), F([0.5, 1.0, 0.5]), F([0.5, 0.5, 1.0])))
    c = mask * c
    m = np.maximum(np.maximum(np.maximum(c[..., 2], c[..., 1]), c[..., 0]), F(5.96046448e-08))
    k = m * (F(1.0) / (m * F(0.487804890) + F(0.512195110)))
    c = W(k) * (W(F(1.0) / m) * c)
    return np.concatenate([c, np.ones_like(c[..., :1])], axis=-1)


def crt_fast_model(tex, u, v, output, filt):
    """The T2D_CRT_FAST variant's own math (CRTShader.shader), to check it is written as designed; how far that
    design is from the original is the comparison with crt_dxbc's reference."""
    at_x, at_y = u * F(398.0) - F(0.5), v * F(224.0) - F(0.5)
    at_z = u * F(output[0]) * F(0.333333343)
    dx, dy = at_x - np.floor(at_x), at_y - np.floor(at_y)
    first_x, first_y = at_x - dx + F(0.5), at_y - dy + F(0.5)
    right = F(1.0) / (F(1.0) + np.exp2(F(4.0) - F(8.0) * dx))
    beams = []
    for distance in (dy, F(1.0) - dy):
        t = np.minimum(distance * F(1.4), F(1.0)) - F(0.5)
        t2 = t * t
        beams.append(F(0.5) - t * (F(1.5703204) + t2 * (F(-2.5684607) + t2 * F(1.1498051))))
    total = beams[0] + beams[1]
    c = sample(tex, (first_x + right) * F(0.0025125628), (first_y + beams[1] / total) * F(0.0044642859), filt)[..., :3]
    c = c * total[..., None]
    group = np.floor((at_z - np.floor(at_z)) * F(3.0))[..., None]
    c = c * (F(1.0) - F(0.5) * np.clip(np.abs(group - F([0.0, 1.0, 2.0])), 0, 1))
    m = c.max(axis=-1, keepdims=True)
    c = c / (m * F(0.48780488) + F(0.51219511))
    return np.concatenate([c, np.ones_like(c[..., :1])], axis=-1)


def blend(src, clear):
    """Blend SrcAlpha OneMinusSrcAlpha, One One over a target cleared to clear (UNORM8: the output clamped)."""
    src = np.clip(src, 0, 1)
    dst = np.round(F(clear) * 255) / 255
    a = src[..., 3:]
    rgb = src[..., :3] * a + dst[:3] * (1 - a)
    return np.concatenate([rgb, np.minimum(src[..., 3:] + dst[3], 1)], axis=-1)


def reference_case(case, inputs):
    w, h = case['size']
    tex = inputs[case['input']]
    v, u = np.meshgrid((np.arange(h, dtype=F) + F(0.5)) / F(h), (np.arange(w, dtype=F) + F(0.5)) / F(w), indexing='ij')
    if case['shader'] == 'Unlit/CRTShader' and case['fast']:
        out = crt_fast_model(tex, u, v, case['output'], case['filter'])
    elif case['shader'] == 'Unlit/CRTShader':
        out = crt_dxbc(tex, u, v, case['output'], case['filter'])  # opaque: no blend
    elif case['shader'] == 'Unlit/NormalShader':
        out = blend(sample(tex, u, v, case['filter']), case['clear'])  # sample o0, v1.xy, t0, s0
    else:
        # mad o0, r0, cb0[2] (_Color), cb0[3] (_Coloff)
        out = blend(sample(tex, u, v, case['filter']) * F(case['color']) + F(case['coloff']), case['clear'])
    return np.clip(np.round(np.clip(out, 0, 1) * 255), 0, 255).astype(np.uint8)[::-1]


def reference():
    cases = parse_cases((INPUTS / 'cases.txt').read_text())
    inputs = {p.stem: load(p) for p in INPUTS.glob('*.png')}
    REFERENCE.mkdir(parents=True, exist_ok=True)
    for case in cases:
        Image.fromarray(reference_case(case, inputs), 'RGBA').save(REFERENCE / f'{case["name"]}.png')
    log(f'reference: {len(cases)} cases in {REFERENCE}')


def compare(renders: Path):
    cases = parse_cases((INPUTS / 'cases.txt').read_text())
    diffs = WORK / f'diff-{renders.name}'
    diffs.mkdir(parents=True, exist_ok=True)
    report, worst = [], 0
    report.append(f'{"case, against":52} {"max":>4} {">1 px":>8} {">1 %":>7} {">8 %":>7} {"mean":>7} {"PSNR dB":>8}')
    # Each case against its reference; the fast variant also against the faithful reference of its case ("vs
    # faithful": what the cheaper design changes), which doesn't count towards the worst difference.
    rows = [(c['name'], c['name'], 'reference', True) for c in cases]
    rows += [(c['name'], c['name'].replace('crtfast_', 'crt_'), 'vs faithful', False) for c in cases if c['fast']]
    for name, reference_name, against, counts in rows:
        got_path, want_path = renders / f'{name}.png', REFERENCE / f'{reference_name}.png'
        if not got_path.exists() or not want_path.exists():
            report.append(f'{name + ", " + against:52} missing')
            worst = max(worst, 999)
            continue
        got = np.asarray(Image.open(got_path).convert('RGBA'), np.int16)
        want = np.asarray(Image.open(want_path).convert('RGBA'), np.int16)
        d = np.abs(got - want)
        per_pixel = d.max(axis=-1)
        mse = float(np.mean((got[..., :3] - want[..., :3]).astype(np.float64) ** 2))
        psnr = 99.0 if mse == 0 else 10 * np.log10(255**2 / mse)
        report.append(
            f'{name + ", " + against:52} {int(d.max()):4} {int((per_pixel > 1).sum()):8} {100 * (per_pixel > 1).mean():6.3f}% '
            f'{100 * (per_pixel > 8).mean():6.3f}% {d[..., :3].mean():7.4f} {psnr:8.2f}'
        )
        if counts:
            worst = max(worst, int(d.max()))
        if d.max() > 1:
            amplified = np.clip(per_pixel * (255 // max(1, int(d.max()))), 0, 255).astype(np.uint8)
            Image.fromarray(amplified, 'L').save(diffs / f'{name}{"" if counts else "-vs-faithful"}.png')
    text = '\n'.join(report)
    (WORK / f'report-{renders.name}.txt').write_text(text + '\n', encoding='utf-8')
    print(text)
    log(f'worst difference from the references: {worst} (of 255); report {WORK / f"report-{renders.name}.txt"}')
    return worst


def sheet(renders: Path):
    """compare-<renders>.png: for the pixel-art CRT case at 960x544, a 240x136 crop, enlarged three times: the
    input stretched to the same size, the reference from the original's DXBC, the faithful render, the fast
    render, and the faithful render's difference from the reference times 32."""
    name, (x, y, w, h), zoom = 'pixelart_960x544', (360, 200, 240, 136), 3
    source = Image.open(INPUTS / 'pixelart.png').convert('RGB').resize((960, 544), Image.NEAREST)
    tiles = [('input, nearest', source), ('original (DXBC reference)', Image.open(REFERENCE / f'crt_{name}.png').convert('RGB')),
             (f'faithful, {renders.name}', Image.open(renders / f'crt_{name}.png').convert('RGB')),
             (f'T2D_CRT_FAST, {renders.name}', Image.open(renders / f'crtfast_{name}.png').convert('RGB'))]
    diff = np.abs(np.asarray(tiles[2][1], np.int16) - np.asarray(tiles[1][1], np.int16)).max(-1)
    tiles.append(('faithful - original, x32', Image.fromarray(np.clip(diff * 32, 0, 255).astype(np.uint8)).convert('RGB')))
    out = Image.new('RGB', (len(tiles) * (w * zoom + 8), h * zoom + 20), (32, 32, 32))
    from PIL import ImageDraw

    draw = ImageDraw.Draw(out)
    for i, (label, image) in enumerate(tiles):
        crop = image.crop((x, y, x + w, y + h)).resize((w * zoom, h * zoom), Image.NEAREST)
        out.paste(crop, (i * (w * zoom + 8), 20))
        draw.text((i * (w * zoom + 8) + 4, 4), label, fill=(230, 230, 230))
    path = WORK / f'compare-{renders.name}.png'
    out.save(path)
    log(f'sheet {path}')


# The PS Vita build: the editor compiles the shaders with psp2cgc; the GXPs come out of the built data.


def vita():
    log_file = WORK / 'build-vita.log'
    if VITA_BUILD.exists():
        shutil.rmtree(VITA_BUILD)
    code = unity(
        ['-nographics', '-buildTarget', 'PSP2', '-executeMethod', 'ShaderTestBuild.BuildVita',
         '-buildOutput', str(VITA_BUILD), '-vitaTitleId', TITLE_ID], log_file,
    )  # fmt: skip
    if code != 0 or not any(VITA_BUILD.glob('*.self')):
        sys.exit('PS Vita build failed')
    plugins = VITA_BUILD / 'Media' / 'Plugins'
    plugins.mkdir(parents=True, exist_ok=True)
    for name in ('ButcherClock.suprx', 'ButcherClock.suprx.b.suprx'):
        if (CLOCK / name).exists():
            shutil.copy2(CLOCK / name, plugins / name)
        else:
            log(f'note: no {CLOCK / name}; the app runs at the default clocks')
    package()
    gxp_cost()


def package():
    selfs = list(VITA_BUILD.glob('*.self'))
    player = bytearray(selfs[0].read_bytes())
    player[0x80] = 0  # trial flag, as butcher-vita's package_vita clears it
    marker = player.find(b'trial.png')
    if marker >= 0:
        player[marker] = 0
    skip = {'SymbolFiles', 'configuration.psp2path'}
    with zipfile.ZipFile(VPK, 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(zipfile.ZipInfo('eboot.bin', date_time=VPK_DATE), bytes(player))
        for path in sorted(VITA_BUILD.rglob('*')):
            relative = path.relative_to(VITA_BUILD)
            if path.is_dir() or relative.parts[0] in skip or path.suffix in ('.self', '.bat'):
                continue
            info = zipfile.ZipInfo(relative.as_posix(), date_time=VPK_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes())
    log(f'packaged {VPK} ({VPK.stat().st_size / 2**20:.1f} MB)')


def gxp_programs():
    """{(shader, index): GXP} for the test's three shaders in the PS Vita build's data."""
    import lz4.block
    import UnityPy

    found = {}
    for path in sorted((VITA_BUILD / 'Media').rglob('*')):
        if not path.is_file() or path.suffix in ('.resS', '.resource', '.suprx', '.dll', '.txt', '.xml', '.png'):
            continue
        try:
            env = UnityPy.load(str(path))
        except Exception:
            continue
        for obj in env.objects:
            if obj.type.name != 'Shader':
                continue
            tree = obj.read_typetree()
            name = tree['m_ParsedForm']['m_Name']
            if not name.startswith('Unlit/') or name.split('/')[1] + '.shader' not in SHADER_FILES:
                continue
            blob = bytes(tree['compressedBlob'])
            for p, _platform in enumerate(tree['platforms']):
                # 2018.2 stores one blob per platform; Unity 6 a list (one per segment).
                offsets, sizes, fulls = (tree[k][p] for k in ('offsets', 'compressedLengths', 'decompressedLengths'))
                if isinstance(offsets, int):
                    offsets, sizes, fulls = [offsets], [sizes], [fulls]
                for offset, size, full in zip(offsets, sizes, fulls):
                    data = lz4.block.decompress(blob[offset : offset + size], uncompressed_size=full)
                    start, index = 0, 0
                    while (start := data.find(b'GXP\x00', start)) >= 0:
                        length = struct.unpack_from('<I', data, start + 8)[0]
                        found[(name.split('/')[1], index)] = data[start : start + length]
                        start += max(length, 4)
                        index += 1
    return found


def gxp_cost():
    GXP.mkdir(parents=True, exist_ok=True)
    perf = SDK / 'host_tools' / 'bin' / 'psp2shaderperf.exe'
    programs = gxp_programs()
    if not programs:
        sys.exit('no GXP programs found in the PS Vita build')
    lines = []
    for (shader, index), gxp in sorted(programs.items()):
        path = GXP / f'{shader}.{index}.gxp'
        path.write_bytes(gxp)
        result = subprocess.run([str(perf), '-stats', '-disasm', str(path)], capture_output=True, text=True, check=False)
        (GXP / f'{shader}.{index}.perf.txt').write_text(result.stdout + result.stderr, encoding='utf-8')
        summary = [line.strip() for line in result.stdout.splitlines() if re.search(r'(?i)cycle|program type|texture|instruction', line)]
        lines.append(f'{shader}.{index}: ' + ' | '.join(summary[:12]))
    (GXP / 'summary.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print('\n'.join(lines))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('stage', choices=['project', 'render', 'reference', 'compare', 'sheet', 'vita', 'gxp', 'all'])
    parser.add_argument('renders', nargs='?', default='d3d11', help='compare: a folder under work/shaders/test')
    args = parser.parse_args()
    if args.stage in ('project', 'all'):
        project()
    if args.stage in ('render', 'all'):
        render()
    if args.stage in ('reference', 'all'):
        reference()
    if args.stage in ('compare', 'all'):
        compare(WORK / args.renders)
    if args.stage in ('sheet', 'all'):
        sheet(WORK / args.renders)
    if args.stage in ('vita', 'all'):
        vita()
    if args.stage == 'gxp':
        gxp_cost()


if __name__ == '__main__':
    main()
