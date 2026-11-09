"""TheTVDB v4 (thetvdb.com) — đặt tên phim lẻ / phim bộ chuẩn Jellyfin ``[tvdbid-…]``.

Thay ``Invoke-TvdbApi``, ``Connect-Tvdb``, ``Get-TvdbLocalName``, ``Find-TvdbMovie``, ``Find-TvdbSeries``,
``Get-TvdbEpisodes``, ``Get-TvdbEpisodeName``, ``Resolve-TvdbEpisode``, ``Select-TvdbShow`` (MuxTool).

* ``POST /login {apikey, pin?}`` → ``data.token`` (Bearer). 401 → đăng nhập lại MỘT lần.
* Đọc phản hồi bằng UTF-8 (TVDB không luôn ghi charset — tên có dấu / tiếng Nhật bị đọc sai nếu để mặc định).
* 404 im lặng khi được yêu cầu; lỗi 4xx khác → báo và thôi; lỗi mạng → thử lại, chờ 2ⁱ giây.
"""
from __future__ import annotations

import json
import time
from typing import Any, Callable

from . import naming, tools
from .tmdb import ConnStatus

try:
    import requests
    _NET_ERR: tuple[type[BaseException], ...] = (requests.RequestException,)
except Exception:  # pragma: no cover
    requests = None  # type: ignore[assignment]
    _NET_ERR = (OSError,)

BASE = "https://api4.thetvdb.com/v4"


