"""Import textures the way Unity 2018.2's TextureImporter does for the PS Vita build.

    stream = Stream('sharedassets1.assets.resS', bytearray())
    data = texture(asset, file_name, stream)     # Texture2D release dict; image bytes appended to stream
    data = sprite(asset, placement, file_name, file_id)  # a Sprite: a single sprite's (21300000) or a sheet's

What the reference build shows (tools/builder/check_imported.py verifies it):
- Format: the importer's PSP2 override if it is on, else the default platform's setting
  (automatic: DXT5/DXT1 when compressed, RGBA32/RGB24 when not, by whether the image has
  alpha). The PS Vita compresses only power-of-two textures: any other size falls back to
  RGBA32 (DXT5) or RGB24 (DXT1). One mip level (no texture here enables mipmaps).
- Pixels are stored bottom row first, linear (not swizzled). With "alpha is transparency" the
  RGB of fully transparent texels is filled from the nearest visible texel (dilate()), so
  filtering at sprite edges does not bleed black.
- Readable textures keep their pixels in the object ("image data"); every other one streams
  them from <file>.resS, appended in path ID order, each at a multiple of 4 bytes (a 1x1 RGB24
  texture is followed by 1 byte of padding; the .resS holds nothing else).
- m_LightmapFormat is 6 ("always padded") for sprites, 0 otherwise; m_ColorSpace is sRGBTexture.
"""

import hashlib
import math
import struct
from functools import cache
from pathlib import Path

import numpy as np
import unityyaml
from paths import PROJECT, WORK
from PIL import Image

RGB24, RGBA32, DXT1, DXT5 = 3, 4, 10, 12
CUBEMAP_FILE_ID = 8900000
COMPRESSED = {DXT1: RGB24, DXT5: RGBA32}  # and what each falls back to when the PS Vita cannot use it
BUILD_TARGET = 'PSP2'
SPRITE = 8  # textureType
ALWAYS_PADDED = 6
CACHE = WORK / 'textures'
CACHE_VERSION = 2  # bump when the pixels change for the same input
FAR = 1 << 30
# Sprite pivots by the importer's alignment (9 is the custom spritePivot).
ALIGNMENTS = [(0.5, 0.5), (0, 1), (0.5, 1), (1, 1), (0, 0.5), (1, 0.5), (0, 0), (0.5, 0), (1, 0)]
SPRITE_FILE_ID = 21300000
TEXTURE_FILE_ID = 2800000


class Stream:
    """A .resS file being written (data: the bytearray build.py keeps in build.streams[name]).
    Textures append their image data in path ID order."""

    def __init__(self, name, data):
        self.name = name
        self.data = data

    def add(self, data):
        self.data += bytes(-len(self.data) % 4)
        offset = len(self.data)
        self.data += data
        return {'offset': offset, 'size': len(data), 'path': self.name}


def importer(asset):
    return meta(asset.meta_path)['TextureImporter']


@cache
def meta(path):
    # unityyaml.meta cannot open the str path AssetDB stores
    return unityyaml.meta(Path(path))


def platform(settings, target):
    for p in settings.get('platformSettings') or []:
        if p.get('buildTarget') == target:
            return p
    return None


def has_alpha(rgba, settings):
    return settings.get('alphaUsage', '1') != '0' and bool((rgba[..., 3] < 255).any())


def is_pot(n):
    return n & (n - 1) == 0


def texture_format(settings, width, height, alpha):
    override = platform(settings, BUILD_TARGET)
    if override and override.get('overridden') == '1' and int(override.get('textureFormat', -1)) >= 0:
        fmt = int(override['textureFormat'])
    else:
        default = platform(settings, 'DefaultTexturePlatform') or {}
        requested = int(default.get('textureFormat', -1))
        if requested >= 0:
            fmt = requested
        elif default.get('textureCompression', '1') != '0':
            fmt = DXT5 if alpha else DXT1
        else:
            fmt = RGBA32 if alpha else RGB24
    if fmt in COMPRESSED and not (is_pot(width) and is_pot(height)):
        fmt = COMPRESSED[fmt]
    if fmt not in (RGB24, RGBA32, DXT1, DXT5):
        raise NotImplementedError(f'texture format {fmt}')
    return fmt


