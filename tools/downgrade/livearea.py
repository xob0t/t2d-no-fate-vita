"""PS Vita LiveArea artwork, composed from Terminator 2D: NO FATE's Steam store art (laid out like Huntdown's port) and, for the bubble, the
game's own high score sprite. From butcher-vita's (MIT, same author).

    uv run tools/downgrade/livearea.py [--force]

The images come from Steam's public CDN (no Steam client, no game launch), downloaded once into
work/livearea/store/ and read from there afterwards. Writes to work/livearea/:

  icon0.png     128x128   bubble: the endoskeleton of the high score screen, read from your copy of the game
                          (the recovered Assembly-CSharp's frame table, the ripped texture packs); before the
                          recover and rip stages have run, Sarah Connor from the vertical key art
  bg0.png       840x500   LiveArea background: the library hero art's middle (the T-1000, Sarah Connor and the
                          endoskeleton), from its 3840x1240 version
  gate.png      280x158   LiveArea start gate: the game's logo alone (logo_2x.png) with a soft dark shadow, on
                          transparency, so the background shows around it (as in Huntdown's port)
  pic0.png      960x544   shown while the app starts: the hero art with the logo along the bottom, over a dark fade

The LiveArea images and pic0 are 8-bit indexed: the installer rejects a truecolor pic0 (promote
error 0x8010113D), and icon0 must not have an alpha channel (The Drifter's port found both). The
outputs are remade when the cached sources or this script change. pipeline.py puts them into the
VPK (vita_app_files).
"""

import argparse
import hashlib
import urllib.request
from pathlib import Path

import io
import sys

import progress
from common import STEAM_APP_ID, TARGET, WORK, log
from PIL import Image, ImageFilter

OUT = WORK / 'livearea'
STORE = OUT / 'store'
FILES = ['icon0.png', 'bg0.png', 'gate.png', 'pic0.png']
PALETTE_COLORS = 256  # 8-bit indexed

CDN = f'https://cdn.akamai.steamstatic.com/steam/apps/{STEAM_APP_ID}/'
# Cached name: (URL, SHA-256 of the file the crops below were made for). A changed file is only
# reported: Valve can replace store art at the same URL.
SOURCES = {
    # Vertical key art, 600x900: the endoskeleton's head behind Sarah Connor, the logo below (the bubble's fallback).
    'library.jpg': (CDN + 'library_600x900_2x.jpg', '2c248cddef37deda386b948eff4a2c1c14559e8e337f35e2a2f38a694efb63c2'),
    # Library hero art, 3840x1240, without the logo: the T-1000, Sarah Connor and the endoskeleton in the middle.
    'hero_2x.jpg': (CDN + 'library_hero_2x.jpg', 'def5b14a979629a74fe29711a2c792ca488fde2ff25c109ff4b065b98fb88891'),
    # The library logo, 1280x720, white with a red shadow on transparency.
    'logo_2x.png': (CDN + 'logo_2x.png', '5ec22a38c0dfe2269d4a09899b1bf7deac27375803b207ffd7e934000075234d'),
}
ICON_CROP = (195, 225, 435, 465)  # Sarah Connor, sunglasses and rifle, in library.jpg: reads at bubble size
HERO_CENTRE = 1940  # the x the crops of hero_2x.jpg centre on: the three figures, a little left of the image's middle
GATE_LOGO_WIDTH = 256  # of the gate's 280
PIC_LOGO_WIDTH = 620  # of pic0's 960
PIC_FADE = 200  # the dark band under pic0's logo, fading in from its top


def fetch():
    """The cached store images, downloading the missing ones."""
    STORE.mkdir(parents=True, exist_ok=True)
    paths = {}
    for name, (url, digest) in SOURCES.items():
        path = STORE / name
        if not path.exists():
            log(f'Downloading {url}')
            request = urllib.request.Request(url, headers={'User-Agent': 't2d-no-fate-vita-livearea'})
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    data = response.read()
            except OSError as error:
                raise RuntimeError(f'cannot download {url} ({error}); the LiveArea art needs it once') from error
            part = path.with_name(path.name + '.part')
            part.write_bytes(data)
            part.replace(path)
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != digest:
            log(f'warning: {path.name} differs from the store image the crops were made for; check work/livearea/')
        paths[name] = path
    return paths


