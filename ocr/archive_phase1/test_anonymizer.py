from presidio_service import PresidioService


def main():

    print("開始測試醫療資料去識別化...")

    # 初始化 Presidio
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

    print("\n========== 原始資料 ==========")
    print(sample_text)

    # 執行去識別化
    result = service.anonymize(sample_text)

    print("\n========== 偵測到的個資 ==========")

    for entity in result["entities"]:

        print(
            f"類型：{entity['entity_type']}\n"
            f"文字：{entity['text']}\n"
            f"信心分數：{entity['score']:.4f}\n"
        )

    print("\n========== 去識別化結果 ==========")

    print(result["anonymized_text"])

    # 基本測試：確認已知個資不再出現
    sensitive_values = [
        "王小明",
        "A123456",
        "A123456789",
        "john.smith@example.com",
        "212-555-0123",
    ]

    remaining_values = [
        value
        for value in sensitive_values
        if value in result["anonymized_text"]
    ]

    print("\n========== 驗證結果 ==========")

    if remaining_values:
        print("❌ 去識別化測試失敗，仍有個資殘留")
        for value in remaining_values:
            print(f"未遮蔽：{value}")
    else:
        print("✅ 已知測試個資皆已遮蔽")

    print("\n去識別化測試完成！")


if __name__ == "__main__":
    main()