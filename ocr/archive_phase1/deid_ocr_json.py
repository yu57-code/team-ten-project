import json
import sys
from pathlib import Path

from presidio_service import PresidioService


# =====================================================
# 1. 專案路徑
# =====================================================

PROJECT_ROOT = Path(__file__).resolve().parent.parent

INPUT_FILE = PROJECT_ROOT / "output" / "test_ocr_pii.json"

OUTPUT_FILE = PROJECT_ROOT / "output" / "test_ocr_pii_deidentified.json"


# =====================================================
# 2. OCR 文字欄位名稱
# =====================================================

TEXT_FIELDS = {
    "text",
    "rec_text",
    "recognized_text",
    "content",
    "line_text",
}


# =====================================================
# 3. 讀取 JSON
# =====================================================

def load_json(file_path):

    if not file_path.exists():
        raise FileNotFoundError(
            f"找不到 OCR JSON：{file_path}"
        )

    with open(file_path, "r", encoding="utf-8") as file:
        return json.load(file)


# =====================================================
# 4. 遞迴處理 JSON
# =====================================================

def deidentify_json(data, service, statistics):

    # JSON 物件
    if isinstance(data, dict):

        result = {}

        for key, value in data.items():

            # 找到文字欄位
            if (
                key.lower() in TEXT_FIELDS
                and isinstance(value, str)
            ):

                deid_result = service.anonymize(value)

                result[key] = deid_result["anonymized_text"]

                statistics["text_fields"] += 1
                statistics["entities"] += len(
                    deid_result["entities"]
                )

            else:

                result[key] = deidentify_json(
                    value,
                    service,
                    statistics,
                )

        return result

    # JSON 陣列
    elif isinstance(data, list):

        return [
            deidentify_json(
                item,
                service,
                statistics,
            )
            for item in data
        ]

    # 其他資料型別直接保留
    else:
        return data


# =====================================================
# 5. 主程式
# =====================================================

def main():

    print("=" * 55)
    print("PaddleOCR → Microsoft Presidio 整合測試")
    print("=" * 55)

    print(f"\n輸入檔案：{INPUT_FILE}")

    try:

        # 讀取 OCR JSON
        ocr_data = load_json(INPUT_FILE)

        print("OCR JSON 讀取成功！")

        # 初始化 Presidio
        service = PresidioService()

        # 統計資料
        statistics = {
            "text_fields": 0,
            "entities": 0,
        }

        # 執行去識別化
        deidentified_data = deidentify_json(
            ocr_data,
            service,
            statistics,
        )

        # 檢查是否找到文字
        if statistics["text_fields"] == 0:

            print("\n⚠️ 沒有找到可處理的文字欄位。")
            print("請確認 organized_ocr.json 的結構。")

            return

        # 建立輸出資料夾
        OUTPUT_FILE.parent.mkdir(
            parents=True,
            exist_ok=True,
        )

        # 輸出 JSON
        with open(
            OUTPUT_FILE,
            "w",
            encoding="utf-8",
        ) as file:

            json.dump(
                deidentified_data,
                file,
                ensure_ascii=False,
                indent=2,
            )

        print("\n========== 處理統計 ==========")

        print(
            f"處理文字欄位數量："
            f"{statistics['text_fields']}"
        )

        print(
            f"偵測個資數量："
            f"{statistics['entities']}"
        )

        print("\n========== 輸出結果 ==========")

        print(f"輸出檔案：{OUTPUT_FILE}")

        print("\n整合測試完成！")

    except Exception as error:

        print(f"\n❌ 發生錯誤：{error}")
        sys.exit(1)


if __name__ == "__main__":
    main()