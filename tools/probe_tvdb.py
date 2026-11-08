"""Dò TVDB v4 API (§2): đăng nhập (+pin), tìm kiếm, danh sách tập có phân trang, absoluteNumber,
tên tập theo ngôn ngữ, 401 với token sai.

Chạy riêng:  python -m tools.probe_tvdb
"""
from __future__ import annotations

from typing import Any

from tools.probe_common import Reporter, http_session, keys_of, load_everything, pause

BASE = "https://api4.thetvdb.com/v4"


def run(rep: Reporter, cfg: Any, secrets: dict[str, str]) -> None:
    rep.section("TVDB")
    key = secrets.get("TvdbApiKey", "")
    if not key:
        rep.conclude("TVDB: chưa có key → bỏ qua (MuxTool cũ của bạn để trống $TvdbApiKey)")
        return
    rep.add_secret(key)
    pin = secrets.get("TvdbPin", "")
    rep.add_secret(pin)
    http = http_session()
    token = ""

    def call(method: str, path: str, params: dict | None = None, body: dict | None = None, tok: str | None = None) -> Any:
        h = {"Accept": "application/json"}
        t = token if tok is None else tok
        if t:
            h["Authorization"] = f"Bearer {t}"
        try:
            r = http.request(method, BASE + path, params=params, json=body, headers=h, timeout=25)
        except Exception as e:  # noqa: BLE001
            rep.line(f"  [LỖI MẠNG] {e}")
            return None
        pause(0.3)
        return r

    rep.step("1. POST /login" + (" (có pin)" if pin else ""))
    body = {"apikey": key, **({"pin": pin} if pin else {})}
    r = call("POST", "/login", body=body)
    d = rep.show(r, 400)
    token = ((d or {}).get("data") or {}).get("token") or ""
    rep.add_secret(token)
    if not token:
        rep.conclude(f"TVDB: đăng nhập LỖI ({getattr(r, 'status_code', '?')})")
        return
    rep.conclude("TVDB: đăng nhập OK")

    rep.step("2. Token sai → mã lỗi")
    r = call("GET", "/search", {"query": "One Piece", "type": "series"}, tok="sai")
    rep.show(r, 200)

    rep.step("3. GET /search?query=One Piece&type=series")
    r = call("GET", "/search", {"query": "One Piece", "type": "series"})
    d = rep.show(r, 300, show_body=False)
    res = (d or {}).get("data") or []
    rep.line(f"  Trường: {keys_of(res)}")
    rep.line(f"  Kết quả: {[(x.get('tvdb_id'), x.get('name'), x.get('year')) for x in res[:6]]}")
    if not res:
        return
    sid = res[0].get("tvdb_id")
    st = cfg["TvdbSeasonType"]
    rep.step(f"4. GET /series/{sid}/episodes/{st}?page=0")
    r = call("GET", f"/series/{sid}/episodes/{st}", {"page": 0})
    d = rep.show(r, 300, show_body=False)
    eps = ((d or {}).get("data") or {}).get("episodes") or []
    rep.line(f"  Trường tập: {keys_of(eps)} | links: {(d or {}).get('links')}")
    hit = next((e for e in eps if e.get("absoluteNumber") == 643), None)
    if eps:
        rep.conclude(f"có absoluteNumber: {'absoluteNumber' in keys_of(eps)}; tập 643 ở trang 0: "
                     f"{(hit.get('seasonNumber'), hit.get('number')) if hit else 'không (ở trang sau)'}")
        e0 = eps[0]
        lang = cfg["TvdbLang"]
        rep.step(f"5. GET /episodes/{e0.get('id')}/translations/{lang}")
        r = call("GET", f"/episodes/{e0.get('id')}/translations/{lang}")
        rep.show(r, 400)


if __name__ == "__main__":
    from core.secrets import Redactor

    cfg, sec = load_everything()
    rp = Reporter(None, Redactor(sec.values()))
    run(rp, cfg, sec)
