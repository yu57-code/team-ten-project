"""
Step 6～7：驗證去識別化圖片的重新 OCR 結果，並產生最終去識別化 JSON（於 .venv-deid 執行）

驗證項目：
1. 圖片遮蔽檢查（尺寸、中繼資料、黑色不透明遮蔽）
2. 重新 OCR 結果不得出現已知個資（完整值與姓名片段）
3. 遮蔽區域內不得再辨識出文字
4. 以相同 Presidio 規則重新掃描，不得再偵測到個資
5. 醫療資訊保留比例
6. 最終 JSON 序列化後再次檢查已知個資

任何一項失敗：不輸出最終 JSON（避免誤送雲端），只輸出 private 驗證報告。
全部通過：仍為 PENDING_REVIEW，不可自動視為可上傳雲端。
"""

import argparse
import json
import re
import sys
from pathlib import Path

from deid_rules import find_medical_terms, normalize_for_compare
from pii_block_detector import Detector, to_blocks


PROJECT_ROOT = Path(__file__).resolve().parent.parent

PRIVATE_DIR = PROJECT_ROOT / "output" / "private"

FINAL_SCHEMA = "deid_ocr/v1"


# =====================================================
# 1. 已知個資殘留檢查
# =====================================================

def find_known_residue(texts, known_values):
    """
    回傳殘留的 (個資類型, 比對方式)，不回傳個資內容。
    """

    normalized_text = normalize_for_compare("".join(texts))

    words = {
        normalize_for_compare(word)
        for text in texts
        for word in re.findall(r"[A-Za-z]+", text)
    }

    residue = []

    for item in known_values:

        value = normalize_for_compare(item["value"])

        if len(value) < 2:
            continue

        if item["kind"] == "name_part" and value.isascii():
            # 英文姓名片段需比對完整單字，避免 Lee ⊂ Sleep 誤判
            hit = value in words
        else:
            hit = value in normalized_text

        if hit:
            residue.append({
                "entity_type": item["entity_type"],
                "kind": item["kind"],
            })

    return residue


# =====================================================
# 2. 遮蔽區域內的文字
# =====================================================

def overlap_ratio(rect, bbox):

    left = max(rect[0], bbox[0])
    top = max(rect[1], bbox[1])
    right = min(rect[2], bbox[2])
    bottom = min(rect[3], bbox[3])

    if right <= left or bottom <= top:
        return 0.0

    area = (bbox[2] - bbox[0]) * (bbox[3] - bbox[1])

    return (right - left) * (bottom - top) / area if area else 0.0


def text_inside_masks(blocks, masks):

    hits = []

    for block in blocks:
        for mask in masks:
            if overlap_ratio(mask["rect"], block["bbox"]) > 0.5:
                hits.append({
                    "block_id": block["block_id"],
                    "mask_id": mask["mask_id"],
                })

    return hits


# =====================================================
# 3. 驗證與最終 JSON
# =====================================================

