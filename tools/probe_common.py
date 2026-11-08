"""Phần dùng chung của các script dò API: in phản hồi đã che bí mật, ghi ra file kết quả."""
from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from core import config as C  # noqa: E402
from core import secrets as S  # noqa: E402

INTERESTING_HEADERS = ("content-type", "ratelimit", "rate-limit", "retry-after", "x-kong", "x-request-id",
                       "content-disposition", "content-length", "cf-ray", "via", "server")


class Reporter:
    """In ra màn hình và ghi vào file, MỌI dòng đều đi qua bộ che bí mật."""

    def __init__(self, out_path: Path | None, redactor: S.Redactor):
        self.out_path = out_path
        self.redactor = redactor
        self._fh = open(out_path, "w", encoding="utf-8") if out_path else None
        self.conclusions: list[str] = []

    def close(self) -> None:
        if self._fh:
            self._fh.close()

    def add_secret(self, value: str | None) -> None:
        self.redactor.add(value)

    def line(self, text: str = "") -> None:
        t = self.redactor.redact(str(text))
        t = re.sub(r"(api_key=)[^&\s\"']+", r"\1•••", t)
        t = re.sub(r"(?i)(\"(?:token|password|apikey|api_key)\"\s*:\s*\")[^\"]+", r"\1•••", t)
        t = re.sub(r"(?i)(Bearer\s+)[A-Za-z0-9._\-]+", r"\1•••", t)
        try:
            print(t)
        except UnicodeEncodeError:
            print(t.encode("ascii", "replace").decode("ascii"))
        if self._fh:
            self._fh.write(t + "\n")
            self._fh.flush()

    def section(self, title: str) -> None:
        self.line()
        self.line("=" * 70)
        self.line(f"  {title}")
        self.line("=" * 70)

    def step(self, title: str) -> None:
        self.line()
        self.line(f"--- {title}")

    def conclude(self, text: str) -> None:
        self.conclusions.append(text)
        self.line(f"  => {text}")

    def show(self, r: Any, max_chars: int = 1500, show_body: bool = True) -> Any:
        """In status + header quan trọng + JSON thô (cắt bớt). Trả JSON (hoặc None)."""
        if r is None:
            self.line("  (không có phản hồi)")
            return None
        url = getattr(r, "url", "")
        self.line(f"  URL     : {url}")
        self.line(f"  Status  : {r.status_code}")
        hs = {k: v for k, v in (r.headers or {}).items() if any(h in k.lower() for h in INTERESTING_HEADERS)}
        for k, v in hs.items():
            self.line(f"  Header  : {k}: {v}")
        data = None
        ctype = (r.headers or {}).get("Content-Type", "")
        if "json" in ctype or (r.content[:1] in (b"{", b"[")):
            try:
                data = r.json()
            except ValueError:
                data = None
        if show_body:
            if data is not None:
                txt = json.dumps(data, ensure_ascii=False, indent=1)
            else:
                txt = f"(dữ liệu nhị phân, {len(r.content)} byte, 8 byte đầu: {r.content[:8]!r})" if r.content and \
                    not ctype.startswith("text") else (r.text or "")
            if len(txt) > max_chars:
                txt = txt[:max_chars] + f"\n  ... (cắt bớt, tổng {len(txt)} ký tự)"
            for ln in txt.splitlines():
                self.line("  │ " + ln)
        return data


def load_everything() -> tuple[Any, dict[str, str]]:
    """Cấu hình (config.json) + bí mật (Windows Credential Manager). Biến môi trường
    ``MUXSTUDIO_<TEN>`` ghi đè bí mật (vd ``MUXSTUDIO_TMDBAPIKEY``)."""
    cfg = C.load_config()
    try:
        secrets = S.store.all_values()
    except S.SecretStoreError as e:
        print(f"[!] {e}")
        secrets = {}
    for k in S.SECRET_KEYS:
        v = os.environ.get(f"MUXSTUDIO_{k.upper()}")
        if v:
            secrets[k] = v
    return cfg, secrets


def http_session(user_agent: str = "MyApp v1.0.0"):
    import requests

    s = requests.Session()
    s.headers["User-Agent"] = user_agent
    return s


def pause(sec: float = 1.1) -> None:
    time.sleep(sec)


def keys_of(items: Iterable[dict], limit: int = 3) -> list[str]:
    out: list[str] = []
    for it in list(items)[:limit]:
        for k in it.keys():
            if k not in out:
                out.append(k)
    return out
