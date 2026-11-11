"""Titulky.com — nguồn phụ đề TIẾNG SÉC cho bước ① (bản 1.3.0, TĐ-40). Trang KHÔNG có API: phải đọc trang web.

ĐÃ KIỂM CHỨNG với trang thật ngày 2026-11-11, không đăng nhập (mẫu HTML thật đã cắt gọn: ``tests/data/titulky/``):

* Tìm: ``GET https://www.titulky.com/?Fulltext=<chữ>`` → bảng ``class="soupis"`` (50 dòng một trang). Cột: Název (link
  ``<Tên>-<ID>.htm``), V. (bản release nằm trong ``title`` của ``<a class="listTip">``), Seriál (``S01E05``), Rok,
  Staž. (lượt tải), Jazyk (``<img alt="CZ">`` / ``SK`` / trống), Uložil.
* Chi tiết: ``GET /<Tên>-<ID>.htm`` → ``FPS: 23,976``, ``Počet CD: 1``, ``typ titulků: srt``, bản release
  (``itemprop='alternateName'``), link IMDb.
* Xem trước: ``GET /nahled.php?Titulky=<ID>`` → ``<div id="nahled_zdroj">`` chứa các dòng SRT đầu (có mốc thời gian)
  ⇒ ước tính độ lệch TRƯỚC khi tải, không tốn lượt tải.
* Tải: ``GET /idown.php?R=<giờ unix>&titulky=<ID>&zip=z&histstamp=`` → trang "Zabezpečené stažení titulků". Vượt giới
  hạn theo địa chỉ IP thì trang có ảnh CAPTCHA (JPEG 400×100, ``./captcha/captcha.php``) + form ``POST idown.php`` với
  ``downkod`` (mã bạn gõ), ``securedown=2``, ``zip``, ``T``, ``titulky``, ``histstamp``.
* ``downfnc.js``: ``CountDown(t)`` mỗi nhịp **1,5 giây**; link tải là ``<a id="downlink">``; trang ghi rõ link trực tiếp
  không chạy nếu bỏ qua thời gian chờ ⇒ phải chờ đủ.
* Form đăng nhập: ``POST https://www.titulky.com/`` với ``Login``, ``Password``, ``foreverlog=1``, ``Detail2=''``,
  ``prihlasit='Přihlásit'``.

CHƯA kiểm chứng được (cần tài khoản / cần người gõ CAPTCHA) — người dùng chạy ``tools/probe_titulky.py`` để xác nhận:

* dấu hiệu đăng nhập thành công (đang dùng: trang trả về không còn ô ``id="log_login"``);
* trang sau khi gõ đúng CAPTCHA, hoặc khi còn lượt tải (đang dựa vào ``downfnc.js``: ``CountDown(n)`` + ``downlink``);
* file trả về (đang nhận: ZIP bắt đầu ``PK``, hoặc file phụ đề trần).

Quy tắc của MuxStudio vẫn giữ: không sửa nội dung phụ đề ở bước ① (chép nguyên byte — đổi bảng mã là việc của bước ②),
không tự vượt CAPTCHA (bạn gõ mã), chờ đủ thời gian trang yêu cầu, nghỉ giữa các lượt gọi.
"""
from __future__ import annotations

import html as _html
import os
import re
import tempfile
import time
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable
from urllib.parse import urljoin

from . import tools
from .subsource import pack_files, safe_extract
from .tmdb import ConnStatus

try:
    import requests
    _NET_ERR: tuple[type[BaseException], ...] = (requests.RequestException,)
except Exception:  # pragma: no cover
    requests = None  # type: ignore[assignment]
    _NET_ERR = (OSError,)

BASE = "https://www.titulky.com/"
#: Tên trình khách trung thực (trang thật chấp nhận — đã thử 2026-11-11); không giả làm trình duyệt.
USER_AGENT = "Mozilla/5.0 (compatible; MuxStudio; +https://github.com/michalvu/MuxStudio)"
#: ``CountDown(t)`` trong ``downfnc.js`` của trang: mỗi nhịp 1,5 giây
COUNTDOWN_STEP = 1.5
LANG = "cs"
#: Hàm hỏi mã CAPTCHA: ``(ảnh JPEG, ghi chú) -> mã`` (``None`` / rỗng = bạn bỏ qua)
CaptchaFn = Callable[[bytes, str], "str | None"]