def verify(plan, report, reocr, service, extra_known_values=None,
           source_type="image", page=1):

    blocks = to_blocks(reocr["blocks"])
    texts = [block["text"] for block in blocks]

    known_values = list(plan["known_values"]) + list(extra_known_values or [])

    checks = []

    # (1) 圖片遮蔽
    checks.append({
        "name": "image_redaction_checks",
        "passed": bool(report["checks"]) and all(c["passed"] for c in report["checks"]),
    })

    # (2) 已知個資殘留
    residue = find_known_residue(texts, known_values)

    checks.append({
        "name": "no_known_pii_in_reocr_text",
        "passed": not residue,
        "detail": residue,
    })

    # (3) 遮蔽區域內不應有文字
    inside = text_inside_masks(blocks, report["masks"])

    checks.append({
        "name": "no_text_inside_masked_regions",
        "passed": not inside,
        "detail": inside,
    })

    # (4) 以相同規則重新掃描
    _, detections, review_items, _ = Detector(service).detect(blocks)

    rescan = sorted({
        (detection["block_id"], detection["entity_type"])
        for detection in detections
    })

    checks.append({
        "name": "no_pii_detected_on_rescan",
        "passed": not rescan,
        "detail": [
            {"block_id": block_id, "entity_type": entity}
            for block_id, entity in rescan
        ],
    })

    # (5) 醫療資訊保留（不影響隱私，但缺少時需人工確認）
    expected_terms = plan.get("medical_terms_present", [])
    retained = find_medical_terms(texts)
    missing_terms = sorted(set(expected_terms) - set(retained))

    medical = {
        "expected_terms": len(expected_terms),
        "retained_terms": len(set(expected_terms) & set(retained)),
        "missing_terms": missing_terms,
        "original_block_count": plan.get("block_count"),
        "reocr_block_count": len(blocks),
    }

    privacy_passed = all(check["passed"] for check in checks)

    review = build_review_items(plan, report, review_items, missing_terms)

    final = None

    if privacy_passed:

        final = build_final_json(
            plan, report, reocr, blocks, checks, review, medical, source_type, page
        )

        # (6) 最終 JSON 序列化後再檢查一次
        serialized = json.dumps(final, ensure_ascii=False)

        final_residue = find_known_residue([serialized], known_values)

        checks.append({
            "name": "no_known_pii_in_final_json",
            "passed": not final_residue,
            "detail": final_residue,
        })

        if final_residue:
            final = None

    passed = final is not None

    return {
        "passed_automated_checks": passed,
        "checks": checks,
        "medical_retention": medical,
        "review_items": review,
        "final": final,
    }


def build_review_items(plan, report, rescan_review_items, missing_terms):
    """
    人工確認項目只記錄類型、原因與座標，不含個資內容。
    """

    items = []

    masks_by_block = {mask["block_id"]: mask for mask in report["masks"]}

    for mask in plan["masks"]:
        if mask["needs_review"]:
            rect = masks_by_block.get(mask["block_id"], {}).get("rect", mask["bbox"])
            items.append({
                "type": "MASKED_REGION",
                "mask_id": mask["mask_id"],
                "region": rect,
                "entity_types": mask["entity_types"],
                "reasons": mask["review_reasons"],
            })

    for item in plan["review_items"]:
        items.append({
            "type": "ORIGINAL_SCAN",
            "reasons": [item["reason"]],
            "entity_types": [item["entity_type"]] if item.get("entity_type") else [],
            "region": item.get("bbox"),
        })

    for item in rescan_review_items:
        if item["reason"] == "LABEL_WITHOUT_VALUE":
            continue
        items.append({
            "type": "REOCR_SCAN",
            "reasons": [item["reason"]],
            "entity_types": [item["entity_type"]] if item.get("entity_type") else [],
            "region": item.get("bbox"),
        })

    for overlap in report.get("mask_overlaps_unmasked_blocks", []):
        items.append({
            "type": "MASK_OVERLAP",
            "mask_id": overlap["mask_id"],
            "reasons": ["MASK_OVERLAPS_UNMASKED_TEXT"],
        })

    if missing_terms:
        items.append({
            "type": "MEDICAL_RETENTION",
            "reasons": ["MEDICAL_TERMS_NOT_FOUND_AFTER_REDACTION"],
            "terms": missing_terms,
        })

    if plan.get("policy_pending_entities"):
        items.append({
            "type": "POLICY",
            "reasons": ["DEID_POLICY_NOT_DEFINED"],
            "entity_types": plan["policy_pending_entities"],
        })

    return items


