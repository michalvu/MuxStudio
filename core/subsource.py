"""SubSource API (§6.11).

Thay ``Invoke-Sub``, ``Find-Entry``, ``Get-AllSubs``, ``Test-SubLang``, ``Get-SubList``,
``Get-LangSummary``, ``Get-SubtitlePack``, ``Select-FromPack`` (SubFetch).

Đã dò thực tế (theo .bat; kiểm lại bằng ``tools/probe_subsource.py``):
* Gốc ``https://api.subsource.net/api/v1``, header ``X-API-Key``; 60 lượt/phút → nghỉ ~1,1 s.
* Tìm: ``/movies/search?searchType=imdb&imdb=tt…`` (ưu tiên), dự phòng ``searchType=text&q=…``.
  Phim bộ: MỖI SEASON là một ``movieId`` riêng, có trường ``season``.
* Danh sách: ``/subtitles?movieId=…&page=N&limit=100`` (bị từ chối → 20), tối đa ``MaxPages`` trang.
  KHÔNG lọc phía máy chủ (``language``/``hearingImpaired``/``foreignParts``/``sort``): bản người
  dùng tự đăng để trống các trường đó nên bị loại oan (từng mất hết phụ đề tiếng Việt).
* Tải: ``/subtitles/{id}/download`` → ZIP (gói season có nhiều tập).
"""
from __future__ import annotations

import os
import re
import tempfile
import time
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Sequence

from . import episodes, naming, tools
from .tmdb import ConnStatus

try:
    import requests
    _NET_ERR: tuple[type[BaseException], ...] = (requests.RequestException,)
except Exception:  # pragma: no cover
    requests = None  # type: ignore[assignment]
    _NET_ERR = (OSError,)

DEFAULT_BASE = "https://api.subsource.net/api/v1"
SUB_EXTS = (".srt", ".ass", ".ssa")
RATE_HEADER_HINTS = ("ratelimit", "rate-limit", "retry-after")


