from PIL import Image
import numpy as np, os

base = r"C:\Users\foxif\Projects\jarvis\resources\anim"
Y0, Y1, X0, X1 = 600, 840, 0, 260

for scene in os.listdir(base):
    d = os.path.join(base, scene)
    if not os.path.isdir(d):
        continue
    cleaned = 0
    for f in sorted(x for x in os.listdir(d) if x.endswith(".png")):
        p = os.path.join(d, f)
        im = Image.open(p).convert("RGBA")
        a = np.asarray(im).copy()
        reg = a[Y0:Y1, X0:X1]
        r = reg[:, :, 0].astype(int)
        g = reg[:, :, 1].astype(int)
        b = reg[:, :, 2].astype(int)
        alpha = reg[:, :, 3]
        pink = (b > 170) & (r > 140) & (g < 200) & (b > g + 10) & (alpha > 20)
        white = (r > 240) & (g > 240) & (b > 240) & (alpha > 200)
        mask = pink | white
        if mask.sum() < 40:
            continue
        ys, xs = np.where(mask)
        x0, x1 = max(0, xs.min() - 6), min(X1 - X0, xs.max() + 6)
        y0, y1 = max(0, ys.min() - 6), min(Y1 - Y0, ys.max() + 6)
        reg[y0:y1, x0:x1, 3] = 0
        a[Y0:Y1, X0:X1] = reg
        Image.fromarray(a, "RGBA").save(p)
        cleaned += 1
    print(scene, "cleaned", cleaned)
