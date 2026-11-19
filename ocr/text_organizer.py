import json
from pathlib import Path
from statistics import median


BASE_DIR = Path(__file__).resolve().parent.parent

INPUT_JSON = BASE_DIR / "output" / "pdf_ocr_result.json"

OUTPUT_JSON = BASE_DIR / "output" / "organized_ocr.json"


def normalize_blocks(blocks):
    """
    將 OCR 區塊標準化，並計算中心點、寬度與高度。
    """

    normalized = []

    for index, block in enumerate(blocks, start=1):

        bbox = block.get("bbox")
        text = block.get("text", "")

        if not bbox or len(bbox) != 4:
            continue

        if not isinstance(text, str) or not text.strip():
            continue

        x1, y1, x2, y2 = bbox

        if x2 <= x1 or y2 <= y1:
            continue

        normalized.append({
            "block_id": f"block_{index:04d}",
            "text": text,
            "confidence": block.get("confidence"),
            "bbox": bbox,
            "center_x": (x1 + x2) / 2,
            "center_y": (y1 + y2) / 2,
            "width": x2 - x1,
            "height": y2 - y1
        })

    return normalized


def group_into_lines(blocks):
    """
    根據文字區塊的垂直位置，進行基本行分組。
    """

    if not blocks:
        return []

    heights = [block["height"] for block in blocks]

    median_height = median(heights)

    # 根據實際文字高度動態設定容許誤差
    threshold = max(3, median_height * 0.5)

    # 先依照 Y 座標排序
    sorted_blocks = sorted(
        blocks,
        key=lambda b: (b["center_y"], b["bbox"][0])
    )

    lines = []

    for block in sorted_blocks:

        best_line = None
        best_distance = float("inf")

        for line in lines:

            distance = abs(
                block["center_y"] - line["center_y"]
            )

            if distance <= threshold and distance < best_distance:
                best_line = line
                best_distance = distance

        if best_line is None:

            lines.append({
                "center_y": block["center_y"],
                "blocks": [block]
            })

        else:

            best_line["blocks"].append(block)

            best_line["center_y"] = sum(
                item["center_y"]
                for item in best_line["blocks"]
            ) / len(best_line["blocks"])

    # 行依照 Y 座標排序
    lines.sort(key=lambda line: line["center_y"])

    organized_lines = []

    for line_index, line in enumerate(lines, start=1):

        # 同一行內依照 X 座標排序
        line_blocks = sorted(
            line["blocks"],
            key=lambda b: b["bbox"][0]
        )

        line_text = " ".join(
            block["text"]
            for block in line_blocks
        )

        organized_lines.append({
            "line_id": f"line_{line_index:04d}",
            "text": line_text,
            "block_ids": [
                block["block_id"]
                for block in line_blocks
            ],
            "blocks": line_blocks
        })

    return organized_lines


def organize_ocr():

    if not INPUT_JSON.exists():
        raise FileNotFoundError(
            f"找不到 OCR JSON：{INPUT_JSON}"
        )

    with open(INPUT_JSON, "r", encoding="utf-8") as f:
        data = json.load(f)

    organized_pages = []

    for page_data in data.get("pages", []):

        page_number = page_data["page"]

        blocks = page_data.get("blocks", [])

        normalized_blocks = normalize_blocks(blocks)

        lines = group_into_lines(normalized_blocks)

        organized_pages.append({
            "page": page_number,
            "total_blocks": len(normalized_blocks),
            "total_lines": len(lines),
            "lines": lines
        })

        print(
            f"第 {page_number} 頁："
            f"{len(normalized_blocks)} 個文字區塊，"
            f"整理成 {len(lines)} 行"
        )

    result = {
        "file_name": data.get("file_name"),
        "total_pages": len(organized_pages),
        "pages": organized_pages
    }

    with open(
        OUTPUT_JSON,
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            result,
            f,
            ensure_ascii=False,
            indent=4
        )

    print("\n文字整理完成！")
    print(f"輸出位置：{OUTPUT_JSON}")


if __name__ == "__main__":
    organize_ocr()