class SubSourceClient:
    def __init__(self, api_key: str, base: str = DEFAULT_BASE, pause_ms: int = 1100, max_pages: int = 10,
                 http: Any = None, sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.monotonic, job: tools.Job | None = None,
                 cache_dir: str | os.PathLike[str] | None = None):
        self.api_key = (api_key or "").strip()
        self.base = (base or DEFAULT_BASE).rstrip("/")
        self.pause = max(0, int(pause_ms)) / 1000.0
        self.max_pages = max(1, int(max_pages))
        self.http = http if http is not None else (requests.Session() if requests else None)
        self.sleep = sleep
        self.clock = clock
        self.job = job or tools.NULL_JOB
        self.calls = 0
        self._last = -1e9
        self.last_status = 0
        self.last_headers: dict[str, str] = {}
        self._entry_cache: dict[Any, Any] = {}
        self._list_cache: dict[int, list[dict]] = {}
        self._zip_cache: dict[int, Path | None] = {}
        self._cache_dir = Path(cache_dir) if cache_dir else None

    @property
    def usable(self) -> bool:
        return bool(self.api_key)

    # ------------------------------------------------------------------ nhip goi
    def _pace(self) -> None:
        """Nghỉ giữa các lượt (trần 60/phút). Lượt đầu không nghỉ."""
        if self.calls > 0:
            wait = self.pause - (self.clock() - self._last)
            if wait > 0:
                self.sleep(wait)
        self.calls += 1
        self._last = self.clock()

    def _keep_headers(self, r: Any) -> None:
        self.last_status = r.status_code
        self.last_headers = {k: v for k, v in (r.headers or {}).items()
                             if any(h in k.lower() for h in RATE_HEADER_HINTS)}

    # ------------------------------------------------------------------ goi API
    def get(self, path: str, params: dict | None = None) -> Any:
        """``Invoke-Sub``: 429 → chờ 20 s; 4xx → báo và dừng; lỗi mạng → thử 3 lần."""
        if not self.usable:
            return None
        q = {k: str(v) for k, v in (params or {}).items() if v is not None and str(v) != ""}
        self._pace()
        for attempt in range(1, 4):
            try:
                r = self.http.request("GET", self.base + path, params=q, headers={"X-API-Key": self.api_key}, timeout=25)
            except _NET_ERR as e:
                if attempt < 3:
                    self.sleep(2 * attempt)
                else:
                    self.job.error(f"    [!] Lỗi mạng: {e}")
                continue
            self._keep_headers(r)
            if r.status_code == 200:
                try:
                    return r.json()
                except ValueError:
                    return None
            if r.status_code == 429:
                self.job.warn("    [!] Chạm trần rate limit, chờ 20 giây...")
                self.sleep(20)
                continue
            if 400 <= r.status_code < 500:
                msg = re.sub(r"\s+", " ", (r.text or ""))[:300]
                self.job.warn(f"    [!] API {r.status_code} : {msg}")
                return None
            if attempt < 3:
                self.sleep(2 * attempt)
            else:
                self.job.error(f"    [!] Lỗi máy chủ SubSource ({r.status_code})")
        return None

    # ------------------------------------------------------------------ tim muc phim
    def find_entry(self, clean_title: str, year: int | None, season: int | None, imdb: str | None = None) -> dict | None:
        """``Find-Entry``: tra IMDb trước, rồi theo tên; lọc season (phim bộ) / năm ±1 (phim lẻ);
        còn nhiều thì lấy giống tên nhất."""
        key = (clean_title, year, season, imdb)
        if key in self._entry_cache:
            return self._entry_cache[key]
        cands: list[dict] = []
        if imdb:
            r = self.get("/movies/search", {"searchType": "imdb", "imdb": imdb})
            if r and r.get("data"):
                cands = list(r["data"])
        if not cands and clean_title:
            r = self.get("/movies/search", {"searchType": "text", "q": clean_title})
            if r and r.get("data"):
                cands = list(r["data"])
        if not cands:
            self._entry_cache[key] = None
            return None
        if season is not None:
            by_season = [c for c in cands if _int(c.get("season")) == season]
            if not by_season:
                have = sorted({str(c.get("season")) for c in cands if c.get("season") is not None})
                self.job.warn(f"    [!] Không có season {season} trên SubSource (có: {', '.join(have)})")
                self._entry_cache[key] = None
                return None
            cands = by_season
        elif year:
            by_year = [c for c in cands if _int(c.get("releaseYear")) and abs(_int(c.get("releaseYear")) - year) <= 1]
            if by_year:
                cands = by_year
        want = naming.title_tokens(clean_title)
        best, best_score = None, -1.0
        for c in cands:
            s = max(naming.title_score(want, naming.title_tokens(str(c.get("title") or ""))),
                    naming.title_score(want, naming.title_tokens(str(c.get("alternateTitle") or ""))))
            if s > best_score:
                best, best_score = c, s
        self._entry_cache[key] = best
        return best

    # ------------------------------------------------------------------ danh sach phu de
    def list_all(self, movie_id: int) -> list[dict]:
        """``Get-AllSubs``: lấy TOÀN BỘ phụ đề của một ``movieId``, không lọc gì phía máy chủ."""
        if movie_id in self._list_cache:
            return self._list_cache[movie_id]
        out: list[dict] = []
        limit = 100
        for page in range(1, self.max_pages + 1):
            r = self.get("/subtitles", {"movieId": movie_id, "page": page, "limit": limit})
            if not r or not r.get("data"):
                if page == 1 and limit == 100:
                    limit = 20  # may chu co the khong nhan limit=100
                    r = self.get("/subtitles", {"movieId": movie_id, "page": 1, "limit": limit})
                    if not r or not r.get("data"):
                        break
                else:
                    break
            out += list(r["data"])
            pg = r.get("pagination")
            if not pg:
                break
            if page >= _int(pg.get("pages")):
                break
        self._list_cache[movie_id] = out
        return out

    def list_for_lang(self, movie_id: int, code: str) -> list[dict]:
        return [s for s in self.list_all(movie_id) if lang_matches(s, code)]

    # ------------------------------------------------------------------ tai goi ZIP
    def _workdir(self) -> Path:
        if self._cache_dir is None:
            self._cache_dir = Path(tempfile.mkdtemp(prefix="muxstudio_subsource_"))
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        return self._cache_dir

    def is_downloaded(self, sub_id: int) -> bool:
        return bool(self._zip_cache.get(int(sub_id)))

    def pack_dir(self, sub_id: int) -> Path | None:
        return self._zip_cache.get(int(sub_id))

    def download_pack(self, sub_id: int) -> Path | None:
        """``Get-SubtitlePack``: tải ZIP MỘT lần (cache theo lượt), giải nén, trả thư mục."""
        sub_id = int(sub_id)
        if sub_id in self._zip_cache:
            return self._zip_cache[sub_id]
        work = self._workdir()
        zpath = work / f"{sub_id}.zip"
        out = work / str(sub_id)
        self._pace()
        try:
            r = self.http.request("GET", f"{self.base}/subtitles/{sub_id}/download",
                                  headers={"X-API-Key": self.api_key}, timeout=90)
            self._keep_headers(r)
            if r.status_code != 200:
                raise OSError(f"HTTP {r.status_code}")
            data = r.content
        except (OSError, *_NET_ERR) as e:
            self.job.error(f"      [!] Tải thất bại (id={sub_id}): {e}")
            self._zip_cache[sub_id] = None
            return None
        if len(data) < 4:
            self._zip_cache[sub_id] = None
            return None
        if data[:2] != b"PK":
            magic = "RAR" if data[:1] == b"R" else "7Z" if data[:1] == b"7" else "không rõ"
            self.job.warn(f"      [!] File tải về không phải ZIP ({magic}) — cần 7-Zip, bỏ qua.")
            self._zip_cache[sub_id] = None
            return None
        zpath.write_bytes(data)
        try:
            safe_extract(zpath, out)
        except (zipfile.BadZipFile, OSError) as e:
            self.job.error(f"      [!] Giải nén lỗi: {e}")
            self._zip_cache[sub_id] = None
            return None
        self._zip_cache[sub_id] = out
        return out

    # ------------------------------------------------------------------ kiem tra ket noi
    def check(self) -> ConnStatus:
        if not self.api_key:
            return ConnStatus(None, "SubSource: chưa điền key")
        r = self.get("/movies/search", {"searchType": "text", "q": "The Matrix"})
        if r is None:
            if self.last_status in (401, 403):
                return ConnStatus(False, f"SubSource: key bị từ chối ({self.last_status})")
            return ConnStatus(False, "SubSource: không kết nối được")
        rem = _rate_text(self.last_headers)
        return ConnStatus(True, "SubSource: key hợp lệ" + (f", {rem}" if rem else ""))


