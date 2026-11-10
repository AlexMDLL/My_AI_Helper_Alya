"""Генерация иконки Джарвиса (светящийся орб) без внешних ассетов."""

from __future__ import annotations

import math

from PIL import Image, ImageDraw, ImageFilter

SIZE = 512


def make_orb(size: int = SIZE) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # внешнее свечение
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    center = size / 2
    for i in range(36):
        t = i / 36
        radius = size * 0.48 * (1 - t * 0.55)
        alpha = int(70 * (1 - t) ** 2)
        gd.ellipse(
            [center - radius, center - radius, center + radius, center + radius],
            fill=(56, 189, 248, alpha),
        )
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.02))
    img = Image.alpha_composite(img, glow)

    # сфера
    sphere = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(sphere)
    radius = size * 0.31
    steps = 96
    for i in range(steps):
        t = i / (steps - 1)
        r = radius * (1 - t)
        shade = t ** 0.75
        color = (
            int(210 * (1 - shade) + 6 * shade),
            int(245 * (1 - shade) + 40 * shade),
            int(255 * (1 - shade) + 90 * shade),
            255,
        )
        # блик смещает центр к верхнему левому краю
        ox = -radius * 0.16 * t
        oy = -radius * 0.2 * t
        draw.ellipse(
            [center - r + ox, center - r + oy, center + r + ox, center + r + oy],
            fill=color,
        )

    # тёмная кромка, чтобы сфера не сливалась с фоном
    rim = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    rd = ImageDraw.Draw(rim)
    rd.ellipse(
        [center - radius, center - radius, center + radius, center + radius],
        outline=(120, 225, 255, 140),
        width=max(2, size // 180),
    )
    sphere = Image.alpha_composite(sphere, rim)
    img = Image.alpha_composite(img, sphere)

    # орбитальное кольцо
    ring = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    rd = ImageDraw.Draw(ring)
    ring_r = size * 0.40
    box = [center - ring_r, center - ring_r, center + ring_r, center + ring_r]
    rd.arc(box, start=-40, end=140, fill=(125, 230, 255, 210), width=max(3, size // 110))
    rd.arc(box, start=160, end=320, fill=(56, 189, 248, 90), width=max(2, size // 170))
    ring = ring.filter(ImageFilter.GaussianBlur(size * 0.004))
    img = Image.alpha_composite(img, ring)

    # маленькая искра
    spark = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sd = ImageDraw.Draw(spark)
    sx, sy = center - radius * 0.45, center - radius * 0.55
    sd.ellipse([sx - size * 0.012, sy - size * 0.012, sx + size * 0.012, sy + size * 0.012], fill=(255, 255, 255, 235))
    spark = spark.filter(ImageFilter.GaussianBlur(size * 0.006))
    img = Image.alpha_composite(img, spark)

    return img


def main() -> None:
    orb = make_orb()
    out_dir = __import__("pathlib").Path(__file__).resolve().parent.parent / "resources"
    out_dir.mkdir(parents=True, exist_ok=True)
    orb.resize((256, 256), Image.LANCZOS).save(out_dir / "icon.png")
    orb.resize((32, 32), Image.LANCZOS).save(out_dir / "tray.png")
    orb.resize((256, 256), Image.LANCZOS).save(
        out_dir / "icon.ico",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print("saved", out_dir / "icon.png", "and tray.png", "and icon.ico")


if __name__ == "__main__":
    main()
