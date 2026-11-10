"""Hai ngôn ngữ giao diện (bản 1.2.0): bảng dịch đủ, đúng số chỗ chèn, ghép câu đúng; tiếng Việt không đổi gì."""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

from core import i18n, i18n_en

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
import i18n_build  # noqa: E402
import i18n_extract  # noqa: E402

# chu tieng Viet duoc phep nam trong ban tieng Anh (ten ngon ngu, loi nhac song ngu o o chon ngon ngu)
ALLOWED_VI = ("Tiếng Việt", "Ngôn ngữ", "Có hiệu lực khi mở lại MuxStudio")


@pytest.fixture
def en():
    i18n.set_language("en")
    yield
    i18n.set_language("vi")


def test_every_displayed_string_has_a_translation():
    """Thêm câu chữ mới vào ``core/`` hoặc ``gui/`` mà quên dịch → test này hỏng (xem tools/i18n_build.py)."""
    found = i18n_extract.extract_all()
    missing = [t for t in found if t not in i18n_en.EN and t not in i18n_en.SKIP]
    assert not missing, f"{len(missing)} câu chưa dịch — thêm vào tools/i18n/extra.json rồi chạy tools/i18n_build.py: {missing[:5]}"
    assert len(found) > 1200 and len(i18n_en.EN) > 1200


def test_translations_keep_placeholders_and_have_no_vietnamese_left():
    for vi, en_text in i18n_en.EN.items():
        assert i18n_build.slots_ok(vi, en_text), (vi, en_text)
        rest = en_text
        for a in ALLOWED_VI:
            rest = rest.replace(a, "")
        assert not i18n.has_vietnamese(rest), (vi, en_text)
        # khoang trang / xuong dong o hai dau giu nguyen (cau duoc noi voi phan khac)
        assert (vi[:1].isspace(), vi[-1:].isspace()) == (en_text[:1].isspace(), en_text[-1:].isspace()), (vi, en_text)


def test_vietnamese_is_identity_and_unknown_language_falls_back():
    assert i18n.language() == "vi"
    s = "▶ 1. Quét phụ đề"
    assert i18n.tr(s) is s and i18n.tr(None) is None and i18n.tr(5) == 5
    assert i18n.set_language("fr") == "vi" and i18n.set_language("EN") == "en" and i18n.set_language(None) == "vi"
    assert i18n.translate_to("en", "Sẵn sàng.") == "Ready." and i18n.language() == "vi"


@pytest.mark.parametrize("vi, expected", [
    ("Lưu cài đặt", "Save settings"),
    ("▶ 1. Quét phụ đề", "▶ 1. Scan subtitles"),  # ky hieu + so buoc o dau
    ("✓ 2. Đổi tên", "✓ 2. Rename"),
    ("Sang ③ Mux →", "Go to ③ Mux →"),  # cau KHONG DAU
    ("Xong: Kiểm tra công cụ.", "Done: Checking tools."),  # gia tri chen vao cung duoc dich
    ("Đang chạy: Lập kế hoạch mux…", "Running: Planning the mux…"),
    ("  [!] File không có audio track nào.", "  [!] The file has no audio track."),
    ("KẾ HOẠCH: 3 sẵn sàng | 0 cần chọn | 1 bỏ qua | 0 lỗi", "PLAN: 3 ready | 0 need choice | 1 skipped | 0 errors"),
    ("dùng file có sẵn (25 mốc khớp): a.srt", "using existing file (25 matching cues): a.srt"),
    ("file đã có en, cs → bỏ qua", "file already has en, cs → skipped"),  # hai manh noi vao nhau
    ("TỔNG KẾT: 2 phụ đề đã tải | 1 dùng file có sẵn | 0 không tìm được | 3 bỏ qua",
     "SUMMARY: 2 subtitles downloaded | 1 existing files used | 0 not found | 3 skipped"),
    ("15 dòng · 1 cảnh báo · 2 lỗi", "15 lines · 1 warnings · 2 errors"),
    ("0 TỐT | 1 CẦN XEM | 0 CÓ VẤN ĐỀ | 0 lỗi/từ chối", "0 GOOD | 1 REVIEW | 0 PROBLEM | 0 errors/rejected"),
    ("1 thư mục mới, 2 tạo, 2 vào Thùng rác", "1 new folders, 2 created, 2 moved to the Recycle Bin"),
    ("Cần chọn: có 2 audio tiếng Anh (#1, #2) — tick track muốn xoá",
     "Needs choice: 2 English audio tracks (#1, #2) — tick the tracks to remove"),
    ("      [v] Khớp thời gian, lệch +0.1s (bước ② sẽ đồng bộ)", "      [v] Timing matches, offset +0.1s (step ② will sync it)"),
    ("[i] Ràng buộc framerate: BẬT (cho phép bản không khai báo fps).",
     "[i] Framerate constraint: ON (releases without declared fps are allowed)."),
    ("phim đã có (trong MKV: track 2 SRT) — không tìm", "video already has it (inside the MKV: track 2 SRT) — not searching"),
    ("Dòng 1\nSẵn sàng.", "Dòng 1\nReady."),  # tung dong mot; dong khong dich duoc giu nguyen
])
def test_english_translation_of_real_sentences(en, vi, expected):
    assert i18n.tr(vi) == expected


def test_file_names_and_untranslatable_text_are_left_alone(en):
    for s in ("Phim Thử (2020).mkv", "[YTS.MX] Phim Thử (2020)", "C:\\Phim\\Tên Tập.S01E05.vi.srt", "Show.S01E01.1080p.WEB.mkv",
              "37% · 3/8", "", "en"):
        assert i18n.tr(s) == s
    assert i18n.tr("Thư mục phim: C:\\Phim Thử") == "Video folder: C:\\Phim Thử"  # chi dich phan nhan


def test_fill_supports_sequential_and_numbered_slots():
    assert i18n.fill("{} of {}", ["a", "b"]) == "a of b"
    assert i18n.fill("{1} then {0}", ["a", "b"]) == "b then a"
    assert i18n_build.slots_ok("{} và {}", "{1} and {0}") and not i18n_build.slots_ok("{} và {}", "{} only")
    assert not i18n_build.slots_ok("{}", "{0} {1}")
