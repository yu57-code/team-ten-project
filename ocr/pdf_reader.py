import fitz
from pathlib import Path


def image_to_single_page_pdf(image_path, pdf_path):
    image_path = Path(image_path)
    if image_path.suffix.lower() not in {".png", ".jpg", ".jpeg"}:
        raise ValueError("只支援 PNG、JPG 或 JPEG")

    with fitz.open(str(image_path)) as image:
        if image.is_pdf or len(image) != 1:
            raise ValueError("需要單頁圖片")
        pdf_bytes = image.convert_to_pdf()

    with Path(pdf_path).open("xb") as target:
        target.write(pdf_bytes)


def pdf_to_images(pdf_path, output_dir, dpi=150):

    pdf_path = Path(pdf_path)
    output_dir = Path(output_dir)

    output_dir.mkdir(parents=True, exist_ok=True)

    if not pdf_path.exists():
        raise FileNotFoundError(
            f"找不到 PDF：{pdf_path}"
        )

    images = []

    with fitz.open(str(pdf_path)) as document:

        print(f"PDF 總頁數：{len(document)}")

        for page_index in range(len(document)):

            page = document[page_index]

            pix = page.get_pixmap(
                dpi=dpi,
                alpha=False
            )

            image_path = (
                output_dir /
                f"page_{page_index + 1:03d}.png"
            )

            pix.save(str(image_path))

            images.append({
                "page": page_index + 1,
                "image_path": str(image_path)
            })

            print(
                f"第 {page_index + 1} 頁轉換完成"
            )

    return images
