"""
Step 4～5：依遮蔽計畫在原始圖片上實際遮蔽個資，並重新執行 PaddleOCR（於 .venv 執行）

- 使用不透明黑色矩形覆蓋 OCR bbox（含少量外擴邊界）
- 以純像素重建圖片，不保留 EXIF / ICC / 文字區塊等中繼資料
- 保留原圖寬高，不覆蓋原始圖片
- 對遮蔽後圖片重新 OCR，結果先存放於 output/private/，待驗證後才產生最終 JSON
"""

import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageOps


PROJECT_ROOT = Path(__file__).resolve().parent.parent

PRIVATE_DIR = PROJECT_ROOT / "output" / "private"

BLACK = (0, 0, 0)


# =====================================================
# 1. 遮蔽範圍計算
# =====================================================

def padded_rect(bbox, image_size, unmasked_blocks=()):
    """
    OCR bbox 可能略小於實際字形，四周外擴後再裁切至圖片範圍內。
    外擴部分不可侵入相鄰的保留區塊（例如姓名下方的檢查日期），但一定完整覆蓋 bbox。
    """

    x1, y1, x2, y2 = bbox
    width, height = image_size

    pad = max(3, round((y2 - y1) * 0.15))

    rect = [
        max(0, x1 - pad),
        max(0, y1 - pad),
        min(width, x2 + pad),
        min(height, y2 + pad),
    ]

    for block in unmasked_blocks:

        bx1, by1, bx2, by2 = block["bbox"]

        x_overlap = bx1 < rect[2] and bx2 > rect[0]
        y_overlap = by1 < rect[3] and by2 > rect[1]

        if not (x_overlap and y_overlap):
            continue

        center_x = (bx1 + bx2) / 2
        center_y = (by1 + by2) / 2

        # 依相鄰區塊所在方向收回外擴
        if center_y >= y2:
            rect[3] = min(rect[3], max(y2, by1))
        elif center_y <= y1:
            rect[1] = max(rect[1], min(y1, by2))
        elif center_x >= x2:
            rect[2] = min(rect[2], max(x2, bx1))
        elif center_x <= x1:
            rect[0] = max(rect[0], min(x1, bx2))

    return rect


def overlap_ratio(rect, bbox):
    """
    rect 覆蓋 bbox 面積的比例。
    """

    left = max(rect[0], bbox[0])
    top = max(rect[1], bbox[1])
    right = min(rect[2], bbox[2])
    bottom = min(rect[3], bbox[3])

    if right <= left or bottom <= top:
        return 0.0

    area = (bbox[2] - bbox[0]) * (bbox[3] - bbox[1])

    return (right - left) * (bottom - top) / area if area else 0.0


# =====================================================
# 2. 圖片遮蔽
# =====================================================

def load_image(image_path):
    """
    依 EXIF 方向轉正（與 OpenCV 讀圖一致），再轉成 RGB。
    """

    with Image.open(image_path) as source:
        image = ImageOps.exif_transpose(source)
        return image.convert("RGB")


def redact_image(image_path, plan, output_path):

    if plan.get("status") != "READY":
        raise RuntimeError(
            "遮蔽計畫狀態不是 READY，已停止自動遮蔽，請人工確認"
        )

    image_path = Path(image_path)
    output_path = Path(output_path)

    if output_path.resolve() == image_path.resolve():
        raise ValueError("輸出路徑不可與原始圖片相同")

    image = load_image(image_path)
    size = image.size

    masks = []
    errors = []

    for mask in plan["masks"]:

        x1, y1, x2, y2 = mask["bbox"]

        if not (0 <= x1 < x2 <= size[0] and 0 <= y1 < y2 <= size[1]):
            errors.append(f"{mask['mask_id']} 的 bbox 超出圖片範圍")
            continue

        masks.append({
            "mask_id": mask["mask_id"],
            "block_id": mask["block_id"],
            "bbox": mask["bbox"],
            "rect": padded_rect(
                mask["bbox"], size, plan.get("unmasked_blocks", [])
            ),
            "entity_types": mask["entity_types"],
        })

    if errors:
        raise RuntimeError("；".join(errors))

    draw = ImageDraw.Draw(image)

    for mask in masks:
        draw.rectangle(
            [mask["rect"][0], mask["rect"][1], mask["rect"][2] - 1, mask["rect"][3] - 1],
            fill=BLACK,
        )

    # 以純像素重建，確保不帶任何原始中繼資料
    clean = Image.frombytes("RGB", size, image.tobytes())

    output_path.parent.mkdir(parents=True, exist_ok=True)

    try:
        clean.save(output_path, format="PNG")
    except OSError as error:
        # Windows 上圖片被相片檢視器開啟時無法覆寫
        raise RuntimeError(
            f"無法寫入 {output_path.name}，請先關閉正在開啟這張圖片的程式後重試"
        ) from error

    return masks, size


