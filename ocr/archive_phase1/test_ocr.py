import json
from pathlib import Path

from paddleocr import PaddleOCR


# ==========================================
# 1. 路徑設定
# ==========================================

PROJECT_ROOT = Path(__file__).resolve().parent.parent

IMAGE_DIR = PROJECT_ROOT / "test_images"

OUTPUT_DIR = PROJECT_ROOT / "output"

OUTPUT_FILE = OUTPUT_DIR / "patient_test_ocr.json"


# ==========================================
# 2. 尋找測試圖片
# ==========================================

def find_image():

    extensions = [".png", ".jpg", ".jpeg"]

    for extension in extensions:

        image_path = IMAGE_DIR / f"patient_test{extension}"

        if image_path.exists():
            return image_path

    raise FileNotFoundError(
        "找不到測試圖片！\n"
        "請將圖片放在 test_images/patient_test.png"
    )


# ==========================================
# 3. 建立 PaddleOCR
# ==========================================

def create_ocr():

    print("正在初始化 PaddleOCR...")

    ocr = PaddleOCR(
        lang="ch",
    device="cpu",
    enable_mkldnn=False,
    cpu_threads=2,
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False
    )

    return ocr


# ==========================================
# 4. 執行 OCR
# ==========================================

def main():

    print("=" * 55)
    print("未去識別化病歷圖片 OCR 測試")
    print("=" * 55)

    image_path = find_image()

    print(f"\n圖片位置：{image_path}")

    ocr = create_ocr()

    print("\n開始辨識圖片...")

    results = ocr.predict(
        input=str(image_path)
    )

    blocks = []

    for result in results:

        data = result.json

        if "res" in data:
            data = data["res"]

        texts = data.get("rec_texts", [])
        scores = data.get("rec_scores", [])
        boxes = data.get("rec_boxes", [])

        for index, text in enumerate(texts):

            confidence = (
                float(scores[index])
                if index < len(scores)
                else None
            )

            bbox = None

            if index < len(boxes):

                box = boxes[index]

                if hasattr(box, "tolist"):
                    bbox = box.tolist()
                else:
                    bbox = list(box)

            blocks.append({
                "block_id": f"block_{len(blocks) + 1:04d}",
                "text": str(text),
                "confidence": confidence,
                "bbox": bbox,
            })

    OUTPUT_DIR.mkdir(
        parents=True,
        exist_ok=True
    )

    output_data = {
        "source_type": "image",
        "image_name": image_path.name,
        "blocks": blocks,
    }

    with open(
        OUTPUT_FILE,
        "w",
        encoding="utf-8"
    ) as file:

        json.dump(
            output_data,
            file,
            ensure_ascii=False,
            indent=2
        )

    print("\n========== OCR 辨識結果 ==========")

    for block in blocks:
        print(block["text"])

    print("\n========== 統計 ==========")

    print(f"文字區塊數量：{len(blocks)}")

    print(f"輸出位置：{OUTPUT_FILE}")

    print("\nOCR 測試完成！")


if __name__ == "__main__":
    main()