import json
import math
from pathlib import Path

import fitz
from PIL import Image


BASE_DIR = Path(__file__).resolve().parent.parent

JSON_PATH = BASE_DIR / "output" / "pdf_ocr_result.json"
PDF_PATH = BASE_DIR / "test_pdfs" / "sample.pdf"
IMAGE_DIR = BASE_DIR / "output" / "pdf_pages"


def validate_pdf_ocr():

    if not JSON_PATH.exists():
        print("❌ 找不到 pdf_ocr_result.json")
        return

    if not PDF_PATH.exists():
        print("❌ 找不到 sample.pdf")
        return

    with open(JSON_PATH, "r", encoding="utf-8") as f:
        data = json.load(f)

    with fitz.open(PDF_PATH) as pdf:
        actual_pages = len(pdf)

    json_pages = data.get("total_pages")
    pages = data.get("pages", [])

    print("========== PDF OCR 驗證 ==========")

    print(f"PDF 實際頁數：{actual_pages}")
    print(f"JSON 紀錄頁數：{json_pages}")
    print(f"JSON 頁面數量：{len(pages)}")

    errors = []
    warnings = []

    if json_pages != actual_pages:
        errors.append("PDF 與 JSON 的總頁數不一致")

    if len(pages) != actual_pages:
        errors.append("JSON pages 數量不正確")

    total_blocks = 0
    low_confidence = 0

    for expected_page, page_data in enumerate(pages, start=1):

        page_number = page_data.get("page")
        blocks = page_data.get("blocks", [])

        if page_number != expected_page:
            errors.append(
                f"頁碼錯誤：預期 {expected_page}，"
                f"實際 {page_number}"
            )

        image_path = (
            IMAGE_DIR / f"page_{expected_page:03d}.png"
        )

        if not image_path.exists():
            errors.append(
                f"第 {expected_page} 頁找不到圖片"
            )
            continue

        with Image.open(image_path) as img:
            width, height = img.size

        print(f"\n第 {expected_page} 頁")
        print(f"圖片尺寸：{width} x {height}")
        print(f"OCR 文字區塊：{len(blocks)}")

        if not blocks:
            warnings.append(
                f"第 {expected_page} 頁沒有辨識到文字"
            )

        for index, block in enumerate(blocks, start=1):

            total_blocks += 1

            text = block.get("text")
            confidence = block.get("confidence")
            bbox = block.get("bbox")

            if not isinstance(text, str):
                errors.append(
                    f"第 {expected_page} 頁區塊 {index}：文字格式錯誤"
                )

            if (
                not isinstance(confidence, (int, float))
                or isinstance(confidence, bool)
                or not math.isfinite(confidence)
                or not 0 <= confidence <= 1
            ):
                errors.append(
                    f"第 {expected_page} 頁區塊 {index}：信心分數錯誤"
                )
            elif confidence < 0.8:
                low_confidence += 1

            if (
                not isinstance(bbox, list)
                or len(bbox) != 4
                or any(
                    not isinstance(value, (int, float))
                    or isinstance(value, bool)
                    or not math.isfinite(value)
                    for value in bbox
                )
            ):
                errors.append(
                    f"第 {expected_page} 頁區塊 {index}：座標格式錯誤"
                )
                continue

            x1, y1, x2, y2 = bbox

            if not (
                0 <= x1 < x2 <= width
                and 0 <= y1 < y2 <= height
            ):
                errors.append(
                    f"第 {expected_page} 頁區塊 {index}：座標超出範圍"
                )

    print("\n========== 驗證結果 ==========")

    print(f"總文字區塊：{total_blocks}")
    print(f"低信心區塊（< 0.8）：{low_confidence}")
    print(f"錯誤數量：{len(errors)}")
    print(f"警告數量：{len(warnings)}")

    for error in errors[:20]:
        print(f"❌ {error}")

    for warning in warnings[:20]:
        print(f"⚠️ {warning}")

    if not errors:
        print("\n✅ JSON 格式、頁碼、座標及信心分數檢查通過")
    else:
        print("\n❌ JSON 驗證未通過")


if __name__ == "__main__":
    validate_pdf_ocr()