"""PS Vita LiveArea artwork, composed from Terminator 2D: NO FATE's Steam store art and, for the bubble, the
game's own high score sprite. From butcher-vita's (MIT, same author).

    uv run tools/downgrade/livearea.py [--force]

The images come from Steam's public CDN (no Steam client, no game launch), downloaded once into
work/livearea/store/ and read from there afterwards. Writes to work/livearea/:

  icon0.png     128x128   bubble: the endoskeleton of the high score screen, read from your copy of the game
                          (the recovered Assembly-CSharp's frame table, the ripped texture packs); before the
                          recover and rip stages have run, Sarah Connor from the vertical key art
  bg0.png       840x500   LiveArea background: the library hero art's middle
  gate.png      280x158   LiveArea start gate: the store header with its logo
  pic0.png      960x544   shown while the app starts: the store header with its logo

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
from PIL import Image

OUT = WORK / 'livearea'
STORE = OUT / 'store'
FILES = ['icon0.png', 'bg0.png', 'gate.png', 'pic0.png']
PALETTE_COLORS = 256  # 8-bit indexed

CDN = f'https://cdn.akamai.steamstatic.com/steam/apps/{STEAM_APP_ID}/'
# Cached name: (URL, SHA-256 of the file the crops below were made for). A changed file is only
# reported: Valve can replace store art at the same URL.
SOURCES = {
    # The store page's header, 460x215, with the logo (the game has no 616x353 capsule).
    'capsule.jpg': (CDN + 'header.jpg', '2791430d33c088ca3bcaff95102e3aaf2d28745c2077fee33fc2f14e23c5155e'),
    # Vertical key art, 600x900: the endoskeleton's head behind Sarah Connor, the logo below.
    'library.jpg': (CDN + 'library_600x900_2x.jpg', '2c248cddef37deda386b948eff4a2c1c14559e8e337f35e2a2f38a694efb63c2'),
    # Library hero art, 1920x620, without the logo: the T-1000, Sarah Connor and the endoskeleton in the middle.
    'hero.jpg': (CDN + 'library_hero.jpg', 'f0b9c38e4860880f8d12b2b03be2bcf4b4357ed135e8ddc5efcbb6109d74cb48'),
}
ICON_CROP = (195, 225, 435, 465)  # Sarah Connor, sunglasses and rifle, in library.jpg: reads at bubble size
HERO_CROP = (560, 0, 1602, 620)  # the hero art's middle, at bg0's 840:500
CAPSULE_CROP = (0, 0, 379, 215)  # the header's left part, with the whole logo, at pic0's 960:544


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
    capsule = Image.open(sources['capsule.jpg']).convert('RGB')
    library = Image.open(sources['library.jpg']).convert('RGB')
    hero = Image.open(sources['hero.jpg']).convert('RGB')
    return {
        'icon0.png': indexed(bubble if bubble is not None else cover(library, (128, 128), ICON_CROP)),
        'bg0.png': indexed(cover(hero, (840, 500), HERO_CROP)),
        'gate.png': indexed(cover(capsule, (280, 158), CAPSULE_CROP)),
        'pic0.png': indexed(cover(capsule, (960, 544), CAPSULE_CROP)),
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