def dilate(rgba):
    """Fill the RGB of fully transparent texels as Unity's "alpha is transparency" does: each
    takes the colour of the visible texel nearest to it in Manhattan distance; on a tie the one
    in the lowest row wins (rows are bottom-up), then the leftmost. Matches all 205 RGBA32
    textures of the reference.

    Computed exactly in two separable passes: the nearest visible texel within each row, then a
    1D distance transform down and up each column that keeps the lower row on ties."""
    visible = rgba[..., 3] != 0
    if visible.all() or not visible.any():
        return rgba
    h, w = visible.shape
    cols = np.arange(w)
    left = np.maximum.accumulate(np.where(visible, cols, -1), axis=1)
    right = np.minimum.accumulate(np.where(visible, cols, FAR)[:, ::-1], axis=1)[:, ::-1]
    to_left = np.where(left >= 0, cols - left, FAR)
    to_right = np.where(right < FAR, right - cols, FAR)
    use_left = to_left <= to_right
    distance = np.where(use_left, to_left, to_right)
    source_x = np.where(use_left, left, right)
    rows = np.broadcast_to(np.arange(h)[:, None], (h, w))

    def sweep(order, keep_tie):
        d, sy, sx = distance.copy(), rows.copy(), source_x.copy()
        for prev, y in order:
            carried = d[prev] + 1
            take = carried <= d[y] if keep_tie else carried < d[y]
            d[y] = np.where(take, carried, d[y])
            sy[y] = np.where(take, sy[prev], sy[y])
            sx[y] = np.where(take, sx[prev], sx[y])
        return d, sy, sx

    # carried up from lower rows (they win ties), and down from higher rows (they lose them)
    up_d, up_y, up_x = sweep([(y - 1, y) for y in range(1, h)], True)
    down_d, down_y, down_x = sweep([(y + 1, y) for y in range(h - 2, -1, -1)], False)
    use_up = up_d <= down_d
    source_y = np.where(use_up, up_y, down_y)
    source_x = np.where(use_up, up_x, down_x)
    out = rgba.copy()
    hidden = ~visible
    out[hidden, :3] = rgba[source_y[hidden], source_x[hidden], :3]
    return out


def encoder():
    """quicktex (rgbcx, Unity-like quality) when installed, else etcpak (UnityPy's dependency)."""
    try:
        import quicktex
        from quicktex.s3tc import bc1, bc3
    except ImportError:
        import etcpak

        def encode_etcpak(rgba, fmt):
            h, w = rgba.shape[:2]
            return (etcpak.compress_bc3 if fmt == DXT5 else etcpak.compress_bc1)(rgba.tobytes(), w, h)

        return 'etcpak', encode_etcpak

    # quality levels: BC3 at 5 already beats Unity's DXT5; BC1 is cheap enough for the best
    encoders = {DXT1: bc1.BC1Encoder(18, bc1.BC1Encoder.ColorMode.FourColor), DXT5: bc3.BC3Encoder(5)}

    def encode(rgba, fmt):
        h, w = rgba.shape[:2]
        return encoders[fmt].encode(quicktex.RawTexture.frombytes(rgba.tobytes(), w, h)).tobytes()

    return 'quicktex bc1 18 bc3 5', encode


ENCODER_NAME, ENCODE = encoder()


def load_rgba(path):
    with Image.open(path) as img:
        return np.ascontiguousarray(np.asarray(img.convert('RGBA'))[::-1])


def pixels(path, settings, fmt, rgba):
    """The image data Unity stores for one texture (cached under work/builder/textures)."""
    recipe = (CACHE_VERSION, fmt, settings.get('alphaIsTransparency'), ENCODER_NAME)
    key = hashlib.sha1(path.read_bytes() + repr(recipe).encode())
    cached = CACHE / f'{key.hexdigest()}.bin'
    if cached.exists():
        return cached.read_bytes()
    if settings.get('alphaIsTransparency') == '1':
        rgba = dilate(rgba)
    if fmt == RGBA32:
        data = rgba.tobytes()
    elif fmt == RGB24:
        data = np.ascontiguousarray(rgba[..., :3]).tobytes()
    else:
        data = ENCODE(rgba, fmt)
    CACHE.mkdir(parents=True, exist_ok=True)
    cached.write_bytes(data)
    return data


