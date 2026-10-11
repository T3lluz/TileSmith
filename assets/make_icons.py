"""Renders TileSmith's app icon and tray icons.

    python assets/make_icons.py

The mark is a 2x2 grid: two tiles, a Gemini-style spark (the AI) and an accent dot.
Writes into src/TileSmith/Assets and a preview sheet into assets/icon-preview.png. Requires Pillow.
"""
import math
import os

from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "TileSmith", "Assets")
os.makedirs(OUT, exist_ok=True)

BLUE_DARK_BG = (76, 194, 255, 255)    # Windows accent on dark surfaces
BLUE_LIGHT_BG = (0, 103, 192, 255)    # Windows accent on light surfaces
GREEN_DARK_BG = (108, 203, 95, 255)   # "update ready"
GREEN_LIGHT_BG = (15, 123, 15, 255)


SPARK_PULL = 0.47  # control-point distance along each axis (fraction of r): higher = plumper body


def spark_points(cx, cy, r, steps=720, pull=None):
    """Four-point spark with smooth curved sides and long tapered tips (like the Gemini spark).

    Each quarter is a cubic Bezier from one tip to the next; both control points lie on the axes, so every
    tip is a clean point and the sides bow in smoothly.
    """
    pull = SPARK_PULL if pull is None else pull
    pts = []
    tips = [(r, 0), (0, r), (-r, 0), (0, -r)]
    per = steps // 4
    for q in range(4):
        x0, y0 = tips[q]
        x3, y3 = tips[(q + 1) % 4]
        c1 = (x0 * pull, y0 * pull)
        c2 = (x3 * pull, y3 * pull)
        for i in range(per):
            t = i / per
            mt = 1 - t
            x = mt ** 3 * x0 + 3 * mt * mt * t * c1[0] + 3 * mt * t * t * c2[0] + t ** 3 * x3
            y = mt ** 3 * y0 + 3 * mt * mt * t * c1[1] + 3 * mt * t * t * c2[1] + t ** 3 * y3
            pts.append((cx + x, cy + y))
    return pts


def spark_mask(size, cx, cy, r, rounding=0.06):
    """Spark as a mask with slightly rounded tips (blur + threshold rounds convex points only)."""
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).polygon(spark_points(cx, cy, r), fill=255)
    if rounding > 0:
        m = m.filter(ImageFilter.GaussianBlur(r * rounding)).point(lambda v: 255 if v > 100 else 0)
    return m


def mark(size, fg, accent, tile_alpha=(255, 255)):
    """The 2x2 mark on a transparent canvas: tiles TL + BL, spark TR, dot BR. All cells identical."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = size * 0.07
    gap = size * 0.09
    t = (size - 2 * pad - gap) / 2
    a, b = pad, pad + t + gap
    radius = t * 0.26
    tl = fg[:3] + (tile_alpha[0],)
    bl = fg[:3] + (tile_alpha[1],)
    d.rounded_rectangle((a, a, a + t, a + t), radius=radius, fill=tl)
    d.rounded_rectangle((a, b, a + t, b + t), radius=radius, fill=bl)
    # spark: tip-to-tip a hair larger than a tile so its optical size matches the solid shapes
    r = t * 0.70
    img.paste(Image.new("RGBA", (size, size), fg), (0, 0), spark_mask(size, b + t / 2, a + t / 2, r))
    d.ellipse((b, b, b + t, b + t), fill=accent)
    return img


def render(fn, size, ss=4):
    """Supersampled render for crisp small sizes."""
    return fn(size * ss).resize((size, size), Image.LANCZOS)


def app_icon(px):
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    m = px / 1024
    shadow = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((84 * m, 108 * m, 940 * m, 964 * m), radius=200 * m, fill=(0, 0, 0, 110))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(26 * m)))

    # dark, slightly cool body with a soft top sheen (Windows 11 "Mica" feel)
    body = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    mask = Image.new("L", (px, px), 0)
    ImageDraw.Draw(mask).rounded_rectangle((64 * m, 64 * m, 960 * m, 960 * m), radius=200 * m, fill=255)
    grad = Image.new("RGBA", (px, px))
    gd = ImageDraw.Draw(grad)
    top, bottom = (52, 56, 72), (22, 23, 30)
    for y in range(px):
        tt = y / (px - 1)
        gd.line([(0, y), (px, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * tt) for i in range(3)) + (255,))
    body.paste(grad, (0, 0), mask)
    rim = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle((64 * m, 64 * m, 960 * m, 960 * m), radius=200 * m, outline=(255, 255, 255, 38), width=max(1, int(6 * m)))
    body.alpha_composite(rim)
    img.alpha_composite(body)

    inner = int(600 * m)
    glyph = mark(inner, (255, 255, 255, 255), BLUE_DARK_BG, tile_alpha=(255, 235))
    img.alpha_composite(glyph, (int((px - inner) / 2), int((px - inner) / 2)))
    return img


def save_ico(frames, path):
    frames = sorted(frames, key=lambda f: f.size[0])
    frames[-1].save(path, format="ICO", sizes=[f.size for f in frames], append_images=frames[:-1])


if __name__ == "__main__":
    app_sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    save_ico([render(app_icon, s) for s in app_sizes], os.path.join(OUT, "TileSmith.ico"))
    render(app_icon, 256).save(os.path.join(OUT, "AppIcon.png"))
    render(app_icon, 64).save(os.path.join(OUT, "AppIcon64.png"))

    tray_sizes = [16, 20, 24, 32, 40, 48, 64]
    white, black = (255, 255, 255, 255), (28, 28, 28, 255)
    variants = {
        "tray-dark.ico": (white, BLUE_DARK_BG),
        "tray-light.ico": (black, BLUE_LIGHT_BG),
        "tray-dark-badge.ico": (white, GREEN_DARK_BG),
        "tray-light-badge.ico": (black, GREEN_LIGHT_BG),
    }
    for name, (fg, accent) in variants.items():
        save_ico([render(lambda n: mark(n, fg, accent), s) for s in tray_sizes], os.path.join(OUT, name))

    # preview sheet
    sheet = Image.new("RGBA", (980, 300), (32, 32, 32, 255))
    sheet.alpha_composite(render(app_icon, 256), (20, 22))
    for i, s in enumerate([16, 24, 32, 48]):
        sheet.alpha_composite(render(app_icon, s), (300 + i * 64, 40))
        sheet.alpha_composite(render(lambda n: mark(n, white, BLUE_DARK_BG), s), (300 + i * 64, 120))
        sheet.alpha_composite(render(lambda n: mark(n, white, GREEN_DARK_BG), s), (300 + i * 64, 200))
    light = Image.new("RGBA", (420, 300), (243, 243, 243, 255))
    for i, s in enumerate([16, 24, 32, 48]):
        light.alpha_composite(render(app_icon, s), (20 + i * 64, 40))
        light.alpha_composite(render(lambda n: mark(n, black, BLUE_LIGHT_BG), s), (20 + i * 64, 120))
        light.alpha_composite(render(lambda n: mark(n, black, GREEN_LIGHT_BG), s), (20 + i * 64, 200))
    big = render(lambda n: mark(n, white, BLUE_DARK_BG), 120)
    light.alpha_composite(Image.new("RGBA", (140, 140), (32, 32, 32, 255)), (275, 80))
    light.alpha_composite(big, (285, 90))
    sheet.alpha_composite(light, (560, 0))
    sheet.save(os.path.join(ROOT, "assets", "icon-preview.png"))
    print("icons written to", OUT)