# ======================================================================
#  Đọc HTML (hàm thuần — test bằng mẫu HTML thật)
# ======================================================================

_TAG = re.compile(r"<[^>]+>")
_WS = re.compile(r"\s+")


def _text(fragment: str) -> str:
    """Chữ hiển thị của một đoạn HTML (bỏ thẻ, ``&nbsp;`` → khoảng trắng, gộp khoảng trắng)."""
    return _WS.sub(" ", _html.unescape(_TAG.sub(" ", fragment)).replace("\xa0", " ")).strip()


def _int(s: str) -> int | None:
    d = re.sub(r"\D", "", s or "")
    return int(d) if d else None


@dataclass
class SearchRow:
    """Một dòng của bảng kết quả tìm kiếm."""

    id: int
    title: str
    href: str  # '<Tên>-<ID>.htm'
    release: str = ""
    series: str = ""  # 'S01E05' ('' = phim lẻ / không ghi)
    year: int | None = None
    downloads: int = 0
    lang: str = ""  # 'CZ' | 'SK' | '' (không ghi)
    # điền thêm khi đã xem trang chi tiết
    fps: str = ""
    cds: int = 0
    kind: str = ""  # 'srt' / 'sub' …
    imdb: str = ""

    @property
    def url(self) -> str:
        return urljoin(BASE, self.href)


#: Tiêu đề cột của bảng ``soupis`` → tên trường (cột có thể thêm / đổi chỗ khi đăng nhập ⇒ dò theo tiêu đề)
_HEADERS = {"název": "title", "nazev": "title", "v.": "release", "seriál": "series", "serial": "series", "rok": "year",
            "staž.": "downloads", "staz.": "downloads", "jazyk": "lang", "uložil": "uploader", "ulozil": "uploader"}
_DEFAULT_COLS = {"title": 0, "release": 1, "series": 2, "year": 3, "downloads": 4, "lang": 5}


def parse_search(page: str) -> list[SearchRow]:
    """Bảng kết quả ``class="soupis"`` → danh sách dòng (mọi ngôn ngữ; lọc ``CZ`` là việc của nơi gọi)."""
    m = re.search(r'<table[^>]*class="[^"]*\bsoupis\b[^"]*"[^>]*>(.*?)</table>', page or "", re.S | re.I)
    if not m:
        return []
    body = m.group(1)
    cols = dict(_DEFAULT_COLS)
    head = re.search(r"<tr[^>]*>(.*?)</tr>", body, re.S | re.I)
    if head and "detailh" in head.group(1):
        found = {}
        for i, cell in enumerate(re.findall(r"<td[^>]*>(.*?)</td>", head.group(1), re.S | re.I)):
            name = _HEADERS.get(_text(cell).lower())
            if name:
                found[name] = i
        if "title" in found:
            cols = found
    out: list[SearchRow] = []
    for tr in re.findall(r'<tr[^>]*class="r1?"[^>]*>(.*?)</tr>', body, re.S | re.I):
        cells = re.findall(r"<td[^>]*>(.*?)</td>", tr, re.S | re.I)

        def cell(name: str) -> str:
            i = cols.get(name, -1)
            return cells[i] if 0 <= i < len(cells) else ""

        link = re.search(r'<a[^>]*href="([^"]*?-(\d+)\.htm)"[^>]*>(.*?)</a>', cell("title"), re.S | re.I)
        if not link:
            continue
        rel = re.search(r'title="([^"]*)"', cell("release"))
        lang = re.search(r'alt="([A-Za-z]{2})"', cell("lang"))
        year = _int(_text(cell("year")))
        out.append(SearchRow(int(link.group(2)), _text(link.group(3)), _html.unescape(link.group(1)).lstrip("/"),
                             _html.unescape(rel.group(1)).strip() if rel else "", _text(cell("series")).upper(),
                             year if year and 1880 <= year <= 2100 else None, _int(_text(cell("downloads"))) or 0,
                             lang.group(1).upper() if lang else ""))
    return out


