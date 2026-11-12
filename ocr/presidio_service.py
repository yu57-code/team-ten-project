from presidio_analyzer import AnalyzerEngine
from presidio_analyzer.nlp_engine import NlpEngineProvider
from presidio_anonymizer import AnonymizerEngine

import re

from custom_recognizers import (
    TaiwanIDRecognizer,
    MRNRecognizer,
    ChinesePatientNameRecognizer,
    DateOfBirthRecognizer,
    VisitDateRecognizer,
    StudyIdRecognizer,
    LabeledNationalIDRecognizer,
    AddressRecognizer,
    TaiwanPhoneRecognizer,
    MedicalStaffNameRecognizer,
    InstitutionRecognizer,
)
from deid_rules import MEDICAL_TERM_SET


# =====================================================
# 目前專題需要辨識的個資類型
# =====================================================

TARGET_ENTITIES = [
    "PATIENT_NAME",
    "MRN",
    "TW_NATIONAL_ID",
    "PERSON",
    "EMAIL_ADDRESS",
    "PHONE_NUMBER",
    "STUDY_ID",
    "ADDRESS",
    # 專案尚未定義保留規則，先辨識並標記待確認
    "MEDICAL_STAFF_NAME",
    # 出生日期、就診日期、醫院名稱依專案規則保留，不列入辨識（見 deid_rules.KEEP_ENTITIES）
]


# =====================================================
# 常見欄位標籤：避免被誤判為人名
# =====================================================

FIELD_LABELS = {
    "email",
    "phone",
    "patient name",
    "mrn",
    "medical record number",
    "age",
    "gender",
    "study date",
    "date of birth",
    "height",
    "weight",
    "address",
}


def is_medical_or_numeric(value):

    if re.search(r"\d", value):
        return True

    words = re.findall(r"[A-Za-z]+", value)

    has_cjk = re.search(r"[一-鿿]", value)

    if has_cjk:
        return False

    # 全小寫（例如 kg、mmhg）不是人名
    if words and not any(word[0].isupper() for word in words):
        return True

    # 只要含醫療術語（例如 Xxxx Stent）即不視為人名
    return any(
        word.lower() in MEDICAL_TERM_SET or word.lower() in FIELD_LABELS
        for word in words
    )


class PresidioService:

    def __init__(self):

        print("正在初始化 Microsoft Presidio...")

        # 設定 spaCy 英文模型
        configuration = {
            "nlp_engine_name": "spacy",
            "models": [
                {
                    "lang_code": "en",
                    "model_name": "en_core_web_sm",
                }
            ],
        }

        provider = NlpEngineProvider(
            nlp_configuration=configuration
        )

        nlp_engine = provider.create_engine()

        # 建立 Analyzer
        self.analyzer = AnalyzerEngine(
            nlp_engine=nlp_engine,
            supported_languages=["en"],
        )

        # 註冊自訂辨識器
        self.analyzer.registry.add_recognizer(
            TaiwanIDRecognizer()
        )

        self.analyzer.registry.add_recognizer(
            MRNRecognizer()
        )

        self.analyzer.registry.add_recognizer(
            ChinesePatientNameRecognizer()
        )

        for recognizer in (
            DateOfBirthRecognizer(),
            VisitDateRecognizer(),
            StudyIdRecognizer(),
            LabeledNationalIDRecognizer(),
            AddressRecognizer(),
            TaiwanPhoneRecognizer(),
            MedicalStaffNameRecognizer(),
            InstitutionRecognizer(),
        ):
            self.analyzer.registry.add_recognizer(recognizer)

        # 建立 Anonymizer
        self.anonymizer = AnonymizerEngine()

        print("Microsoft Presidio 初始化完成！")

    # =================================================
    # 個資辨識
    # =================================================

    def analyze(self, text):

        results = self.analyzer.analyze(
            text=text,
            language="en",
            entities=TARGET_ENTITIES,
        )

        filtered_results = []

        for result in results:

            value = text[
                result.start:result.end
            ].strip()

            # 過濾欄位標籤誤判
            if (
                result.entity_type == "PERSON"
                and value.lower() in FIELD_LABELS
            ):
                continue

            # 過濾醫療術語或含數字的人名誤判（例如 LAD、Stent）
            if (
                result.entity_type == "PERSON"
                and is_medical_or_numeric(value)
            ):
                continue

            filtered_results.append(result)

        return filtered_results

    # =================================================
    # 去識別化
    # =================================================

    def anonymize(self, text):

        results = self.analyze(text)

        anonymized_result = self.anonymizer.anonymize(
            text=text,
            analyzer_results=results,
        )

        return {
            "original_text": text,
            "anonymized_text": anonymized_result.text,
            "entities": [
                {
                    "entity_type": result.entity_type,
                    "text": text[
                        result.start:result.end
                    ],
                    "start": result.start,
                    "end": result.end,
                    "score": result.score,
                }
                for result in results
            ],
        }