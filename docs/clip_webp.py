import subprocess, os, json, shutil
import numpy as np
from PIL import Image
from scipy import ndimage

FF = r"C:\Users\foxif\AppData\Local\CapCut\Apps\9.7.1.4203\ffmpeg.exe"
SRC = r"C:\Users\foxif\Videos\NVIDIA\Desktop"
OUT = r"C:\Users\foxif\Projects\jarvis\resources\anim"
WORK = r"C:\Users\foxif\Projects\jarvis\docs\vidworkW"
os.makedirs(WORK, exist_ok=True)

CONFIG = {
    "Free_Standing.mp4": ("VideoStand", 60, 15.0, 25.0),
    "Подмигивание.mp4": ("VideoWink", 60, 6.0, None),
    "Отрицание.mp4": ("VideoDeny", 60, 6.0, None),
    "Удивление.mp4": ("VideoSurprise", 60, 6.0, None),
    "Липсинк.mp4": ("VideoTalk", 60, 8.0, None),
    "Смущение или радость.mp4": ("VideoShyJoy", 60, 8.0, None),
}

ROI = (705, 20, 450, 700)
W, H = 540, 840

def run(args):
    r = subprocess.run(args, capture_output=True)
    if r.returncode != 0:
        print("ffmpeg error", r.stderr.decode(errors="ignore")[-300:])

def motion_peak(path):
    raw = os.path.join(WORK, "proxy.raw")
    run([FF, "-hide_banner", "-loglevel", "error", "-i", path,
         "-vf", f"crop={ROI[2]}:{ROI[3]}:{ROI[0]}:{ROI[1]},scale=96:143,format=gray",
         "-r", "5", "-f", "rawvideo", raw, "-y"])
    data = np.fromfile(raw, dtype=np.uint8)
    w, h = 96, 143
    n = len(data) // (w * h)
    frames = data[:n * w * h].reshape(n, h, w).astype(np.int16)
    diff = np.abs(np.diff(frames, axis=0)).mean(axis=(1, 2))
    sm = np.convolve(diff, np.ones(3) / 3, mode="same")
    return float(np.argmax(sm) / 5.0)

def make_frame(rgb):
    a = rgb.astype(np.float32)
    r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
    dist = np.sqrt((r - 0.0) ** 2 + (g - 214.0) ** 2 + (b - 0.0) ** 2)
    alpha = np.clip((dist - 18.0) / 130.0, 0.0, 1.0)
    alpha = ndimage.gaussian_filter(alpha, 0.6)
    g2 = np.minimum(g, np.maximum(r, b) * 1.05)
    rgba = np.stack([r, g2, b, alpha * 255.0], axis=2).astype(np.uint8)
    im = Image.fromarray(rgba, "RGBA").resize((W, H), Image.LANCZOS)
    arr = np.asarray(im).copy()
    arr[0:6, :, 3] = 0
    return Image.fromarray(arr, "RGBA")

for filename, (scene, fps, duration, fixed_start) in CONFIG.items():
    path = os.path.join(SRC, filename)
    start = fixed_start if fixed_start is not None else max(0.0, motion_peak(path) - 2.0)
    tmp = os.path.join(WORK, "raw_" + scene)
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp, exist_ok=True)
    run([FF, "-hide_banner", "-loglevel", "error", "-ss", str(start), "-t", str(duration), "-i", path,
         "-vf", f"crop={ROI[2]}:{ROI[3]}:{ROI[0]}:{ROI[1]}", "-r", str(fps),
         os.path.join(tmp, "%04d.png"), "-y"])
    files = sorted(f for f in os.listdir(tmp) if f.endswith(".png"))
    dst = os.path.join(OUT, scene)
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    os.makedirs(dst, exist_ok=True)
    for i, name in enumerate(files):
        rgb = np.asarray(Image.open(os.path.join(tmp, name)).convert("RGB"))
        im = make_frame(rgb)
        im.save(os.path.join(dst, f"{i:04d}.webp"), "WEBP", quality=95, method=4)
    with open(os.path.join(dst, "meta.json"), "w", encoding="ascii") as f:
        json.dump({"fps": fps}, f)
    print(scene, "frames", len(files), "fps", fps)
