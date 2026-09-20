from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
PNG_PATH = ASSETS / "codex-widget.png"
ICO_PATH = ASSETS / "codex-widget.ico"


def build_icon(size: int = 1024) -> Image.Image:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # Transparent corners keep the taskbar icon free of a dark outer border.
    draw.rounded_rectangle((48, 48, 976, 976), radius=240, fill="#B7E46C")

    # A broad four-point sparkle remains distinct at 16x16 pixels.
    sparkle = [
        (512, 236),
        (570, 454),
        (788, 512),
        (570, 570),
        (512, 788),
        (454, 570),
        (236, 512),
        (454, 454),
    ]
    draw.polygon(sparkle, fill="#172015")
    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    icon = build_icon()
    icon.save(PNG_PATH, format="PNG", optimize=True)
    icon.save(
        ICO_PATH,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
