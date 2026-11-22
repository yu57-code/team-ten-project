import json
from pathlib import Path

from PIL import Image, ImageDraw


BASE_DIR = Path(__file__).resolve().parent.parent

JSON_PATH = BASE_DIR / "output" / "pdf_ocr_result.json"

IMAGE_DIR = BASE_DIR / "output" / "pdf_pages"

OUTPUT_DIR = BASE_DIR / "output" / "visualized"

OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


with open(JSON_PATH, "r", encoding="utf-8") as f:
    data = json.load(f)


for page_data in data["pages"]:

    page_number = page_data["page"]

    image_path = (
        IMAGE_DIR / f"page_{page_number:03d}.png"
    )

    if not image_path.exists():
        print(f"找不到第 {page_number} 頁圖片")
        continue

    with Image.open(image_path) as source:
        image = source.convert("RGB")

    draw = ImageDraw.Draw(image)

    for block in page_data["blocks"]:

        bbox = block.get("bbox")

        if not bbox or len(bbox) != 4:
            continue

        draw.rectangle(
            bbox,
            outline="red",
            width=2
        )

    output_path = (
        OUTPUT_DIR / f"page_{page_number:03d}_bbox.png"
    )

    image.save(output_path)

    print(f"已產生：{output_path}")

print("\n所有頁面座標視覺化完成！")