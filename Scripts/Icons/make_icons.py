"""
Generates every FluxStrap icon and logo from the Fluxer brand art in ./source.

    python Scripts/Icons/make_icons.py

Needs Pillow (pip install pillow). Run it from anywhere; paths are relative to the repo.

The FluxStrap mark is Fishstrap's tilted rounded tile, recoloured in Fluxer red,
with Fluxer's chunky white outlined "F" on top.
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).resolve().parent / "source"
APP = ROOT / "Bloxstrap"
RESOURCES = APP / "Resources"
IMAGES = ROOT / "Images"

SIZE = 1024  # master canvas

# Fluxer palette
RED_LIGHT = (255, 92, 72)
RED = (232, 24, 40)
RED_DARK = (150, 0, 22)
OUTLINE = (92, 0, 14)
WHITE = (255, 255, 255)

ICO_SIZES = [16, 24, 32, 48, 64, 72, 80, 96, 128, 256]


def rounded_mask(size, radius, scale=4):
    """Anti-aliased rounded rectangle mask."""
    big = Image.new("L", (size * scale, size * scale), 0)
    ImageDraw.Draw(big).rounded_rectangle((0, 0, size * scale - 1, size * scale - 1), radius * scale, fill=255)
    return big.resize((size, size), Image.LANCZOS)


def gradient_tile(size):
    """Red diagonal gradient with a warm glow in the top left, like Fishstrap's tile lighting."""
    tile = Image.new("RGB", (size, size))
    px = tile.load()

    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            if t < 0.5:
                k = t / 0.5
                c = tuple(int(RED_LIGHT[i] + (RED[i] - RED_LIGHT[i]) * k) for i in range(3))
            else:
                k = (t - 0.5) / 0.5
                c = tuple(int(RED[i] + (RED_DARK[i] - RED[i]) * k) for i in range(3))
            px[x, y] = c

    glow = Image.new("L", (size, size), 0)
    ImageDraw.Draw(glow).ellipse((-size * 0.25, -size * 0.3, size * 0.6, size * 0.5), fill=110)
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.12))
    tile = Image.composite(Image.new("RGB", (size, size), (255, 170, 120)), tile, glow)

    return tile


def f_mask(height, slant=0.10):
    """
    A chunky, slightly italic "F" in the style of the Fluxer wordmark, drawn on a 100 unit grid,
    then rounded off by blurring and re-thresholding.
    """
    unit = height / 100
    width = int(height * 0.95)
    scale = 4

    w, h = width * scale, int(height * scale)
    img = Image.new("L", (w, h), 0)
    draw = ImageDraw.Draw(img)

    def pt(x, y):
        # shear to the right going up, for a bit of motion
        return ((x + (100 - y) * slant) * unit * scale, y * unit * scale)

    def rect(x0, y0, x1, y1):
        draw.polygon([pt(x0, y0), pt(x1, y0), pt(x1, y1), pt(x0, y1)], fill=255)

    rect(0, 0, 36, 100)    # stem
    rect(0, 0, 80, 29)     # top bar
    rect(0, 42, 68, 68)    # middle bar

    img = img.filter(ImageFilter.GaussianBlur(5 * scale))
    img = img.point(lambda v: 255 if v > 128 else 0)
    img = img.filter(ImageFilter.GaussianBlur(1.2 * scale))

    return img.resize((width, int(height)), Image.LANCZOS)


def outlined_glyph(mask, outline_px, colour=WHITE, outline_colour=OUTLINE, shadow=True):
    """White glyph with a dark red outline and a soft drop shadow, padded to fit."""
    pad = outline_px * 3
    w, h = mask.size
    canvas = Image.new("L", (w + pad * 2, h + pad * 2), 0)
    canvas.paste(mask, (pad, pad))

    outline = canvas.filter(ImageFilter.MaxFilter(outline_px * 2 + 1)).filter(ImageFilter.GaussianBlur(outline_px * 0.15))

    out = Image.new("RGBA", canvas.size, (0, 0, 0, 0))

    if shadow:
        sh = outline.filter(ImageFilter.GaussianBlur(outline_px * 0.5))
        sh = ImageChops.offset(sh, int(outline_px * 0.35), int(outline_px * 0.55))
        out.paste(Image.new("RGBA", canvas.size, (60, 0, 8, 90)), (0, 0), sh)

    out.paste(Image.new("RGBA", canvas.size, outline_colour + (255,)), (0, 0), outline)
    out.paste(Image.new("RGBA", canvas.size, colour + (255,)), (0, 0), canvas)

    return out


