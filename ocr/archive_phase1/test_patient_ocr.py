from paddleocr import PaddleOCR
from pathlib import Path
import json

# ==========================================
# 1. 設定專案路徑
# ==========================================

BASE_DIR = Path(__file__).resolve().parent.parent

IMAGE_DIR = BASE_DIR / "test_images"
OUTPUT_DIR = BASE_DIR / "output"

OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

# 優先使用 PNG，沒有則尋找 JPG
IMAGE_PATH = IMAGE_DIR / "patient_test.png"

if not IMAGE_PATH.exists():
    IMAGE_PATH = IMAGE_DIR / "patient_test.jpg"

OUTPUT_PATH = OUTPUT_DIR / "patient_ocr_result.json"

# ==========================================
# 2. 確認圖片存在
# ==========================================

if not IMAGE_PATH.exists():
    raise FileNotFoundError(
        f"找不到測試圖片：{IMAGE_PATH}"
    )

print("====================================")
print("開始測試病患病例 OCR")
print("====================================")

print(f"圖片位置：{IMAGE_PATH}")

# ==========================================
# 3. 初始化 PaddleOCR
# ==========================================

ocr = PaddleOCR(
    lang="ch",
    device="cpu",
    enable_mkldnn=False,
    cpu_threads=2,
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False
)

# ==========================================
# 4. 執行 OCR
# ==========================================

print("\n開始 OCR 辨識...")

results = ocr.predict(str(IMAGE_PATH))

# ==========================================
# 5. 整理辨識結果
# ==========================================

ocr_data = []

for result in results:

    data = result.json

    if "res" in data:
        data = data["res"]

    texts = data.get("rec_texts", [])
    scores = data.get("rec_scores", [])
    boxes = data.get("rec_boxes", [])

    for index, text in enumerate(texts):

        score = (
            float(scores[index])
            if index < len(scores)
            else None
        )

        box = (
            boxes[index].tolist()
            if hasattr(boxes[index], "tolist")
            else list(boxes[index])
        ) if index < len(boxes) else None

        ocr_data.append({
            "text": text,
            "confidence": score,
            "box": box
        })

# ==========================================
# 6. 儲存 JSON
# ==========================================

with open(
    OUTPUT_PATH,
    "w",
    encoding="utf-8"
) as f:

    json.dump(
        ocr_data,
        f,
        ensure_ascii=False,
        indent=4
    )

# ==========================================
# 7. 顯示辨識結果
# ==========================================

print("\n====================================")
print("OCR 辨識結果")
print("====================================")

for item in ocr_data:

    print(
        f"文字：{item['text']} "
        f"| 信心分數：{item['confidence']}"
    )

print("\n====================================")
print("OCR 測試完成")
print("====================================")

print(f"總共辨識 {len(ocr_data)} 個文字區塊")
print(f"JSON 已儲存：{OUTPUT_PATH}")