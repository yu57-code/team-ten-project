import re


# =====================================================
# 1. 個資欄位標籤
#    (標籤正規表示式, 標準標籤文字, 對應個資類型)
# =====================================================

PII_FIELD_LABELS = [
    (
        r"(?:patient|pt\.?)\s*name|病人姓名|患者姓名|病患姓名",
        "Patient Name",
        "PATIENT_NAME",
    ),
    (
        # 單獨的 Name / 姓名：需配合「病人資料」區段才視為病人姓名
        r"姓\s*名|name",
        "Name",
        "PERSON",
    ),
    (
        r"mrn|medical\s+record\s+(?:number|no\.?)|chart\s+(?:number|no\.?)"
        r"|病歷號碼|病歷號",
        "MRN",
        "MRN",
    ),
    (
        r"身分證字號|身分證號碼|身分證|national\s+id(?:\s+no\.?)?",
        "身分證字號",
        "TW_NATIONAL_ID",
    ),
    (
        r"date\s+of\s+birth|dob|birth\s*date|birthday|出生日期|生日",
        "Date of Birth",
        "DATE_OF_BIRTH",
    ),
    (
        r"(?:study|exam(?:ination)?|visit|admission|procedure)\s+date"
        r"|檢查日期|就診日期|住院日期|入院日期|手術日期",
        "Study Date",
        "VISIT_DATE",
    ),
    (
        r"(?:study|accession|exam)\s+(?:number|no\.?|id)"
        r"|檢查單號|檢查編號|檢查號碼|檢查號",
        "Study Number",
        "STUDY_ID",
    ),
    (
        r"phone|tel\.?|telephone|mobile|聯絡電話|電話|手機",
        "Phone",
        "PHONE_NUMBER",
    ),
    (
        r"e-?mail|電子郵件",
        "Email",
        "EMAIL_ADDRESS",
    ),
    (
        r"address|地址|住址",
        "Address",
        "ADDRESS",
    ),
]

# 非個資欄位標籤：用來判斷「下一個欄位開始」
NON_PII_FIELD_LABELS = (
    r"age|gender|sex|height|weight|bmi|bsa|blood\s+type"
    r"|年齡|性別|身高|體重|血型"
)

# 姓名後方不可能接續的欄位字詞（避免英文姓名吃到下一個欄位）
NAME_STOP_WORDS = (
    r"Age|Gender|Sex|Male|Female|MRN|Height|Weight|Study|Date|Phone|Tel"
    r"|Email|Address|Years|Dr|MD"
)

# 病人資料區段標題
PATIENT_SECTION_RE = re.compile(
    r"^\s*(?:patient\s+(?:information|info|data|details|profile)"
    r"|病人資料|病患資料|病人基本資料|病患基本資料|基本資料)\s*[:：]?\s*$",
    re.IGNORECASE,
)

# 一般區段標題的常見字尾
SECTION_TITLE_RE = re.compile(
    r"(?:information|summary|findings|signs|procedure|procedures"
    r"|medication|medications|history|diagnosis|impression|staff"
    r"|personnel|report|結果|摘要|紀錄|資料|人員)\s*(?:\([^)]*\))?\s*$",
    re.IGNORECASE,
)

# 醫師職稱：避免被當成姓名
STAFF_ROLE_WORDS = {
    "主治", "住院", "值班", "主任", "總", "實習", "專科", "會診",
    "主刀", "執行", "報告", "審核", "簽核", "檢查", "操作", "負責",
}


# =====================================================
# 2. 醫療術語
#    - 避免 spaCy 將醫療術語誤判為人名
#    - 去識別化後檢查醫療資訊是否保留
# =====================================================

