"""
Step 3：Presidio 個資辨識 + OCR 座標對應（於 .venv-deid 執行）

讀取第一階段 OCR JSON，結合下列資訊判斷需要遮蔽的 OCR 區塊：
1. 單一區塊文字（Patient Name: 王小明）
2. 標籤與右側／下方相鄰區塊（Patient Name | 王小明）
3. 同一行合併文字（跨區塊的電話、日期等）
4. 區段上下文（Patient Information 底下的 Name）

輸出遮蔽計畫（含原始個資，僅存放於 output/private/，不可上傳雲端）。
"""

import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path
from itertools import groupby
from statistics import median

from deid_rules import (
    CRITICAL_LABEL_ENTITIES,
    KEEP_ENTITIES,
    NATIONAL_ID_LABEL,
    PATIENT_SECTION_RE,
    POLICY_PENDING_ENTITIES,
    SECTION_TITLE_RE,
    find_medical_terms,
    match_field_label,
)
from text_organizer import group_into_lines, normalize_blocks


PROJECT_ROOT = Path(__file__).resolve().parent.parent

# 只在同一行合併掃描中採用的類型（不含 spaCy PERSON 與地址，避免跨欄誤遮）
LINE_SCAN_ENTITIES = {
    "PATIENT_NAME",
    "MRN",
    "TW_NATIONAL_ID",
    "EMAIL_ADDRESS",
    "PHONE_NUMBER",
    "STUDY_ID",
    "DATE_OF_BIRTH",
    "VISIT_DATE",
    "MEDICAL_STAFF_NAME",
}

LOW_DETECTION_SCORE = 0.7
LOW_OCR_CONFIDENCE = 0.8

EXACT_NATIONAL_ID_FIELD = re.compile(
    rf"^\s*(?P<label>{NATIONAL_ID_LABEL})"
    r"\s*[:：]\s*(?P<value>[A-Z][0-9]{9})\s*$", re.IGNORECASE,
)

BED_FIELD_LABEL = re.compile(
    r"^\s*(?:床[號号]|病床[號号]|bed(?:\s+(?:number|no\.?))?)(?=\s*[:：]|\s*$)", re.IGNORECASE,
)
STANDALONE_TIMESTAMP = re.compile(
    r"(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)/(\d{1,2})/(\d{4})"
    r"\s+(AM|PM)\s+(\d{1,2}):(\d{2})", re.IGNORECASE,
)


def is_name_field_boundary(text):
    if BED_FIELD_LABEL.match(text):
        return True
    timestamp = STANDALONE_TIMESTAMP.fullmatch(text.strip())
    if not timestamp:
        return False
    month, day, year, _, hour, minute = timestamp.groups()
    if not 1 <= int(hour) <= 12 or int(minute) > 59:
        return False
    try:
        month_number = ("jan", "feb", "mar", "apr", "may", "jun", "jul", "aug",
                        "sep", "oct", "nov", "dec").index(month.lower()) + 1
        datetime(int(year), month_number, int(day))
    except ValueError:
        return False
    return True


# =====================================================
# 1. 讀取 OCR JSON（相容 box / bbox 與多種結構）
# =====================================================

def load_ocr_blocks(file_path, page=None):
    """
    page：多頁 PDF OCR JSON（test_pdf.py 輸出）要處理的頁碼（從 1 開始）。
    """

    with open(file_path, "r", encoding="utf-8") as file:
        data = json.load(file)

    if isinstance(data, dict):
        raw_blocks = data.get("blocks")

        if raw_blocks is None and data.get("pages"):
            pages = data["pages"]

            if page is None:
                if len(pages) != 1:
                    raise ValueError("多頁 OCR JSON 請以 --page 指定頁碼")
                page = pages[0].get("page", 1)

            matched = [item for item in pages if item.get("page") == page]

            if len(matched) != 1:
                raise ValueError(f"OCR JSON 中找不到第 {page} 頁")

            raw_blocks = matched[0].get("blocks", [])
    else:
        raw_blocks = data

    if not isinstance(raw_blocks, list):
        raise ValueError("無法辨識 OCR JSON 結構")

    return to_blocks(raw_blocks)


