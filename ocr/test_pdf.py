import argparse
import json
from pathlib import Path

from paddleocr import PaddleOCR

from pdf_reader import image_to_single_page_pdf, pdf_to_images


BASE_DIR = Path(__file__).resolve().parent.parent

PDF_PATH = BASE_DIR / "test_pdfs" / "sample.pdf"

OUTPUT_DIR = BASE_DIR / "output"

IMAGE_DIR = OUTPUT_DIR / "pdf_pages"

OUTPUT_JSON = OUTPUT_DIR / "pdf_ocr_result.json"


def main():

    parser = argparse.ArgumentParser(description="PDF 轉圖片 + PaddleOCR 逐頁辨識")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--pdf", type=Path)
    source.add_argument("--image", type=Path)
    parser.add_argument("--working-pdf", type=Path)
    parser.add_argument("--output", type=Path, default=OUTPUT_JSON)
    parser.add_argument("--image-dir", type=Path, default=IMAGE_DIR)
    args = parser.parse_args()

    if (args.image is None) != (args.working_pdf is None):
        parser.error("--image 和 --working-pdf 必須同時指定")
    pdf_path = args.pdf or args.working_pdf or PDF_PATH
    if args.image:
        image_to_single_page_pdf(args.image, pdf_path)
    output_json = args.output

    if not pdf_path.exists():
        raise FileNotFoundError(
            f"找不到 PDF：{pdf_path}"
        )

    output_json.parent.mkdir(
        parents=True,
        exist_ok=True
    )

    # 第一步：PDF 轉換圖片
    print("開始讀取 PDF...")

    pages = pdf_to_images(
        pdf_path=pdf_path,
        output_dir=args.image_dir,
        dpi=150
    )

    # 第二步：初始化 PaddleOCR
    print("初始化 PaddleOCR...")

    ocr = PaddleOCR(
        lang="ch",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=False,
        cpu_threads=2
    )

    all_pages = []

    # 第三步：逐頁辨識
    for page_info in pages:

        page_number = page_info["page"]
        image_path = page_info["image_path"]

        print(
            f"正在辨識第 {page_number} 頁..."
        )

        results = ocr.predict(image_path)

        blocks = []

        for result in results:

            data = result.json
            data = data.get("res", data)

            texts = data.get("rec_texts", [])
            scores = data.get("rec_scores", [])
            boxes = data.get("rec_boxes", [])

            for index, text in enumerate(texts):

                blocks.append({
                    "text": text,
                    "confidence": float(
                        scores[index]
                    ),
                    "bbox": [
                        int(value)
                        for value in boxes[index]
                    ]
                })

        all_pages.append({
            "page": page_number,
            "image_path": image_path,
            "blocks": blocks
        })

        print(
            f"第 {page_number} 頁完成，"
            f"辨識 {len(blocks)} 個文字區塊"
        )

    # 第四步：輸出 JSON
    output_data = {
        "file_name": pdf_path.name,
        "total_pages": len(all_pages),
        "pages": all_pages
    }

    with open(
        output_json,
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            output_data,
            f,
            ensure_ascii=False,
            indent=4
        )

    print("\nPDF OCR 辨識完成！")

    print(
        f"JSON 儲存位置：{output_json}"
    )


if __name__ == "__main__":
    main()
