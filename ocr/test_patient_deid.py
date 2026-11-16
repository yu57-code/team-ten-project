"""
個資區塊偵測與去識別化驗證測試（於 .venv-deid 執行，全部使用虛構資料）

執行：
    cd ocr
    ..\.venv-deid\Scripts\python.exe -m unittest test_patient_deid -v
"""

import json
import unittest

from pii_block_detector import build_document_plans, build_mask_plan, to_blocks
from presidio_service import PresidioService
from verify_deid import merge_pages, verify


def make_blocks(items):
    """
    items: [(文字, x1, y1, x2, y2)] → OCR 區塊（block_0001 起編號）
    """

    return to_blocks([
        {"text": text, "confidence": 0.99, "bbox": [x1, y1, x2, y2]}
        for text, x1, y1, x2, y2 in items
    ])


class DetectorTestBase(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.service = PresidioService()

    def plan_for(self, items):
        return build_mask_plan(make_blocks(items), self.service)

    def masked(self, plan):
        return {mask["block_id"]: mask for mask in plan["masks"]}


# =====================================================
# 1. 姓名辨識情境
# =====================================================

class PatientNameScenarioTest(DetectorTestBase):

    def test_a_chinese_name_same_block(self):

        plan = self.plan_for([
            ("Patient Name: 王小明", 100, 100, 400, 130),
        ])

        masks = self.masked(plan)

        self.assertEqual(plan["status"], "READY")
        self.assertIn("PATIENT_NAME", masks["block_0001"]["entity_types"])
        self.assertNotIn(
            "MASKED_BLOCK_CONTAINS_OTHER_TEXT",
            masks["block_0001"]["review_reasons"],
        )

    def test_b_label_and_name_in_separate_blocks(self):

        plan = self.plan_for([
            ("Patient Name", 100, 100, 280, 130),
            ("王小明", 400, 100, 480, 130),
            ("Age", 700, 100, 760, 130),
            ("56 Years", 800, 100, 900, 130),
        ])

        masks = self.masked(plan)

        self.assertEqual(set(masks), {"block_0002"})
        self.assertIn("PATIENT_NAME", masks["block_0002"]["entity_types"])

    def test_b_name_below_label(self):

        plan = self.plan_for([
            ("Patient Name", 100, 100, 280, 130),
            ("王小明", 100, 140, 180, 170),
        ])

        self.assertEqual(set(self.masked(plan)), {"block_0002"})

    def test_c_mixed_name_same_block(self):

        plan = self.plan_for([
            ("Patient Name: 王小明 Wang, Xiao-Ming", 100, 100, 600, 130),
        ])

        masks = self.masked(plan)

        self.assertIn("PATIENT_NAME", masks["block_0001"]["entity_types"])
        # 整個姓名（中文 + 英文）都必須被辨識，不可只遮部分
        name = [
            d for d in plan["detections"]
            if d["entity_type"] == "PATIENT_NAME"
        ][0]
        self.assertEqual(name["value"], "王小明 Wang, Xiao-Ming")

    def test_c_mixed_name_across_blocks(self):

        plan = self.plan_for([
            ("Patient Name", 100, 100, 280, 130),
            ("王小明", 400, 100, 480, 130),
            ("Wang, Xiao-Ming", 500, 100, 700, 130),
            ("Age", 900, 100, 960, 130),
        ])

        masks = self.masked(plan)

        self.assertEqual(set(masks), {"block_0002", "block_0003"})

    def test_d_table_name_in_patient_section(self):

        plan = self.plan_for([
            ("Patient Information", 100, 40, 420, 85),
            ("Name　王小明", 100, 100, 300, 130),
            ("Age　56", 100, 140, 200, 170),
            ("Gender　Male", 100, 180, 260, 210),
        ])

        masks = self.masked(plan)

        self.assertEqual(set(masks), {"block_0002"})
        self.assertIn("PATIENT_NAME", masks["block_0002"]["entity_types"])
        self.assertEqual(masks["block_0002"]["review_reasons"], ["PARTIAL_MASK_ESTIMATED"])

    def test_label_kept_when_in_same_block(self):

        plan = self.plan_for([
            ("身分證字號: A123456789", 100, 100, 450, 130),
        ])

        mask = self.masked(plan)["block_0001"]

        # 只遮蔽欄位值：遮蔽起點在標籤之後，終點為區塊右緣
        self.assertGreater(mask["bbox"][0], 100 + 150)
        self.assertEqual(mask["bbox"][2], 450)
        self.assertEqual(mask["block_bbox"], [100, 100, 450, 130])

    def test_d_table_cells(self):

        plan = self.plan_for([
            ("Patient Information", 100, 40, 420, 85),
            ("Name", 100, 100, 180, 130),
            ("王小明", 300, 100, 380, 130),
            ("Age", 100, 140, 160, 170),
            ("56", 300, 140, 340, 170),
        ])

        self.assertEqual(set(self.masked(plan)), {"block_0003"})

    def test_english_name(self):

        plan = self.plan_for([
            ("Patient Name: John Smith", 100, 100, 450, 130),
        ])

        self.assertIn("block_0001", self.masked(plan))

    def test_name_label_outside_patient_section_needs_review(self):

        plan = self.plan_for([
            ("Staff", 100, 40, 220, 85),
            ("Name", 100, 100, 180, 130),
            ("Emily Carter", 300, 100, 480, 130),
        ])

        mask = self.masked(plan)["block_0003"]

        self.assertTrue(mask["needs_review"])
        self.assertIn("NAME_LABEL_OUTSIDE_PATIENT_SECTION", mask["review_reasons"])

    def test_missing_name_value_halts(self):

        plan = self.plan_for([
            ("Patient Name", 100, 100, 280, 130),
        ])

        self.assertEqual(plan["status"], "HALTED")


# =====================================================
# 2. 其他個資
# =====================================================

class OtherPiiTest(DetectorTestBase):

    def test_mrn_id_phone_email(self):

        plan = self.plan_for([
            ("MRN", 100, 100, 170, 130),
            ("12345678", 400, 100, 540, 130),
            ("身分證字號: A123456789", 100, 150, 450, 180),
            ("Phone: 0912-345-678", 100, 200, 400, 230),
            ("Email: wang.test@example.com", 100, 250, 520, 280),
        ])

        types = {
            entity
            for mask in plan["masks"]
            for entity in mask["entity_types"]
        }

        self.assertEqual(
            set(self.masked(plan)),
            {"block_0002", "block_0003", "block_0004", "block_0005"},
        )
        self.assertTrue(
            {"MRN", "TW_NATIONAL_ID", "PHONE_NUMBER", "EMAIL_ADDRESS"} <= types
        )

    def test_dates_institution_and_staff_kept(self):

        plan = self.plan_for([
            ("Date of Birth", 100, 100, 280, 130),
            ("1970/01/02", 400, 100, 540, 130),
            ("陳大同 Chen, Ta-Tung, Dr", 100, 150, 450, 180),
            ("Study Date", 100, 200, 260, 230),
            ("2024/05/01", 400, 200, 540, 230),
            ("測試紀念醫院心導管室", 100, 250, 400, 280),
        ])

        masks = self.masked(plan)

        self.assertEqual(masks, {})
        self.assertEqual(plan["policy_pending_entities"], [])


# =====================================================
# 3. 醫療資訊不可誤遮
# =====================================================

class MedicalTermTest(DetectorTestBase):

    def test_medical_terms_not_masked(self):

        plan = self.plan_for([
            ("Vital Signs", 100, 100, 300, 140),
            ("Pressure Summary (mmHg)", 100, 160, 500, 200),
            ("Coronary Findings", 100, 220, 400, 260),
            ("LAD stenosis 80%", 100, 280, 400, 310),
            ("RCA stenosis 30%", 100, 320, 400, 350),
            ("PCI", 100, 360, 160, 390),
            ("Stent implantation", 100, 400, 400, 430),
            ("Stent Name", 600, 400, 800, 430),
            ("Resolute Onyx", 900, 400, 1100, 430),
        ])

        self.assertEqual(plan["masks"], [])
        self.assertTrue(
            {"LAD", "RCA", "PCI", "Stent", "implantation", "Vital Signs"}
            <= set(plan["medical_terms_present"])
        )

    def test_chinese_words_without_label_not_masked(self):

        # 不可將所有 2～4 個中文字都視為姓名
        plan = self.plan_for([
            ("冠狀動脈", 100, 100, 220, 130),
            ("心導管", 100, 150, 200, 180),
            ("支架置放", 100, 200, 220, 230),
        ])

        self.assertEqual(plan["masks"], [])


# =====================================================
# 4. 重新 OCR 驗證與最終 JSON
# =====================================================

class VerificationTest(DetectorTestBase):

    def setUp(self):

        self.plan = self.plan_for([
            ("Patient Name", 100, 100, 280, 130),
            ("王小明 Wang, Xiao-Ming", 400, 100, 700, 130),
            ("LAD stenosis 80%", 100, 200, 400, 230),
        ])

        self.report = {
            "checks": [{"name": "all_masks_fully_black_and_cover_bbox", "passed": True}],
            "masks": [
                {"mask_id": "mask_001", "block_id": "block_0002",
                 "bbox": [400, 100, 700, 130], "rect": [395, 95, 705, 135],
                 "entity_types": ["PATIENT_NAME"]},
            ],
            "mask_overlaps_unmasked_blocks": [],
        }

    def reocr(self, blocks):
        return {
            "source_image": "test_deidentified.png",
            "image_size": {"width": 800, "height": 300},
            "blocks": [
                {"text": text, "confidence": 0.99, "bbox": bbox}
                for text, bbox in blocks
            ],
        }

    def test_clean_reocr_produces_pending_review_json(self):

        result = verify(self.plan, self.report, self.reocr([
            ("Patient Name", [100, 100, 280, 130]),
            ("LAD stenosis 80%", [100, 200, 400, 230]),
        ]), self.service)

        final = result["final"]

        self.assertTrue(result["passed_automated_checks"])
        self.assertEqual(final["review_status"], "PENDING_REVIEW")
        self.assertEqual(final["deid_status"], "AUTO_REDACTED_PENDING_REVIEW")
        self.assertFalse(final["cloud_upload_allowed"])
        self.assertEqual(final["source_type"], "image")
        self.assertEqual(final["page_count"], 1)

        serialized = json.dumps(final, ensure_ascii=False)

        for value in ("王小明", "Wang", "Xiao", "Ming"):
            self.assertNotIn(value, serialized)

        self.assertIn("LAD stenosis 80%", serialized)

        for block in final["blocks"]:
            self.assertTrue({"block_id", "text", "confidence", "bbox"} <= set(block))

    def test_name_residue_fails_and_no_final_json(self):

        result = verify(self.plan, self.report, self.reocr([
            ("Patient Name", [100, 100, 280, 130]),
            ("王小明", [900, 100, 980, 130]),
        ]), self.service)

        self.assertFalse(result["passed_automated_checks"])
        self.assertIsNone(result["final"])

    def test_partial_english_name_residue_fails(self):

        result = verify(self.plan, self.report, self.reocr([
            ("Xiao", [900, 100, 960, 130]),
        ]), self.service)

        self.assertIsNone(result["final"])

    def test_text_inside_mask_fails(self):

        result = verify(self.plan, self.report, self.reocr([
            ("abc", [420, 100, 500, 130]),
        ]), self.service)

        self.assertIsNone(result["final"])

    def test_failed_image_check_fails(self):

        self.report["checks"][0]["passed"] = False

        result = verify(self.plan, self.report, self.reocr([
            ("LAD stenosis 80%", [100, 200, 400, 230]),
        ]), self.service)

        self.assertIsNone(result["final"])

    def test_new_pii_on_rescan_fails(self):

        result = verify(self.plan, self.report, self.reocr([
            ("Email: other.test@example.com", [100, 250, 500, 280]),
        ]), self.service)

        self.assertIsNone(result["final"])


# =====================================================
# 5. 多頁 PDF 合併
# =====================================================

class PdfMergeTest(DetectorTestBase):

    # 沿用單頁驗證的測試資料（不重複執行單頁測試）
    setUp = VerificationTest.setUp
    reocr = VerificationTest.reocr

    def page_final(self, page, blocks):

        result = verify(
            self.plan, self.report, self.reocr(blocks), self.service,
            source_type="pdf", page=page,
        )

        self.assertIsNotNone(result["final"])

        return result["final"]

    def test_merge_clean_pages(self):

        empty_plan = self.plan_for([("LAD stenosis 80%", 100, 200, 400, 230)])

        merged = merge_pages(
            [
                self.page_final(1, [("Patient Name", [100, 100, 280, 130])]),
                self.page_final(2, [("PCI", [100, 100, 160, 130])]),
            ],
            [self.plan, empty_plan],
            "sample.pdf",
        )["final"]

        self.assertEqual(merged["source_type"], "pdf")
        self.assertEqual(merged["page_count"], 2)
        self.assertEqual(merged["review_status"], "PENDING_REVIEW")
        self.assertFalse(merged["cloud_upload_allowed"])
        self.assertEqual(
            [block["block_id"] for block in merged["blocks"]],
            ["p001_block_0001", "p002_block_0001"],
        )

    def test_name_from_page_1_left_on_page_2_fails(self):

        # 第 2 頁本身偵測不到（沒有標籤），但第 1 頁已知此姓名
        page_2_plan = self.plan_for([("王小明", 100, 100, 180, 130)])

        self.assertEqual(page_2_plan["masks"], [])

        result = merge_pages(
            [
                self.page_final(1, [("Patient Name", [100, 100, 280, 130])]),
                {**self.page_final(2, [("PCI", [100, 100, 160, 130])]),
                 "blocks": [{"block_id": "block_0001", "page": 2, "text": "王小明",
                             "confidence": 0.99, "bbox": [100, 100, 180, 130]}]},
            ],
            [self.plan, page_2_plan],
            "sample.pdf",
        )

        self.assertIsNone(result["final"])

    def test_known_value_masked_on_other_page(self):

        plans = build_document_plans({
            1: make_blocks([
                ("Patient Name", 100, 100, 280, 130),
                ("王小明", 400, 100, 480, 130),
            ]),
            # 第 2 頁沒有標籤，單頁偵測不到
            2: make_blocks([
                ("Report reviewed: 王小明", 100, 100, 450, 130),
                ("LAD stenosis 80%", 100, 200, 400, 230),
            ]),
        }, self.service)

        page_2 = {mask["block_id"]: mask for mask in plans[2]["masks"]}

        self.assertEqual(set(page_2), {"block_0001"})
        self.assertIn("KNOWN_VALUE_FROM_OTHER_PAGE", page_2["block_0001"]["review_reasons"])

        # 姓名前方的標題保留，只遮蔽姓名
        self.assertIn("PARTIAL_MASK_ESTIMATED", page_2["block_0001"]["review_reasons"])
        self.assertNotIn("MASKED_BLOCK_CONTAINS_OTHER_TEXT", page_2["block_0001"]["review_reasons"])
        self.assertGreater(page_2["block_0001"]["bbox"][0], 100 + 150)
        self.assertEqual(page_2["block_0001"]["bbox"][2], 450)

    def test_name_in_middle_of_block_masks_only_name(self):

        plan = self.plan_for([
            ("Patient Information", 100, 40, 420, 85),
            ("Name 王小明 Age 56", 100, 100, 400, 130),
        ])

        mask = self.masked(plan)["block_0002"]

        # 只遮蔽中間的姓名，兩側的標籤與年齡保留
        self.assertGreater(mask["bbox"][0], 100)
        self.assertLess(mask["bbox"][2], 400)


if __name__ == "__main__":
    unittest.main()
