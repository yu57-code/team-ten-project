import re
from presidio_analyzer import (
    Pattern,
    PatternRecognizer,
    RecognizerResult,
)

from deid_rules import (
    NAME_STOP_WORDS,
    PII_FIELD_LABELS,
    STAFF_ROLE_WORDS,
)


# =====================================================
# 1. 台灣身分證檢查碼驗證
# =====================================================

LETTER_CODES = {
    "A": 10, "B": 11, "C": 12, "D": 13,
    "E": 14, "F": 15, "G": 16, "H": 17,
    "I": 34, "J": 18, "K": 19, "L": 20,
    "M": 21, "N": 22, "O": 35, "P": 23,
    "Q": 24, "R": 25, "S": 26, "T": 27,
    "U": 28, "V": 29, "W": 32, "X": 30,
    "Y": 31, "Z": 33,
}


def validate_taiwan_id(value):

    value = value.upper()

    if not re.fullmatch(r"[A-Z][12]\d{8}", value):
        return False

    letter_number = LETTER_CODES.get(value[0])

    if letter_number is None:
        return False

    digits = [
        letter_number // 10,
        letter_number % 10,
    ]

    digits.extend(int(x) for x in value[1:])

    weights = [1, 9, 8, 7, 6, 5, 4, 3, 2, 1, 1]

    total = sum(
        digit * weight
        for digit, weight in zip(digits, weights)
    )

    return total % 10 == 0


# =====================================================
# 2. 台灣身分證辨識器
# =====================================================

class TaiwanIDRecognizer(PatternRecognizer):

    def __init__(self):

        patterns = [
            Pattern(
                name="taiwan_national_id",
                regex=r"\b[A-Z][12]\d{8}\b",
                score=0.8,
            )
        ]

        super().__init__(
            supported_entity="TW_NATIONAL_ID",
            patterns=patterns,
            supported_language="en",
        )

    def analyze(
        self,
        text,
        entities,
        nlp_artifacts=None,
        regex_flags=None,
    ):

        results = super().analyze(
            text=text,
            entities=entities,
            nlp_artifacts=nlp_artifacts,
            regex_flags=regex_flags,
        )

        valid_results = []

        for result in results:

            value = text[result.start:result.end]

            if validate_taiwan_id(value):
                valid_results.append(result)

        return valid_results


# =====================================================
# 3. MRN 辨識器
# =====================================================

class MRNRecognizer(PatternRecognizer):

    def __init__(self):

        patterns = [
            Pattern(
                name="medical_record_number",
                regex=(
                    r"(?i)(?:MRN|Medical Record Number|病歷號碼|病歷號)"
                    r"\s*[:：]?\s*"
                    r"([A-Z0-9][A-Z0-9-]{3,19})"
                ),
                score=0.85,
            )
        ]

        super().__init__(
            supported_entity="MRN",
            patterns=patterns,
            supported_language="en",
        )

    def analyze(
        self,
        text,
        entities,
        nlp_artifacts=None,
        regex_flags=None,
    ):

        results = []

        for pattern in self.patterns:

            for match in re.finditer(
                pattern.regex,
                text,
                flags=re.IGNORECASE,
            ):

                start, end = match.span(1)

                # 病歷號碼必定含數字（避免 MRN 後方的 Height 等欄位名稱被誤判）
                if not re.search(r"\d", text[start:end]):
                    continue

                results.append(
                    RecognizerResult(
                        entity_type="MRN",
                        start=start,
                        end=end,
                        score=0.85,
                    )
                )

        return results


# =====================================================
# 4. 姓名與欄位值的共用正規表示式
# =====================================================

# 中文姓名：2～4 個中文字（必須搭配標籤或稱謂才使用）
CJK_NAME = r"[一-鿿]{2,4}"

# 英文單字：大寫開頭，且不能是下一個欄位名稱
EN_WORD = rf"(?!(?:{NAME_STOP_WORDS})\b)[A-Z][a-zA-Z'’]+"

# 英文姓名：Wang, Xiao-Ming 或 John Smith
EN_NAME = (
    rf"{EN_WORD}(?:[ -]{EN_WORD})*\s*,\s*{EN_WORD}(?:[- ]{EN_WORD})*"
    rf"|{EN_WORD}(?:[ -]{EN_WORD}){{1,3}}"
)

# 姓名欄位值：中文、英文或中英混合（王小明 Wang, Xiao-Ming）
NAME_VALUE = (
    rf"(?:{CJK_NAME}(?:\s*[(（]?\s*(?:{EN_NAME})\s*[)）]?)?"
    rf"|{EN_NAME})"
)