def to_blocks(raw_blocks):
    """
    轉成 text_organizer.normalize_blocks 使用的格式，並保留原始順序編號。
    """

    prepared = []

    for block in raw_blocks:

        bbox = block.get("bbox", block.get("box"))

        prepared.append({
            "text": block.get("text", ""),
            "confidence": block.get("confidence"),
            "bbox": [int(round(v)) for v in bbox] if bbox else None,
        })

    blocks = normalize_blocks(prepared)

    if len(blocks) != len(prepared):
        skipped = len(prepared) - len(blocks)
        print(f"⚠️ 有 {skipped} 個 OCR 區塊缺少文字或座標，已略過")

    return blocks


# =====================================================
# 2. 版面分析：行、區段標題、相鄰區塊
# =====================================================

def vertical_overlap(a, b):

    top = max(a["bbox"][1], b["bbox"][1])
    bottom = min(a["bbox"][3], b["bbox"][3])

    return max(0, bottom - top) / min(a["height"], b["height"])


def horizontal_overlap(a, b):

    left = max(a["bbox"][0], b["bbox"][0])
    right = min(a["bbox"][2], b["bbox"][2])

    return max(0, right - left) / min(a["width"], b["width"])


class Layout:

    def __init__(self, blocks):

        self.blocks = blocks
        self.by_id = {block["block_id"]: block for block in blocks}
        self.lines = group_into_lines(blocks)
        self.median_height = median(b["height"] for b in blocks) if blocks else 0

        self.labels = {
            block["block_id"]: match_field_label(block["text"])
            for block in blocks
        }

        self.headers = self._find_section_headers()

    def is_label(self, block):

        return self.labels.get(block["block_id"]) is not None

    def is_pure_label(self, block):

        label = self.labels.get(block["block_id"])

        return label is not None and not block["text"][label[2]:].strip()

    def _find_section_headers(self):

        headers = []

        for line in self.lines:

            if len(line["blocks"]) != 1:
                continue

            block = line["blocks"][0]
            text = block["text"].strip()

            if self.is_label(block):
                continue

            is_patient = bool(PATIENT_SECTION_RE.match(text))

            if (
                is_patient
                or SECTION_TITLE_RE.search(text)
                or block["height"] >= self.median_height * 1.2
            ):
                headers.append({
                    "block_id": block["block_id"],
                    "y": block["bbox"][1],
                    "is_patient": is_patient,
                })

        return sorted(headers, key=lambda header: header["y"])

    def in_patient_section(self, block):

        current = None

        for header in self.headers:

            if header["y"] <= block["bbox"][1] and header["block_id"] != block["block_id"]:
                current = header

        return bool(current and current["is_patient"])

    def name_fallback_is_other_column(self, anchor, chain):
        # Apply only to a bare, generic Name label without a recognizer-confirmed value.
        label = self.labels[anchor["block_id"]]
        if label[1] != "PERSON" or anchor["text"][label[2]:].strip():
            return False
        first = chain[0]
        height = max(anchor["height"], self.median_height)
        if (first["bbox"][0] - anchor["bbox"][2] < 3 * height
                or anchor["center_y"] - first["center_y"] < anchor["height"] * 0.35
                or not re.fullmatch(r"\d+[.,][A-Za-z]", first["text"].strip())):
            return False

        # Require evidence of a distinct numeric column both above and below the fragment.
        neighbors = [
            block for block in self.blocks
            if block not in chain and not self.is_label(block)
            and horizontal_overlap(first, block) >= 0.5
            and re.search(r"\d", block["text"])
            and abs(block["center_y"] - first["center_y"]) <= 3 * height
        ]
        return (
            any(block["bbox"][3] <= first["bbox"][1] for block in neighbors)
            and any(block["bbox"][1] >= first["bbox"][3] for block in neighbors)
        )

    def right_chain(self, anchor, max_blocks=3, *, name_field=False):
        """
        標籤右側同一列的欄位值，遇到下一個標籤或間距過大即停止。
        """

        row_height = anchor["height"]

        candidates = sorted(
            (
                block for block in self.blocks
                if block is not anchor
                and block["bbox"][0] >= anchor["bbox"][2] - row_height * 0.3
                and vertical_overlap(anchor, block) >= 0.5
            ),
            key=lambda block: block["bbox"][0],
        )

        chain = []
        previous_right = anchor["bbox"][2]

        for block in candidates:

            if self.is_label(block) or (name_field and is_name_field_boundary(block["text"])):
                break

            # 標籤與欄位值之間可能有欄位對齊空白；欄位值之間必須緊鄰
            max_gap = row_height * (10 if not chain else 3)

            if block["bbox"][0] - previous_right > max_gap:
                break

            chain.append(block)
            previous_right = block["bbox"][2]

            if len(chain) >= max_blocks:
                break

        return chain

    def below_chain(self, anchor, max_blocks=2, *, name_field=False):
        """
        標籤正下方的欄位值（表格欄位或換行的姓名）。
        """

        # 同列已有下一個欄位時，不把下一列的內容當成目前欄位的值。
        if any(
            block is not anchor
            and (self.is_label(block) or (name_field and is_name_field_boundary(block["text"])))
            and 0 <= block["bbox"][0] - anchor["bbox"][2] <= anchor["height"] * 10
            and vertical_overlap(anchor, block) >= 0.5
            for block in self.blocks
        ):
            return []

        chain = []
        current = anchor

        while len(chain) < max_blocks:

            candidates = [
                block for block in self.blocks
                if block is not anchor
                and block not in chain
                and block["bbox"][1] >= current["bbox"][1] + current["height"] * 0.5
                and block["bbox"][1] - current["bbox"][3] <= current["height"] * 1.0
                and (
                    horizontal_overlap(anchor, block) >= 0.3
                    or abs(block["bbox"][0] - anchor["bbox"][0]) <= anchor["height"]
                )
            ]

            if not candidates:
                break

            block = min(candidates, key=lambda b: b["bbox"][1])

            if self.is_label(block) or (name_field and is_name_field_boundary(block["text"])):
                break

            chain.append(block)
            current = block

        return chain


