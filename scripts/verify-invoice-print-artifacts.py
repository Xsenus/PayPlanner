"""Inspect the real synthetic invoice PDFs; never substitute HTML for PDF content."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import unicodedata

import fitz


def normalize(value):
    return " ".join(unicodedata.normalize("NFKC", value).split())


def inspect_pdf(root, name, render_directory):
    path = root / name
    assert path.resolve().parent == root and path.is_file() and not path.is_symlink(), name
    data = path.read_bytes()
    assert 1000 < len(data) <= 20 * 1024 * 1024 and data.startswith(b"%PDF-"), name
    with fitz.open(path) as document:
        assert not document.is_encrypted and 1 <= len(document) <= 3, name
        text = normalize(" ".join(page.get_text("text") for page in document))
        for expected in ["Original lost response service", "Синтетическая организация для проверки печати", "WO-1-1"]:
            assert normalize(expected) in text, f"{name}: missing PDF text: {expected}"
        assert re.search(r"(?<!\d)35[,.]00(?!\d)", text), f"{name}: missing invoice amount"
        images = []
        fonts = set()
        sizes = []
        for index, page in enumerate(document):
            assert abs(page.rect.width - 595.28) < 2 and abs(page.rect.height - 841.89) < 2, f"{name}: expected A4"
            for image in page.get_image_info(xrefs=True):
                rectangle = fitz.Rect(image["bbox"])
                assert rectangle.width > 0 and rectangle.height > 0, f"{name}: empty image"
                assert (page.rect + (-2, -2, 2, 2)).contains(rectangle), f"{name}: clipped image"
                images.append({"page": index + 1, "width": image["width"], "height": image["height"], "bbox": list(rectangle)})
            for block in page.get_text("dict")["blocks"]:
                for line in block.get("lines", []):
                    for span in line["spans"]:
                        if span["text"].strip():
                            fonts.add(span["font"])
                            sizes.append(span["size"])
                            assert 4 <= span["size"] <= 72, f"{name}: invalid print typography"
                            assert (page.rect + (-2, -2, 2, 2)).contains(fitz.Rect(span["bbox"])), f"{name}: clipped text"
            if render_directory is not None:
                destination = render_directory / f"{path.stem}-page-{index + 1}.png"
                assert not destination.exists(), "Preserve previous PDF rendering evidence"
                page.get_pixmap(matrix=fitz.Matrix(1.5, 1.5), alpha=False).save(destination)
        assert fonts and sizes and images, f"{name}: missing printed fonts or images"
        return {"file": name, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                "pages": len(document), "textKeysVerified": True, "amountVerified": True,
                "imageOccurrences": len(images), "images": images, "fonts": sorted(fonts),
                "fontSizeRange": [min(sizes), max(sizes)], "a4AndClippingVerified": True}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--render-directory")
    arguments = parser.parse_args()
    root = Path(arguments.root)
    assert root.is_absolute()
    root = root.resolve(strict=True)
    render = Path(arguments.render_directory) if arguments.render_directory else None
    if render is not None:
        assert render.is_absolute()
        render.mkdir(parents=True, exist_ok=True)
    files = [inspect_pdf(root, name, render) for name in [
        "invoice-print-plain.pdf", "invoice-print-signed.pdf", "invoice-download-pdf.pdf"]]
    plain, signed, download = files
    assert signed["pages"] == plain["pages"], "Signature/seal unexpectedly changed invoice pagination"
    assert plain["imageOccurrences"] >= 1 and signed["imageOccurrences"] >= plain["imageOccurrences"] + 2, "Seal/signature missing from signed PDF"
    assert download["imageOccurrences"] >= 1, "Base image resources missing from downloaded PDF"
    print(json.dumps({"qualified": True, "kind": "Actual PDF text/images/typography/page geometry validation",
                      "python": sys.version.split()[0], "mupdf": fitz.VersionBind, "files": files}, ensure_ascii=True))


if __name__ == "__main__":
    main()
