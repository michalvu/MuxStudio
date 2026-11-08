"""Tạo bản sao file .bat đã xoá sạch bí mật (để đưa vào reference/).

Cách dùng:
    python tools/sanitize_bat.py <file.bat> [<file.bat> ...] --out reference

Chỉ thay GIÁ TRỊ của 7 biến bí mật ở dòng gán cấp ngoài cùng thành chuỗi rỗng '',
giữ nguyên mọi byte khác (kể cả CRLF). Sau khi ghi, kiểm tra lại rằng không còn
giá trị bí mật cũ nào trong file mới.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

SECRET_VARS = (
    "SubSourceApiKey", "TmdbApiKey", "TvdbApiKey", "TvdbPin",
    "OsApiKey", "OsUsername", "OsPassword",
)

# $Ten = 'gia tri'  hoac  $Ten = "gia tri"   (giu lai phan comment phia sau)
_ASSIGN = re.compile(
    rb"^(?P<head>\$(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*)"
    rb"(?P<q>['\"])(?P<val>.*?)(?P=q)",
    re.MULTILINE,
)


def sanitize(data: bytes) -> tuple[bytes, list[bytes]]:
    removed: list[bytes] = []

    def repl(m: re.Match[bytes]) -> bytes:
        if m.group("name").decode("ascii", "replace") in SECRET_VARS:
            if m.group("val"):
                removed.append(m.group("val"))
            return m.group("head") + m.group("q") + m.group("q")
        return m.group(0)

    return _ASSIGN.sub(repl, data), removed


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("files", nargs="+", type=Path)
    ap.add_argument("--out", type=Path, required=True)
    args = ap.parse_args(argv)
    args.out.mkdir(parents=True, exist_ok=True)
    for src in args.files:
        data = src.read_bytes()
        clean, removed = sanitize(data)
        # ten dich: bo tien to tai len kieu "0477025b-" neu co
        name = re.sub(r"^[0-9a-f]{8}-", "", src.name)
        dst = args.out / name
        dst.write_bytes(clean)
        left = [v for v in removed if len(v) >= 4 and v in clean]
        if left:
            print(f"[LOI] {dst}: van con gia tri bi mat!", file=sys.stderr)
            return 1
        print(f"{src.name} -> {dst}  (da xoa {len(removed)} bi mat)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