DATE_VALUE = (
    r"\d{4}\s*[/.-]\s*\d{1,2}\s*[/.-]\s*\d{1,2}"
    r"|\d{3}\s*[/.]\s*\d{1,2}\s*[/.]\s*\d{1,2}"
    r"|\d{1,2}\s*[/.-]\s*\d{1,2}\s*[/.-]\s*\d{2,4}"
    r"|(?:民國\s*)?\d{2,4}\s*年\s*\d{1,2}\s*月\s*\d{1,2}\s*日"
    r"|\d{1,2}\s+[A-Z][a-z]{2,8}\.?\s+\d{4}"
    r"|[A-Z][a-z]{2,8}\.?\s+\d{1,2},?\s+\d{4}"
)

LABEL_PREFIX = r"(?<![A-Za-z])"


def labeled(label_regex, value_regex):
    """
    建立「標籤 + 欄位值」正規表示式，標籤不分大小寫，欄位值為 group(1)。
    """

    return (
        rf"{LABEL_PREFIX}(?i:{label_regex})(?![A-Za-z])"
        rf"\s*[:：]?\s*({value_regex})"
    )


def label_regex_of(entity):

    return "|".join(
        pattern
        for pattern, _, label_entity in PII_FIELD_LABELS
        if label_entity == entity
    )


# =====================================================
# 5. 共用的正規表示式辨識器
#    - 欄位值區分大小寫（判斷英文姓名需要大寫開頭）
#    - 有 group(1) 時只回傳欄位值位置
# =====================================================

class RegexFieldRecognizer(PatternRecognizer):

    def __init__(self, entity, patterns):

        super().__init__(
            supported_entity=entity,
            patterns=patterns,
            supported_language="en",
        )

    def validate_value(self, value):

        return bool(value.strip())

    def analyze(
        self,
        text,
        entities,
        nlp_artifacts=None,
        regex_flags=None,
    ):

        results = []

        for pattern in self.patterns:

            for match in re.finditer(pattern.regex, text):

                group = 1 if match.re.groups else 0

                start, end = match.span(group)

                value = text[start:end]

                # 去除欄位值前後空白
                start += len(value) - len(value.lstrip())
                end -= len(value) - len(value.rstrip())

                if start >= end:
                    continue

                if not self.validate_value(text[start:end]):
                    continue

                results.append(
                    RecognizerResult(
                        entity_type=self.supported_entities[0],
                        start=start,
                        end=end,
                        score=pattern.score,
                    )
                )

        return results


# =====================================================
# 6. 病人姓名辨識器（中文、英文、中英混合）
# =====================================================

class ChinesePatientNameRecognizer(RegexFieldRecognizer):

    def __init__(self):

        patterns = [
            Pattern(
                name="patient_name_with_label",
                regex=labeled(
                    label_regex_of("PATIENT_NAME"),
                    NAME_VALUE,
                ),
                score=0.85,
            ),
            Pattern(
                # 單獨的 Name / 姓名，前方不可接其他字（排除 Stent Name）
                name="bare_name_label",
                regex=(
                    r"(?<![A-Za-z一-鿿])"
                    r"(?<![A-Za-z][ \t])"
                    rf"(?i:{label_regex_of('PERSON')})(?![A-Za-z])"
                    rf"\s*[:：]?\s*({NAME_VALUE})"
                ),
                score=0.6,
            ),
        ]

        super().__init__("PATIENT_NAME", patterns)


# =====================================================
# 7. 標籤式欄位辨識器
# =====================================================

class DateOfBirthRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "DATE_OF_BIRTH",
            [
                Pattern(
                    name="date_of_birth",
                    regex=labeled(
                        label_regex_of("DATE_OF_BIRTH"),
                        DATE_VALUE,
                    ),
                    score=0.85,
                )
            ],
        )


class VisitDateRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "VISIT_DATE",
            [
                Pattern(
                    name="visit_date",
                    regex=labeled(
                        label_regex_of("VISIT_DATE"),
                        DATE_VALUE,
                    ),
                    score=0.8,
                )
            ],
        )


class StudyIdRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "STUDY_ID",
            [
                Pattern(
                    name="study_id",
                    regex=labeled(
                        label_regex_of("STUDY_ID"),
                        r"[A-Za-z0-9][A-Za-z0-9-]{3,29}",
                    ),
                    score=0.8,
                )
            ],
        )

    def validate_value(self, value):

        # 檢查編號必定含數字（避免 Study Number 後方的 Weight 被誤判）
        return bool(re.search(r"\d", value))