MEDICAL_TERMS = [
    "Vital Signs", "Pressure Summary", "Coronary Findings", "Coronary",
    "Findings", "Summary", "Procedure", "Diagnosis", "Impression",
    "LAD", "LCX", "RCA", "LM", "LMCA", "OM", "PDA", "PLV", "D1", "D2",
    "stenosis", "occlusion", "PCI", "PTCA", "CABG", "Stent", "stent",
    "implantation", "Balloon", "Inflation", "Deployment", "Dilatation",
    "Predilatation", "Postdilatation", "Catheter", "Guide", "Wire",
    "Sheath", "Radial", "Femoral", "Heparin", "Nitroglycerin",
    "Contrast", "Aspirin", "Clopidogrel", "Ticagrelor",
    "Pressure", "Systolic", "Diastolic", "Mean", "Heart", "Rate",
    "SpO2", "HR", "BP", "RR", "Temp", "ECG", "EKG", "mmHg", "BPM",
    "Aorta", "Artery", "Ventricle", "LV", "RV", "LVEDP", "AO",
    "Lesion", "Vessel", "Size", "Time", "Event", "Device", "Name",
    "Resolute", "Onyx", "Xience", "Promus", "Synergy", "Orsiro",
    "Ultimaster", "Firehawk", "Sprinter", "Emerge", "Euphora", "NC",
    "TIMI", "FFR", "IVUS", "OCT", "Mild", "Moderate", "Severe",
    "Normal", "Total", "Left", "Right", "Proximal", "Mid", "Distal",
]

MEDICAL_TERM_SET = {
    term.lower()
    for term in MEDICAL_TERMS
}


# =====================================================
# 3. 個資類型處理規則
# =====================================================

# 專案已明確需要遮蔽
MASK_ENTITIES = {
    "PATIENT_NAME",
    "PERSON",
    "MRN",
    "TW_NATIONAL_ID",
    "EMAIL_ADDRESS",
    "PHONE_NUMBER",
    "STUDY_ID",
    "ADDRESS",
}

# 專案尚未定義規則：先保守遮蔽，並標記待確認
POLICY_PENDING_ENTITIES = {
    "MEDICAL_STAFF_NAME",
}

# 專案規則：保留不遮蔽（出生日期、就診日期、醫院名稱）
KEEP_ENTITIES = {
    "DATE_OF_BIRTH",
    "VISIT_DATE",
    "INSTITUTION_NAME",
}

# 遮蔽後不可殘留的必要個資：無法定位時停止自動遮蔽
CRITICAL_LABEL_ENTITIES = {
    "PATIENT_NAME",
    "MRN",
    "TW_NATIONAL_ID",
}


# =====================================================
# 4. 共用工具
# =====================================================

def match_field_label(text):
    """
    檢查文字是否以欄位標籤開頭。

    回傳 (標準標籤, 個資類型, 標籤結束位置)；
    非個資標籤回傳 (None, None, 結束位置)；找不到回傳 None。
    """

    for pattern, canonical, entity in PII_FIELD_LABELS:

        match = re.match(
            rf"^\s*(?:{pattern})(?![A-Za-z])\s*[:：]?\s*",
            text,
            re.IGNORECASE,
        )

        if match:
            return canonical, entity, match.end()

    match = re.match(
        rf"^\s*(?:{NON_PII_FIELD_LABELS})(?![A-Za-z])\s*[:：]?\s*",
        text,
        re.IGNORECASE,
    )

    if match:
        return None, None, match.end()

    return None


def normalize_for_compare(text):
    """
    比對個資殘留用：全形轉半形、轉小寫、移除空白與標點。
    """

    import unicodedata

    text = unicodedata.normalize("NFKC", text).lower()

    return re.sub(r"[\W_]+", "", text)


def find_medical_terms(texts):
    """
    回傳文字中出現的醫療術語（不含個資）。
    """

    joined = "\n".join(texts)

    found = []

    for term in MEDICAL_TERMS:

        if re.search(
            rf"(?<![A-Za-z]){re.escape(term)}(?![A-Za-z])",
            joined,
        ):
            found.append(term)

    return sorted(set(found))
