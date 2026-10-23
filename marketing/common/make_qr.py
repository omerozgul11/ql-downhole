"""Generate a QR code PNG and verify it decodes to exactly the intended URL.

Needs segno (QR encoder) and OpenCV (decoder for the check), for example:
    PYTHONPATH=.tools/marketing-pylib/lib python marketing/common/make_qr.py URL OUT.png
"""
import sys

import cv2
import segno


def build(url: str, out_path: str) -> None:
    segno.make(url, error='m', micro=False).save(out_path, scale=10, border=2)
    found, _, _ = cv2.QRCodeDetector().detectAndDecode(cv2.imread(out_path))
    if found != url:
        raise SystemExit(f'FAIL: QR at {out_path} decodes to {found!r}, expected {url!r}')
    print(f'OK  QR verified -> {found}')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    build(sys.argv[1], sys.argv[2])
