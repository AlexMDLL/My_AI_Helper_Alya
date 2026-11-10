import subprocess, os, json, shutil
import numpy as np
from PIL import Image

FF = r"C:\Users\foxif\AppData\Local\CapCut\Apps\9.7.1.4203\ffmpeg.exe"
SRC = r"C:\Users\foxif\Videos\NVIDIA\Desktop"
OUT = r"C:\Users\foxif\Projects\jarvis\resources\anim"
WORK = r"C:\Users\foxif\Projects\jarvis\docs\vidwork3"
os.makedirs(WORK, exist_ok=True)

videos = {
    "Подмигивание.mp4": "VideoWink",
    "Отрицание.mp4": "VideoDeny",
    "Удивление.mp4": "VideoSurprise",
    "Липсинк.mp4": "VideoTalk",
    "Смущение или радость.mp4": "VideoShyJoy",
    "Free_Standing.mp4": "VideoStand",
}

ROI = (600, 20, 700, 1040)  # x, y, w, h

def run(args):
    r = subprocess.run(args, capture_output=True)
    if r.returncode != 0:
        print("ffmpeg error", r.stderr.decode(errors="ignore")[-300:])

def motion_window(path):
    raw = os.path.join(WORK, "proxy.raw")
    run([FF, "-hide_banner", "-loglevel", "error", "-i", path,
         "-vf", f"crop={ROI[2]}:{ROI[3]}:{ROI[0]}:{ROI[1]},scale=96:143,format=gray",
         "-r", "5", "-f", "rawvideo", raw, "-y"])
    data = np.fromfile(raw, dtype=np.uint8)
    w, h = 96, 143
    n = len(data) // (w * h)
    frames = data[:n * w * h].reshape(n, h, w).astype(np.int16)
    diff = np.abs(np.diff(frames, axis=0)).mean(axis=(1, 2))
    # smooth
    k = 3
    sm = np.convolve(diff, np.ones(k) / k, mode="same")
    peak = int(np.argmax(sm))
    center = peak / 5.0
    start = max(0.0, center - 1.0)
    duration = 2.4
    print(os.path.basename(path), "peak", round(center, 2), "window", round(start, 2), duration, "max", round(float(sm[peak]), 2))
    return start, duration

def key_frame(rgb, bbox, target_h=540):
    a = rgb.astype(np.float32)
    r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
    dist = np.sqrt((r - 0.0) ** 2 + (g - 214.0) ** 2 + (b - 0.0) ** 2)
    alpha = np.clip((dist - 30.0) / 70.0, 0.0, 1.0)
    g2 = np.minimum(g, np.maximum(r, b) * 1.05)
    rgba = np.stack([r, g2, b, alpha * 255.0], axis=2).astype(np.uint8)
    im = Image.fromarray(rgba, "RGBA").crop(bbox)
    scale = target_h / im.height
    im = im.resize((max(1, int(im.width * scale)), target_h), Image.LANCZOS)
    canvas = Image.new("RGBA", (252, 560), (0, 0, 0, 0))
    x = (252 - im.width) // 2
    y = 560 - im.height - 8
    canvas.alpha_composite(im, (x, y))
    # стираем случайный мелкий персонаж в левом нижнем углу
    px = canvas.load()
    for yy in range(430, 560):
        for xx in range(0, 55):
            px[xx, yy] = (0, 0, 0, 0)
    return canvas

for filename, scene in videos.items():
    path = os.path.join(SRC, filename)
    start, duration = motion_window(path)
    tmp = os.path.join(WORK, scene)
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp, exist_ok=True)
    run([FF, "-hide_banner", "-loglevel", "error", "-ss", str(start), "-t", str(duration), "-i", path,
         "-vf", f"crop={ROI[2]}:{ROI[3]}:{ROI[0]}:{ROI[1]}", "-r", "60",
         os.path.join(tmp, "%04d.png"), "-y"])
    files = sorted(f for f in os.listdir(tmp) if f.endswith(".png"))
    if not files:
        print("no frames", filename)
        continue
    bbox = (165, 10, 555, 1005)
    dst = os.path.join(OUT, scene)
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    os.makedirs(dst, exist_ok=True)
    for i, name in enumerate(files):
        rgb = np.asarray(Image.open(os.path.join(tmp, name)).convert("RGB"))
        key_frame(rgb, bbox).save(os.path.join(dst, f"{i:04d}.png"))
    with open(os.path.join(dst, "meta.json"), "w", encoding="ascii") as f:
        json.dump({"fps": 60}, f)
    print(scene, "frames", len(files), "bbox", bbox)
