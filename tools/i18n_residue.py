"""Liệt kê câu hiển thị mà bản tiếng Anh còn SÓT chữ tiếng Việt.

    MUXSTUDIO_I18N_RECORD=/tmp/rec.json xvfb-run -a python -m pytest -q     # gom mọi câu hiển thị khi chạy test
    python tools/i18n_residue.py /tmp/rec.json                               # in câu còn sót

Tên file / thư mục của dữ liệu test có dấu tiếng Việt ("Phim Thử") được bỏ qua.
"""
from __future__ import annotations

import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from core import i18n  # noqa: E402

IGNORE = re.compile(r"Phim Thử|Phim Thu|Vũ Hoàng Anh|Tiếng Việt|Ngôn ngữ\)|Có hiệu lực khi mở lại MuxStudio|cfg\] MuxStudio|"
                    r"Tên Tập|Phim Khác|đường dẫn có dấu|Thư mục có dấu")


def main() -> int:
    with open(sys.argv[1], encoding="utf-8") as fh:
        items = json.load(fh)
    bad = []
    for s in items:
        out = i18n.translate_to("en", s)
        rest = IGNORE.sub("", out)
        if i18n.has_vietnamese(rest):
            bad.append((s, out))
    for s, out in bad:
        print(json.dumps(s, ensure_ascii=False)[:300])
        print("   →", json.dumps(out, ensure_ascii=False)[:300])
    print(f"# {len(bad)} / {len(items)} câu còn sót chữ tiếng Việt", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
