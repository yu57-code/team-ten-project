import json
from pathlib import Path

from paddleocr import PaddleOCR


BASE_DIR = Path(__file__).resolve().parent.parent

IMAGE_PATH = BASE_DIR / "test_images" / "sample.png"

OUTPUT_DIR = BASE_DIR / "output"
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


def run_ocr(image_path):

    # 初始化 PaddleOCR
    ocr = PaddleOCR(
        lang="ch",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=False,
        cpu_threads=2
    )

    # 執行文字辨識
    results = ocr.predict(str(image_path))

    extracted_data = []

    for result in results:

        data = result.json
        data = data.get("res", data)

        texts = data.get("rec_texts", [])
        scores = data.get("rec_scores", [])
        boxes = data.get("rec_boxes", [])

        for index, text in enumerate(texts):

            extracted_data.append({
                "text": text,
                "confidence": float(scores[index]),
                "bbox": [
                    int(value)
                    for value in boxes[index]
                ]
            })

    return extracted_data


if __name__ == "__main__":

    import argparse

    parser = argparse.ArgumentParser(description="PaddleOCR 圖片辨識")
    parser.add_argument("--image", type=Path, default=IMAGE_PATH)
    parser.add_argument(
        "--output",
        type=Path,
        default=OUTPUT_DIR / "ocr_result.json",
    )
    args = parser.parse_args()

    if not args.image.exists():
        raise FileNotFoundError(
            f"找不到圖片：{args.image}"
        )

    print("開始 OCR 辨識...")

    result = run_ocr(args.image)

    # 輸出 JSON
    output_file = args.output
    output_file.parent.mkdir(parents=True, exist_ok=True)

    with open(
        output_file,
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            result,
            f,
            ensure_ascii=False,
            indent=4
        )

    print(f"OCR 辨識完成，共辨識 {len(result)} 個文字區塊")

    print(f"JSON 已儲存至：{output_file}")