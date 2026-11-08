"""Chạy cả 4 script dò API, ghi kết quả (đã che key/token/mật khẩu) ra ``ket_qua_do_api.txt``.

    python -m tools.probe_all [--download]
"""
from __future__ import annotations

import datetime as dt
import sys
import traceback

from tools import probe_opensubs, probe_subsource, probe_tmdb, probe_tvdb
from tools.probe_common import ROOT, Reporter, load_everything

OUT = ROOT / "ket_qua_do_api.txt"


def main() -> int:
    from core.secrets import Redactor

    cfg, secrets = load_everything()
    rep = Reporter(OUT, Redactor(secrets.values()))
    rep.line(f"MuxStudio — kết quả dò API, {dt.datetime.now():%Y-%m-%d %H:%M}")
    rep.line(f"Bí mật tìm thấy trong Windows Credential Manager: {', '.join(sorted(secrets)) or '(không có)'}")
    rep.line(f"User-Agent OpenSubtitles: {cfg['OsUserAgent']}")
    jobs = [
        ("SubSource", lambda: probe_subsource.run(rep, cfg, secrets)),
        ("OpenSubtitles", lambda: probe_opensubs.run(rep, cfg, secrets, download="--download" in sys.argv)),
        ("TMDB", lambda: probe_tmdb.run(rep, cfg, secrets)),
        ("TVDB", lambda: probe_tvdb.run(rep, cfg, secrets)),
    ]
    for name, fn in jobs:
        try:
            fn()
        except Exception:  # noqa: BLE001
            rep.line(f"[LỖI khi dò {name}]")
            for ln in traceback.format_exc().splitlines():
                rep.line("  " + ln)
    rep.section("TÓM TẮT")
    for c in rep.conclusions:
        rep.line(f"  • {c}")
    rep.line()
    rep.line(f"Đã ghi: {OUT}")
    rep.line("Gửi file này cho Claude (key, token, mật khẩu đã được che).")
    rep.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