# =====================================================
# 3. 文字組合與位置對應
# =====================================================

def compose(parts):
    """
    parts: [(block_id 或 None, 文字, 區塊內起始位置)]
    回傳組合文字與各片段位置。
    """

    text = ""
    segments = []

    for block_id, part, local_offset in parts:

        if text:
            text += " "

        start = len(text)
        text += part

        if block_id is not None:
            segments.append((block_id, start, len(text), local_offset))

    return text, segments


def is_reliable_ner_name(value):
    """
    沒有欄位上下文時，只接受至少兩個大寫開頭英文字的人名（John Smith）。
    """

    if re.search(r"[一-鿿]", value):
        return False

    words = re.findall(r"[A-Za-z][A-Za-z'’-]*", value)

    return len(words) >= 2 and all(word[0].isupper() for word in words)


def map_span(start, end, segments):

    mapped = []

    for block_id, seg_start, seg_end, local_offset in segments:

        overlap_start = max(start, seg_start)
        overlap_end = min(end, seg_end)

        if overlap_start < overlap_end:
            mapped.append((
                block_id,
                overlap_start - seg_start + local_offset,
                overlap_end - seg_start + local_offset,
            ))

    return mapped


def is_krt_measurement_match(layout, row, source_spans):
    if len(source_spans) != 3 or any(
        (layout.labels.get(block["block_id"]) or (None, None))[1] == "PHONE_NUMBER"
        for block in row
    ):
        return False

    values = [layout.by_id[block_id] for block_id, _, _ in source_spans]
    if any(start != 0 or end != len(block["text"])
           for (_, start, end), block in zip(source_spans, values)):
        return False
    if not all(re.fullmatch(r"[+-]?\d{1,3}\.\d{2}", block["text"].strip()) for block in values[:2]):
        return False
    axis = values[2]["text"].strip()
    if not re.fullmatch(r"[0-9]{1,3}", axis) or int(axis) > 180:
        return False

    height = max(layout.median_height, *(block["height"] for block in values))
    top = min(block["bbox"][1] for block in values)
    left = values[0]["bbox"][0]
    for header_line in layout.lines:
        headers = [next((
            header for header in header_line["blocks"]
            if (
            header["text"].strip().upper() == expected
            and value["bbox"][0] <= header["center_x"] <= value["bbox"][2]
            and 0 < top - header["bbox"][1] <= 4 * height
            )
        ), None) for expected, value in zip(("D", "MM", "A"), values)]
        if any(header is None for header in headers):
            continue
        header_top = min(header["bbox"][1] for header in headers)
        if any(
            re.fullmatch(r"KRT[.\s]+DATA", anchor["text"].strip(), re.IGNORECASE)
            and 0 < header_top - anchor["bbox"][1] <= 3 * height
            and 0 < left - anchor["bbox"][0] <= 5 * height
            for anchor in layout.blocks
        ):
            return True
    return False


