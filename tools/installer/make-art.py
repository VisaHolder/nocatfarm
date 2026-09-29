"""Draws the setup wizard's pictures in nocat.farm's look: near-black, the n. logo, the wordmark with "farm" in purple.

    python tools/installer/make-art.py

Writes tools/installer/art/side-*.png (the Welcome and Finished pages' left panel, one per screen scale) and
tools/installer/art/small-*.png (the corner logo on the other pages). Run again after changing the logo.
"""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
ART = os.path.join(HERE, "art")
LOGO = os.path.join(REPO, "assets", "logo.png")

BG = (10, 10, 10)
TEXT = (232, 232, 232)
MUTED = (138, 138, 138)
PURPLE = (139, 92, 246)

FONT_BOLD = r"C:\Windows\Fonts\consolab.ttf"
FONT = r"C:\Windows\Fonts\consola.ttf"

# The left panel's size at each Windows scale (100% .. 200%), from Inno Setup's documentation.
SIDE_SIZES = [(202, 386), (269, 515), (336, 643), (430, 824)]
SMALL_SIZES = [58, 73, 87, 116]


def spaced(draw, xy, text, font, fill, spacing):
    x, y = xy
    for ch in text:
        draw.text((x, y), ch, font=font, fill=fill)
        x += draw.textlength(ch, font=font) + spacing
    return x


def width_spaced(draw, text, font, spacing):
    return sum(draw.textlength(ch, font=font) for ch in text) + spacing * (len(text) - 1)


def side(w, h):
    img = Image.new("RGB", (w, h), BG)

    # A soft purple glow rising from the bottom - drawn big and blurred so it has no edge.
    glow = Image.new("RGB", (w, h), BG)
    g = ImageDraw.Draw(glow)
    g.ellipse((-w * 0.6, h * 0.72, w * 1.6, h * 1.45), fill=(46, 26, 92))
    glow = glow.filter(ImageFilter.GaussianBlur(w * 0.22))
    img = Image.blend(img, glow, 0.9)

    d = ImageDraw.Draw(img)
    logo = Image.open(LOGO).convert("RGBA")
    size = int(w * 0.46)
    logo = logo.resize((size, size), Image.LANCZOS)
    img.paste(logo, ((w - size) // 2, int(h * 0.22)), logo)

    # "nocat.farm", letter-spaced like the dashboard's wordmark.
    fsize = max(12, int(w * 0.085))
    font = ImageFont.truetype(FONT_BOLD, fsize)
    spacing = fsize * 0.28
    total = width_spaced(d, "nocat.farm", font, spacing)
    x = (w - total) / 2
    y = int(h * 0.22) + size + int(h * 0.05)
    x = spaced(d, (x, y), "nocat.", font, TEXT, spacing)
    spaced(d, (x, y), "farm", font, PURPLE, spacing)

    # A thin purple rule and the maker, near the bottom.
    rule_w = int(w * 0.18)
    ry = int(h * 0.86)
    d.line(((w - rule_w) / 2, ry, (w + rule_w) / 2, ry), fill=PURPLE, width=max(1, w // 200))
    small = ImageFont.truetype(FONT, max(9, int(w * 0.05)))
    by = "by reap."
    d.text(((w - d.textlength(by, font=small)) / 2, ry + int(h * 0.02)), by, font=small, fill=MUTED)
    return img


def small(n):
    logo = Image.open(LOGO).convert("RGBA")
    return logo.resize((n, n), Image.LANCZOS)


def main():
    os.makedirs(ART, exist_ok=True)
    for w, h in SIDE_SIZES:
        side(w, h).save(os.path.join(ART, f"side-{w}x{h}.png"), optimize=True)
    for n in SMALL_SIZES:
        small(n).save(os.path.join(ART, f"small-{n}.png"), optimize=True)
    print("wrote", len(SIDE_SIZES) + len(SMALL_SIZES), "images to", ART)


if __name__ == "__main__":
    main()