def texture_settings(settings):
    t = settings.get('textureSettings') or {}
    return {
        'm_FilterMode': int(t.get('filterMode', -1)),
        'm_Aniso': int(t.get('aniso', -1)),
        'm_MipBias': float(t.get('mipBias', -100)),
        'm_WrapU': int(t.get('wrapU', -1)),
        'm_WrapV': int(t.get('wrapV', -1)),
        'm_WrapW': int(t.get('wrapW', -1)),
    }


def texture(asset, file_name, stream, project=PROJECT):
    """The Texture2D of an imported image. Non-readable textures append their pixels to stream
    (a Stream for file_name's .resS)."""
    settings = importer(asset)
    path = project / asset.path
    rgba = load_rgba(path)
    height, width = rgba.shape[:2]
    if settings['mipmaps'].get('enableMipMap', '0') != '0':
        raise NotImplementedError(f'{asset.path}: mipmaps')
    size = int((platform(settings, BUILD_TARGET) or {}).get('maxTextureSize') or settings.get('maxTextureSize', 2048))
    if max(width, height) > size:
        raise NotImplementedError(f'{asset.path}: downscaling to {size}')
    scaled = settings.get('nPOTScale', '0') != '0' and settings.get('textureType') != str(SPRITE)
    if scaled and not (is_pot(width) and is_pot(height)):
        raise NotImplementedError(f'{asset.path}: non-power-of-two scaling')
    fmt = texture_format(settings, width, height, has_alpha(rgba, settings))
    data = pixels(path, settings, fmt, rgba)
    readable = settings.get('isReadable') == '1'
    if readable:
        image, streamed = data, {'offset': 0, 'size': 0, 'path': ''}
    else:
        image, streamed = b'', stream.add(data)
    return {
        'm_Name': asset.name_of(TEXTURE_FILE_ID),
        'm_ForcedFallbackFormat': RGBA32,
        'm_DownscaleFallback': False,
        'm_Width': width,
        'm_Height': height,
        'm_CompleteImageSize': len(data),
        'm_TextureFormat': fmt,
        'm_MipCount': 1,
        'm_IsReadable': readable,
        'm_StreamingMipmaps': settings.get('streamingMipmaps', '0') == '1',
        'm_StreamingMipmapsPriority': int(settings.get('streamingMipmapsPriority', 0)),
        'm_ImageCount': 1,
        'm_TextureDimension': 2,
        'm_TextureSettings': texture_settings(settings),
        'm_LightmapFormat': ALWAYS_PADDED if settings.get('textureType') == str(SPRITE) else 0,
        'm_ColorSpace': int(settings['mipmaps'].get('sRGBTexture', 1)),
        'image data': image,
        'm_StreamData': streamed,
    }


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def mip_chain(image, srgb):
    """An image (H x W x channels, uint8) and its mip levels down to 1x1: Unity's box filter (mipMapMode 0),
    each level from the one before, averaged in linear light for an sRGB texture, rounded."""
    levels = [image]
    while max(levels[-1].shape[:2]) > 1:
        a = levels[-1].astype(np.float64) / 255
        if srgb:
            a = srgb_to_linear(a)
        if a.shape[0] > 1:
            a = (a[0::2] + a[1::2]) / 2
        if a.shape[1] > 1:
            a = (a[:, 0::2] + a[:, 1::2]) / 2
        if srgb:
            a = linear_to_srgb(a)
        levels.append(np.round(a * 255).astype(np.uint8))
    return levels