# =====================================================
# 4. 個資區塊偵測
# =====================================================

class Detector:

    def __init__(self, service, *, page_number=1, stage="original_ocr"):

        self.service = service
        self.page_number = page_number
        self.stage = stage

    def detect(self, blocks):

        layout = Layout(blocks)

        detections = []
        review_items = []
        halt_reasons = []

        anchor_ids = set()

        # ---------- (1) 標籤 + 相鄰區塊 ----------
        for block in blocks:

            label = layout.labels.get(block["block_id"])

            if not label or label[1] is None:
                continue

            # 依專案規則保留的欄位（出生日期、就診日期）不遮蔽
            if label[1] in KEEP_ENTITIES:
                continue

            anchor_ids.add(block["block_id"])

            canonical, label_entity, label_end = label

            in_patient = layout.in_patient_section(block)

            if label_entity == "PERSON" and in_patient:
                canonical, label_entity = "Patient Name", "PATIENT_NAME"

            remainder = block["text"][label_end:]
            name_field = label_entity in {"PERSON", "PATIENT_NAME"}

            parts = [(None, f"{canonical}:", 0)]

            if remainder.strip():
                parts.append((block["block_id"], remainder, label_end))
                chain = layout.right_chain(block, max_blocks=2, name_field=name_field)
            else:
                chain = layout.right_chain(block, name_field=name_field) or layout.below_chain(
                    block, name_field=name_field
                )

            for value_block in chain:
                parts.append((value_block["block_id"], value_block["text"], 0))

            text, segments = compose(parts)

            results = self.service.analyze(text)

            found = False

            for result in results:

                source_spans = map_span(result.start, result.end, segments)
                for block_id, start, end in source_spans:

                    found = True

                    entity = result.entity_type
                    reasons = []

                    if (
                        label_entity == "PERSON"
                        and entity == "PATIENT_NAME"
                    ):
                        entity = "PERSON"
                        reasons.append("NAME_LABEL_OUTSIDE_PATIENT_SECTION")

                    detections.append(self._detection(
                        layout, block_id, entity, result.score,
                        "label_context", start, end, reasons,
                        label_block_id=block["block_id"],
                        result=result, source_spans=source_spans,
                    ))

            if found:
                continue

            # 有標籤但 Presidio 無法確認欄位值：保守遮蔽並標記人工確認
            if remainder.strip():
                exact_id = EXACT_NATIONAL_ID_FIELD.fullmatch(block["text"]) if label_entity == "TW_NATIONAL_ID" else None
                start, end = exact_id.span("value") if exact_id else (label_end, len(block["text"]))
                detections.append(self._detection(
                    layout, block["block_id"], label_entity, 0.5,
                    "label_fallback", start, end,
                    ["LABEL_CONTEXT_ONLY"],
                    label_block_id=block["block_id"],
                ))

            elif chain:
                # 姓名可能跨多個區塊；其他欄位只取第一個欄位值
                targets = (
                    chain if label_entity in {"PATIENT_NAME", "PERSON"}
                    else chain[:1]
                )
                if name_field and layout.name_fallback_is_other_column(block, chain):
                    targets = chain[1:]

                for value_block in targets:
                    detections.append(self._detection(
                        layout, value_block["block_id"], label_entity, 0.5,
                        "label_fallback", 0, len(value_block["text"]),
                        ["LABEL_CONTEXT_ONLY"],
                        label_block_id=block["block_id"],
                    ))

            elif label_entity in CRITICAL_LABEL_ENTITIES:
                # 必要個資標籤找不到欄位值：無法確認個資位置
                halt_reasons.append({
                    "reason": "LABEL_WITHOUT_LOCATABLE_VALUE",
                    "entity_type": label_entity,
                    "label_block_id": block["block_id"],
                    "label_bbox": block["bbox"],
                })

            else:
                review_items.append({
                    "reason": "LABEL_WITHOUT_VALUE",
                    "entity_type": label_entity,
                    "block_ids": [block["block_id"]],
                })

        # ---------- (2) 單一區塊 ----------
        for block in blocks:

            if block["block_id"] in anchor_ids:
                continue

            for result in self.service.analyze(block["text"]):

                reasons = []

                if result.entity_type == "PERSON":

                    value = block["text"][result.start:result.end]

                    # 英文 spaCy 對單字或中文判斷不可靠：只標記人工確認，不自動遮蔽
                    if not is_reliable_ner_name(value):
                        review_items.append({
                            "reason": "POSSIBLE_NAME_UNVERIFIED",
                            "entity_type": "PERSON",
                            "block_ids": [block["block_id"]],
                            "bbox": block["bbox"],
                        })
                        continue

                    reasons.append("NER_ONLY")

                detections.append(self._detection(
                    layout, block["block_id"], result.entity_type,
                    result.score, "block", result.start, result.end, reasons,
                    result=result,
                ))

        # ---------- (3) 同一行合併文字（跨區塊） ----------
        for line in layout.lines:

            def is_non_pii_field(block):
                label = layout.labels.get(block["block_id"])
                return label is not None and label[1] is None

            for boundary, group in groupby(line["blocks"], key=is_non_pii_field):
                candidates = list(group)
                if boundary or len(candidates) < 2:
                    continue

                text, segments = compose(
                    (block["block_id"], block["text"], 0)
                    for block in candidates
                )

                for result in self.service.analyze(text):

                    if result.entity_type not in LINE_SCAN_ENTITIES:
                        continue

                    source_spans = map_span(result.start, result.end, segments)
                    if result.entity_type == "PHONE_NUMBER" and is_krt_measurement_match(
                        layout, line["blocks"], source_spans
                    ):
                        continue
                    for block_id, start, end in source_spans:
                        detections.append(self._detection(
                            layout, block_id, result.entity_type, result.score,
                            "line_context", start, end, [],
                            result=result, source_spans=source_spans,
                        ))

        return layout, detections, review_items, halt_reasons

    def _detection(
        self, layout, block_id, entity, score, method,
        start, end, reasons, label_block_id=None, *, result=None, source_spans=None,
    ):

        block = layout.by_id[block_id]

        reasons = list(reasons)

        if score < LOW_DETECTION_SCORE:
            reasons.append("LOW_DETECTION_SCORE")

        if entity in POLICY_PENDING_ENTITIES:
            reasons.append("POLICY_PENDING")

        confidence = block.get("confidence")

        if confidence is not None and confidence < LOW_OCR_CONFIDENCE:
            reasons.append("LOW_OCR_CONFIDENCE")

        metadata = getattr(result, "recognition_metadata", None) or {}
        recognizer_name = metadata.get("recognizer_name")
        detected_text = block["text"][start:end]
        return {
            "block_id": block_id,
            "entity_type": entity,
            "score": round(float(score), 4),
            "method": method,
            "start": start,
            "end": end,
            "value": detected_text,
            "label_block_id": label_block_id,
            "review_reasons": reasons,
            "page_number": self.page_number,
            "stage": self.stage,
            "detected_text": detected_text,
            "recognizer_name": recognizer_name if isinstance(recognizer_name, str) else None,
            "detection_method": method,
            "bbox": block["bbox"],
            "source_spans": [
                {"block_id": source_id, "start": source_start, "end": source_end,
                 "bbox": layout.by_id[source_id]["bbox"]}
                for source_id, source_start, source_end in (source_spans or [(block_id, start, end)])
            ],
        }