def parse_detail(page: str) -> dict[str, Any]:
    """Trang chi tiết → ``fps`` ('23.976' / ''), ``cds`` (0 = không ghi), ``kind`` ('srt'…), ``release``, ``imdb``."""
    page = page or ""

    def grab(pattern: str) -> str:
        m = re.search(pattern, page, re.S | re.I)
        return _text(m.group(1)) if m else ""

    fps = grab(r"FPS:\s*</span>\s*([\d][\d,\.]*)").replace(",", ".")
    return {"fps": fps, "cds": _int(grab(r"Počet CD:\s*</span>\s*(\d+)")) or 0,
            "kind": grab(r"typ titulků:\s*</span>\s*([A-Za-z0-9]+)").lower(),
            "release": grab(r"itemprop=['\"]alternateName['\"][^>]*>([^<]*)<"),
            "imdb": grab(r"imdb\.com/title/(tt\d+)")}


def parse_preview(page: str) -> str:
    """Trang xem trước (``nahled.php``) → chữ SRT (để đọc mốc thời gian bằng ``srt.preview_times``)."""
    m = re.search(r'<div[^>]*id="nahled_zdroj"[^>]*>(.*?)</div>', page or "", re.S | re.I)
    if not m:
        return ""
    return _html.unescape(_TAG.sub("", re.sub(r"<br\s*/?>", "\n", m.group(1), flags=re.I)))


@dataclass
class DownloadPage:
    """Trang ``idown.php``: có CAPTCHA, có link (sau thời gian chờ), hoặc chỉ có thông báo."""

    captcha: str = ""  # dia chi anh CAPTCHA ('' = khong co)
    form: dict[str, str] = field(default_factory=dict)  # cac o cua form gui ma CAPTCHA
    link: str = ""  # link tai that ('' = chua co)
    wait: int = 0  # so nhip CountDown
    message: str = ""  # chu hien thi cua trang (de bao loi)


def parse_download_page(page: str) -> DownloadPage:
    page = page or ""
    out = DownloadPage()
    body = re.search(r"<body[^>]*>(.*)", page, re.S | re.I)
    out.message = _text(re.sub(r"<(script|style)[^>]*>.*?</\1>", " ", body.group(1) if body else page, flags=re.S | re.I))
    cap = re.search(r'<img[^>]*src="([^"]*captcha[^"]*)"', page, re.I)
    if cap:
        out.captcha = _html.unescape(cap.group(1))
    form = re.search(r'<form[^>]*name="downform"[^>]*>(.*?)</form>', page, re.S | re.I)
    if form:
        for tag in re.findall(r"<input[^>]*>", form.group(1), re.I):
            name = re.search(r'name="([^"]*)"', tag)
            if not name:
                continue
            value = re.search(r'value="([^"]*)"', tag)
            out.form[name.group(1)] = _html.unescape(value.group(1)) if value else ""
    link = (re.search(r'<a[^>]*id="downlink"[^>]*href="([^"]+)"', page, re.I)
            or re.search(r'<a[^>]*href="([^"]+)"[^>]*id="downlink"', page, re.I))
    if link:
        out.link = _html.unescape(link.group(1))
    wait = re.search(r"CountDown\(\s*(\d+)\s*\)", page)
    if wait:
        out.wait = int(wait.group(1))
    return out


def has_login_form(page: str) -> bool:
    """Trang còn ô đăng nhập = CHƯA đăng nhập (đã xem trang thật: ``<input id="log_login" name="Login">``)."""
    return bool(re.search(r'id="log_login"|name="Login"', page or ""))


def looks_like_html(data: bytes) -> bool:
    head = data.lstrip(b"\xef\xbb\xbf \t\r\n")[:16].lower()
    return head.startswith((b"<!doctype", b"<html", b"<head", b"<body", b"<?xml", b"<br", b"<div", b"<b>", b"<h"))


def search_query(title: str, season: int | None = None, episode: int | None = None) -> str:
    """Chuỗi tìm: phim bộ → ``Tên S01E05`` (trang ghi tập đúng kiểu này: tìm "Breaking Bad S01E05" ra đúng 4 dòng)."""
    t = _WS.sub(" ", title or "").strip()
    if season is not None and episode is not None:
        return f"{t} S{int(season):02d}E{int(episode):02d}"
    return t