def cubemap(asset, file_name, stream, project=PROJECT):
    """The Cubemap of an image imported with textureShape 2 (Butcher's one: GroundLoop, a 64x384 strip of
    six faces, RGB24 with mipmaps). Each face top row first, its mip levels after it, face after face."""
    settings = importer(asset)
    with Image.open(project / asset.path) as img:
        rgba = np.asarray(img.convert('RGBA'))
    height, width = rgba.shape[:2]
    if height == 6 * width:
        faces = [rgba[i * width : (i + 1) * width] for i in range(6)]
    elif width == 6 * height:
        faces = [rgba[:, i * height : (i + 1) * height] for i in range(6)]
    else:
        raise NotImplementedError(f'{asset.path}: a cubemap that is not a strip of six faces')
    size = faces[0].shape[0]
    fmt = texture_format(settings, size, size, has_alpha(rgba, settings))
    channels = {RGB24: 3, RGBA32: 4}.get(fmt)
    if channels is None:
        raise NotImplementedError(f'{asset.path}: a compressed cubemap')
    mipmaps = settings['mipmaps'].get('enableMipMap', '0') != '0'
    srgb = settings['mipmaps'].get('sRGBTexture', '1') != '0'
    face_data = []
    for face in faces:
        levels = mip_chain(np.ascontiguousarray(face[..., :channels]), srgb) if mipmaps else [face[..., :channels]]
        face_data.append(b''.join(np.ascontiguousarray(level).tobytes() for level in levels))
    data = b''.join(face_data)
    return {
        'm_Name': asset.name_of(CUBEMAP_FILE_ID),
        'm_ForcedFallbackFormat': RGBA32,
        'm_DownscaleFallback': False,
        'm_Width': size,
        'm_Height': size,
        'm_CompleteImageSize': len(face_data[0]),
        'm_TextureFormat': fmt,
        'm_MipCount': len(mip_chain(faces[0][:, :, :1], False)) if mipmaps else 1,
        'm_IsReadable': settings.get('isReadable') == '1',
        'm_StreamingMipmaps': settings.get('streamingMipmaps', '0') == '1',
        'm_StreamingMipmapsPriority': int(settings.get('streamingMipmapsPriority', 0)),
        'm_ImageCount': 6,
        'm_TextureDimension': 2,
        'm_TextureSettings': texture_settings(settings),
        'm_LightmapFormat': 0,
        'm_ColorSpace': int(srgb),
        'image data': b'',
        'm_StreamData': stream.add(data),
        'm_SourceTextures': [{'m_FileID': 0, 'm_PathID': 0}] * 6,
    }


def f32(x):
    return struct.unpack('<f', struct.pack('<f', x))[0]


def sprite_entry(asset, settings, file_id):
    """(name, rect, alignment, pivot, border, outline, physics shape) of one sprite: the importer's own for a
    texture imported as one sprite, the sprites[] entry of that name for a sprite sheet."""
    if settings.get('spriteMode') == '2':
        name = asset.name_of(file_id)
        entry = next((s for s in settings['spriteSheet'].get('sprites') or [] if str(s.get('name')) == name), None)
        if entry is None:
            raise NotImplementedError(f'{asset.path}: no sprites[] entry for {file_id} ({name})')
        r = entry['rect']
        rect = tuple(float(r[k]) for k in ('x', 'y', 'width', 'height'))
        return (
            name,
            rect,
            int(entry.get('alignment', 0)),
            entry.get('pivot') or {},
            entry.get('border') or {},
            entry.get('outline') or [],
            entry.get('physicsShape') or [],
        )
    with Image.open(PROJECT / asset.path) as img:
        width, height = img.size
    sheet = settings.get('spriteSheet') or {}
    return (
        asset.name_of(SPRITE_FILE_ID),
        (0.0, 0.0, float(width), float(height)),
        int(settings.get('alignment', 0)),
        settings.get('spritePivot') or {},
        settings.get('spriteBorder') or {},
        sheet.get('outline') or [],
        sheet.get('physicsShape') or [],
    )


def outline_triangles(outline, path):
    """Triangles (x0, y0, x1, y1, x2, y2), relative to the rect's centre, from the importer's outline
    polygons: a triangle as it is (the port's sprite rule writes each sprite's mesh as one polygon per
    triangle, sprite_rules.mesh_outline), a larger polygon fanned out when convex. Unity drops
    triangles with no area, and so does this."""
    out = []
    for polygon in outline:
        points = [(float(p['x']), float(p['y'])) for p in polygon]
        if len(points) < 3:
            continue
        if len(points) > 3 and not convex(points):
            raise NotImplementedError(f'{path}: a concave outline polygon of {len(points)} points')
        for i in range(1, len(points) - 1):
            (ax, ay), (bx, by), (cx, cy) = points[0], points[i], points[i + 1]
            if (bx - ax) * (cy - ay) - (cx - ax) * (by - ay) != 0:
                out.append((ax, ay, bx, by, cx, cy))
    return out


