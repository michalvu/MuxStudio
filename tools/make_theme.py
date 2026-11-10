"""Tạo ``assets/glass.json`` — bộ màu "kính" của MuxStudio (bản 1.2.0) cho CustomTkinter.

Lấy theme ``blue`` đi kèm CustomTkinter làm khung (để không thiếu khoá nào) rồi đè màu / độ bo góc:
nền cửa sổ xanh xám nhạt, các khung "kính mờ" sáng hơn nền một bậc có viền mảnh, nút bo tròn dạng viên thuốc,
màu nhấn xanh dương. Mỗi màu là cặp (sáng, tối). Chạy lại khi muốn chỉnh màu:  python tools/make_theme.py
"""
from __future__ import annotations

import json
import os

import customtkinter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ACCENT = ["#0a84ff", "#0a84ff"]
ACCENT_HOVER = ["#0060df", "#409cff"]
TEXT = ["#1c1c1e", "#f2f2f7"]
TEXT_DIM = ["#8e8e93", "#8e8e93"]
WINDOW = ["#dde5ef", "#15181d"]  # nen cua so (lop duoi cung)
GLASS = ["#eef2f7", "#20242b"]  # khung "kinh" lop 1
GLASS_TOP = ["#f8fafc", "#2a2f38"]  # khung long ben trong (lop 2)
EDGE = ["#ffffff", "#3a414d"]  # vien sang manh cua tam kinh
FIELD = ["#ffffff", "#181b20"]  # o nhap
FIELD_EDGE = ["#c5cfdb", "#3d4450"]
NEUTRAL = ["#c9d2dd", "#3a404a"]  # nut / thanh trung tinh
NEUTRAL_HOVER = ["#b7c2cf", "#4a515d"]

OVERRIDES: dict[str, dict] = {
    "CTk": {"fg_color": WINDOW},
    "CTkToplevel": {"fg_color": WINDOW},
    "CTkFrame": {"corner_radius": 14, "border_width": 1, "fg_color": GLASS, "top_fg_color": GLASS_TOP, "border_color": EDGE},
    "CTkButton": {"corner_radius": 14, "border_width": 0, "fg_color": ACCENT, "hover_color": ACCENT_HOVER,
                  "border_color": EDGE, "text_color": ["#ffffff", "#ffffff"], "text_color_disabled": ["#e3e8ee", "#8a909a"]},
    "CTkLabel": {"corner_radius": 0, "fg_color": "transparent", "text_color": TEXT},
    "CTkEntry": {"corner_radius": 10, "border_width": 1, "fg_color": FIELD, "border_color": FIELD_EDGE, "text_color": TEXT,
                 "placeholder_text_color": TEXT_DIM},
    "CTkCheckBox": {"corner_radius": 7, "border_width": 2, "fg_color": ACCENT, "border_color": ["#8f9aa8", "#7b8491"],
                    "hover_color": ACCENT_HOVER, "checkmark_color": ["#ffffff", "#ffffff"], "text_color": TEXT,
                    "text_color_disabled": TEXT_DIM},
    "CTkSwitch": {"fg_color": NEUTRAL, "progress_color": ACCENT, "button_color": ["#ffffff", "#e6e6ea"],
                  "button_hover_color": ["#f0f0f0", "#ffffff"], "text_color": TEXT, "text_color_disabled": TEXT_DIM},
    "CTkRadioButton": {"fg_color": ACCENT, "border_color": ["#8f9aa8", "#7b8491"], "hover_color": ACCENT_HOVER,
                       "text_color": TEXT, "text_color_disabled": TEXT_DIM},
    "CTkProgressBar": {"corner_radius": 1000, "border_width": 0, "fg_color": NEUTRAL, "progress_color": ["#2e9e44", "#3fbf5a"]},
    "CTkSlider": {"fg_color": NEUTRAL, "progress_color": ACCENT, "button_color": ACCENT, "button_hover_color": ACCENT_HOVER},
    "CTkOptionMenu": {"corner_radius": 12, "fg_color": ACCENT, "button_color": ["#0060df", "#0060df"],
                      "button_hover_color": ACCENT_HOVER, "text_color": ["#ffffff", "#ffffff"],
                      "text_color_disabled": ["#e3e8ee", "#8a909a"]},
    "CTkComboBox": {"corner_radius": 10, "border_width": 1, "fg_color": FIELD, "border_color": FIELD_EDGE,
                    "button_color": NEUTRAL, "button_hover_color": NEUTRAL_HOVER, "text_color": TEXT,
                    "text_color_disabled": TEXT_DIM},
    "CTkScrollbar": {"corner_radius": 1000, "border_spacing": 4, "fg_color": "transparent", "button_color": NEUTRAL,
                     "button_hover_color": NEUTRAL_HOVER},
    "CTkSegmentedButton": {"corner_radius": 14, "border_width": 3, "fg_color": NEUTRAL, "selected_color": ACCENT,
                           "selected_hover_color": ACCENT_HOVER, "unselected_color": NEUTRAL,
                           "unselected_hover_color": NEUTRAL_HOVER, "text_color": TEXT, "text_color_disabled": TEXT_DIM},
    "CTkTextbox": {"corner_radius": 12, "border_width": 1, "fg_color": FIELD, "border_color": FIELD_EDGE, "text_color": TEXT,
                   "scrollbar_button_color": NEUTRAL, "scrollbar_button_hover_color": NEUTRAL_HOVER},
    "CTkScrollableFrame": {"label_fg_color": GLASS_TOP},
    "DropdownMenu": {"fg_color": GLASS_TOP, "hover_color": NEUTRAL, "text_color": TEXT},
}


def build() -> dict:
    base = os.path.join(os.path.dirname(customtkinter.__file__), "assets", "themes", "blue.json")
    with open(base, encoding="utf-8") as fh:
        theme = json.load(fh)
    for widget, vals in OVERRIDES.items():
        theme.setdefault(widget, {}).update(vals)
    return theme


def main() -> None:
    out = os.path.join(ROOT, "assets", "glass.json")
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(build(), fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print("Đã ghi", out)


if __name__ == "__main__":
    main()