# =====================================================
# 5. 建立遮蔽計畫
# =====================================================

def residual_has_other_data(block, ranges, label_end):
    """
    遮蔽整個區塊時，檢查區塊內是否還有個資以外的資料（可能誤遮醫療資訊）。
    """

    keep = [True] * len(block["text"])

    for start, end in ranges:
        for index in range(max(0, start), min(len(keep), end)):
            keep[index] = False

    for index in range(min(label_end, len(keep))):
        keep[index] = False

    residual = "".join(
        char for char, flag in zip(block["text"], keep) if flag
    )

    residual = re.sub(r"[\s,，:：;；()（）\-–—/|.]+", "", residual)

    return bool(residual)


def char_width(char):
    """
    估計字元相對寬度（中文全形 = 1）。
    """

    if re.match(r"[⺀-鿿＀-￯　-〿]", char):
        return 1.0
    if char.isspace() or char in ":;,.'|!":
        return 0.3
    if char.isupper() or char.isdigit():
        return 0.6
    return 0.5


def value_bbox(block, start, end=None):
    """
    依字元寬度推算 block["text"][start:end] 的位置，只遮蔽個資（保留前方標籤或標題）。
    兩側各多留半個字寬，避免推算誤差造成漏遮；個資在區塊結尾時遮到區塊右緣。
    """

    x1, y1, x2, y2 = block["bbox"]

    text = block["text"]

    if end is None or not text[end:].strip():
        end = len(text)

    widths = [char_width(char) for char in text]
    total = sum(widths)

    if total <= 0:
        return block["bbox"]

    unit = (x2 - x1) / total

    value_x1 = x1 + sum(widths[:start]) * unit - unit * 0.5

    value_x2 = x2 if end >= len(text) else x1 + sum(widths[:end]) * unit + unit * 0.5

    return [max(x1, int(value_x1)), y1, min(x2, int(value_x2 + 0.999)), y2]