# ======================================================================
#  Hàm không cần mạng
# ======================================================================


def _int(v: Any) -> int:
    try:
        return int(v)
    except (TypeError, ValueError):
        return 0


def _rate_text(h: dict[str, str]) -> str:
    """Header giới hạn lượt → ``còn 59/60 lượt/phút, 1794/1800 lượt/giờ, 7194/7200 lượt/ngày``.

    Tên header thật (dò 2026-11-09): ``X-RateLimit-Limit`` / ``-Remaining`` (theo phút, ``X-RateLimit-Window: 60``),
    ``X-RateLimit-Limit-Hour`` / ``-Remaining-Hour``, ``X-RateLimit-Limit-Day`` / ``-Remaining-Day``."""
    low = {k.lower(): v for k, v in h.items()}
    parts = []
    for suf, unit in (("", "phút"), ("-hour", "giờ"), ("-day", "ngày")):
        rem, lim = low.get(f"x-ratelimit-remaining{suf}"), low.get(f"x-ratelimit-limit{suf}")
        if rem and lim:
            parts.append(f"{rem}/{lim} lượt/{unit}")
    if parts:
        return "còn " + ", ".join(parts)
    rem = next((v for k, v in low.items() if "remaining" in k), None)  # ten header khac (du phong)
    lim = next((v for k, v in low.items() if k.endswith("limit") and "remaining" not in k), None)
    if rem and lim:
        return f"còn {rem}/{lim} lượt"
    if rem:
        return f"còn {rem} lượt"
    return ""


# Ten ngon ngu SubSource that (do 2026-11-09): "chinese_bg_code" (gian the), "big_5_code" (phon the),
# "portuguese" + "brazilian_portuguese", "farsi_persian"...
_LANG_PREFIX = {"vi": ("viet",), "cs": ("czech", "cze"), "en": ("english",), "zh": ("chinese", "big_5", "big5"),
                "pt": ("portuguese", "brazilian_portuguese", "brazillian_portuguese")}


