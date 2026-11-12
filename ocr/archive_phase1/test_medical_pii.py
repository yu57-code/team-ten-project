from presidio_service import PresidioService


def main():

    print("開始測試台灣醫療個資辨識...")

    # 建立 Presidio 服務
    service = PresidioService()

    # 合成測試資料
    sample_text = """
Patient Name: 王小明
MRN: A123456
身分證字號: A123456789
Email: john.smith@example.com
Phone: 212-555-0123
Age: 56 Years
Gender: Male
"""

    print("\n========== 原始測試文字 ==========")
    print(sample_text)

    # 執行個資辨識
    results = service.analyze(sample_text)

    print("\n========== 個資辨識結果 ==========")

    if not results:
        print("沒有偵測到個資")

    for result in results:

        value = sample_text[
            result.start:result.end
        ]

        print(
            f"類型：{result.entity_type}\n"
            f"文字：{value}\n"
            f"信心分數：{result.score:.4f}\n"
        )

    print("個資辨識測試完成！")


if __name__ == "__main__":
    main()