class LabeledNationalIDRecognizer(RegexFieldRecognizer):
    """
    有身分證標籤時，即使檢查碼錯誤（OCR 誤判）也視為身分證。
    """

    def __init__(self):

        super().__init__(
            "TW_NATIONAL_ID",
            [
                Pattern(
                    name="labeled_taiwan_id",
                    regex=labeled(
                        label_regex_of("TW_NATIONAL_ID"),
                        r"[A-Za-z][12]\d{8}",
                    ),
                    score=0.7,
                )
            ],
        )


class AddressRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "ADDRESS",
            [
                Pattern(
                    name="labeled_address",
                    regex=labeled(
                        label_regex_of("ADDRESS"),
                        r"[^\n]{4,80}",
                    ),
                    score=0.7,
                )
            ],
        )


# =====================================================
# 8. 台灣電話號碼辨識器
# =====================================================

class TaiwanPhoneRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "PHONE_NUMBER",
            [
                Pattern(
                    name="tw_mobile",
                    regex=r"(?<![\w-])09\d{2}[-\s]?\d{3}[-\s]?\d{3}(?![\d-])",
                    score=0.75,
                ),
                Pattern(
                    name="tw_landline",
                    regex=(
                        r"(?<![\w-])(?:\(0\d{1,2}\)\s?|0\d{1,2}-)"
                        r"\d{3,4}-?\d{4}(?![\d-])"
                    ),
                    score=0.7,
                ),
                Pattern(
                    name="tw_international",
                    regex=(
                        r"(?<![\w-])\+886[-\s]?\d{1,2}[-\s]?"
                        r"\d{3,4}[-\s]?\d{3,4}(?![\d-])"
                    ),
                    score=0.75,
                ),
                Pattern(
                    name="labeled_phone",
                    regex=labeled(
                        label_regex_of("PHONE_NUMBER"),
                        r"\+?[\d(][\d()\s-]{6,18}\d",
                    ),
                    score=0.8,
                ),
            ],
        )


# =====================================================
# 9. 醫療人員姓名辨識器（處理規則待確認）
# =====================================================

class MedicalStaffNameRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "MEDICAL_STAFF_NAME",
            [
                Pattern(
                    # 王大同 Chen, Ta-Tung, Dr
                    name="name_with_dr_suffix",
                    regex=(
                        rf"((?:{CJK_NAME})?\s*(?:{EN_NAME})?)"
                        r"\s*[,，]\s*(?:Dr|M\.?D)\.?(?![A-Za-z])"
                    ),
                    score=0.75,
                ),
                Pattern(
                    name="dr_prefix",
                    regex=rf"(?<![A-Za-z])Dr\.?\s+((?:{EN_NAME})|{CJK_NAME})",
                    score=0.75,
                ),
                Pattern(
                    name="chinese_name_with_title",
                    regex=rf"({CJK_NAME})\s*醫師",
                    score=0.6,
                ),
                Pattern(
                    name="labeled_staff",
                    regex=labeled(
                        r"physician|doctor|operator|surgeon|cardiologist"
                        r"|referring\s+(?:physician|doctor)|attending"
                        r"|主治醫師|檢查醫師|報告醫師|操作醫師|醫師",
                        NAME_VALUE,
                    ),
                    score=0.75,
                ),
            ],
        )

    def validate_value(self, value):

        value = value.strip()

        if not value:
            return False

        return not any(
            role in value
            for role in STAFF_ROLE_WORDS
        )


# =====================================================
# 10. 醫療機構名稱辨識器（處理規則待確認）
# =====================================================

class InstitutionRecognizer(RegexFieldRecognizer):

    def __init__(self):

        super().__init__(
            "INSTITUTION_NAME",
            [
                Pattern(
                    name="chinese_institution",
                    regex=(
                        r"[一-鿿]{0,20}"
                        r"(?:醫院|醫學中心|醫療中心|診所)"
                        r"(?:[一-鿿]{0,6}分院)?"
                    ),
                    score=0.7,
                ),
                Pattern(
                    name="english_institution",
                    regex=(
                        r"(?:[A-Z][A-Za-z&.'-]*\s+){1,6}"
                        r"(?:Hospital|Medical\s+Center|Clinic)"
                    ),
                    score=0.7,
                ),
            ],
        )
