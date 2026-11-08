"""Dò TMDB API (§2): key 32 ký tự / token eyJ, IMDb ID, One Piece trùng tên, số tập tuyệt đối,
404 khi dò tập, 401 với key sai.

Chạy riêng:  python -m tools.probe_tmdb
"""
from __future__ import annotations

from typing import Any

from tools.probe_common import Reporter, http_session, load_everything, pause

BASE = "https://api.themoviedb.org/3"


def run(rep: Reporter, cfg: Any, secrets: dict[str, str]) -> None:
    rep.section("TMDB")
    key = secrets.get("TmdbApiKey", "")
    if not key:
        rep.conclude("TMDB: chưa có key → bỏ qua")
        return
    rep.add_secret(key)
    http = http_session()
    bearer = key.startswith("eyJ")

    def get(path: str, params: dict | None = None, k: str | None = None) -> Any:
        kk = k if k is not None else key
        p = dict(params or {})
        h = {"Accept": "application/json"}
        if kk.startswith("eyJ"):
            h["Authorization"] = f"Bearer {kk}"
        else:
            p["api_key"] = kk
        try:
            r = http.get(BASE + path, params=p, headers=h, timeout=20)
        except Exception as e:  # noqa: BLE001
            rep.line(f"  [LỖI MẠNG] {e}")
            return None
        pause(0.3)
        return r

    rep.step(f"1. /configuration (key dạng {'token Bearer' if bearer else 'API key 32 ký tự'}, dài {len(key)})")
    r = get("/configuration")
    rep.show(r, 300)
    code = getattr(r, "status_code", "?")
    rep.conclude("TMDB: sẵn sàng" if code == 200 else f"TMDB: lỗi {code}")

    rep.step("2. Key sai → mã lỗi (để nhận diện 401)")
    r = get("/configuration", k="00000000000000000000000000000000")
    rep.show(r, 300)

    rep.step("3. Phim lẻ: search/movie?query=The Matrix&year=1999 → external_ids")
    r = get("/search/movie", {"query": "The Matrix", "year": 1999, "language": cfg["TmdbLang"]})
    d = rep.show(r, 600, show_body=False)
    res = (d or {}).get("results") or []
    rep.line(f"  Kết quả đầu: {[(x.get('id'), x.get('title'), x.get('release_date')) for x in res[:3]]}")
    if res:
        r = get(f"/movie/{res[0]['id']}/external_ids")
        d = rep.show(r, 300)
        rep.conclude(f"IMDb của The Matrix: {(d or {}).get('imdb_id')}")

    rep.step("4. Phim bộ trùng tên: search/tv?query=One Piece")
    r = get("/search/tv", {"query": "One Piece", "language": cfg["TmdbLang"]})
    d = rep.show(r, 300, show_body=False)
    res = (d or {}).get("results") or []
    rep.line(f"  Kết quả: {[(x.get('id'), x.get('name'), x.get('first_air_date')) for x in res[:6]]}")
    anime = None
    for x in res[:4]:
        r = get(f"/tv/{x['id']}", {"language": cfg["TmdbLang"]})
        dd = (r.json() if r is not None and r.status_code == 200 else {}) or {}
        ss = [s for s in dd.get("seasons") or [] if (s.get("season_number") or 0) >= 1]
        total = sum(int(s.get("episode_count") or 0) for s in ss)
        rep.line(f"  tmdbid-{x['id']} {x.get('name')} ({x.get('first_air_date')}): {len(ss)} season, {total} tập")
        if total >= 643 and anime is None:
            anime = (x["id"], ss)
    if anime:
        acc = 0
        for s in sorted(anime[1], key=lambda s: s["season_number"]):
            n = int(s.get("episode_count") or 0)
            if 643 <= acc + n:
                rep.conclude(f"tập tuyệt đối 643 → S{s['season_number']:02d}E{643 - acc:02d} (cộng dồn episode_count)")
                break
            acc += n
        rep.step(f"5. Dò tập không tồn tại /tv/{anime[0]}/season/1/episode/9999 (phải 404)")
        r = get(f"/tv/{anime[0]}/season/1/episode/9999")
        rep.show(r, 200)


if __name__ == "__main__":
    from core.secrets import Redactor

    cfg, sec = load_everything()
    rp = Reporter(None, Redactor(sec.values()))
    run(rp, cfg, sec)
