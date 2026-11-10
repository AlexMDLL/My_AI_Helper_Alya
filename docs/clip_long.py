import subprocess, os, json, shutil
import numpy as np
from PIL import Image

FF = r"C:\Users\foxif\AppData\Local\CapCut\Apps\9.7.1.4203\ffmpeg.exe"
SRC = r"C:\Users\foxif\Videos\NVIDIA\Desktop"
OUT = r"C:\Users\foxif\Projects\jarvis\resources\anim"
WORK = r"C:\Users\foxif\Projects\jarvis\docs\vidwork3"
os.makedirs(WORK, exist_ok=True)

# name: (scene, fps, duration, fixed_start or None)
CONFIG = {
    "Free_Standing.mp4": ("VideoStand", 30, 20.0, 25.0),
    "Подмигивание.mp4": ("VideoWink", 60, 6.0, None),
    "Отрицание.mp4": ("VideoDeny", 60, 6.0, None),
    "Удивление.mp4": ("VideoSurprise", 60, 6.0, None),
    "Липсинк.mp4": ("VideoTalk", 60, 8.0, None),
    "Смущение или радость.mp4": ("VideoShyJoy", 60, 8.0, None),
}

ROI = (600, 20, 700, 1040)
bbox = (165, 10, 555, 1005)

def run(args):
    r = subprocess.run(args, capture_output=True)
    if r.returncode != 0:
        print("ffmpeg error", r.stderr.decode(errors="ignore")[-300:])

def motion_peak(path):
    raw = os.path.join(WORK, "proxy_long.raw")
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

def key_frame(rgb):
    a = rgb.astype(np.float32)
    r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
    dist = np.sqrt((r - 0.0) ** 2 + (g - 214.0) ** 2 + (b - 0.0) ** 2)
    alpha = np.clip((dist - 30.0) / 70.0, 0.0, 1.0)
    g2 = np.minimum(g, np.maximum(r, b) * 1.05)
    rgba = np.stack([r, g2, b, alpha * 255.0], axis=2).astype(np.uint8)
    im = Image.fromarray(rgba, "RGBA").crop(bbox)
    scale = 540 / im.height
    im = im.resize((max(1, int(im.width * scale)), 540), Image.LANCZOS)
    canvas = Image.new("RGBA", (252, 560), (0, 0, 0, 0))
    canvas.alpha_composite(im, ((252 - im.width) // 2, 560 - im.height - 8))
    px = canvas.load()
    for yy in range(430, 560):
        for xx in range(0, 55):
            px[xx, yy] = (0, 0, 0, 0)
    return canvas

for filename, (scene, fps, duration, fixed_start) in CONFIG.items():
    path = os.path.join(SRC, filename)
    if fixed_start is None:
        peak = motion_peak(path)
        start = max(0.0, peak - 2.0)
    else:
        start = fixed_start
    tmp = os.path.join(WORK, "long_" + scene)
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
        key_frame(rgb).save(os.path.join(dst, f"{i:04d}.png"))
    with open(os.path.join(dst, "meta.json"), "w", encoding="ascii") as f:
        json.dump({"fps": fps}, f)
    print(scene, "frames", len(files), "fps", fps, "start", round(start, 1))
