"""TMDB (themoviedb.org).

Giai đoạn 2: tra IMDb ID cho bước ① (thay ``Get-ImdbId``/``Get-ImdbIdCached`` của SubFetch) và
các phần dùng chung với bước ③ (``Invoke-TmdbApi``, ``Find-TmdbMovie``, ``Find-TmdbTv``,
``Get-TmdbTvDetails``, ``Select-TmdbTvShow``, phần cộng dồn tập của ``Resolve-TmdbEpisode``).
Giai đoạn 4: đặt tên phim lẻ/phim bộ (``search_movie``, ``search_series``, ``pick_show``, ``episode_title``,
``resolve_episode`` = ``Get-TmdbEpisodeTitle`` + ``Resolve-TmdbEpisode``) — dùng qua ``naming.resolve_output_name``.

* Key 32 ký tự → ``?api_key=``; token dài bắt đầu ``eyJ`` → ``Authorization: Bearer`` (§6.10).
  (SubFetch cũ chỉ hỗ trợ kiểu đầu — TĐ-10.)
* 401 → tắt TMDB cả lượt chạy, báo MỘT lần. 404 khi dò tập là bình thường → im lặng.
* Lỗi mạng → thử lại ``TmdbRetry`` lần, chờ 2, 4… giây.
"""
from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any, Callable

from . import naming, tools

try:  # requests chi can khi goi mang that
    import requests
    _NET_ERR: tuple[type[BaseException], ...] = (requests.RequestException,)
except Exception:  # pragma: no cover
    requests = None  # type: ignore[assignment]
    _NET_ERR = (OSError,)

BASE = "https://api.themoviedb.org/3"


@dataclass
class ConnStatus:
    ok: bool | None  # None = chua co key
    text: str


def _year_of(date: str | None) -> int | None:
    if date and len(date) >= 4 and date[:4].isdigit():
        return int(date[:4])
    return None