def convex(points):
    signs = set()
    for i in range(len(points)):
        (ax, ay), (bx, by), (cx, cy) = points[i - 2], points[i - 1], points[i]
        cross = (bx - ax) * (cy - by) - (by - ay) * (cx - bx)
        if cross:
            signs.add(cross > 0)
    return len(signs) < 2


def mesh_bounds(positions, width, height):
    """(left, bottom, right, top) in whole pixels of the rect: the mesh's bounds (points relative to the
    rect's centre), rounded out and kept within the rect. textureRect is these, so a tight mesh's is
    smaller than the rect where the sprite's edges are see-through (the game copies text glyphs by it)."""
    xs = [p[0] + width / 2 for p in positions]
    ys = [p[1] + height / 2 for p in positions]
    left, bottom = max(0, math.floor(min(xs) + 1e-3)), max(0, math.floor(min(ys) + 1e-3))
    right, top = min(width, math.ceil(max(xs) - 1e-3)), min(height, math.ceil(max(ys) - 1e-3))
    return left, bottom, right, top


def sprite(asset, placement, file_name, file_id=SPRITE_FILE_ID, project=PROJECT):
    """The Sprite of an imported texture: the one sprite a single-sprite texture makes (file_id
    21300000), or one of a sprite sheet's (its recycled file_id). A full-rect sprite is a quad over its
    rect; a tight one is the mesh of its outline, which the port gives every sprite (the game's own
    triangles, sprite_rules.py). As the reference build has them: positions in units from the pivot, the
    UVs left zero (the player derives them from textureRect), settingsRaw 64 for a tight mesh, a zero
    submesh AABB."""
    import convert

    settings = importer(asset)
    name, rect, alignment, custom_pivot, border, outline, shape = sprite_entry(asset, settings, file_id)
    x, y, width, height = rect
    pivot = tuple(f32(float(custom_pivot[k])) for k in 'xy') if alignment == 9 else ALIGNMENTS[alignment]
    ppu = f32(float(settings.get('spritePixelsToUnits', 100)))
    px, py = f32(pivot[0] * width), f32(pivot[1] * height)
    tight = settings.get('spriteMeshType', '1') != '0'
    if tight:
        triangles = outline_triangles(outline, asset.path)
        if not triangles:
            raise NotImplementedError(f'{asset.path} {name}: a tight sprite mesh without an outline (from the alpha)')
    else:
        hw, hh = width / 2, height / 2
        triangles = [(-hw, hh, hw, hh, -hw, -hh), (-hw, -hh, hw, hh, hw, -hh)]
    vertex_of, positions, indices = {}, [], []
    for t in triangles:
        for i in range(0, 6, 2):
            point = (t[i], t[i + 1])
            if point not in vertex_of:
                vertex_of[point] = len(positions)
                positions.append(point)
            indices.append(vertex_of[point])
    # Outline points are relative to the rect's centre; positions are in units from the pivot.
    cx, cy = width / 2 - px, height / 2 - py
    data = b''.join(struct.pack('<3f', f32((ox + cx) / ppu), f32((oy + cy) / ppu), 0) for ox, oy in positions)
    vertex_data = data + bytes(8 * len(positions))  # the UVs, left zero
    channels = [{'stream': 0, 'offset': 0, 'format': 0, 'dimension': 0} for _ in range(14)]
    channels[0]['dimension'] = 3
    channels[4].update(stream=1, dimension=2)
    file, texture_pid = placement.find(asset.guid, TEXTURE_FILE_ID)
    zero = {'x': 0.0, 'y': 0.0, 'z': 0.0}
    rect_dict = {'x': x, 'y': y, 'width': width, 'height': height}
    left, bottom, right, top = mesh_bounds(positions, width, height)
    texture_rect = {'x': x + left, 'y': y + bottom, 'width': float(right - left), 'height': float(top - bottom)}
    return {
        'm_Name': name,
        'm_Rect': dict(rect_dict),
        # The pivot less the centre, as Unity takes them: in float32, in the texture's pixels.
        'm_Offset': {'x': f32(f32(x + px) - f32(x + width / 2)), 'y': f32(f32(y + py) - f32(y + height / 2))},
        'm_Border': {k: float(border.get(k, 0)) for k in 'xyzw'},
        'm_PixelsToUnits': ppu,
        'm_Pivot': {'x': pivot[0], 'y': pivot[1]},
        'm_Extrude': int(settings.get('spriteExtrude', 1)),
        'm_IsPolygon': False,
        'm_RenderDataKey': (convert.guid_words(asset.guid), file_id),
        'm_AtlasTags': [],
        'm_SpriteAtlas': {'m_FileID': 0, 'm_PathID': 0},
        'm_RD': {
            'texture': {'m_FileID': 0, 'm_PathID': texture_pid, 'file': file},
            'alphaTexture': {'m_FileID': 0, 'm_PathID': 0},
            'm_SubMeshes': [
                {
                    'firstByte': 0,
                    'indexCount': len(indices),
                    'topology': 0,
                    'baseVertex': 0,
                    'firstVertex': 0,
                    'vertexCount': len(positions),
                    'localAABB': {'m_Center': dict(zero), 'm_Extent': dict(zero)},
                }
            ],
            'm_IndexBuffer': list(struct.pack(f'<{len(indices)}H', *indices)),
            'm_VertexData': {'m_VertexCount': len(positions), 'm_Channels': channels, 'm_DataSize': vertex_data},
            'm_Bindpose': [],
            'textureRect': texture_rect,
            'textureRectOffset': {'x': float(left), 'y': float(bottom)},
            'atlasRectOffset': {'x': -1.0, 'y': -1.0},
            'settingsRaw': 64 if tight else 0,
            'uvTransform': {'x': ppu, 'y': f32(x + px), 'z': ppu, 'w': f32(y + py)},
            'downscaleMultiplier': 1.0,
        },
        'm_PhysicsShape': physics_shape_of(asset, settings, shape, (width, height), pivot, ppu),
        'm_Bones': [],
    }


