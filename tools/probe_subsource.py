"""Dò SubSource API (§2) — kiểm lại những gì .bat đã dò: searchType, mỗi season một movieId,
tên trường language/releaseInfo/framerate/preview, limit=100, không lọc phía máy chủ, gói ZIP.

Chạy riêng:  python -m tools.probe_subsource
"""
from __future__ import annotations

import io
import zipfile
from typing import Any

from tools.probe_common import Reporter, http_session, keys_of, load_everything, pause

FIELDS = ("subtitleId", "language", "releaseInfo", "framerate", "hearingImpaired", "foreignParts", "files", "downloads", "preview")


def run(rep: Reporter, cfg: Any, secrets: dict[str, str]) -> None:
    rep.section("SUBSOURCE")
    key = secrets.get("SubSourceApiKey", "")
    if not key:
        rep.conclude("SubSource: chưa có key → bỏ qua")
        return
    rep.add_secret(key)
    base = cfg["SubSourceApiBase"].rstrip("/")
    http = http_session()
    H = {"X-API-Key": key}

    def get(path: str, params: dict | None = None, **kw: Any) -> Any:
        try:
            r = http.get(base + path, params=params, headers=H, timeout=30, **kw)
        except Exception as e:  # noqa: BLE001
            rep.line(f"  [LỖI MẠNG] {e}")
            return None
        pause(cfg["ApiPauseMs"] / 1000)
        return r

    rep.step("1. Tìm theo tên (searchType=text): The Matrix")
    r = get("/movies/search", {"searchType": "text", "q": "The Matrix"})
    d = rep.show(r, 1200)
    if r is None or r.status_code in (401, 403):
        rep.conclude(f"SubSource: key bị từ chối ({getattr(r, 'status_code', '?')})")
        return
    items = (d or {}).get("data") or []
    rep.line(f"  Số kết quả: {len(items)} | trường: {keys_of(items)}")
    rl = {k: v for k, v in r.headers.items() if "ratelimit" in k.lower() or "rate-limit" in k.lower()}
    rep.conclude("SubSource: key hợp lệ" + (f", header giới hạn: {rl}" if rl else ", KHÔNG thấy header giới hạn lượt"))

    rep.step("2. Tìm theo IMDb (searchType=imdb&imdb=tt0133093)")
    r = get("/movies/search", {"searchType": "imdb", "imdb": "tt0133093"})
    d = rep.show(r, 800)
    imdb_items = (d or {}).get("data") or []
    rep.conclude(f"searchType=imdb: {'chạy' if imdb_items else 'KHÔNG ra kết quả'} ({len(imdb_items)} mục)")
    movie = (imdb_items or items or [{}])[0]
    mid = movie.get("movieId")

    rep.step("3. Phim bộ: mỗi season có movieId riêng? (Breaking Bad)")
    r = get("/movies/search", {"searchType": "text", "q": "Breaking Bad"})
    d = rep.show(r, 800)
    seasons = [(x.get("movieId"), x.get("season")) for x in (d or {}).get("data") or []]
    rep.line(f"  (movieId, season): {seasons[:10]}")
    rep.conclude("trường 'season' có mặt" if any(s for _, s in seasons) else "KHÔNG thấy trường 'season'")

    if not mid:
        rep.conclude("không có movieId để thử danh sách phụ đề")
        return
    rep.step(f"4. Danh sách phụ đề movieId={mid}, limit=100")
    r = get("/subtitles", {"movieId": mid, "page": 1, "limit": 100})
    d = rep.show(r, 1500)
    subs = (d or {}).get("data") or []
    if not subs:
        rep.step("4b. limit=100 không ra → thử limit=20")
        r = get("/subtitles", {"movieId": mid, "page": 1, "limit": 20})
        d = rep.show(r, 1500)
        subs = (d or {}).get("data") or []
        rep.conclude(f"limit=100 bị từ chối, limit=20: {len(subs)} mục")
    else:
        rep.conclude(f"limit=100 được nhận ({len(subs)} mục, pagination={d.get('pagination')})")
    have = keys_of(subs, 10)
    missing = [f for f in FIELDS if f not in have]
    rep.line(f"  Trường có: {have}")
    rep.conclude("đủ các trường cần dùng" if not missing else f"THIẾU trường: {missing}")
    langs: dict[str, int] = {}
    for s in subs:
        v = str(s.get("language") or "(trống)")
        langs[v] = langs.get(v, 0) + 1
    rep.line(f"  Giá trị 'language': {dict(sorted(langs.items(), key=lambda kv: -kv[1]))}")
    empty = sum(1 for s in subs if s.get("hearingImpaired") in (None, "") or s.get("foreignParts") in (None, ""))
    rep.line(f"  Bản để trống hearingImpaired/foreignParts: {empty}/{len(subs)}")
    pv = next((s.get("preview") for s in subs if s.get("preview")), None)
    rep.line(f"  Ví dụ preview: {str(pv)[:300]!r}")

    rep.step("5. So sánh: lọc phía máy chủ language=english (để biết có loại oan không)")
    r = get("/subtitles", {"movieId": mid, "page": 1, "limit": 100, "language": "english"})
    rep.show(r, 300, show_body=False)
    n_srv = len(((r.json() if r is not None and r.status_code == 200 else {}) or {}).get("data") or [])
    n_cli = sum(1 for s in subs if str(s.get("language", "")).lower().startswith("english"))
    rep.conclude(f"lọc phía máy chủ: {n_srv} bản english | tự lọc: {n_cli} bản english (trên trang 1)")

    if subs:
        sid = subs[0].get("subtitleId")
        rep.step(f"6. Tải thử subtitleId={sid}")
        r = get(f"/subtitles/{sid}/download")
        rep.show(r, 200, show_body=False)
        if r is not None and r.status_code == 200:
            magic = r.content[:4]
            kind = "ZIP" if magic[:2] == b"PK" else "RAR" if magic[:1] == b"R" else "7Z" if magic[:1] == b"7" else "khác"
            rep.line(f"  Kích thước: {len(r.content)} byte, loại: {kind}")
            if kind == "ZIP":
                with zipfile.ZipFile(io.BytesIO(r.content)) as z:
                    rep.line(f"  Trong gói: {z.namelist()[:20]}")
            rep.conclude(f"tải: {kind}")
        else:
            rep.conclude(f"tải thất bại ({getattr(r, 'status_code', '?')})")


if __name__ == "__main__":
    from core.secrets import Redactor

    cfg, sec = load_everything()
    rp = Reporter(None, Redactor(sec.values()))
    run(rp, cfg, sec)