class TmdbClient:
    db_name = "tmdb"

    def __init__(self, api_key: str, lang: str = "en-US", retry: int = 3, http: Any = None,
                 sleep: Callable[[float], None] = time.sleep, job: tools.Job | None = None):
        self.api_key = (api_key or "").strip()
        self.lang = lang
        self.retry = max(1, int(retry))
        self.http = http if http is not None else (requests.Session() if requests else None)
        self.sleep = sleep
        self.job = job or tools.NULL_JOB
        self.auth_failed = False
        self._cache: dict[Any, Any] = {}

    @property
    def usable(self) -> bool:
        return bool(self.api_key) and not self.auth_failed

    # ------------------------------------------------------------------ goi API
    def get(self, path: str, params: dict | None = None, quiet404: bool = False) -> Any:
        """``Invoke-TmdbApi``."""
        if not self.usable:
            return None
        q = {k: v for k, v in (params or {}).items() if v not in (None, "")}
        headers = {"Accept": "application/json"}
        if self.api_key.startswith("eyJ"):
            headers["Authorization"] = f"Bearer {self.api_key}"
        else:
            q["api_key"] = self.api_key
        for i in range(1, self.retry + 1):
            code, msg = 0, ""
            try:
                r = self.http.request("GET", BASE + path, params=q, headers=headers, timeout=20)
                code = r.status_code
                if code == 200:
                    try:
                        return r.json()
                    except ValueError:
                        return None
                msg = (r.text or "")[:200]
            except _NET_ERR as e:
                msg = str(e)
            if code == 404:
                if not quiet404:
                    self.job.warn(f"  [!] TMDB không có dữ liệu (404): {path}")
                return None
            if code == 401:
                self.job.error("  [!] TMDB từ chối API key (401) → tắt TMDB cho lần chạy này.")
                self.job.warn("      Kiểm tra TMDB key: dán 'API Key' (32 ký tự) hoặc 'API Read Access Token' (bắt đầu eyJ).")
                self.auth_failed = True
                return None
            if i < self.retry:
                wait = 2 ** i
                self.job.warn(f"  [!] TMDB lỗi mạng (lần {i}/{self.retry}), thử lại sau {wait} giây...")
                self.sleep(wait)
            else:
                self.job.error(f"  [!] TMDB thất bại sau {self.retry} lần: {msg}")
        return None

    def _cached(self, key: Any, fn: Callable[[], Any]) -> Any:
        if key not in self._cache:
            self._cache[key] = fn()
        return self._cache[key]

    # ------------------------------------------------------------------ tim kiem
    def search(self, kind: str, title: str, year: int | None) -> list[dict]:
        """``Find-TmdbMovie``/``Find-TmdbTv``: có năm mà không ra → bỏ năm tìm lại."""
        if not title:
            return []

        def do() -> list[dict]:
            params = {"language": self.lang, "include_adult": "false", "query": title}
            if year:
                params["first_air_date_year" if kind == "tv" else "year"] = year
            r = self.get(f"/search/{kind}", params)
            res = list((r or {}).get("results") or [])
            if not res and year and r is not None:
                return self.search(kind, title, None)
            return res

        return self._cached(("search", kind, title, year), do)

    def tv_details(self, tv_id: int) -> dict | None:
        """``Get-TmdbTvDetails`` (có cache)."""
        return self._cached(("tv", tv_id), lambda: self.get(f"/tv/{tv_id}", {"language": self.lang}, quiet404=True))

    @staticmethod
    def pick_exact(res: list[dict], want_title: str, want_year: int | None, name_prop: str, orig_prop: str,
                   date_prop: str) -> dict | None:
        """Phần không hỏi của ``Select-TmdbCandidate``: trùng tên chuẩn hoá và năm ±1."""
        return naming.pick_match(res, want_title, want_year, name_prop, orig_prop, date_prop)

    # ------------------------------------------------------------------ dat ten (buoc 3)
    def search_movie(self, title: str, year: int | None) -> list[dict]:
        """``Find-TmdbMovie``."""
        return self.search("movie", title, year)

    def search_series(self, title: str, year: int | None) -> list[dict]:
        """``Find-TmdbTv``."""
        return self.search("tv", title, year)

    def pick_show(self, res: list[dict], ep) -> tuple[dict | None, bool]:
        """``Select-TmdbTvShow`` cho ``naming.tv_name``."""
        return self.select_show(res, ep.series, ep.year, ep.season, ep.episode)

    def episode_title(self, tv_id: int, season: int, episode: int) -> str | None:
        """``Get-TmdbEpisodeTitle`` (404 im lặng, có cache)."""
        r = self._cached(("ep", tv_id, season, episode),
                         lambda: self.get(f"/tv/{tv_id}/season/{season}/episode/{episode}", {"language": self.lang},
                                          quiet404=True))
        v = (r or {}).get("name")
        return str(v) if v else None

    def resolve_episode(self, tv_id: int, season: int, episode: int, want_title: bool = True) -> naming.EpisodeHit:
        """``Resolve-TmdbEpisode``: tra thẳng → nếu ngoài phạm vi season: cộng dồn ``episode_count`` thử số
        tương đối, rồi số tuyệt đối ngay trong season đó (một số anime TMDB giữ số tuyệt đối)."""
        if season >= 1:
            t = self.episode_title(tv_id, season, episode)
            if t:
                return naming.EpisodeHit(season, episode, t if want_title else None, False, True)
        d = self.tv_details(tv_id)
        ss = sorted((s for s in ((d or {}).get("seasons") or []) if int(s.get("season_number") or 0) >= 1),
                    key=lambda s: int(s.get("season_number")))
        miss = naming.EpisodeHit(season, episode)
        if not ss:
            return miss
        cur = next((s for s in ss if int(s.get("season_number")) == season), None)
        if cur is not None and episode <= int(cur.get("episode_count") or 0):
            return miss  # trong pham vi season -> khong phai so tuyet doi, chi la TMDB chua co ten
        m = self.absolute_to_season(tv_id, episode)
        if m is None:
            return miss
        s_, rel = m
        t = self.episode_title(tv_id, s_, rel)
        if t:
            return naming.EpisodeHit(s_, rel, t if want_title else None, True, True)
        t = self.episode_title(tv_id, s_, episode)
        if t:
            return naming.EpisodeHit(s_, episode, t if want_title else None, s_ != season, True)
        return miss

    def select_show(self, res: list[dict], series: str, year: int | None, season: int | None,
                    episode: int | None) -> tuple[dict | None, bool]:
        """``Select-TmdbTvShow``: nhiều series TRÙNG TÊN (One Piece 1999 / 2023), tên file không có năm →
        chọn series thực sự CÓ season/tập đó. Trả (series, chắc_chắn)."""
        if not res:
            return None, False
        want = naming.norm_title(series)
        exact = [r for r in res if naming.norm_title(r.get("name")) == want or naming.norm_title(r.get("original_name")) == want]
        if year or len(exact) <= 1 or episode is None:
            p = self.pick_exact(res, series, year, "name", "original_name", "first_air_date")
            return (p, True) if p else (res[0], False)
        self.job.warn(f"  [?] Có {len(exact)} series cùng tên '{series}' — kiểm tra series nào có tập này...")
        best, best_score = None, -1
        for c in exact[:6]:
            d = self.tv_details(int(c.get("id")))
            ss = [s for s in ((d or {}).get("seasons") or []) if int(s.get("season_number") or 0) >= 1]
            has_season = season is not None and any(int(s.get("season_number")) == season for s in ss)
            total = sum(int(s.get("episode_count") or 0) for s in ss)
            score = (2 if has_season else 0) + (1 if total >= episode else 0)
            y = _year_of(c.get("first_air_date")) or "????"
            self.job.dim(f"      - {c.get('name')} ({y})  tmdbid-{c.get('id')}: {len(ss)} season, {total} tập")
            if score > best_score:
                best, best_score = c, score
        if best_score <= 0:
            self.job.warn("  [!] Không series nào có tập này.")
            p = self.pick_exact(res, series, year, "name", "original_name", "first_air_date")
            return (p, True) if p else (res[0], False)
        self.job.ok(f"  [i] → Chọn: {best.get('name')} ({_year_of(best.get('first_air_date')) or '????'})")
        return best, True

    def absolute_to_season(self, tv_id: int, abs_ep: int) -> tuple[int, int] | None:
        """Số tập tuyệt đối → (season, tập trong season) bằng cách cộng dồn ``episode_count`` (§6.10)."""
        d = self.tv_details(tv_id)
        ss = sorted((s for s in ((d or {}).get("seasons") or []) if int(s.get("season_number") or 0) >= 1),
                    key=lambda s: int(s.get("season_number")))
        acc = 0
        for s in ss:
            n = int(s.get("episode_count") or 0)
            if abs_ep <= acc + n:
                return int(s.get("season_number")), abs_ep - acc
            acc += n
        return None

    # ------------------------------------------------------------------ IMDb ID cho buoc 1
    def find_show(self, series: str, year: int | None, season: int | None, episode: int | None) -> dict | None:
        res = self.search("tv", series, year)
        pick, _ = self.select_show(res, series, year, season, episode)
        return pick

    def imdb_id(self, title: str, year: int | None, is_tv: bool, season: int | None = None,
                episode: int | None = None) -> str | None:
        """IMDb ID (``tt…``) qua ``/search/{movie|tv}`` → ``/{kind}/{id}/external_ids``. Có cache."""
        if not self.usable or not title:
            return None

        def do() -> str | None:
            kind = "tv" if is_tv else "movie"
            if is_tv:
                pick = self.find_show(title, year, season, episode)
            else:
                res = self.search("movie", title, year)
                pick = self.pick_exact(res, title, year, "title", "original_title", "release_date") or (res[0] if res else None)
            if not pick:
                return None
            e = self.get(f"/{kind}/{pick.get('id')}/external_ids")
            v = (e or {}).get("imdb_id")
            return str(v) if v else None

        return self._cached(("imdb", title, year, is_tv, season, episode), do)

    # ------------------------------------------------------------------ kiem tra ket noi
    def check(self) -> ConnStatus:
        if not self.api_key:
            return ConnStatus(None, "TMDB: chưa điền key")
        r = self.get("/configuration", quiet404=True)
        if self.auth_failed:
            return ConnStatus(False, "TMDB: key bị từ chối (401)")
        if r is None:
            return ConnStatus(False, "TMDB: không kết nối được")
        kind = "token (Bearer)" if self.api_key.startswith("eyJ") else "API key"
        return ConnStatus(True, f"TMDB: sẵn sàng ({kind} hợp lệ)")
