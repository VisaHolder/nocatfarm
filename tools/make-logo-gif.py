# The nocat.farm logo with the n filling up with purple liquid, as a looping GIF for the Discord card.
#   python tools/make-logo-gif.py assets/logo-liquid.gif
# Then copy it to src/NocatFarm/wwwroot/logo-liquid.gif (the settings preview) and to the site as /nocatfarm/logo.gif
# (what the Discord card shows).
import math, os, sys
import numpy as np
from PIL import Image, ImageFilter

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "logo.png")
OUT = sys.argv[1] if len(sys.argv) > 1 else "logo-liquid.gif"
SIZE = 256          # what's saved
FPS = 25

logo = np.asarray(Image.open(SRC).convert("RGBA")).astype(np.float32) / 255
H, W = logo.shape[:2]
lum = logo[..., :3].mean(axis=2)
white = (lum > 0.5) & (logo[..., 3] > 0.5)
xs = np.arange(W)[None, :].repeat(H, 0)
ys = np.arange(H)[:, None].repeat(W, 1)
n_mask = white & (xs < 680)
dot_mask = white & (xs >= 680)
# soft edges: the anti-aliased coverage of the white shapes
cover = np.clip((lum - 0.25) / (0.94 - 0.25), 0, 1) * logo[..., 3]
cover[(xs < 140) | (xs > 890) | (ys < 200) | (ys > 750)] = 0   # the icon's border and corners: flat background
n_cov = np.where(xs < 680, cover, 0)
dot_cov = np.where(xs >= 680, cover, 0)

rows = np.where(n_mask.any(axis=1))[0]
TOP, BOT = rows.min(), rows.max()          # the n's top and bottom

BG = np.array([20, 20, 20]) / 255          # the logo's own dark square, full bleed - Discord rounds the corners
WHITE = np.array([240, 240, 240]) / 255
DEEP = np.array([91, 33, 182]) / 255       # bottom of the liquid
MID = np.array([139, 92, 246]) / 255       # the dashboard's purple
LIGHT = np.array([196, 181, 253]) / 255    # the surface line

def ease(t):
    return t * t * (3 - 2 * t)

# a few bubbles: x, speed, size, phase
rng = np.random.default_rng(7)
BUBBLES = [(rng.uniform(190, 590), rng.uniform(160, 260), rng.uniform(7, 15), rng.uniform(0, 1)) for _ in range(9)]

RISE, HOLD, DRAIN, REST = 2.6, 1.4, 0.9, 0.5
TOTAL = RISE + HOLD + DRAIN + REST
frames = []
for i in range(int(TOTAL * FPS)):
    t = i / FPS
    if t < RISE:
        fill = ease(t / RISE)
    elif t < RISE + HOLD:
        fill = 1.0
    elif t < RISE + HOLD + DRAIN:
        fill = 1 - ease((t - RISE - HOLD) / DRAIN)
    else:
        fill = 0.0

    # the surface: a travelling wave that calms down as it gets full or empty
    level = BOT + 20 - fill * (BOT - TOP + 60)
    amp = 16 * math.sin(math.pi * min(1, max(0, fill))) + 3
    surface = level + amp * np.sin(xs / 95.0 + t * 7.0) + amp * 0.45 * np.sin(xs / 41.0 - t * 11.0)
    depth = ys - surface                                    # >0 below the surface
    liquid = np.clip(depth / 2.5 + 0.5, 0, 1)               # anti-aliased surface edge

    # colour down the liquid: a light rim, then purple getting deeper towards the bottom
    k = np.clip((ys - TOP) / (BOT - TOP), 0, 1)[..., None]
    col = MID * (1 - k) + DEEP * k
    rim = np.clip(1 - np.abs(depth - 4) / 7, 0, 1)[..., None]
    col = col * (1 - rim) + LIGHT * rim
    # a soft sheen down the left of each stem
    sheen = np.exp(-((xs - 215) / 22.0) ** 2) + np.exp(-((xs - 515) / 22.0) ** 2)
    col = col + (0.10 * sheen)[..., None] * (depth > 0)[..., None]

    # bubbles, only inside the liquid
    for bx, sp, r, ph in BUBBLES:
        by = BOT - ((t * sp + ph * 600) % 600)
        if by < surface.min() - 10:
            continue
        d = np.sqrt((xs - (bx + 6 * math.sin(t * 3 + ph * 9))) ** 2 + (ys - by) ** 2)
        ring = np.clip(1 - np.abs(d - r) / 2.2, 0, 1) * 0.7 + np.clip(1 - d / r, 0, 1) * 0.12
        col = col * (1 - ring[..., None] * 0.6) + LIGHT * (ring[..., None] * 0.6)

    col = np.clip(col, 0, 1)
    a_n = n_cov[..., None]
    # the dot turns purple while the n is full - a drop that spilled over
    dot_mix = 0.0   # the dot stays white
    dot_col = WHITE * (1 - dot_mix) + MID * dot_mix

    # what fills the n (white above the surface, liquid below), then the n's soft edge over the background once -
    # layering liquid over white at the edge left a thin white rim
    inside = WHITE * (1 - liquid[..., None]) + col * liquid[..., None]
    img = BG * (1 - a_n) + inside * a_n
    a_dot = dot_cov[..., None]
    img = img * (1 - a_dot) + dot_col * a_dot

    frame = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8), "RGB")
    frame = frame.crop((22, 22, W - 22, H - 22)).resize((SIZE, SIZE), Image.LANCZOS)
    frames.append(frame)

# one shared palette so the colours don't shimmer between frames
pick = [frames[j] for j in range(0, len(frames), 6)]
sheet = Image.new("RGB", (SIZE * len(pick), SIZE))
for j, f in enumerate(pick):
    sheet.paste(f, (j * SIZE, 0))
pal = sheet.quantize(colors=192, method=Image.Quantize.MEDIANCUT)
q = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
q[0].save(OUT, save_all=True, append_images=q[1:], duration=int(1000 / FPS), loop=0, optimize=True, disposal=1)
print(OUT, len(frames), "frames", os.path.getsize(OUT) // 1024, "KB")