def build_final_json(plan, report, reocr, blocks, checks, review, medical,
                     source_type="image", page=1):

    entity_counts = {}

    for mask in plan["masks"]:
        for entity in mask["entity_types"]:
            entity_counts[entity] = entity_counts.get(entity, 0) + 1

    return {
        "schema_version": FINAL_SCHEMA,
        "source_type": source_type,
        "source_file": reocr["source_image"],
        "page_count": 1,
        "image_size": reocr["image_size"],
        "deid_method": "presidio_ocr_bbox_opaque_black_box",
        # 未經人工確認前不得視為完全通過
        "deid_status": "AUTO_REDACTED_PENDING_REVIEW",
        "review_status": "PENDING_REVIEW",
        "cloud_upload_allowed": False,
        "verification": {
            "passed_automated_checks": True,
            "checks": [
                {"name": check["name"], "passed": check["passed"]}
                for check in checks
            ],
            "redacted_region_count": len(report["masks"]),
            "redacted_entity_counts": entity_counts,
            "medical_retention": {
                "expected_terms": medical["expected_terms"],
                "retained_terms": medical["retained_terms"],
            },
        },
        "review_items": review,
        "blocks": [
            {
                "block_id": block["block_id"],
                "page": page,
                "text": block["text"],
                "confidence": block["confidence"],
                "bbox": block["bbox"],
            }
            for block in blocks
        ],
    }


# =====================================================
# 4. 本機私密紀錄整併
# =====================================================

def write_private_record(record_path, source_file, pages, remove_paths, merge_checks=None):
    """
    驗證通過後，將遮蔽計畫（含原始個資）、遮蔽報告與驗證報告整併為一份本機紀錄，
    並刪除流程暫存檔（重新 OCR 原始結果、單頁最終 JSON 等，內容已在最終 JSON 中）。

    pages：[{page, mask_plan, redaction_report, verification_report}]
    """

    record = {
        "schema_version": "deid_private_record/v1",
        "warning": "含原始個資，僅供本機稽核與人工確認使用，禁止上傳或提供雲端 AI",
        "source_file": source_file,
        "pages": pages,
    }

    if merge_checks is not None:
        record["cross_page_checks"] = merge_checks

    record_path.parent.mkdir(parents=True, exist_ok=True)

    with open(record_path, "w", encoding="utf-8") as file:
        json.dump(record, file, ensure_ascii=False, indent=2)

    removed = 0

    for path in remove_paths:
        if path.exists() and path.resolve() != record_path.resolve():
            path.unlink()
            removed += 1

    print(f"本機私密紀錄（已整併 {removed} 個暫存檔）：{record_path}")


# =====================================================
# 5. 多頁 PDF：合併各頁結果並跨頁檢查
# =====================================================

def merge_pages(page_finals, plans, source_file):
    """
    page_finals / plans：各頁驗證通過的最終 JSON 與遮蔽計畫（依頁碼排序）。

    某頁偵測到的個資（例如第 1 頁的病人姓名）可能在其他頁以無標籤形式出現，
    因此以所有頁的已知個資，再檢查一次全部頁面的文字。
    """

    known_values = [item for plan in plans for item in plan["known_values"]]

    texts = [block["text"] for final in page_finals for block in final["blocks"]]

    residue = find_known_residue(texts, known_values)

    checks = [
        {
            "name": "all_pages_passed_automated_checks",
            "passed": all(
                final["verification"]["passed_automated_checks"] for final in page_finals
            ),
        },
        {
            "name": "no_known_pii_across_pages",
            "passed": not residue,
            "detail": residue,
        },
    ]

    merged = None

    if all(check["passed"] for check in checks):

        entity_counts = {}

        for final in page_finals:
            for entity, count in final["verification"]["redacted_entity_counts"].items():
                entity_counts[entity] = entity_counts.get(entity, 0) + count

        merged = {
            "schema_version": FINAL_SCHEMA,
            "source_type": "pdf",
            "source_file": source_file,
            "page_count": len(page_finals),
            "deid_method": "presidio_ocr_bbox_opaque_black_box",
            # 未經人工確認前不得視為完全通過
            "deid_status": "AUTO_REDACTED_PENDING_REVIEW",
            "review_status": "PENDING_REVIEW",
            "cloud_upload_allowed": False,
            "verification": {
                "passed_automated_checks": True,
                "checks": [
                    {"name": check["name"], "passed": check["passed"]}
                    for check in checks
                ],
                "redacted_region_count": sum(
                    final["verification"]["redacted_region_count"] for final in page_finals
                ),
                "redacted_entity_counts": entity_counts,
            },
            "pages": [
                {
                    "page": index,
                    "image_file": final["source_file"],
                    "image_size": final["image_size"],
                    "verification": final["verification"],
                    "review_items": final["review_items"],
                }
                for index, final in enumerate(page_finals, start=1)
            ],
            "blocks": [
                # 頁碼加入 block_id，確保整份文件唯一
                {**block, "block_id": f"p{block['page']:03d}_{block['block_id']}"}
                for final in page_finals
                for block in final["blocks"]
            ],
        }

        final_residue = find_known_residue(
            [json.dumps(merged, ensure_ascii=False)], known_values
        )

        checks.append({
            "name": "no_known_pii_in_merged_json",
            "passed": not final_residue,
            "detail": final_residue,
        })

        if final_residue:
            merged = None

    return {"checks": checks, "final": merged}


