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
# Phan dau khong can dich: khoang trang, nhan trong ngoac vuong ("[!]", "[v]", "[T]"), ky hieu ("▶", "✓", "→"), so thu
# tu buoc ("1. ") hoac so dem ("2 ")
_HEAD = re.compile(r"^((?:\s*\[[^\]\s]{1,4}\])?[^\w(\"']*(?:\d+\. )?)(.+)$", re.S)
_TAIL = re.compile(r"^(.*?)([^\w\"'%]+)$", re.S)
_COUNT = re.compile(r"^(\d+ )(.+)$", re.S)
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
    """Bảng dịch đã lập chỉ mục cho một ngôn ngữ.

    Ngoài mẫu nguyên câu, mẫu bắt đầu / kết thúc bằng ký tự KHÔNG phải chữ-số (dấu cách, ngoặc, ``→``, ``:``…) còn được
    coi là MẢNH ghép: mã nguồn hay nối chúng với phần khác (``"file đã có " + ngôn ngữ``, ``câu + " → bỏ qua"``), nên
    phía đó được phép có thêm chữ (dịch tiếp bằng đệ quy)."""

    def __init__(self, entries: dict[str, str]):
        self.exact: dict[str, str] = {}
        # (do uu tien, khoa loc, regex, mau tieng Anh, co phan dau thua, co phan duoi thua)
        self.patterns: list[tuple[tuple[int, int, int], str, re.Pattern[str], str, bool, bool]] = []
        for vi, en in entries.items():
            if not en or not vi:
                continue
            lits = vi.split("{}")
            fixed = sum(len(x) for x in lits)
            if not fixed:
                continue
            body = "(.*?)".join(re.escape(x) for x in lits)
            key = max(lits, key=len)
            n = len(lits) - 1
            if n == 0:
                self.exact[vi] = en
            else:
                self.patterns.append(((-fixed, 0, n), key, re.compile("^" + body + "$", re.S), en, False, False))
            if len(vi.strip()) < 5:  # manh qua ngan ("còn ") de khop nham giua cau khac
                continue
            lead, trail = not vi[0].isalnum(), not vi[-1].isalnum()
            for a, b in {(lead, False), (False, trail), (lead, trail)} - {(False, False)}:
                rx = re.compile("^" + ("(.+)" if a else "") + body + ("(.+)" if b else "") + "$", re.S)
                self.patterns.append(((-fixed, 1 + a + b, n), key, rx, en, a, b))
        self.patterns.sort(key=lambda p: p[0])
        # mau KHONG DAU ("trong MKV: {}", "Xong: {}."): danh sach rieng, thu cho cau khong co chu co dau
        self.ascii_patterns = [p for p in self.patterns if not _VI.search(p[2].pattern) and not p[4] and not p[5]]

    def match(self, text: str, plain: bool = False) -> tuple[str, tuple[str, ...], str, str] | None:
        """→ (mẫu tiếng Anh, các giá trị, phần đầu thừa, phần đuôi thừa). ``plain`` = câu không có chữ có dấu."""
        for _prio, key, rx, en, a, b in (self.ascii_patterns if plain else self.patterns):
            if key in text:
                m = rx.match(text)
                if m:
                    g = m.groups()
                    head = g[0] if a else ""
                    tail = g[-1] if b else ""
                    return en, g[(1 if a else 0):(len(g) - 1 if b else len(g))], head, tail
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
    m = t.match(text, plain=not _VI.search(text))
    if m is None:
        return None
    en, groups, head, tail = m
    return (_tr(head, depth + 1) if head else "") + fill(en, [_tr(g, depth + 1) if g else g for g in groups]) + \
        (_tr(tail, depth + 1) if tail else "")


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
    # 1. bo phan dau khong can dich ("  [!] ", "▶ 1. ", "2 "), roi ca ky hieu cuoi (" →", "…", ".")
    head, core = "", text
    m = _HEAD.match(text)
    if m and m.group(1):
        head, core = m.group(1), m.group(2)
        got = _whole(core, depth)
        if got is not None:
            return head + got
    t = _TAIL.match(core)
    if t and t.group(1):
        got = _whole(t.group(1), depth)
        if got is not None:
            return head + got + t.group(2)
    if not _VI.search(text):
        return text
    # 2. phan TRUOC mot dau ngan cach la mot cau tron ven -> dich no, phan sau dich tiep
    cuts = sorted({i for sep in _SEPS for i in _find_all(core, sep)})
    for i in cuts:
        sep = next(x for x in _SEPS if core.startswith(x, i))
        got = _whole(core[:i], depth) if core[:i] else None
        if got is not None:
            return head + got + sep + _tr(core[i + len(sep):], depth + 1)
    # 3. "(…)" boc ngoai
    st = core.strip()
    if len(st) > 2 and st[0] == "(" and st[-1] == ")" and depth < 6:
        inner = _tr(st[1:-1], depth + 1)
        if inner != st[1:-1]:
            return head + core.replace(st, "(" + inner + ")", 1)
    if st.endswith(")") and depth < 6:  # "… (…)" o cuoi: dich rieng phan trong ngoac va phan truoc no
        level, k = 0, len(st) - 1
        while k >= 0:
            level += st[k] == ")"
            level -= st[k] == "("
            if level == 0:
                break
            k -= 1
        if k > 0:
            before, inner = st[:k], st[k + 1:-1]
            b2, i2 = _tr(before, depth + 1), _tr(inner, depth + 1)
            if b2 != before or i2 != inner:
                return head + core.replace(st, b2 + "(" + i2 + ")", 1)
    # 4. tach het theo mot dau ngan cach
    for sep in _SEPS:
        if sep in core:
            parts = core.split(sep)
            done = [_tr(p, depth + 1) for p in parts]
            if done != parts:
                return head + sep.join(done)
    if head:
        got2 = _tr(core, depth + 1)
        if got2 != core:
            return head + got2
    c = _COUNT.match(core)  # "2 tạo", "1 CẦN XEM": so dem + nhan
    if c:
        got3 = _tr(c.group(2), depth + 1)
        if got3 != c.group(2):
            return head + c.group(1) + got3
    missing.add(text)
    return text


def _find_all(text: str, sep: str) -> list[int]:
    out, i = [], text.find(sep)
    while i >= 0:
        out.append(i)
        i = text.find(sep, i + 1)
    return out


recorded: set[str] | None = None  # cong cu phat trien: gom moi cau di qua tr() (xem tools/i18n_residue.py)


def tr(text: object) -> object:
    """Dịch một câu để HIỂN THỊ. Không phải chuỗi / đang dùng tiếng Việt → trả nguyên."""
    if recorded is not None and isinstance(text, str) and text:
        recorded.add(text)
    if _lang == "vi" or not isinstance(text, str) or not text:
        return text
    return _tr(text)


def translate_to(code: str, text: str) -> str:
    """Dịch ``text`` sang ngôn ngữ ``code`` mà không đổi ngôn ngữ đang hiển thị (cho test / công cụ kiểm tra)."""
    global _lang
    old = _lang
    _lang = normalize(code)
    try:
        return text if _lang == "vi" else _tr(text)
    finally:
        _lang = old


def tr_all(items: Iterable[object]) -> list[object]:
    return [tr(x) for x in items]


def init_from_env() -> None:
    """``MUXSTUDIO_LANG=en`` ép ngôn ngữ (dùng khi thử nghiệm)."""
    v = os.environ.get("MUXSTUDIO_LANG")
    if v:
        set_language(v)
