# The gamer.farm logo: a white "g." on the dark rounded square, drawn once at 1024 and written out at every size the
# app and the site use.   python tools/make-logo.py
# Then run tools/make-logo-gif.py for the liquid version.
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
FONT = r"C:\Windows\Fonts\arialbd.ttf"
BG, BORDER, WHITE = (20, 20, 20, 255), (81, 81, 81, 255), (240, 240, 240, 255)
S = 4  # drawn 4x bigger, then scaled down, for smooth edges


def glyphs(d, scale, ox, oy):
    """The g and the dot, in the 1024 layout, scaled and moved."""
    f = ImageFont.truetype(FONT, int(780 * scale * S))
    # place the g so its x-height top sits at y=225 and its left edge at x=180 (of 1024)
    gx0, gy0, gx1, gy1 = d.textbbox((0, 0), "g", font=f)
    xh = d.textbbox((0, 0), "x", font=f)  # x-height box: top of the bowl
    x = (ox + 180 * scale) * S - gx0
    y = (oy + 225 * scale) * S - xh[1]
    d.text((x, y), "g", font=f, fill=WHITE)
    base = y + xh[3]                       # the baseline (bottom of the x)
    right = x + gx1                        # the g's right edge
    r = 71 * scale * S
    cx = right + 72 * scale * S + r
    d.ellipse((cx - r, base - 2 * r, cx + r, base), fill=WHITE)


def icon(size):
    n = 1024 * S
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((20 * S, 20 * S, n - 20 * S, n - 20 * S), radius=170 * S, fill=BORDER)
    d.rounded_rectangle((46 * S, 46 * S, n - 46 * S, n - 46 * S), radius=146 * S, fill=BG)
    glyphs(d, 1.0, 0, 0)
    return im.resize((size, size), Image.LANCZOS)


def maskable(size):
    n = 1024 * S
    im = Image.new("RGBA", (n, n), (10, 10, 10, 255))
    d = ImageDraw.Draw(im)
    k = 0.72
    glyphs(d, k, 512 * (1 - k), 512 * (1 - k))
    return im.resize((size, size), Image.LANCZOS)


def save(rel, im, **kw):
    p = os.path.join(ROOT, rel)
    im.save(p, **kw)
    print("wrote", rel, im.size)


master = icon(1024)
save("assets/logo.png", master)
for rel, n in [("assets/icon.png", 256), ("src/NocatFarm/wwwroot/logo.png", 256), ("src/NocatFarm/wwwroot/icon-512.png", 512),
               ("src/NocatFarm/wwwroot/icon-192.png", 192)]:
    save(rel, master.resize((n, n), Image.LANCZOS))
# iOS draws its own corners: a flat square, no see-through edge
apple = Image.new("RGBA", (180, 180), BG)
apple.alpha_composite(master.resize((180, 180), Image.LANCZOS))
save("src/NocatFarm/wwwroot/apple-touch-icon.png", apple.convert("RGB"))
save("src/NocatFarm/wwwroot/icon-maskable-512.png", maskable(512))
ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (256, 256)]
for rel in ["src/NocatFarm/nocatFarm.ico", "src/NocatFarm/wwwroot/favicon.ico"]:
    save(rel, master, format="ICO", sizes=ico_sizes)