# ======================================================================
#  Client
# ======================================================================


class TitulkyClient:
    def __init__(self, username: str = "", password: str = "", pause_ms: int = 1100, http: Any = None,
                 sleep: Callable[[float], None] = time.sleep, job: tools.Job | None = None,
                 temp_dir: str | os.PathLike[str] | None = None, clock: Callable[[], float] = time.monotonic,
                 now: Callable[[], float] = time.time, user_agent: str = USER_AGENT):
        self.username = (username or "").strip()
        self.password = password or ""
        self.pause = max(0, int(pause_ms)) / 1000.0
        self.http = http if http is not None else (requests.Session() if requests else None)
        self.sleep = sleep
        self.job = job or tools.NULL_JOB
        self.temp_dir = Path(temp_dir) if temp_dir else None
        self.clock = clock
        self.now = now
        self.user_agent = user_agent or USER_AGENT
        self.logged_in: bool | None = None  # None = khong dung tai khoan
        self.disabled = False
        self.captcha_declined = False  # ban da bo qua mot lan -> khong hoi nua trong luot nay
        self.last_status = 0
        self._last_call: float | None = None
        self._search_cache: dict[str, list[SearchRow]] = {}
        self._detail_done: set[int] = set()
        self._preview_cache: dict[int, str] = {}
        self._down_cache: dict[int, Path | None] = {}

    @property
    def has_account(self) -> bool:
        return bool(self.username and self.password)

    # ------------------------------------------------------------------ goi trang
    def _pace(self) -> None:
        if self._last_call is not None and self.pause > 0:
            wait = self.pause - (self.clock() - self._last_call)
            if wait > 0:
                self.sleep(wait)
        self._last_call = self.clock()

    def _request(self, method: str, url: str, params: dict | None = None, data: dict | None = None,
                 referer: str | None = None, timeout: int = 30) -> Any:
        """Một lượt gọi (nghỉ ``pause`` giữa các lượt; lỗi mạng thử 3 lần). ``None`` = không lấy được."""
        if self.http is None or self.disabled:
            return None
        headers = {"User-Agent": self.user_agent, "Referer": referer or BASE, "Accept-Language": "cs,en;q=0.7"}
        for attempt in range(1, 4):
            self.job.check()
            self._pace()
            try:
                r = self.http.request(method, url, params=params, data=data, headers=headers, timeout=timeout)
            except _NET_ERR as e:
                if attempt < 3:
                    self.sleep(2 * attempt)
                    continue
                self.job.error(f"    [!] Titulky lỗi mạng: {e}")
                return None
            self.last_status = r.status_code
            if r.status_code == 200:
                return r
            if r.status_code in (429, 502, 503) and attempt < 3:
                self.job.dim(f"    [i] Titulky đang bận (HTTP {r.status_code}), chờ {5 * attempt} giây...")
                self.sleep(5 * attempt)
                continue
            self.job.warn(f"    [!] Titulky trả về HTTP {r.status_code}.")
            return None
        return None

    # ------------------------------------------------------------------ dang nhap
    def login(self, quiet: bool = False) -> bool:
        """Đăng nhập (tuỳ chọn). Không có tài khoản → ``False`` và vẫn dùng được kiểu khách."""
        if not self.has_account:
            self.logged_in = None
            return False
        data = {"Login": self.username, "Password": self.password, "foreverlog": "1", "Detail2": "",
                "prihlasit": "Přihlásit"}
        r = self._request("POST", BASE, data=data)
        ok = False
        if r is not None:
            page = r.text or ""
            if "BadLogin" not in page and "BadLogin" not in str(getattr(r, "url", "")):
                ok = not has_login_form(page)
                if not ok:  # trang tra ve co the la trang trung gian: xem lai trang chu mot lan
                    r2 = self._request("GET", BASE)
                    ok = r2 is not None and not has_login_form(r2.text or "")
        self.logged_in = ok
        if ok and not quiet:
            self.job.info("[i] Titulky: đã đăng nhập.")
        elif not ok and not quiet:
            self.job.warn("[!] Đăng nhập Titulky thất bại — kiểm tra tên đăng nhập / mật khẩu. Vẫn tìm và tải được kiểu "
                          "khách (hay phải gõ CAPTCHA).")
        return ok

    # ------------------------------------------------------------------ tim
    def search(self, query: str) -> list[SearchRow]:
        """Tìm theo chữ (một trang, tối đa 50 dòng). Có nhớ đệm trong lượt chạy."""
        q = _WS.sub(" ", query or "").strip()
        if not q:
            return []
        key = q.lower()
        if key in self._search_cache:
            return self._search_cache[key]
        r = self._request("GET", BASE, params={"Fulltext": q})
        rows = parse_search(r.text) if r is not None else []
        if r is not None:
            self._search_cache[key] = rows
        return rows

    def fill_detail(self, row: SearchRow) -> SearchRow:
        """Xem trang chi tiết MỘT lần cho dòng này: FPS, số CD, loại file, bản release (nếu bảng không ghi), IMDb."""
        if row.id in self._detail_done:
            return row
        r = self._request("GET", row.url)
        if r is None:
            return row
        self._detail_done.add(row.id)
        d = parse_detail(r.text)
        row.fps, row.cds, row.kind, row.imdb = d["fps"], d["cds"], d["kind"], d["imdb"]
        if d["release"] and not row.release:
            row.release = d["release"]
        return row

    def preview(self, row: SearchRow) -> str:
        """Chữ SRT của bản xem trước ('' nếu không có). Không tốn lượt tải."""
        if row.id in self._preview_cache:
            return self._preview_cache[row.id]
        r = self._request("GET", urljoin(BASE, "nahled.php"), params={"Titulky": row.id, "histstamp": ""}, referer=row.url)
        if r is None:
            return ""
        txt = parse_preview(r.text)
        self._preview_cache[row.id] = txt
        return txt

    # ------------------------------------------------------------------ tai
    def _workdir(self) -> Path:
        if self.temp_dir is None:
            self.temp_dir = Path(tempfile.mkdtemp(prefix="muxstudio_titulky_"))
        self.temp_dir.mkdir(parents=True, exist_ok=True)
        return self.temp_dir

    def _wait_countdown(self, ticks: int) -> None:
        """Chờ đủ thời gian trang yêu cầu (``CountDown(n)``: n nhịp × 1,5 s). Bấm "Dừng ngay" là thoát."""
        total = max(0.0, ticks * COUNTDOWN_STEP) + (0.5 if ticks > 0 else 0.0)
        if total <= 0:
            return
        self.job.dim(f"      (Titulky yêu cầu chờ {round(total)} giây trước khi tải)")
        left = total
        while left > 0:
            self.job.check()
            step = min(0.5, left)
            self.sleep(step)
            left -= step

    def _store(self, sub_id: int, r: Any) -> Path | None:
        """Lưu file trả về vào thư mục tạm riêng của ``sub_id`` (ZIP → giải nén; file trần → giữ NGUYÊN byte)."""
        data: bytes = r.content or b""
        out = self._workdir() / str(sub_id)
        if data[:2] == b"PK":
            zpath = self._workdir() / f"{sub_id}.zip"
            zpath.write_bytes(data)
            try:
                safe_extract(zpath, out)
            except (zipfile.BadZipFile, OSError) as e:
                self.job.error(f"      [!] Giải nén lỗi: {e}")
                return None
            return out
        if data[:4] == b"Rar!" or data[:2] == b"7z":
            self.job.warn("      [!] Titulky trả về file nén RAR / 7z — cần 7-Zip, bỏ qua.")
            return None
        if len(data) < 16:
            self.job.warn("      [!] File Titulky tải về rỗng.")
            return None
        name = ""
        cd = str((getattr(r, "headers", None) or {}).get("Content-Disposition") or "")
        m = re.search(r'filename\*?=(?:UTF-8\'\')?"?([^";]+)"?', cd, re.I)
        if m:
            name = os.path.basename(m.group(1).strip())
        if not re.search(r"\.(srt|ass|ssa|sub|vtt|txt)$", name, re.I):
            name = f"{sub_id}{'.ass' if b'[Script Info]' in data[:400] else '.srt'}"
        out.mkdir(parents=True, exist_ok=True)
        (out / name).write_bytes(data)
        return out

    def download(self, row: SearchRow, captcha: CaptchaFn | None = None) -> Path | None:
        """Tải MỘT phụ đề → thư mục tạm chứa file (ZIP đã giải nén). ``None`` = không tải được (đã ghi lý do).

        Gặp CAPTCHA thì gọi ``captcha(ảnh, ghi chú)`` để BẠN gõ mã (tối đa 3 lần gõ sai); bạn bỏ qua một lần thì các
        bản cần CAPTCHA sau đó trong lượt này không hỏi nữa."""
        sid = int(row.id)
        if sid in self._down_cache:
            return self._down_cache[sid]
        got = self._download(row, captcha)
        self._down_cache[sid] = got
        return got

    def _download(self, row: SearchRow, captcha: CaptchaFn | None) -> Path | None:
        sid = int(row.id)
        url = urljoin(BASE, "idown.php")
        r = self._request("GET", url, params={"R": int(self.now()), "titulky": sid, "zip": "z", "histstamp": ""},
                          referer=row.url)
        asked = 0
        while r is not None:
            data: bytes = r.content or b""
            if not looks_like_html(data):
                return self._store(sid, r)
            page = parse_download_page(r.text or "")
            if page.link:
                self._wait_countdown(page.wait)
                f = self._request("GET", urljoin(url, page.link), referer=url, timeout=90)
                if f is None:
                    return None
                if not looks_like_html(f.content or b""):
                    return self._store(sid, f)
                msg = parse_download_page(f.text or "").message
                self.job.warn(f"      [!] Titulky không trả file: {msg[:200]}")
                return None
            if page.captcha:
                if self.captcha_declined:
                    self.job.warn("      [!] Titulky đòi CAPTCHA (bạn đã bỏ qua trong lượt này) → bỏ bản này.")
                    return None
                if captcha is None:
                    self.job.warn("      [!] Titulky đòi CAPTCHA nhưng không có cửa sổ để nhập → bỏ bản này.")
                    return None
                if asked >= 3:
                    self.job.warn("      [!] Mã CAPTCHA sai 3 lần → bỏ bản này.")
                    return None
                img = self._request("GET", urljoin(url, page.captcha), referer=url)
                if img is None or not img.content:
                    self.job.warn("      [!] Không tải được ảnh CAPTCHA của Titulky.")
                    return None
                asked += 1
                note = row.release or row.title
                if asked > 1:
                    note += "\n(mã vừa gõ không đúng — thử lại)"
                code = captcha(img.content, note)
                if not code or not str(code).strip():
                    self.captcha_declined = True
                    self.job.warn("      [!] Bạn bỏ qua CAPTCHA → các bản Titulky cần CAPTCHA sẽ bị bỏ qua trong lượt này.")
                    return None
                form = dict(page.form) or {"securedown": "2", "zip": "z", "T": "", "titulky": str(sid), "histstamp": ""}
                form["downkod"] = str(code).strip()
                r = self._request("POST", url, data=form, referer=url)
                continue
            self.job.warn(f"      [!] Titulky không cho tải: {page.message[:240] or 'trang không có link tải'}")
            return None
        return None

    def subtitle_files(self, d: Path | None) -> list[Path]:
        """File phụ đề dùng được trong thư mục đã tải (``.srt`` / ``.ass`` / ``.ssa``)."""
        return pack_files(d) if d else []

    # ------------------------------------------------------------------ kiem tra ket noi
    def check(self) -> ConnStatus:
        if self.http is None:
            return ConnStatus(False, "Titulky: thiếu thư viện requests")
        r = self._request("GET", BASE)
        if r is None:
            return ConnStatus(False, f"Titulky: không kết nối được ({self.last_status or 'lỗi mạng'})")
        if not self.has_account:
            return ConnStatus(True, "Titulky: dùng kiểu khách (không tài khoản) — tìm được; tải hay phải gõ CAPTCHA")
        if self.login(quiet=True):
            return ConnStatus(True, "Titulky: đăng nhập được")
        return ConnStatus(False, "Titulky: đăng nhập lỗi — kiểm tra tên đăng nhập / mật khẩu")