# =====================================================
# 3. 遮蔽結果驗證
# =====================================================

def verify_redacted_image(output_path, masks, original_size, unmasked_blocks):

    checks = []

    with Image.open(output_path) as saved:

        saved.load()

        checks.append({
            "name": "image_size_preserved",
            "passed": saved.size == original_size,
        })

        metadata_keys = sorted(
            key for key in saved.info
            if key not in {"dpi"}
        )

        checks.append({
            "name": "metadata_removed",
            "passed": not saved.getexif() and not metadata_keys,
            "detail": metadata_keys,
        })

        checks.append({
            "name": "opaque_rgb_image",
            "passed": saved.mode == "RGB",
        })

        rgb = saved.convert("RGB")

        not_black = []

        for mask in masks:

            region = rgb.crop(mask["rect"])

            if any(channel != (0, 0) for channel in region.getextrema()):
                not_black.append(mask["mask_id"])

            # 遮蔽範圍必須完整包含 OCR bbox
            if overlap_ratio(mask["rect"], mask["bbox"]) < 1.0:
                not_black.append(mask["mask_id"])

        checks.append({
            "name": "all_masks_fully_black_and_cover_bbox",
            "passed": not not_black,
            "detail": not_black,
        })

    overlaps = []

    for mask in masks:
        for block in unmasked_blocks:
            ratio = overlap_ratio(mask["rect"], block["bbox"])
            if ratio > 0.2:
                overlaps.append({
                    "mask_id": mask["mask_id"],
                    "block_id": block["block_id"],
                    "ratio": round(ratio, 3),
                })

    return checks, overlaps


# =====================================================
# 4. 重新 OCR
# =====================================================

def rerun_ocr(image_path):

    from ocr_engine import run_ocr

    return run_ocr(image_path)


# =====================================================
# 5. 主程式
# =====================================================

def main():

    parser = argparse.ArgumentParser(description="個資圖片遮蔽與重新 OCR")
    parser.add_argument(
        "--image",
        type=Path,
        default=PROJECT_ROOT / "test_images" / "patient_test.jpg",
    )
    parser.add_argument(
        "--plan",
        type=Path,
        default=PRIVATE_DIR / "patient_test_mask_plan.json",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=PROJECT_ROOT / "output" / "patient_test_deidentified.png",
    )
    parser.add_argument(
        "--reocr-json",
        type=Path,
        default=PRIVATE_DIR / "patient_test_reocr_raw.json",
    )
    parser.add_argument(
        "--report",
        type=Path,
        default=PRIVATE_DIR / "patient_test_redaction_report.json",
    )
    args = parser.parse_args()

    with open(args.plan, "r", encoding="utf-8") as file:
        plan = json.load(file)

    try:
        masks, size = redact_image(args.image, plan, args.output)
    except (RuntimeError, ValueError) as error:
        print(f"❌ {error}")
        sys.exit(2)

    checks, overlaps = verify_redacted_image(
        args.output, masks, size, plan.get("unmasked_blocks", [])
    )

    print("\n========== 圖片遮蔽結果 ==========")
    print(f"圖片尺寸：{size[0]} x {size[1]}")
    print(f"遮蔽區域：{len(masks)} 個")

    for check in checks:
        print(f"  {'✅' if check['passed'] else '❌'} {check['name']}")

    if overlaps:
        print(f"⚠️ 遮蔽範圍與未遮蔽區塊重疊：{len(overlaps)} 處（需人工確認）")

    print(f"去識別化圖片：{args.output}")

    print("\n開始對去識別化圖片重新 OCR...")

    blocks = rerun_ocr(args.output)

    args.reocr_json.parent.mkdir(parents=True, exist_ok=True)

    with open(args.reocr_json, "w", encoding="utf-8") as file:
        json.dump(
            {
                "source_image": args.output.name,
                "image_size": {"width": size[0], "height": size[1]},
                "blocks": blocks,
            },
            file,
            ensure_ascii=False,
            indent=2,
        )

    report = {
        "schema_version": "redaction_report/v1",
        "source_image": str(args.image),
        "output_image": str(args.output),
        "image_size": {"width": size[0], "height": size[1]},
        "masks": masks,
        "checks": checks,
        "mask_overlaps_unmasked_blocks": overlaps,
        "reocr_json": str(args.reocr_json),
        "reocr_block_count": len(blocks),
    }

    with open(args.report, "w", encoding="utf-8") as file:
        json.dump(report, file, ensure_ascii=False, indent=2)

    print(f"重新 OCR 完成：{len(blocks)} 個文字區塊（尚未驗證，暫存於 private）")

    if not all(check["passed"] for check in checks):
        print("❌ 圖片遮蔽檢查未通過")
        sys.exit(2)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