def known_value_pattern(item):
    """
    與 verify_deid.find_known_residue 相同的比對規則：
    英文姓名片段比對完整單字，其餘允許中間有空白或標點。
    """

    value = item["value"].strip()

    if item["kind"] == "name_part" and value.isascii():
        return re.compile(rf"(?<![A-Za-z]){re.escape(value)}(?![A-Za-z])", re.IGNORECASE)

    chars = [re.escape(char) for char in re.sub(r"[\W_]+", "", value)]

    if len(chars) < 2:
        return None

    return re.compile(r"[\W_]*".join(chars), re.IGNORECASE)


def find_document_known_values(detector, layout, blocks, detections, known_values):
    """
    多頁文件：其他頁已確認的個資值（例如第 1 頁的醫師姓名），
    在本頁沒有標籤時也必須遮蔽，並標記待人工確認。
    """

    found = []

    covered = {}

    for detection in detections:
        covered.setdefault(detection["block_id"], []).append(
            (detection["start"], detection["end"])
        )

    for item in known_values:

        pattern = known_value_pattern(item)

        if pattern is None:
            continue

        for block in blocks:

            for match in pattern.finditer(block["text"]):

                ranges = covered.setdefault(block["block_id"], [])

                if any(match.start() < end and match.end() > start for start, end in ranges):
                    continue

                ranges.append((match.start(), match.end()))

                found.append(detector._detection(
                    layout, block["block_id"], item["entity_type"], 0.9,
                    "document_known_value", match.start(), match.end(),
                    ["KNOWN_VALUE_FROM_OTHER_PAGE"],
                ))

    return found