def lang_matches(sub: dict, code: str) -> bool:
    """``Test-SubLang``: ``language`` của SubSource (tên tiếng Anh viết thường) có phải ngôn ngữ ``code``.

    So linh hoạt: ``vietnamese``/``viet*``/``vi-vn``; ``czech``/``cze*``/``cs-*``; ``english``/``en-*``…
    ``language`` để trống → không nhận (như .bat).
    """
    v = str(sub.get("language") or "").strip().lower()
    if not v:
        return False
    lg = naming.get_lang(code)
    names = {code}
    if lg:
        names |= {lg.subsource, lg.iso2b, lg.iso2t}
    names.discard("")
    if v in names:
        return True
    if v.startswith(code + "-") or v.startswith(code + "_"):
        return True
    return any(v.startswith(p) for p in _LANG_PREFIX.get(code, ()))


def lang_summary(subs: Sequence[dict]) -> list[str]:
    """``Get-LangSummary``: ``english(40), vietnamese(3), (trong)(2)`` — nhiều nhất trước."""
    counts: dict[str, int] = {}
    for s in subs:
        v = str(s.get("language") or "").strip().lower() or "(trống)"
        counts[v] = counts.get(v, 0) + 1
    return [f"{k}({n})" for k, n in sorted(counts.items(), key=lambda kv: -kv[1])]


def safe_extract(zpath: str | os.PathLike[str], out: str | os.PathLike[str]) -> None:
    """Giải nén ZIP, chặn đường dẫn thoát ra ngoài thư mục (zip-slip)."""
    out = Path(out)
    if out.exists():
        import shutil

        shutil.rmtree(out)
    out.mkdir(parents=True)
    root = os.path.normcase(os.path.abspath(out))
    with zipfile.ZipFile(zpath) as z:
        for info in z.infolist():
            name = info.filename.replace("\\", "/")
            if info.is_dir() or not name or name.endswith("/"):
                continue
            target = os.path.abspath(os.path.join(out, *[p for p in name.split("/") if p not in ("", ".", "..")]))
            if not os.path.normcase(target).startswith(root + os.sep):
                continue
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with z.open(info) as src, open(target, "wb") as dst:
                dst.write(src.read())


def pack_files(d: str | os.PathLike[str]) -> list[Path]:
    """File phụ đề trong gói (đệ quy): file ở thư mục trên trước, theo tên."""
    out: list[Path] = []
    base = Path(d)
    if not base.is_dir():
        return out

    def walk(p: Path) -> None:
        entries = sorted(os.scandir(p), key=lambda e: e.name.lower())
        out.extend(Path(e.path) for e in entries if e.is_file() and os.path.splitext(e.name)[1].lower() in SUB_EXTS)
        for e in entries:
            if e.is_dir():
                walk(Path(e.path))

    walk(base)
    return out


def select_from_pack(d: str | os.PathLike[str] | None, ep: episodes.EpisodeInfo | None, video_tokens: list[str],
                     exclude_forced: bool = True, exclude_sdh: bool = True) -> Path | None:
    """``Select-FromPack``: chọn ĐÚNG file trong gói (gói season có thể có hàng chục file).

    Bỏ file có ``forced``/``sdh``/``hi``/``hearing``/``cc`` trong tên; cả gói chỉ có loại đó → ``None``.
    Có số tập → chỉ giữ file CÙNG TẬP (``same_episode`` — nhận cả số tuyệt đối, TĐ-02); không có → ``None``.
    """
    if not d:
        return None
    files = pack_files(d)
    if not files:
        return None
    pat = []
    if exclude_forced:
        pat.append("forced")
    if exclude_sdh:
        pat.append(r"sdh|hi\b|hearing|cc\b")
    if pat:
        rx = re.compile(r"(?i)\b(" + "|".join(pat) + ")")
        normal = [f for f in files if not rx.search(f.stem)]
        if not normal:
            return None
        files = normal
    if ep is not None:
        hit = [f for f in files if episodes.same_episode(ep, episodes.parse_episode(f.stem))]
        if not hit:
            return None
        files = hit
    if len(files) == 1:
        return files[0]
    best, best_score = None, -1.0
    for f in files:
        s = naming.title_score(video_tokens, naming.title_tokens(f.stem))
        if s > best_score:
            best, best_score = f, s
    return best
