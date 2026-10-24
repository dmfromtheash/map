"""Sheet of stills for a quick look: python stills_sheet.py <stills_dir> <out.jpg> [names...]"""
import sys
from PIL import Image

src, out = sys.argv[1], sys.argv[2]
names = sys.argv[3:] or ["day", "evening", "night", "day_normals_off"]
ims = [Image.open(f"{src}/{n}.png").convert("RGB").resize((1280, 720), Image.LANCZOS) for n in names]
cols = 2
rows = (len(ims) + 1) // 2
sheet = Image.new("RGB", (1280 * cols, 720 * rows))
for i, im in enumerate(ims):
    sheet.paste(im, ((i % cols) * 1280, (i // cols) * 720))
sheet.thumbnail((2400, 2400))
sheet.save(out, quality=88)
