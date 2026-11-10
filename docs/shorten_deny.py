import subprocess, os, json, shutil
import numpy as np
from PIL import Image
from scipy import ndimage

FF = r"C:\Users\foxif\AppData\Local\CapCut\Apps\9.7.1.4203\ffmpeg.exe"
SRC = os.path.join(r"C:\Users\foxif\Videos\NVIDIA\Desktop", "Отрицание.mp4")
OUT = r"C:\Users\foxif\Projects\jarvis\resources\anim\VideoDeny"
WORK = r"C:\Users\foxif\Projects\jarvis\docs\vidworkDeny"
os.makedirs(WORK, exist_ok=True)

ROI = (705, 20, 450, 700)
W, H = 540, 840
BASE = 17.4
SPAN = 6.0

raw = os.path.join(WORK, "proxy.raw")
subprocess.run([FF, "-hide_banner", "-loglevel", "error", "-ss", str(BASE), "-t", str(SPAN), "-i", SRC,
                "-vf", f"crop={ROI[2]}:{ROI[3]}:{ROI[0]}:{ROI[1]},scale=96:143,format=gray",
                "-r", "10", "-f", "rawvideo", raw, "-y"], capture_output=True)
data = np.fromfile(raw, dtype=np.uint8)
w, h = 96, 143
n = len(data) // (w * h)
frames = data[:n * w * h].reshape(n, h, w).astype(np.int16)
diff = np.abs(np.diff(frames, axis=0)).mean(axis=(1, 2))
sm = np.convolve(diff, np.ones(3) / 3, mode="same")
peak = float(np.argmax(sm) / 10.0)
start = max(0.0, BASE + peak - 0.7)
print("peak in clip at", round(BASE + peak, 2), "start", round(start, 2))

tmp = os.path.join(WORK, "frames")
if os.path.isdir(tmp):
    shutil.rmtree(tmp)
os.makedirs(tmp)
subprocess.run([FF, "-hide_banner", "-loglevel", "error", "-ss", str(start), "-t", "1.7", "-i", SRC,
                "-vf", f"crop={ROI[2]}:{ROI[3]}:{ROI[0]}:{ROI[1]}", "-r", "60",
                os.path.join(tmp, "%04d.png"), "-y"], capture_output=True)

files = sorted(f for f in os.listdir(tmp) if f.endswith(".png"))
if os.path.isdir(OUT):
    shutil.rmtree(OUT)
os.makedirs(OUT)
for i, name in enumerate(files):
    rgb = np.asarray(Image.open(os.path.join(tmp, name)).convert("RGB")).astype(np.float32)
    r, g, b = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    dist = np.sqrt((r - 0.0) ** 2 + (g - 214.0) ** 2 + (b - 0.0) ** 2)
    alpha = np.clip((dist - 18.0) / 130.0, 0.0, 1.0)
    alpha = ndimage.gaussian_filter(alpha, 0.6)
    g2 = np.minimum(g, np.maximum(r, b) * 1.05)
    rgba = np.stack([r, g2, b, alpha * 255.0], axis=2).astype(np.uint8)
    im = Image.fromarray(rgba, "RGBA").resize((W, H), Image.LANCZOS)
    arr = np.asarray(im).copy()
    arr[0:6, :, 3] = 0
    Image.fromarray(arr, "RGBA").save(os.path.join(OUT, f"{i:04d}.webp"), "WEBP", quality=95, method=4)
with open(os.path.join(OUT, "meta.json"), "w", encoding="ascii") as f:
    json.dump({"fps": 60}, f)
print("VideoDeny frames", len(files))