def main_merge(args):

    def load(path):
        with open(path, "r", encoding="utf-8") as file:
            return json.load(file)

    if len(args.merge_pages) != len(args.merge_plans):
        print("❌ --merge-pages 與 --merge-plans 數量不一致")
        sys.exit(2)

    result = merge_pages(
        [load(path) for path in args.merge_pages],
        [load(path) for path in args.merge_plans],
        args.source_file,
    )

    print("\n========== 多頁合併驗證結果 ==========")

    for check in result["checks"]:
        detail = check.get("detail")
        suffix = f"（{len(detail)} 筆）" if detail else ""
        print(f"  {'✅' if check['passed'] else '❌'} {check['name']}{suffix}")

    if result["final"] is None:

        if args.output.exists():
            try:
                if load(args.output).get("schema_version") == FINAL_SCHEMA:
                    args.output.unlink()
                    print("已移除先前產生的最終 JSON（本次驗證失敗）")
            except (json.JSONDecodeError, OSError):
                pass

        print("\n❌ 跨頁驗證未通過：不產生最終去識別化 JSON，請人工確認")
        sys.exit(2)

    with open(args.output, "w", encoding="utf-8") as file:
        json.dump(result["final"], file, ensure_ascii=False, indent=2)

    print(f"\n✅ 共 {result['final']['page_count']} 頁合併完成（狀態仍為 PENDING_REVIEW）")
    print(f"最終去識別化 JSON：{args.output}")

    if args.private_record:

        reports = args.merge_reports or []
        verifications = args.merge_verifications or []

        pages = [
            {
                "page": index,
                "mask_plan": load(plan),
                "redaction_report": load(reports[index - 1]) if reports else None,
                "verification_report": load(verifications[index - 1]) if verifications else None,
            }
            for index, plan in enumerate(args.merge_plans, start=1)
        ]

        write_private_record(
            args.private_record,
            args.source_file,
            pages,
            list(args.merge_pages) + list(args.merge_plans) + list(reports)
            + list(verifications) + list(args.merge_reocr or []),
            merge_checks=result["checks"],
        )


# =====================================================
# 6. 主程式
# =====================================================

