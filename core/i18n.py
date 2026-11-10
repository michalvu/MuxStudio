"""Hai ngôn ngữ giao diện: Tiếng Việt (gốc) và English (bản 1.2.0).

Mã nguồn giữ nguyên chữ tiếng Việt — mọi phép so sánh, tên trạng thái, nhật ký hoàn tác… chạy Y NHƯ NHAU ở cả hai ngôn
ngữ. Việc dịch chỉ xảy ra ở CHỖ HIỂN THỊ (nhãn, nút, ô bảng, hộp thoại, dòng Nhật ký): ``tr(chữ)``.

Bảng dịch ``i18n_en.EN``: ``{mẫu tiếng Việt: mẫu tiếng Anh}``. Mẫu là câu trong mã nguồn, mỗi chỗ chèn giá trị viết
``{}`` (do ``tools/i18n_extract.py`` rút ra). ``tr`` nhận câu ĐÃ GHÉP giá trị nên phải so ngược với mẫu:

1. trùng nguyên văn một mẫu không có ``{}``;
2. khớp một mẫu có ``{}`` (chọn mẫu có phần chữ cố định dài nhất) → các giá trị được dịch tiếp (đệ quy) rồi ghép vào
   mẫu tiếng Anh;
3. không khớp cả câu → tách theo dòng, bỏ ký hiệu đầu / cuối (``▶ 1. …``, ``[!] …``), tách theo ``" | "``, ``"; "``… và
   dịch từng phần.

Không dịch được thì giữ nguyên tiếng Việt (không bao giờ làm hỏng chữ). Tiếng Việt: ``tr`` trả nguyên văn, không tốn gì.
"""
from __future__ import annotations

import os
import re
from typing import Iterable

LANGUAGES = ("vi", "en")
_lang = "vi"
_VI = re.compile("[àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ"
                 "ÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬĐÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ]")
_SLOT = re.compile(r"\{(\d*)\}")
_SEPS = ("  |  ", " | ", "; ", " — ", " · ", " → ", ": ", ", ", " - ", " (")
_EDGE = re.compile(r"^([^\w(\"']*(?:\d+\. )?)(.*?)([^\w)\"'%]*)$", re.S)
_MAX_CACHE = 20000


def has_vietnamese(text: str) -> bool:
    return bool(_VI.search(text))


def language() -> str:
    return _lang


def normalize(code: object) -> str:
    c = str(code or "").strip().lower()
    return c if c in LANGUAGES else "vi"


def set_language(code: object) -> str:
    """Đặt ngôn ngữ hiển thị (``vi`` / ``en``; giá trị lạ → ``vi``). Trả ngôn ngữ đang dùng."""
    global _lang
    _lang = normalize(code)
    _cache.clear()
    return _lang


class _Table:
    """Bảng dịch đã lập chỉ mục cho một ngôn ngữ."""

    def __init__(self, entries: dict[str, str]):
        self.exact: dict[str, str] = {}
        self.patterns: list[tuple[int, int, str, re.Pattern[str], str]] = []  # (-dai chu co dinh, so cho, khoa loc, regex, en)
        for vi, en in entries.items():
            if not en:
                continue
            if "{}" not in vi:
                self.exact[vi] = en
                continue
            lits = vi.split("{}")
            fixed = sum(len(x) for x in lits)
            if not fixed:
                continue
            rx = re.compile("^" + "(.*?)".join(re.escape(x) for x in lits) + "$", re.S)
            self.patterns.append((-fixed, len(lits) - 1, max(lits, key=len), rx, en))
        self.patterns.sort(key=lambda p: (p[0], p[1]))

    def match(self, text: str) -> tuple[str, tuple[str, ...]] | None:
        for _neg, _n, key, rx, en in self.patterns:
            if key in text:
                m = rx.match(text)
                if m:
                    return en, m.groups()
        return None


_tables: dict[str, _Table] = {}
_cache: dict[str, str] = {}
missing: set[str] = set()  # cau co chu tieng Viet ma khong dich duoc (de kiem tra khi phat trien)


def _table() -> _Table:
    t = _tables.get(_lang)
    if t is None:
        from . import i18n_en  # noqa: PLC0415

        t = _tables[_lang] = _Table(i18n_en.EN)
    return t


def fill(template: str, args: Iterable[str]) -> str:
    """Ghép giá trị vào mẫu tiếng Anh: ``{}`` lần lượt, hoặc ``{0}`` ``{1}`` để đổi thứ tự."""
    vals = list(args)
    it = iter(range(len(vals)))

    def one(m: re.Match[str]) -> str:
        i = int(m.group(1)) if m.group(1) else next(it, -1)
        return vals[i] if 0 <= i < len(vals) else ""

    return _SLOT.sub(one, template)


def _whole(text: str, depth: int) -> str | None:
    t = _table()
    hit = t.exact.get(text)
    if hit is not None:
        return hit
    if not _VI.search(text):
        return None
    m = t.match(text)
    if m is None:
        return None
    en, groups = m
    return fill(en, [_tr(g, depth + 1) if g else g for g in groups])


def _tr(text: str, depth: int = 0) -> str:
    if not text or depth > 6:
        return text
    got = _cache.get(text)
    if got is not None:
        return got
    out = _whole(text, depth)
    if out is None:
        out = _pieces(text, depth)
    if len(_cache) > _MAX_CACHE:
        _cache.clear()
    _cache[text] = out
    return out


def _pieces(text: str, depth: int) -> str:
    if "\n" in text:
        return "\n".join(_tr(x, depth + 1) for x in text.split("\n"))
    m = _EDGE.match(text)
    if m and (m.group(1) or m.group(3)) and m.group(2):
        core = _whole(m.group(2), depth)
        if core is not None:
            return m.group(1) + core + m.group(3)
    if not _VI.search(text):
        return text
    for sep in _SEPS:
        if sep in text:
            parts = text.split(sep)
            done = [_tr(p, depth + 1) for p in parts]
            if done != parts:
                return sep.join(done)
    if m and (m.group(1) or m.group(3)) and m.group(2) != text:
        core2 = _tr(m.group(2), depth + 1)
        if core2 != m.group(2):
            return m.group(1) + core2 + m.group(3)
    missing.add(text)
    return text


def tr(text: object) -> object:
    """Dịch một câu để HIỂN THỊ. Không phải chuỗi / đang dùng tiếng Việt → trả nguyên."""
    if _lang == "vi" or not isinstance(text, str) or not text:
        return text
    return _tr(text)


def tr_all(items: Iterable[object]) -> list[object]:
    return [tr(x) for x in items]


def init_from_env() -> None:
    """``MUXSTUDIO_LANG=en`` ép ngôn ngữ (dùng khi thử nghiệm)."""
    v = os.environ.get("MUXSTUDIO_LANG")
    if v:
        set_language(v)
