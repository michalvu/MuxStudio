"""Xuất bảng dịch cho bản .NET từ ``core/i18n_en.py`` (nguồn chuẩn duy nhất, do ``tools/i18n_build.py`` sinh).

Ghi ra:
* ``dotnet/src/MuxStudio.Core/Resources/i18n_en.json`` — ``{"EN": {mẫu vi: mẫu en}, "SKIP": [...]}`` (nhúng vào DLL)
* ``dotnet/tests/MuxStudio.Core.Tests/Data/python_i18n_cases.json`` — câu đã ghép giá trị + bản dịch của bản Python,
  để test C# so từng chuỗi với thuật toán dò ngược của ``core/i18n.py``.

Chạy lại sau mỗi lần ``python tools/i18n_build.py``:  ``python tools/gen_dotnet_i18n.py``
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from core import i18n, i18n_en  # noqa: E402

OUT_RES = ROOT / "dotnet" / "src" / "MuxStudio.Core" / "Resources" / "i18n_en.json"
OUT_CASES = ROOT / "dotnet" / "tests" / "MuxStudio.Core.Tests" / "Data" / "python_i18n_cases.json"

#: giá trị chèn vào chỗ {} — có số, tên file, đường dẫn tiếng Việt, câu dịch được, câu không dịch được
FILLERS = ["3", "a.srt", "Sẵn sàng.", "C:\\Phim Thử\\Tập 1.mkv", "en, cs", "S01E05", "Kiểm tra công cụ", "xyz", "12.5",
           "Phim Thử (2020)", "bỏ qua", "#1, #2"]
WRAPS = ["{}", "  [!] {}", "▶ 1. {}", "{} →", "({})", "2 {}", "{}…", "    [v] {}."]


def main() -> None:
    OUT_RES.parent.mkdir(parents=True, exist_ok=True)
    OUT_RES.write_text(json.dumps({"EN": i18n_en.EN, "SKIP": sorted(i18n_en.SKIP)}, ensure_ascii=False, indent=0),
                       encoding="utf-8", newline="")

    cases: list[str] = []
    k = 0
    for vi in i18n_en.EN:
        n = vi.count("{}")
        if n == 0:
            cases.append(vi)
            continue
        for _ in range(2):
            s = vi
            for _slot in range(n):
                s = s.replace("{}", FILLERS[k % len(FILLERS)], 1)
                k += 1
            cases.append(s)
    wrapped = [WRAPS[i % len(WRAPS)].replace("{}", c, 1) for i, c in enumerate(cases)]
    joined = [cases[i] + " | " + cases[i + 1] for i in range(0, min(len(cases) - 1, 600), 3)]
    multi = [cases[i] + "\n" + cases[i + 1] for i in range(1, min(len(cases) - 1, 400), 5)]
    allcases = list(dict.fromkeys(cases + wrapped + joined + multi))
    data = [[c, i18n.translate_to("en", c)] for c in allcases]
    OUT_CASES.parent.mkdir(parents=True, exist_ok=True)
    OUT_CASES.write_text(json.dumps(data, ensure_ascii=False, indent=0), encoding="utf-8", newline="")
    print(f"{len(i18n_en.EN)} mẫu, {len(i18n_en.SKIP)} SKIP -> {OUT_RES.relative_to(ROOT)}; "
          f"{len(data)} câu đối chiếu -> {OUT_CASES.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