def main():

    parser = argparse.ArgumentParser(description="去識別化驗證與最終 JSON 輸出")
    parser.add_argument("--plan", type=Path, default=PRIVATE_DIR / "patient_test_mask_plan.json")
    parser.add_argument("--report", type=Path, default=PRIVATE_DIR / "patient_test_redaction_report.json")
    parser.add_argument("--reocr-json", type=Path, default=PRIVATE_DIR / "patient_test_reocr_raw.json")
    parser.add_argument(
        "--output",
        type=Path,
        default=PROJECT_ROOT / "output" / "patient_test_deidentified.json",
    )
    parser.add_argument(
        "--verification-report",
        type=Path,
        default=PRIVATE_DIR / "patient_test_verification_report.json",
    )
    parser.add_argument(
        "--expected-pii",
        type=Path,
        default=None,
        help="測試用：額外的已知虛構個資清單（JSON）",
    )
    parser.add_argument("--source-type", default="image", choices=["image", "pdf"])
    parser.add_argument("--page", type=int, default=1)
    # 多頁 PDF 合併模式
    parser.add_argument("--merge-pages", type=Path, nargs="+", default=None)
    parser.add_argument("--merge-plans", type=Path, nargs="+", default=None)
    parser.add_argument("--source-file", default=None)
    parser.add_argument("--merge-reports", type=Path, nargs="+", default=None)
    parser.add_argument("--merge-verifications", type=Path, nargs="+", default=None)
    parser.add_argument("--merge-reocr", type=Path, nargs="+", default=None)
    # 驗證通過後整併暫存檔為單一本機紀錄（未指定則保留各暫存檔）
    parser.add_argument("--private-record", type=Path, default=None)
    args = parser.parse_args()

    if args.merge_pages:
        main_merge(args)
        return

    from presidio_service import PresidioService

    def load(path):
        with open(path, "r", encoding="utf-8") as file:
            return json.load(file)

    plan = load(args.plan)
    report = load(args.report)
    reocr = load(args.reocr_json)

    extra = load(args.expected_pii)["known_values"] if args.expected_pii else None

    result = verify(
        plan, report, reocr, PresidioService(), extra, args.source_type, args.page
    )

    private_report = {key: value for key, value in result.items() if key != "final"}

    args.verification_report.parent.mkdir(parents=True, exist_ok=True)

    with open(args.verification_report, "w", encoding="utf-8") as file:
        json.dump(private_report, file, ensure_ascii=False, indent=2)

    print("\n========== 重新 OCR 驗證結果 ==========")

    for check in result["checks"]:
        detail = check.get("detail")
        suffix = f"（{len(detail)} 筆）" if detail else ""
        print(f"  {'✅' if check['passed'] else '❌'} {check['name']}{suffix}")

    medical = result["medical_retention"]

    print(
        f"醫療術語保留：{medical['retained_terms']}/{medical['expected_terms']}"
        f"，重新 OCR 區塊：{medical['reocr_block_count']}"
        f"（原始 {medical['original_block_count']}）"
    )

    print(f"需人工確認項目：{len(result['review_items'])} 項")

    if result["final"] is None:

        # 移除舊的最終輸出，避免誤用過期結果
        if args.output.exists():
            try:
                if load(args.output).get("schema_version") == FINAL_SCHEMA:
                    args.output.unlink()
                    print("已移除先前產生的最終 JSON（本次驗證失敗）")
            except (json.JSONDecodeError, OSError):
                pass

        print("\n❌ 驗證未通過：不產生最終去識別化 JSON，請人工確認")
        print(f"驗證報告（本機）：{args.verification_report}")
        sys.exit(2)

    with open(args.output, "w", encoding="utf-8") as file:
        json.dump(result["final"], file, ensure_ascii=False, indent=2)

    print("\n✅ 自動驗證通過（狀態仍為 PENDING_REVIEW，需人工確認後才可使用）")
    print(f"最終去識別化 JSON：{args.output}")

    if args.private_record:
        write_private_record(
            args.private_record,
            reocr["source_image"],
            [{
                "page": args.page,
                "mask_plan": plan,
                "redaction_report": report,
                "verification_report": private_report,
            }],
            [args.plan, args.report, args.reocr_json, args.verification_report],
        )
    else:
        print(f"驗證報告（本機）：{args.verification_report}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