def make_mark(size=SIZE):
    """The FluxStrap app icon: tilted red tile with the outlined F."""
    tile_size = int(size * 0.80)
    tile = gradient_tile(tile_size).convert("RGBA")
    tile.putalpha(rounded_mask(tile_size, int(tile_size * 0.17)))

    # subtle inner edge so the tile doesn't melt into red backgrounds
    edge = rounded_mask(tile_size, int(tile_size * 0.17))
    inner = edge.filter(ImageFilter.MinFilter(9))
    rim = ImageChops.subtract(edge, inner)
    tile.paste(Image.new("RGBA", tile.size, (255, 255, 255, 30)), (0, 0), rim)

    # tilt it the same way as the fishstrap tile
    tile = tile.rotate(-15, resample=Image.BICUBIC, expand=True)

    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.alpha_composite(tile, ((size - tile.width) // 2, (size - tile.height) // 2))

    glyph = outlined_glyph(f_mask(int(size * 0.50)), outline_px=int(size * 0.03))
    canvas.alpha_composite(glyph, ((size - glyph.width) // 2 + int(size * 0.01), (size - glyph.height) // 2))

    return canvas


def make_small_mark(size=SIZE):
    """
    Simplified mark for 16-32px: an upright tile that fills the square and a bigger F with a solid outline.
    The tilt and the soft shadow just turn to mush at those sizes.
    """
    tile = gradient_tile(size).convert("RGBA")
    tile.putalpha(rounded_mask(size, int(size * 0.22)))

    glyph = outlined_glyph(f_mask(int(size * 0.62), slant=0.06), outline_px=int(size * 0.045), shadow=False)
    tile.alpha_composite(glyph, ((size - glyph.width) // 2 + int(size * 0.01), (size - glyph.height) // 2))

    return tile


def _bmp_frame(image):
    """32bpp DIB icon frame (BITMAPINFOHEADER, bottom-up BGRA pixels, empty AND mask)."""
    import struct

    w, h = image.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)

    rgba = image.convert("RGBA")
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = rgba.getpixel((x, y))
            row += bytes((b, g, r, a))
        rows.append(bytes(row))

    mask_stride = ((w + 31) // 32) * 4
    return header + b"".join(rows) + bytes(mask_stride * h)


def save_ico(image, path, sizes=ICO_SIZES, small_image=None):
    """
    Writes the icon like the original fishstrap one: largest frame first, classic bitmap frames,
    PNG only for 256px. WPF shows the first frame, so a small first frame comes out blurry in the app.
    """
    import io
    import struct

    entries = []
    for s in sorted(sizes, reverse=True):
        source = small_image if small_image is not None and s <= 32 else image
        frame = source.resize((s, s), Image.LANCZOS)

        if s <= 32:
            # keep tiny sizes crisp
            frame = frame.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=0))

        if s >= 256:
            buffer = io.BytesIO()
            frame.save(buffer, format="PNG")
            data = buffer.getvalue()
        else:
            data = _bmp_frame(frame)

        entries.append((s, data))

    offset = 6 + 16 * len(entries)
    directory = b""
    blobs = b""
    for s, data in entries:
        dim = 0 if s >= 256 else s
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)

    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(entries)) + directory + blobs)


def white_silhouette(image):
    """Turns a logo into a plain white shape, as the 2025-style bootstrapper expects."""
    alpha = image.getchannel("A")
    out = Image.new("RGBA", image.size, WHITE + (0,))
    out.putalpha(alpha)
    return out


def white_fill(image):
    """Keeps only the white letter fill of the Fluxer wordmark, dropping its red outline."""
    rgba = image.convert("RGBA")
    r, g, b, a = rgba.split()
    lightness = ImageChops.darker(ImageChops.darker(r, g), b)
    mask = ImageChops.multiply(lightness.point(lambda v: 255 if v > 170 else 0), a)
    out = Image.new("RGBA", image.size, WHITE + (0,))
    out.putalpha(mask.filter(ImageFilter.GaussianBlur(0.6)))
    return out.crop(mask.getbbox())


def fit(image, box_w, box_h, align="left"):
    scale = min(box_w / image.width, box_h / image.height)
    resized = image.resize((max(1, int(image.width * scale)), max(1, int(image.height * scale))), Image.LANCZOS)
    canvas = Image.new("RGBA", (box_w, box_h), (0, 0, 0, 0))
    x = 0 if align == "left" else (box_w - resized.width) // 2
    canvas.alpha_composite(resized, (x, (box_h - resized.height) // 2))
    return canvas


def rubik(size, weight):
    font = ImageFont.truetype(str(RESOURCES / "Fonts" / "Rubik-VariableFont_wght.ttf"), size)
    font.set_variation_by_axes([weight])
    return font


def make_banner(mark, text_colour):
    """Icon + "FluxStrap" wordmark, the same layout as the Fishstrap README banner (768x213)."""
    w, h = 768, 213
    scale = 3
    banner = Image.new("RGBA", (w * scale, h * scale), (0, 0, 0, 0))

    icon = mark.resize((h * scale, h * scale), Image.LANCZOS)
    banner.alpha_composite(icon, (0, 0))

    draw = ImageDraw.Draw(banner)
    font = rubik(100 * scale, 500)
    text = "FluxStrap"
    bbox = draw.textbbox((0, 0), text, font=font)
    x = int(h * scale * 1.04)
    y = (h * scale - (bbox[3] - bbox[1])) // 2 - bbox[1]
    draw.text((x, y), text, font=font, fill=text_colour)

    return banner.resize((w, h), Image.LANCZOS)


def make_studio_wordmark(logo_white, width, height):
    """White FLUXER wordmark followed by "Studio", sized like the original Studio.png."""
    scale = 4
    W, H = width * scale, height * scale
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))

    logo_h = int(H * 0.62)
    logo = logo_white.resize((int(logo_white.width * logo_h / logo_white.height), logo_h), Image.LANCZOS)
    canvas.alpha_composite(logo, (0, (H - logo_h) // 2))

    draw = ImageDraw.Draw(canvas)
    font = rubik(int(H * 0.6), 500)
    bbox = draw.textbbox((0, 0), "Studio", font=font)
    x = logo.width + int(H * 0.18)
    y = (H - (bbox[3] - bbox[1])) // 2 - bbox[1]
    draw.text((x, y), "Studio", font=font, fill=WHITE)

    return canvas.resize((width, height), Image.LANCZOS)


def main():
    mark = make_mark()
    small_mark = make_small_mark()

    # app + bootstrapper icons
    save_ico(mark, APP / "Bloxstrap.ico", small_image=small_mark)
    save_ico(mark, RESOURCES / "IconBloxstrap.ico", small_image=small_mark)
    save_ico(mark, RESOURCES / "IconFishstrap.ico", small_image=small_mark)
    mark.resize((919, 919), Image.LANCZOS).save(APP / "Bloxstrap.png")

    # the official Fluxer icon, offered as a bootstrapper icon choice
    fluxer_icon = Image.open(SOURCE / "FluxerIcon.ico")
    fluxer_icon.size = (256, 256)
    fluxer_icon.load()
    save_ico(fluxer_icon.convert("RGBA"), RESOURCES / "IconFluxer.ico", [16, 24, 32, 48, 64, 128, 256])

    # readme art
    IMAGES.mkdir(exist_ok=True)
    mark.resize((919, 919), Image.LANCZOS).save(IMAGES / "FluxStrap.png")
    make_banner(mark, (0, 0, 0)).save(IMAGES / "FluxStrap-Light.png")
    make_banner(mark, WHITE).save(IMAGES / "FluxStrap-Dark.png")

    # 2025-style bootstrapper (white art on a dark window)
    twenty_five = RESOURCES / "BootstrapperStyles" / "TwentyFiveDialog"
    f_white = outlined_glyph(f_mask(200), outline_px=0, shadow=False)
    f_white = white_silhouette(f_white.crop(f_white.getchannel("A").getbbox()))
    fit(f_white, 254, 254, "center").save(twenty_five / "PlayerLogo.png")
    fit(f_white, 254, 254, "center").save(twenty_five / "StudioLogo.png")

    logo_white = white_fill(Image.open(SOURCE / "FluxerLogo.png"))
    fit(logo_white, 103, 32).save(twenty_five / "Roblox.png")
    make_studio_wordmark(logo_white, 169, 32).save(twenty_five / "Studio.png")

    # 2023-style (byfron) bootstrapper logo tiles
    byfron = RESOURCES / "BootstrapperStyles" / "ByfronDialog"
    for name, bg in (("ByfronLogoDark.jpg", (0, 0, 0)), ("ByfronLogoLight.jpg", (243, 244, 246))):
        tile = Image.new("RGBA", (114, 108), bg + (255,))
        icon = mark.resize((96, 96), Image.LANCZOS)
        tile.alpha_composite(icon, ((114 - 96) // 2, (108 - 96) // 2))
        tile.convert("RGB").save(byfron / name, quality=95)

    print("Icons written")


if __name__ == "__main__":
    main()