class TvdbClient:
    db_name = "tvdb"

    def __init__(self, api_key: str, pin: str = "", lang: str = "eng", season_type: str = "default", retry: int = 3,
                 http: Any = None, sleep: Callable[[float], None] = time.sleep, job: tools.Job | None = None):
        self.api_key = (api_key or "").strip()
        self.pin = (pin or "").strip()
        self.lang = (lang or "eng").strip()
        self.season_type = season_type or "default"
        self.retry = max(1, int(retry))
        self.http = http if http is not None else (requests.Session() if requests else None)
        self.sleep = sleep
        self.job = job or tools.NULL_JOB
        self.token: str | None = None
        self.login_failed = False
        self._cache: dict[Any, Any] = {}

    @property
    def usable(self) -> bool:
        return bool(self.api_key) and not self.login_failed

    # ------------------------------------------------------------------ goi API
    def request(self, path: str, quiet404: bool = False, no_auth: bool = False, body: dict | None = None) -> Any:
        """``Invoke-TvdbApi``."""
        if not no_auth and not self.token and not self.login():
            return None
        url = BASE + path
        tries = max(2, self.retry)
        relogged = False
        i = 0
        while i < tries:
            i += 1
            h = {"Accept": "application/json"}
            if not no_auth:
                h["Authorization"] = f"Bearer {self.token}"
            code, msg = 0, ""
            try:
                if body is not None:
                    r = self.http.request("POST", url, json=body, headers=h, timeout=25)
                else:
                    r = self.http.request("GET", url, headers=h, timeout=25)
                code = r.status_code
                if code == 200:
                    try:
                        return json.loads(r.content.decode("utf-8", errors="replace"))
                    except ValueError:
                        return None
                msg = (r.content or b"")[:200].decode("utf-8", errors="replace")
            except _NET_ERR as e:
                msg = str(e)
            if code == 404:
                if not quiet404:
                    self.job.warn(f"  [!] TVDB không có: {path}")
                return None
            if code == 401 and not no_auth and not relogged:
                relogged = True  # token het han -> dang nhap lai MOT lan
                self.token = None
                if self.login():
                    i -= 1
                    continue
                return None
            if 400 <= code < 500:
                self.job.error(f"  [!] TVDB từ chối yêu cầu ({code}): {msg}")
                return None
            if i < self.retry:
                wait = 2 ** i
                self.job.warn(f"  [!] TVDB lỗi mạng (lần {i}/{self.retry}), thử lại sau {wait} giây...")
                self.sleep(wait)
            else:
                self.job.error(f"  [!] TVDB thất bại: {msg}")
                return None
        return None

    def login(self) -> bool:
        """``Connect-Tvdb``."""
        if self.token:
            return True
        if self.login_failed or not self.api_key:
            return False
        b = {"apikey": self.api_key}
        if self.pin:
            b["pin"] = self.pin
        r = self.request("/login", no_auth=True, body=b)
        tok = ((r or {}).get("data") or {}).get("token")
        if tok:
            self.token = str(tok)
            return True
        self.login_failed = True
        self.job.error("[!] Đăng nhập TVDB thất bại — kiểm tra TVDB API key (và PIN nếu key của bạn cần PIN).")
        return False

    def _cached(self, key: Any, fn: Callable[[], Any]) -> Any:
        if key not in self._cache:
            self._cache[key] = fn()
        return self._cache[key]

    # ------------------------------------------------------------------ tim kiem
    def local_name(self, obj: dict) -> str:
        """``Get-TvdbLocalName``: ``TvdbLang`` → ``eng`` → tên gốc."""
        tr = obj.get("translations") or {}
        if isinstance(tr, dict):
            for lg in (self.lang, "eng"):
                v = tr.get(lg)
                if v:
                    return str(v)
        return str(obj.get("name") or "")

    def _search(self, kind: str, title: str, year: int | None) -> list[dict]:
        if not title:
            return []
        from urllib.parse import quote  # noqa: PLC0415

        def do() -> list[dict]:
            q = f"/search?query={quote(title, safe='')}&type={kind}" + (f"&year={year}" if year else "")
            r = self.request(q)
            data = list((r or {}).get("data") or [])
            if not data and year:
                return self._search(kind, title, None)
            out = []
            for x in data:
                if not x.get("tvdb_id"):
                    continue
                yy = str(x.get("year") or "")
                fa = str(x.get("first_air_time") or "")
                if not yy and len(fa) >= 4:
                    yy = fa[:4]
                date = f"{yy}-01-01" if yy else ""
                tid = int(x.get("tvdb_id"))
                if kind == "movie":
                    out.append({"id": tid, "title": self.local_name(x), "original_title": str(x.get("name") or ""),
                                "release_date": date})
                else:
                    out.append({"id": tid, "name": self.local_name(x), "original_name": str(x.get("name") or ""),
                                "first_air_date": date})
            return out

        return self._cached(("search", kind, title, year), do)

    def search_movie(self, title: str, year: int | None) -> list[dict]:
        """``Find-TvdbMovie`` → cùng dạng kết quả TMDB (title / original_title / release_date)."""
        return self._search("movie", title, year)

    def search_series(self, title: str, year: int | None) -> list[dict]:
        """``Find-TvdbSeries`` → cùng dạng TMDB (name / original_name / first_air_date)."""
        return self._search("series", title, year)

    # ------------------------------------------------------------------ tap
    def episodes(self, series_id: int) -> list[dict]:
        """``Get-TvdbEpisodes``: ``/series/{id}/episodes/{TvdbSeasonType}?page=N``, theo ``links.next``, ≤ 30 trang."""

        def do() -> list[dict]:
            out: list[dict] = []
            for p in range(30):
                r = self.request(f"/series/{series_id}/episodes/{self.season_type}?page={p}", quiet404=True)
                data = (r or {}).get("data")
                if not data:
                    break
                out += list(data.get("episodes") or [])
                if not ((r or {}).get("links") or {}).get("next"):
                    break
            return out

        return self._cached(("eps", series_id, self.season_type), do)

    def episode_name(self, e: dict) -> str | None:
        """``Get-TvdbEpisodeName``: ``/episodes/{id}/translations/{lang}`` (ngôn ngữ cấu hình, rồi ``eng``)."""
        have = list(e.get("nameTranslations") or [])
        for lg in dict.fromkeys((self.lang, "eng")):
            if have and lg not in have:
                continue
            t = self.request(f"/episodes/{e.get('id')}/translations/{lg}", quiet404=True)
            nm = ((t or {}).get("data") or {}).get("name")
            if nm:
                return str(nm)
        return None

    def resolve_episode(self, series_id: int, season: int, episode: int, want_title: bool = True) -> naming.EpisodeHit:
        """``Resolve-TvdbEpisode``: khớp ``seasonNumber``+``number``; không có thì coi là số tuyệt đối (``absoluteNumber``)."""
        lst = self.episodes(series_id)
        if not lst:
            return naming.EpisodeHit(season, episode)
        hit = None
        mapped = False
        if season >= 1:
            hit = next((x for x in lst if int(x.get("seasonNumber") or 0) == season and int(x.get("number") or 0) == episode), None)
        if hit is None:
            hit = next((x for x in lst if int(x.get("seasonNumber") or 0) >= 1 and int(x.get("absoluteNumber") or 0) == episode), None)
            mapped = hit is not None
        if hit is None:
            return naming.EpisodeHit(season, episode)
        title = self.episode_name(hit) if want_title else None
        return naming.EpisodeHit(int(hit.get("seasonNumber") or 0), int(hit.get("number") or 0), title, mapped, True)

    def pick_show(self, res: list[dict], ep) -> tuple[dict | None, bool]:
        """``Select-TvdbShow``: nhiều series trùng tên, tên file không có năm → series thực sự có tập này."""
        if not res:
            return None, False
        want = naming.norm_title(ep.series)
        exact = [r for r in res if naming.norm_title(r.get("name")) == want or naming.norm_title(r.get("original_name")) == want]
        if ep.year or len(exact) <= 1:
            p = naming.pick_match(res, ep.series, ep.year, "name", "original_name", "first_air_date")
            return (p, True) if p else (res[0], False)
        self.job.warn(f"  [?] Có {len(exact)} series cùng tên '{ep.series}' — kiểm tra series nào có {ep.ref_label}...")
        for c in exact[:6]:
            n = len(self.episodes(int(c["id"])))
            rt = self.resolve_episode(int(c["id"]), ep.season, ep.episode, False)
            yy = naming.year_of(c.get("first_air_date")) or "????"
            self.job.dim(f"      - {c.get('name')} ({yy})  tvdbid-{c['id']}: {n} tập" + ("  ← có tập này" if rt.found else ""))
            if rt.found:
                self.job.ok(f"  [i] → Chọn: {c.get('name')} ({yy})")
                return c, True
        self.job.warn(f"  [!] Không series nào có {ep.ref_label}.")
        p = naming.pick_match(res, ep.series, ep.year, "name", "original_name", "first_air_date")
        return (p, True) if p else (res[0], False)

    # ------------------------------------------------------------------ kiem tra ket noi
    def check(self) -> ConnStatus:
        if not self.api_key:
            return ConnStatus(None, "TVDB: chưa điền key")
        if self.login():
            return ConnStatus(True, "TVDB: sẵn sàng (đăng nhập được)")
        return ConnStatus(False, "TVDB: đăng nhập lỗi")
