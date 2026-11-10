from PIL import Image
import numpy as np, os
from scipy import ndimage

base = r"C:\Users\foxif\Projects\jarvis\resources\anim"
Y0, Y1, X0, X1 = 380, 560, 0, 160

for scene in os.listdir(base):
    d = os.path.join(base, scene)
    if not os.path.isdir(d):
        continue
    count = 0
    for f in sorted(x for x in os.listdir(d) if x.endswith(".png")):
        p = os.path.join(d, f)
        im = Image.open(p).convert("RGBA")
        a = np.asarray(im).copy()
        reg = a[Y0:Y1, X0:X1]
        r = reg[:, :, 0].astype(int)
        g = reg[:, :, 1].astype(int)
        b = reg[:, :, 2].astype(int)
        alpha = reg[:, :, 3]
        purple = (b > 180) & (r > 140) & (g < 200) & (b > g + 15) & (alpha > 10)
        white = (r > 230) & (g > 230) & (b > 230) & (alpha > 10)
        mask = purple | white
        if mask.sum() == 0:
            continue
        mask = ndimage.binary_dilation(mask, iterations=8)
        reg[:, :, 3][mask] = 0
        a[Y0:Y1, X0:X1] = reg
        Image.fromarray(a, "RGBA").save(p)
        count += 1
    print(scene, "color-cleaned", count)