def cover(image: Image.Image, size, box=None):
    """image (or its box) scaled to fill size, centred, the overflow cut off."""
    if box:
        image = image.crop(box)
    width, height = size
    scale = max(width / image.width, height / image.height)
    scaled = image.resize((round(image.width * scale), round(image.height * scale)), Image.LANCZOS)
    left, top = (scaled.width - width) // 2, (scaled.height - height) // 2
    return scaled.crop((left, top, left + width, top + height))


def hero_crop(hero: Image.Image, size):
    """hero_2x.jpg's full height at size's aspect, centred on HERO_CENTRE, scaled to size."""
    half = round(hero.height * size[0] / size[1]) // 2
    return cover(hero, size, (HERO_CENTRE - half, 0, HERO_CENTRE + half, hero.height))


def shadowed(logo: Image.Image, width, blur):
    """The logo (cut to what isn't transparent) at width, over a soft black shadow of itself, with room for it."""
    logo = logo.crop(logo.getbbox())
    logo = logo.resize((width, round(logo.height * width / logo.width)), Image.LANCZOS)
    pad = blur * 3
    out = Image.new('RGBA', (logo.width + 2 * pad, logo.height + 2 * pad), (0, 0, 0, 0))
    shadow = Image.new('RGBA', logo.size, (0, 0, 0, 255))
    shadow.putalpha(logo.getchannel('A'))
    out.alpha_composite(shadow, (pad, pad))
    out = out.filter(ImageFilter.GaussianBlur(blur))
    out.putalpha(out.getchannel('A').point(lambda a: min(255, a * 3)))
    out.alpha_composite(logo, (pad, pad))
    return out


