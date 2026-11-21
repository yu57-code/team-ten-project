from presidio_analyzer import AnalyzerEngine
from presidio_analyzer.nlp_engine import NlpEngineProvider


def main():

    print("開始初始化 Microsoft Presidio...")

    # 指定使用輕量英文模型
    configuration = {
        "nlp_engine_name": "spacy",
        "models": [
            {
                "lang_code": "en",
                "model_name": "en_core_web_sm"
            }
        ]
    }

    provider = NlpEngineProvider(
        nlp_configuration=configuration
    )

    nlp_engine = provider.create_engine()

    analyzer = AnalyzerEngine(
        nlp_engine=nlp_engine,
        supported_languages=["en"]
    )

    # 使用合成資料進行測試
    sample_text = """
    Patient Name: John Smith
    Email: john.smith@example.com
    Phone: 212-555-0123
    Age: 56 Years
    """

    print("\n========== 原始測試文字 ==========")
    print(sample_text)

    results = analyzer.analyze(
        text=sample_text,
        language="en"
    )

    print("\n========== 個資辨識結果 ==========")

    if not results:
        print("沒有偵測到個資")

    for result in results:

        detected_text = sample_text[
            result.start:result.end
        ]

        print(
            f"類型：{result.entity_type}\n"
            f"文字：{detected_text}\n"
            f"信心分數：{result.score:.4f}\n"
        )

    print("\nPresidio 測試完成！")


if __name__ == "__main__":
    main()