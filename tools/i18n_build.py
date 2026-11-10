"""Ghép bảng dịch: ``tools/i18n/vi.json`` (danh sách mẫu tiếng Việt, đúng thứ tự lúc dịch) + ``tools/i18n/en_*.txt`` (mỗi
dòng một chuỗi JSON tiếng Anh, cùng thứ tự; ``""`` = không dịch, vd chuỗi không hiển thị) → ``core/i18n_en.py``.

Thêm ``tools/i18n/extra.json`` (``{mẫu: bản dịch}``; ``""`` = cố ý không dịch): câu thêm SAU đợt dịch đầu — chạy
``python tools/i18n_extract.py --missing`` để biết câu nào chưa có, điền vào extra.json rồi chạy lại file này.

Kiểm tra: đủ số dòng, số chỗ chèn ``{}`` của bản dịch khớp mẫu.   python tools/i18n_build.py
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
D = os.path.join(ROOT, "tools", "i18n")
SLOT = re.compile(r"\{(\d*)\}")


def slots_ok(vi: str, en: str) -> bool:
    n = vi.count("{}")
    found = SLOT.findall(en)
    if any(found) and not all(found):
        return False
    if any(found):
        return all(int(x) < n for x in found)
    return len(found) == n


def main() -> int:
    with open(os.path.join(D, "vi.json"), encoding="utf-8") as fh:
        vi = json.load(fh)
    en: list[str] = []
    for p in sorted(glob.glob(os.path.join(D, "en_*.txt"))):
        with open(p, encoding="utf-8") as fh:
            for n, line in enumerate(fh, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    en.append(json.loads(line))
                except ValueError as e:
                    print(f"{os.path.basename(p)}:{n}: JSON hỏng: {e}")
                    return 1
    bad = 0
    if len(en) > len(vi):
        print(f"Thừa dòng: {len(en)} bản dịch / {len(vi)} mẫu")
        return 1
    table: dict[str, str] = {}
    for i, (v, e) in enumerate(zip(vi, en)):
        if e and not slots_ok(v, e):
            print(f"#{i}: số chỗ chèn không khớp\n   vi: {v!r}\n   en: {e!r}")
            bad += 1
        if e:
            table[v] = e
    skip = [v for v, e in zip(vi, en) if not e]
    extra_path = os.path.join(D, "extra.json")
    if os.path.exists(extra_path):  # cau them sau dot dich dau (va cau tieng Viet KHONG DAU ma cong cu rut chu khong thay)
        with open(extra_path, encoding="utf-8") as fh:
            for v, e in json.load(fh).items():
                if e and not slots_ok(v, e):
                    print(f"extra.json: số chỗ chèn không khớp\n   vi: {v!r}\n   en: {e!r}")
                    bad += 1
                if e:
                    table[v] = e
                    if v in skip:
                        skip.remove(v)
                elif v not in skip:
                    skip.append(v)
    if bad:
        return 1
    out = os.path.join(ROOT, "core", "i18n_en.py")
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write('"""Bảng dịch Tiếng Việt → English. FILE TỰ SINH bởi tools/i18n_build.py — sửa ở tools/i18n/, đừng sửa ở đây."""\n')
        fh.write("EN: dict[str, str] = {\n")
        for v, e in table.items():
            fh.write(f"    {json.dumps(v, ensure_ascii=False)}: {json.dumps(e, ensure_ascii=False)},\n")
        fh.write("}\n\n#: Chuỗi có chữ tiếng Việt nhưng CỐ Ý không dịch (không hiển thị, hoặc chỉ nằm trong báo cáo tự kiểm tra).\n")
        fh.write("SKIP: frozenset[str] = frozenset({\n")
        for v in skip:
            fh.write(f"    {json.dumps(v, ensure_ascii=False)},\n")
        fh.write("})\n")
    print(f"{len(en)}/{len(vi)} dòng đã có, {len(table)} mẫu được dịch → core/i18n_en.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