def build_mask_plan(blocks, service, source_ocr_json=None, document_known_values=None, *, page_number=1):

    detector = Detector(service, page_number=page_number)

    layout, detections, review_items, halt_reasons = detector.detect(blocks)

    if document_known_values:
        detections += find_document_known_values(
            detector, layout, blocks, detections, document_known_values
        )

    masks = []

    detections_by_block = {}

    for detection in detections:
        detections_by_block.setdefault(detection["block_id"], []).append(detection)

    for index, (block_id, items) in enumerate(
        sorted(detections_by_block.items()), start=1
    ):

        block = layout.by_id[block_id]

        # 同類型已有高分偵測（例如標籤上下文）時，不因其他方法的低分而標記
        confident_entities = {
            item["entity_type"]
            for item in items
            if item["score"] >= LOW_DETECTION_SCORE
        }

        reasons = sorted({
            reason
            for item in items
            for reason in item["review_reasons"]
            if not (
                reason == "LOW_DETECTION_SCORE"
                and item["entity_type"] in confident_entities
            )
        })

        label = layout.labels.get(block_id)
        label_end = label[2] if label else 0

        mask_bbox = block["bbox"]

        ranges = [(item["start"], item["end"]) for item in items]
        span_start = min(start for start, _ in ranges)
        span_end = max(end for _, end in ranges)

        if residual_has_other_data(block, ranges, label_end):

            # 只遮蔽個資所在範圍；範圍內仍夾雜其他文字時才需特別確認
            inner = {"text": block["text"][span_start:span_end]}

            if residual_has_other_data(
                inner, [(s - span_start, e - span_start) for s, e in ranges], 0
            ):
                reasons.append("MASKED_BLOCK_CONTAINS_OTHER_TEXT")

            mask_bbox = value_bbox(block, span_start, span_end)
            reasons.append("PARTIAL_MASK_ESTIMATED")

        elif label_end and span_start >= label_end:
            # 標籤與欄位值在同一區塊：只遮蔽欄位值
            mask_bbox = value_bbox(block, span_start, span_end)
            reasons.append("PARTIAL_MASK_ESTIMATED")

        exact_id = EXACT_NATIONAL_ID_FIELD.fullmatch(block["text"])
        protected_left_edge = None
        if (exact_id and {item["entity_type"] for item in items} == {"TW_NATIONAL_ID"}
                and (span_start, span_end) == exact_id.span("value")):
            widths = [char_width(char) for char in block["text"]]
            x1, _, x2, _ = block["bbox"]
            protected_left_edge = min(mask_bbox[0], round(
                x1 + (x2 - x1) * sum(widths[:exact_id.end("label")]) / sum(widths)
            ))

        masks.append({
            "mask_id": f"mask_{index:03d}",
            "block_id": block_id,
            "block_bbox": block["bbox"],
            "bbox": mask_bbox,
            "entity_types": sorted({item["entity_type"] for item in items}),
            "methods": sorted({item["method"] for item in items}),
            "max_score": max(item["score"] for item in items),
            "needs_review": bool(reasons),
            "review_reasons": sorted(set(reasons)),
            **({"padding_left_limit": protected_left_edge} if protected_left_edge is not None else {}),
        })

    if not masks:
        review_items.append({"reason": "NO_PII_DETECTED", "block_ids": []})

    if not any("PATIENT_NAME" in mask["entity_types"] for mask in masks):
        review_items.append({"reason": "NO_PATIENT_NAME_DETECTED", "block_ids": []})

    entity_counts = {}

    for mask in masks:
        for entity in mask["entity_types"]:
            entity_counts[entity] = entity_counts.get(entity, 0) + 1

    masked_ids = {mask["block_id"] for mask in masks}

    # 已遮蔽區塊不需重複列為疑似個資
    review_items = [
        item for item in review_items
        if item["reason"] != "POSSIBLE_NAME_UNVERIFIED"
        or not set(item["block_ids"]) <= masked_ids
    ]

    plan = {
        "schema_version": "mask_plan/v1",
        "warning": "含原始個資，僅供本機驗證使用，禁止上傳或提供雲端 AI",
        "source_ocr_json": str(source_ocr_json) if source_ocr_json else None,
        "status": "HALTED" if halt_reasons else "READY",
        "halt_reasons": halt_reasons,
        "block_count": len(blocks),
        "masks": masks,
        "entity_counts_by_block": entity_counts,
        "review_items": review_items,
        "policy_pending_entities": sorted(
            set(entity_counts) & POLICY_PENDING_ENTITIES
        ),
        "detections": detections,
        "known_values": collect_known_values(detections),
        "unmasked_blocks": [
            {"block_id": block["block_id"], "bbox": block["bbox"]}
            for block in blocks
            if block["block_id"] not in masked_ids
        ],
        "medical_terms_present": find_medical_terms(
            block["text"]
            for block in blocks
            if block["block_id"] not in masked_ids
        ),
    }

    return plan


def collect_known_values(detections):
    """
    建立驗證用的已知個資清單（完整值與姓名片段）。
    """

    values = {}

    for detection in detections:

        value = detection["value"].strip()

        if len(value) < 2:
            continue

        entity = detection["entity_type"]

        values[(value, "full")] = entity

        if entity in {"PATIENT_NAME", "PERSON", "MEDICAL_STAFF_NAME"}:

            for part in re.findall(r"[一-鿿]{2,}", value):
                values[(part, "name_part")] = entity

            for part in re.findall(r"[A-Za-z]{3,}", value):
                values[(part, "name_part")] = entity

    return [
        {"value": value, "kind": kind, "entity_type": entity}
        for (value, kind), entity in sorted(values.items())
    ]


# =====================================================
# 6. 主程式
# =====================================================

