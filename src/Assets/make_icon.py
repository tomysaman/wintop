# Generates wintop.ico (16-256 px). Run: python make_icon.py
from PIL import Image, ImageDraw

BG = (22, 24, 29, 255)
BORDER = (61, 123, 70, 255)          # theme cpu_box
STOPS = [(80, 240, 149), (242, 226, 102), (250, 30, 30)]   # theme cpu_start/mid/end
HEIGHTS = [0.42, 0.68, 0.52, 0.88, 0.60]

def grad(t):
    t = max(0.0, min(1.0, t))
    a, b, u = (STOPS[0], STOPS[1], t * 2) if t < 0.5 else (STOPS[1], STOPS[2], (t - 0.5) * 2)
    return tuple(int(a[i] + (b[i] - a[i]) * u) for i in range(3)) + (255,)

def detailed(size):
    S = 1024                                   # draw big, downsample for anti-aliasing
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    m, r, bw = 24, 190, 44
    d.rounded_rectangle([m, m, S - m, S - m], radius=r, fill=BG, outline=BORDER, width=bw)
    # btop-style title notch on the top border
    d.rounded_rectangle([200, m - 6, 470, m + bw + 6], radius=26, fill=BG)
    d.rounded_rectangle([230, m + 2, 440, m + bw - 2], radius=18, fill=(238, 238, 238, 255))
    # meter bars made of stacked segments, colored by height like btop meters
    left, right, bottom, top = 170, S - 170, S - 160, 230
    n = len(HEIGHTS); gap = 34
    w = (right - left - gap * (n - 1)) / n
    seg, sgap = 46, 16
    for i, h in enumerate(HEIGHTS):
        x0 = left + i * (w + gap)
        y = bottom
        limit = bottom - h * (bottom - top)
        while y - seg >= limit - 1:
            t = (bottom - (y - seg / 2)) / (bottom - top)
            d.rounded_rectangle([x0, y - seg, x0 + w, y], radius=8, fill=grad(t))
            y -= seg + sgap
    return im.resize((size, size), Image.LANCZOS)

def simple(size):
    # tiny sizes: solid bars, no segments or notch, pixel aligned
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=max(2, size // 6), fill=BG, outline=BORDER, width=1)
    hs = [0.45, 0.85, 0.6] if size <= 16 else [0.45, 0.75, 0.55, 0.9]
    inner = size - 6
    bw = {16: 2, 20: 2, 24: 3}.get(size, 4)
    gap = max(1, (inner - bw * len(hs)) // (len(hs) - 1))
    x = 3 + (inner - (bw * len(hs) + gap * (len(hs) - 1))) // 2
    bottom = size - 4
    for h in hs:
        top = bottom - max(2, round(h * (size - 7)))
        for y in range(top, bottom + 1):
            t = (bottom - y) / (size - 7)
            d.line([x, y, x + bw - 1, y], fill=grad(t))
        x += bw + gap
    return im

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [simple(s) if s <= 32 else detailed(s) for s in sizes]
imgs[-1].save("wintop.ico", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
imgs[-1].save("wintop_256.png")
# preview sheet
sheet = Image.new("RGBA", (sum(sizes) + 10 * len(sizes) + 10, 276), (40, 40, 40, 255))
x = 10
for s, im in zip(sizes, imgs):
    sheet.paste(im, (x, 266 - s), im); x += s + 10
sheet.save("preview.png")
print("ok")
