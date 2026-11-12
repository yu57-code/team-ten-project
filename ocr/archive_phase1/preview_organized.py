import json
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parent.parent

JSON_PATH = BASE_DIR / "output" / "organized_ocr.json"


with open(JSON_PATH, "r", encoding="utf-8") as f:
    data = json.load(f)


for page in data["pages"]:

    print(f"\n========== 第 {page['page']} 頁 ==========")

    for line in page["lines"]:
        print(line["text"])