def main():

    parser = argparse.ArgumentParser(description="Presidio 個資區塊偵測")
    parser.add_argument(
        "--ocr-json",
        default=PROJECT_ROOT / "output" / "private" / "patient_test_ocr_result.json",
        type=Path,
    )
    parser.add_argument(
        "--plan",
        default=PROJECT_ROOT / "output" / "private" / "patient_test_mask_plan.json",
        type=Path,
    )
    parser.add_argument(
        "--page",
        type=int,
        default=None,
        help="多頁 PDF OCR JSON 要處理的頁碼",
    )
    parser.add_argument(
        "--all-pages",
        action="store_true",
        help="多頁 PDF：一次偵測所有頁面並跨頁比對已知個資；--plan 需含 {page}",
    )
    args = parser.parse_args()

    from presidio_service import PresidioService

    if args.all_pages:
        main_all_pages(args, PresidioService())
        return

    blocks = load_ocr_blocks(args.ocr_json, args.page)

    print(f"讀取 OCR 區塊：{len(blocks)} 個")

    plan = build_mask_plan(blocks, PresidioService(), args.ocr_json, page_number=args.page or 1)

    args.plan.parent.mkdir(parents=True, exist_ok=True)

    with open(args.plan, "w", encoding="utf-8") as file:
        json.dump(plan, file, ensure_ascii=False, indent=2)

    print_summary(plan)

    print(f"\n遮蔽計畫（含原始個資，請勿上傳）：{args.plan}")

    if plan["status"] != "READY":
        print("\n❌ 無法可靠判斷個資位置，已停止自動遮蔽，請人工確認。")
        sys.exit(2)


def build_document_plans(pages, service, source_ocr_json=None, max_rounds=3):
    """
    pages：{頁碼: OCR 區塊}。先逐頁偵測，再以全部頁面的已知個資重新建立各頁計畫，
    直到已知個資不再增加。
    """

    plans = {
        page: build_mask_plan(blocks, service, source_ocr_json, page_number=page)
        for page, blocks in pages.items()
    }

    def collect(plans):
        return {
            (item["value"], item["kind"]): item
            for plan in plans.values()
            for item in plan["known_values"]
        }

    known = collect(plans)

    for _ in range(max_rounds):

        plans = {
            page: build_mask_plan(blocks, service, source_ocr_json, list(known.values()), page_number=page)
            for page, blocks in pages.items()
        }

        updated = collect(plans)

        if set(updated) <= set(known):
            break

        known.update(updated)

    for page, plan in plans.items():
        plan["page"] = page

    return plans


def main_all_pages(args, service):

    if "{page}" not in str(args.plan):
        print("❌ --all-pages 模式的 --plan 需包含 {page}，例如 sample_p{page}_mask_plan.json")
        sys.exit(2)

    with open(args.ocr_json, "r", encoding="utf-8") as file:
        page_numbers = [item["page"] for item in json.load(file)["pages"]]

    pages = {page: load_ocr_blocks(args.ocr_json, page) for page in page_numbers}

    plans = build_document_plans(pages, service, args.ocr_json)

    halted = []

    for page, plan in plans.items():

        path = Path(str(args.plan).replace("{page}", f"{page:03d}"))
        path.parent.mkdir(parents=True, exist_ok=True)

        with open(path, "w", encoding="utf-8") as file:
            json.dump(plan, file, ensure_ascii=False, indent=2)

        print(f"\n---------- 第 {page} 頁（{len(pages[page])} 個區塊）----------")
        print_summary(plan)

        if plan["status"] != "READY":
            halted.append(page)

    print(f"\n遮蔽計畫（含原始個資，請勿上傳）：{args.plan.parent}")

    if halted:
        print(f"\n❌ 第 {halted} 頁無法可靠判斷個資位置，已停止自動遮蔽，請人工確認。")
        sys.exit(2)


def print_summary(plan):
    """
    只顯示類型、數量與區塊編號，不顯示個資內容。
    """

    print("\n========== 個資偵測摘要 ==========")
    print(f"狀態：{plan['status']}")
    print(f"需遮蔽區塊：{len(plan['masks'])} 個")

    for entity, count in sorted(plan["entity_counts_by_block"].items()):
        print(f"  {entity}: {count} 個區塊")

    review_masks = [mask for mask in plan["masks"] if mask["needs_review"]]

    print(f"需人工確認的遮蔽區塊：{len(review_masks)} 個")

    for mask in review_masks:
        print(
            f"  {mask['mask_id']} ({mask['block_id']}) "
            f"{','.join(mask['entity_types'])} → {','.join(mask['review_reasons'])}"
        )

    for item in plan["review_items"]:
        print(f"  待確認：{item['reason']} {item.get('entity_type', '')}")

    for item in plan["halt_reasons"]:
        print(f"  ⛔ {item['reason']} {item['entity_type']} ({item['label_block_id']})")

    if plan["policy_pending_entities"]:
        print(
            "處理規則待確認的類型（已先保守遮蔽）："
            + ", ".join(plan["policy_pending_entities"])
        )


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