def physics_shape_of(asset, settings, shape, size, pivot, ppu):
    """The sprite's physics shape: its own outline from the importer (relative to the rect's centre), else
    the fallback Unity traces from the alpha when generateFallbackPhysicsShape is on (physics_shape, for a
    single sprite). The port turns both off (sprite_rules.py): Butcher has no 2D colliders."""
    if shape:
        w, h = size
        cx, cy = w / 2 - pivot[0] * w, h / 2 - pivot[1] * h
        return [
            [{'x': f32((float(p['x']) + cx) / ppu), 'y': f32((float(p['y']) + cy) / ppu)} for p in poly]
            for poly in shape
        ]
    if settings.get('spriteGenerateFallbackPhysicsShape', '1') == '0':
        return []
    if settings.get('spriteMode') == '2':
        raise NotImplementedError(f'{asset.path}: a fallback physics shape for a sprite sheet')
    return physics_shape(PROJECT / asset.path, settings, pivot, ppu)


def physics_shape(path, settings, pivot, ppu):
    """The fallback physics shape (generateFallbackPhysicsShape). Unity traces the alpha outline
    2 pixels out from the visible texels, within the sprite's rect, and simplifies it; this takes the
    convex hull of the same area, which has the same bounds (what the game reads, GuiUtils)."""
    if settings.get('spriteGenerateFallbackPhysicsShape', '1') == '0':
        return []
    rgba = load_rgba(path)
    h, w = rgba.shape[:2]
    ys, xs = np.nonzero(rgba[..., 3] > 0)
    if not len(ys):
        return []
    texels = zip(xs.tolist(), ys.tolist(), strict=True)
    corners = {(x + dx, y + dy) for x, y in texels for dx in (0, 1) for dy in (0, 1)}
    grown = {(min(max(x + dx, 0), w), min(max(y + dy, 0), h)) for x, y in convex_hull(sorted(corners))
             for dx in (-2, 2) for dy in (-2, 2)}  # fmt: skip
    hull = convex_hull(sorted(grown))
    px, py = pivot[0] * w, pivot[1] * h
    return [[{'x': f32((x - px) / ppu), 'y': f32((y - py) / ppu)} for x, y in hull]]


def convex_hull(points):
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in points:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(points):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]
