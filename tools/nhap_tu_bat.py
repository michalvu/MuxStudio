"""Nhập cấu hình + bí mật từ các file .bat cũ (phần lõi của nút "Nhập từ .bat").

    python -m tools.nhap_tu_bat "C:\\thu muc chua .bat"      (hoặc liệt kê từng file .bat)

Cấu hình → ``%APPDATA%\\MuxStudio\\config.json``; key/tài khoản → Windows Credential Manager.
Không chạy PowerShell, chỉ đọc các dòng ``$Ten = giá trị``.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

from tools.probe_common import ROOT  # noqa: F401 (them thu muc goc vao sys.path)

from core import config as C  # noqa: E402
from core import secrets as S  # noqa: E402


def find_bats(args: list[str]) -> list[Path]:
    out: list[Path] = []
    for a in args:
        p = Path(a.strip().strip('"'))
        if p.is_dir():
            out += sorted(x for x in p.iterdir() if x.is_file() and x.suffix.lower() == ".bat")
        elif p.is_file():
            out.append(p)
    return out


def main(argv: list[str]) -> int:
    files = find_bats(argv)
    if not files:
        print("[!] Không tìm thấy file .bat nào. Cách dùng: python -m tools.nhap_tu_bat <thư mục chứa .bat>")
        return 1
    imp = C.import_from_bat(files)
    known = [(p, k) for p, k in imp.files if k]
    print("File .bat nhận ra:")
    for p, k in imp.files:
        print(f"  {'✓' if k else '–'} {os.path.basename(p)}  ({k or 'không phải SubFetch/SubPrep/MuxTool'})")
    if not known:
        return 1
    cfg = C.load_config()
    resolutions = {}
    for k, items in imp.conflicts.items():
        print(f"[!] '{k}' khác nhau giữa các file: " + " | ".join(f"{i.source}={i.value!r}" for i in items)
              + f" → dùng giá trị của {items[0].source}")
        resolutions[k] = items[0].value
    for k, items in imp.secret_conflicts.items():
        print(f"[!] Bí mật '{k}' khác nhau giữa các file → dùng giá trị của {items[0].source}")
        resolutions[k] = items[0].value
    try:
        done = C.apply_bat_import(cfg, imp, resolutions, S.store)
    except S.SecretStoreError as e:
        print(f"[LỖI] {e}")
        return 1
    path = C.save_config(cfg)
    n_secret = len([k for k in done if k in S.SECRET_KEYS])
    print(f"\nĐã nhập {len(done) - n_secret} khoá cấu hình → {path}")
    print(f"Đã lưu {n_secret} bí mật vào Windows Credential Manager (mục 'MuxStudio'):")
    for k in S.SECRET_KEYS:
        v = S.store.get(k)
        print(f"  {k:16s} {S.mask(v) if v else '(trống)'}")
    for f, name, line, why in imp.problems:
        print(f"  [?] {f}:{line} {name}: {why}")
    if not any(S.store.get(k) for k in S.SECRET_KEYS):
        print("\n[!] CHƯA CÓ KEY NÀO: các file .bat trên không chứa key/tài khoản.")
        if any(Path(p).parent.name.lower() == "reference" for p, _ in known):
            print("    Đây là bản sao ĐÃ XOÁ KEY trong thư mục 'reference' của MuxStudio.")
        print("    Hãy nhập thư mục chứa 3 file .bat CŨ mà bạn vẫn dùng (SubFetch_v30.bat, SubPrep_v20.bat,")
        print("    MuxTool_v17.bat — bản có key thật), không phải thư mục MuxStudio.")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
