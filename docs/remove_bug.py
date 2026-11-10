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
        crop = a[Y0:Y1, X0:X1, 3] > 10
        labels, n = ndimage.label(crop)
        if n == 0:
            continue
        # компонент, касающийся верхней границы кропа — это нога модели
        top = set(labels[0, :].tolist()) - {0}
        changed = False
        for idx in range(1, n + 1):
            if idx in top:
                continue
            comp = labels == idx
            area = int(comp.sum())
            if area < 5000:
                a[Y0:Y1, X0:X1, 3][comp] = 0
                changed = True
        if changed:
            Image.fromarray(a, "RGBA").save(p)
            count += 1
    print(scene, "cleaned", count)