def logo_gate(logo: Image.Image, size):
    """The shadowed logo centred on a transparent image of size, 8-bit indexed with its transparent pixels one
    palette entry (tRNS)."""
    mark = shadowed(logo, GATE_LOGO_WIDTH, 3)
    out = Image.new('RGBA', size, (0, 0, 0, 0))
    out.alpha_composite(mark, ((size[0] - mark.width) // 2, (size[1] - mark.height) // 2))
    # Edges blended against black (the shadow's colour), then cut at half coverage: an indexed image has one
    # transparent entry.
    rgb = Image.new('RGB', size, (0, 0, 0))
    rgb.paste(out, mask=out.getchannel('A'))
    image = rgb.quantize(PALETTE_COLORS - 1, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    clear = PALETTE_COLORS - 1
    palette = image.getpalette()[:3 * clear] + [0, 0, 0]
    alpha = out.getchannel('A').point(lambda a: 255 if a >= 128 else 0)
    image.paste(clear, mask=alpha.point(lambda a: 255 - a))
    image.putpalette(palette)
    image.info['transparency'] = clear
    return image


def splash(hero: Image.Image, logo: Image.Image, size):
    """pic0: the hero art, a dark fade along the bottom, the logo over it."""
    image = hero_crop(hero, size).convert('RGBA')
    band = Image.new('RGBA', (size[0], PIC_FADE), (8, 6, 14, 255))
    band.putalpha(Image.linear_gradient('L').resize((size[0], PIC_FADE)).point(lambda v: int(v * 0.85)))
    image.alpha_composite(band, (0, size[1] - PIC_FADE))
    mark = shadowed(logo, PIC_LOGO_WIDTH, 5)
    image.alpha_composite(mark, ((size[0] - mark.width) // 2, size[1] - mark.height - 10))
    return image


def indexed(image: Image.Image):
    return image.convert('RGB').quantize(PALETTE_COLORS, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)


# The bubble from the game: the high score screen's endoskeleton, the last of the four images of its rise out of the
# fog (HighscoreTable.draw, Assets.IMAGE_ARRAY_HIGH_T800), its head and shoulders scaled to the bubble. Read at
# build time from the user's game, like every other image of the port; nothing of it is in the repository.
BUBBLE_IMAGE = 'IMAGE_HIGH_T800_ARRAY_3'
BUBBLE_SQUARE = 96  # the square around the head and shoulders, from the image's top, centred on the figure
BUBBLE_BACKGROUND = (18, 14, 22)
ASSEMBLY = WORK / 'recover' / 'assemblies' / 'Assembly-CSharp.dll'
PACKS = WORK / f'rip-{TARGET}' / 'ExportedProject' / 'Assets' / 'Resources' / 'texturepack'


def game_bubble():
    """(icon0 image, the input files it came from), or None (with a warning) when the recover and rip stages
    haven't run or the image isn't where it was."""
    if not (ASSEMBLY.exists() and PACKS.is_dir()):
        return None
    atlas = Path(__file__).resolve().parent / 'atlas'
    sys.path.insert(0, str(atlas))
    try:
        import frametable
        import numpy
        import repack

        table = frametable.read(str(ASSEMBLY), log=lambda *a: None)
        row = next(r for r in table['images'] if r['name'] == BUBBLE_IMAGE)
        frame = row['frames'][0]
        pack = next(p for p in sorted(PACKS.glob('TexturePack*.bytes')) if int(''.join(filter(str.isdigit, p.stem))) == frame['pack'])
        pixels = numpy.asarray(Image.open(io.BytesIO(pack.read_bytes())).convert('RGBA'))
        sprite = Image.new('RGBA', (row['sfw'], row['sfh']), (0, 0, 0, 0))
        top = row['sfh'] - frame['oy'] - frame['h']
        sprite.paste(Image.fromarray(numpy.ascontiguousarray(repack.png_rect(pixels, frame))), (frame['ox'], top))
    except Exception as error:  # noqa: BLE001 - a missing image falls back to the store art, never fails the package
        log(f'warning: the bubble falls back to the store art: {BUBBLE_IMAGE} not read ({error!r})')
        return None
    finally:
        sys.path.remove(str(atlas))
    columns = numpy.nonzero(numpy.asarray(sprite)[:, :, 3].any(axis=0))[0]
    if columns.size == 0:  # nothing drawn: columns.min() would stop the package
        log(f'warning: the bubble falls back to the store art: {BUBBLE_IMAGE} has no opaque pixel')
        return None
    centre = (columns.min() + columns.max()) // 2
    half = BUBBLE_SQUARE // 2
    square = sprite.crop((centre - half, 0, centre + half, BUBBLE_SQUARE))
    # Twice the size with nearest neighbour, then down to 128: the pixel art stays sharper than a direct resample.
    square = square.resize((BUBBLE_SQUARE * 2,) * 2, Image.NEAREST).resize((128, 128), Image.LANCZOS)
    bubble = Image.new('RGBA', (128, 128), BUBBLE_BACKGROUND + (255,))
    bubble.alpha_composite(square)
    return bubble.convert('RGB'), [ASSEMBLY, pack]


def compose(sources, bubble=None):
    library = Image.open(sources['library.jpg']).convert('RGB')
    hero = Image.open(sources['hero_2x.jpg']).convert('RGB')
    logo = Image.open(sources['logo_2x.png']).convert('RGBA')
    return {
        'icon0.png': indexed(bubble if bubble is not None else cover(library, (128, 128), ICON_CROP)),
        'bg0.png': indexed(hero_crop(hero, (840, 500))),
        'gate.png': logo_gate(logo, (280, 158)),
        'pic0.png': indexed(splash(hero, logo, (960, 544))),
    }


def stamp(sources, inputs=()):
    digest = hashlib.sha256(Path(__file__).read_bytes())
    for path in [*sources.values(), *inputs]:
        digest.update(path.read_bytes())
    return digest.hexdigest()


def generate(force=False):
    """The artwork folder, made if missing or out of date."""
    sources = fetch()
    game = game_bubble()
    bubble, inputs = game if game else (None, [])
    marker = OUT / 'stamp.txt'
    current = stamp(sources, inputs)
    if not force and marker.exists() and marker.read_text() == current and all((OUT / f).exists() for f in FILES):
        progress.cached('LiveArea art', f'{OUT} is from the same images')
        return OUT
    for name, image in compose(sources, bubble).items():
        image.save(OUT / name, optimize=True)
    marker.write_text(current)
    log(f'LiveArea art in {OUT}')
    return OUT


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--force', action='store_true')
    generate(parser.parse_args().force)
