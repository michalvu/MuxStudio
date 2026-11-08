"""Dò OpenSubtitles API (§2): User-Agent được phép, đăng nhập + base_url VIP, /infos/user,
tham số tìm kiếm, mã ngôn ngữ. KHÔNG tải file (tốn quota) trừ khi chạy với ``--download``.

Chạy riêng:  python -m tools.probe_opensubs [--download]
"""
from __future__ import annotations

import sys
from typing import Any

from tools.probe_common import Reporter, http_session, keys_of, load_everything, pause

OUR_LANGS = ("vi", "en", "cs", "ja", "ko", "zh", "fr", "de", "es", "th", "id", "ar", "pt", "ru", "it", "pl", "nl", "tr")


def run(rep: Reporter, cfg: Any, secrets: dict[str, str], download: bool = False) -> None:
    rep.section("OPENSUBTITLES")
    key = secrets.get("OsApiKey", "")
    if not key:
        rep.conclude("OpenSubtitles: chưa có key → bỏ qua")
        return
    rep.add_secret(key)
    user, pw = secrets.get("OsUsername", ""), secrets.get("OsPassword", "")
    rep.add_secret(user)
    rep.add_secret(pw)
    ua = cfg["OsUserAgent"]
    http = http_session(ua)
    host = "api.opensubtitles.com"
    token: str | None = None

    def call(method: str, path: str, params: dict | None = None, body: dict | None = None, agent: str | None = None) -> Any:
        h = {"Api-Key": key, "Accept": "application/json", "User-Agent": agent or ua}
        if token:
            h["Authorization"] = f"Bearer {token}"
        try:
            r = http.request(method, f"https://{host}/api/v1{path}", params=params, json=body, headers=h, timeout=30)
        except Exception as e:  # noqa: BLE001
            rep.line(f"  [LỖI MẠNG] {e}")
            return None
        pause(1.1)
        return r

    rep.step("1. User-Agent nào được phép? (GET /infos/formats, không cần đăng nhập)")
    for agent in dict.fromkeys([ua, "MyApp v1.0.0", "MuxStudio v0.1.0"]):
        r = call("GET", "/infos/formats", agent=agent)
        code = getattr(r, "status_code", "?")
        body = (r.text or "")[:160].replace("\n", " ") if r is not None else ""
        rep.line(f"  UA '{agent}': {code} {body}")
        if agent == ua:
            rep.conclude(f"User-Agent đang cấu hình '{ua}': {'được phép' if code == 200 else f'bị chặn ({code})'}")

    rep.step("2. Danh sách mã ngôn ngữ (GET /infos/languages)")
    r = call("GET", "/infos/languages")
    d = rep.show(r, 400, show_body=False) if r is not None else None
    codes = {str(x.get("language_code")).lower() for x in ((r.json() if r is not None and r.status_code == 200 else {}) or {}).get("data", [])}
    if codes:
        miss = [c for c in OUR_LANGS if c not in codes]
        rep.line(f"  Mã có dạng khác: {sorted(c for c in codes if '-' in c)}")
        rep.conclude("mọi mã 2 chữ của MuxStudio đều có" if not miss else f"mã 2 chữ KHÔNG có: {miss} (cần đổi, vd zh → zh-cn)")

    if not user or not pw:
        rep.conclude("chưa có tài khoản → bỏ qua đăng nhập/quota")
    else:
        rep.step("3. Đăng nhập (POST /login)")
        r = call("POST", "/login", body={"username": user, "password": pw})
        d = rep.show(r, 800)
        if r is not None and r.status_code == 200 and d and d.get("token"):
            token = d["token"]
            rep.add_secret(token)
            host = str(d.get("base_url") or host).replace("https://", "").strip("/")
            u = d.get("user") or {}
            rep.conclude(f"đăng nhập OK: {'VIP' if u.get('vip') else 'thường'}, máy chủ {host}, "
                         f"allowed_downloads={u.get('allowed_downloads')}")
        else:
            rep.conclude(f"đăng nhập LỖI ({getattr(r, 'status_code', '?')})")

        if token:
            rep.step(f"4. Quota (GET /infos/user trên {host}, kèm token)")
            r = call("GET", "/infos/user")
            d = rep.show(r, 600)
            dd = (d or {}).get("data") or {}
            rep.conclude(f"còn {dd.get('remaining_downloads')}/{dd.get('allowed_downloads')} lượt tải")

    rep.step("5. Tìm: imdb_id=133093 (The Matrix), languages=vi, order_by=download_count")
    r = call("GET", "/subtitles", {"imdb_id": "133093", "languages": "vi", "order_by": "download_count", "order_direction": "desc"})
    d = rep.show(r, 1500)
    items = (d or {}).get("data") or []
    attrs = [x.get("attributes") or {} for x in items]
    rep.line(f"  total_count={(d or {}).get('total_count')} | trường attributes: {keys_of(attrs)}")
    need = ("release", "fps", "download_count", "hearing_impaired", "foreign_parts_only", "machine_translated",
            "ai_translated", "files")
    have = keys_of(attrs, 10)
    rep.conclude("đủ trường cần dùng" if all(n in have for n in need) else f"THIẾU trường: {[n for n in need if n not in have]}")
    rep.line(f"  Bản dịch máy/AI: {sum(1 for a in attrs if a.get('machine_translated') or a.get('ai_translated'))}/{len(attrs)}")

    rep.step("6. Phim bộ: imdb_id=903747 (Breaking Bad) season_number=1 episode_number=1 languages=en")
    r = call("GET", "/subtitles", {"imdb_id": "903747", "season_number": 1, "episode_number": 1, "languages": "en"})
    d = rep.show(r, 600, show_body=False)
    rep.conclude(f"tìm theo tập: {len((d or {}).get('data') or [])} bản")

    rep.step("7. Anime số tập tuyệt đối: query='One Piece' episode_number=643 (không có season) languages=en")
    r = call("GET", "/subtitles", {"query": "One Piece", "episode_number": 643, "languages": "en"})
    d = rep.show(r, 600, show_body=False)
    rels = [((x.get("attributes") or {}).get("release")) for x in (d or {}).get("data") or []][:5]
    rep.conclude(f"tìm tập tuyệt đối không season: {len((d or {}).get('data') or [])} bản, ví dụ {rels}")

    if download and token and items:
        fid = ((attrs[0].get("files") or [{}])[0]).get("file_id")
        rep.step(f"8. TẢI THỬ (tốn 1 lượt) file_id={fid}")
        r = call("POST", "/download", body={"file_id": fid})
        d = rep.show(r, 600)
        if d and d.get("link"):
            rep.conclude(f"tải OK, remaining={d.get('remaining')}, reset_time={d.get('reset_time')}")
    elif not download:
        rep.line()
        rep.line("  (Bỏ qua tải thử để không tốn quota. Muốn thử: python -m tools.probe_opensubs --download)")


if __name__ == "__main__":
    from core.secrets import Redactor

    cfg, sec = load_everything()
    rp = Reporter(None, Redactor(sec.values()))
    run(rp, cfg, sec, download="--download" in sys.argv)
