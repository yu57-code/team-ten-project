"""Extract unverified candidates from a private, page-based OCR result."""

import argparse
import json
import math
import re
from pathlib import Path

from pii_block_detector import Layout, to_blocks


LABELS = {
    "patient_name": r"patient\s*name|(?:病人|病患|患者)?姓名",
    "mrn": r"mrn|medical\s+record\s+(?:number|no\.?)|病歷(?:號碼|號)",
    "gender": r"gender|sex|性[別别]",
    "national_id": r"national\s+id(?:\s+(?:number|no\.?))?|身[分份][證证](?:字[號号]|[號号]碼)?",
    "birth_date": r"date\s+of\s+birth|birth\s*date|dob|出生日期|出生年月日",
    "age": r"age|年[齡龄]",
    "report_date": r"report\s+date|報告日期",
    "report_type": r"report\s+type|報告類型|報告種類",
}
FIELD_LABELS = {
    field: re.compile(rf"^\s*(?:{pattern})(?![A-Za-z])\s*[:：]?\s*", re.I)
    for field, pattern in LABELS.items()
}
NEXT_LABEL = re.compile(
    r"\s+(?=(?:" + "|".join(LABELS.values()) + r"|出生日期|就診日期|檢查日期)\s*[:：])",
    re.I,
)


def validated_pages(document):
    if not isinstance(document, dict):
        raise ValueError("Invalid OCR document")
    pages = document.get("pages")
    total = document.get("total_pages")
    if type(total) is not int or total < 1 or not isinstance(pages, list) or total != len(pages):
        raise ValueError("Invalid OCR page count")
    for number, page in enumerate(pages, 1):
        if not isinstance(page, dict) or type(page.get("page")) is not int or page["page"] != number:
            raise ValueError("Invalid OCR page number")
        blocks = page.get("blocks")
        if not isinstance(blocks, list):
            raise ValueError("Invalid OCR blocks")
        for block in blocks:
            if not isinstance(block, dict):
                raise ValueError("Invalid OCR block")
            text, box, confidence = (block.get(key) for key in ("text", "bbox", "confidence"))
            if (
                not isinstance(text, str) or not text.strip()
                or not isinstance(box, list) or len(box) != 4
                or not all(type(value) is int and value >= 0 for value in box)
                or not (box[0] < box[2] and box[1] < box[3])
                or type(confidence) not in (int, float)
                or not 0 <= confidence <= 1 or not math.isfinite(confidence)
            ):
                raise ValueError("Invalid OCR block")
        yield number, to_blocks(blocks)


def extract(document):
    fields = {field: {"value": None, "candidates": []} for field in LABELS}
    for page, blocks in validated_pages(document):
        layout = Layout(blocks)
        anchors = []
        for block in blocks:
            for field, pattern in FIELD_LABELS.items():
                match = pattern.match(block["text"])
                if match:
                    layout.labels[block["block_id"]] = (field, field, match.end())
                    parts = NEXT_LABEL.split(block["text"])
                    for part in parts:
                        for candidate_field, candidate_pattern in FIELD_LABELS.items():
                            candidate_match = candidate_pattern.match(part)
                            if candidate_match:
                                anchors.append((candidate_field, block,
                                                part[candidate_match.end():].strip(), len(parts) == 1))
                                break
                    break
        for field, block, value, allow_neighbor in anchors:
            source = block
            if not value:
                if not allow_neighbor:
                    continue
                neighbors = layout.right_chain(block, max_blocks=1)
                if not neighbors:
                    neighbors = layout.below_chain(block, max_blocks=1)
                if not neighbors:
                    continue
                source = neighbors[0]
                value = source["text"].strip()
            value = NEXT_LABEL.split(value, maxsplit=1)[0].strip()
            if not value:
                continue
            fields[field]["candidates"].append({
                "value": value,
                "source": "PADDLEOCR",
                "page": page,
                "block_id": source["block_id"],
                "bbox": source["bbox"],
                "confidence": source["confidence"],
                "review_status": "UNVERIFIED",
            })
    for field in fields.values():
        distinct = {candidate["value"] for candidate in field["candidates"]}
        if len(distinct) == 1:
            field["value"] = next(iter(distinct))
    return {"schema_version": "ocr_extracted_fields/v1", "review_status": "UNVERIFIED", "fields": fields}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = extract(json.loads(args.input.read_text(encoding="utf-8")))
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
