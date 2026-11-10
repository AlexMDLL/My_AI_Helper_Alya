from PIL import Image
import os

base = r"C:\Users\foxif\Projects\jarvis\resources\anim"
scenes = [f"Video_{i}" for i in range(1, 11)]
tw, th = 130, 290
img = Image.new("RGB", (tw * 5, th * 2), (18, 18, 18))
for i, name in enumerate(scenes):
    p = os.path.join(base, name, "0000.png")
    if not os.path.exists(p):
        continue
    im = Image.open(p).convert("RGBA")
    bg = Image.new("RGBA", im.size, (18, 18, 18, 255))
    bg.alpha_composite(im)
    im = bg.convert("RGB").resize((tw, th))
    img.paste(im, ((i % 5) * tw, (i // 5) * th))
img.save(r"C:\Users\foxif\Projects\jarvis\docs\vidwork\video_scenes.png")
print("saved")
