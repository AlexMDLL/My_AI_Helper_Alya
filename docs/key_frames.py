from PIL import Image
import numpy as np
import os, json, shutil

base = r"C:\Users\foxif\Projects\jarvis\docs\vidwork"
out_base = r"C:\Users\foxif\Projects\jarvis\resources\anim"
durations = [4.6, 4.5, 4.7, 1.2, 2.0, 0.8, 0.8, 0.6, 0.8, 1.0]

for i, dur in enumerate(durations, start=1):
    src = os.path.join(base, f"raw_{i}")
    dst = os.path.join(out_base, f"Video_{i}")
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    os.makedirs(dst, exist_ok=True)
    files = sorted(f for f in os.listdir(src) if f.endswith(".png"))
    for idx, name in enumerate(files):
        im = Image.open(os.path.join(src, name)).convert("RGB")
        a = np.asarray(im).astype(np.float32)
        r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
        dist = np.sqrt((r - 0.0) ** 2 + (g - 214.0) ** 2 + (b - 0.0) ** 2)
        alpha = np.clip((dist - 30.0) / 70.0, 0.0, 1.0)
        g2 = np.minimum(g, np.maximum(r, b) * 1.05)
        out = np.stack([r, g2, b, alpha * 255.0], axis=2).astype(np.uint8)
        Image.fromarray(out, "RGBA").save(os.path.join(dst, f"{idx:04d}.png"))
    fps = round(len(files) / dur, 1)
    with open(os.path.join(dst, "meta.json"), "w", encoding="ascii") as f:
        json.dump({"fps": fps}, f)
    print(f"Video_{i}: {len(files)} frames, fps {fps}")
