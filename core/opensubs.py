"""OpenSubtitles.com API (§6.11) — nguồn dự phòng, TỐN QUOTA nên để sau cùng.

Thay ``Invoke-Os``, ``Get-ErrBody``, ``Connect-OpenSubtitles``, ``Test-OsQuota``,
``Get-OsCandidates``, ``Test-OsUsable``, ``Save-OsSubtitle`` (SubFetch).

* Gốc ``https://api.opensubtitles.com/api/v1``, header ``Api-Key``. User-Agent PHẢI nằm trong
  danh sách cho phép (``MyApp v1.0.0`` chạy được; tên riêng cần đăng ký API Consumer, nếu không:
  403 ``kong-user-agent-block``).
* ``POST /login`` → ``token`` + ``base_url``. VIP được chuyển sang ``vip-api.opensubtitles.com`` và
  MỌI request sau đó phải gửi kèm token. 401 → đăng nhập lại, tối đa 2 lần.
* Quota: cảnh báo khi còn ≤ ``OsWarnRemaining`` (50), ngừng khi còn ≤ ``OsStopRemaining`` (5).
"""
from __future__ import annotations

import os
import re
import tempfile
import time
from pathlib import Path
from typing import Any, Callable

from . import tools
from .tmdb import ConnStatus

try:
    import requests
    _NET_ERR: tuple[type[BaseException], ...] = (requests.RequestException,)
except Exception:  # pragma: no cover
    requests = None  # type: ignore[assignment]
    _NET_ERR = (OSError,)

DEFAULT_HOST = "api.opensubtitles.com"


class OpenSubtitlesClient:
    def __init__(self, api_key: str, username: str = "", password: str = "", user_agent: str = "MyApp v1.0.0",
                 pause_ms: int = 1100, warn_remaining: int = 50, stop_remaining: int = 5, http: Any = None,
                 sleep: Callable[[float], None] = time.sleep, job: tools.Job | None = None,
                 temp_dir: str | os.PathLike[str] | None = None):
        self.api_key = (api_key or "").strip()
        self.username = (username or "").strip()
        self.password = password or ""
        self.user_agent = user_agent or "MyApp v1.0.0"
        self.pause = max(0, int(pause_ms)) / 1000.0
        self.warn_remaining = warn_remaining
        self.stop_remaining = stop_remaining
        self.http = http if http is not None else (requests.Session() if requests else None)
        self.sleep = sleep
        self.job = job or tools.NULL_JOB
        self.temp_dir = Path(temp_dir) if temp_dir else None
        self.token: str | None = None
        self.host = DEFAULT_HOST
        self.remaining = -1
        self.allowed = -1
        self.vip: bool | None = None
        self.level = ""
        self.disabled = False
        self.reset_msg = ""
        self.relogins = 0
        self.last_status = 0
        self._warned_low = False

    # ------------------------------------------------------------------ goi API
    def _url(self, path: str) -> str:
        return f"https://{self.host}/api/v1{path}"

    def request(self, method: str, path: str, params: dict | None = None, body: dict | None = None) -> Any:
        """``Invoke-Os``. Dựng lại URL ở MỖI lần thử (sau khi đăng nhập lại, máy chủ/token có thể đổi)."""
        q = {k: str(v) for k, v in (params or {}).items() if v is not None and str(v) != ""}
        for attempt in range(1, 4):
            headers = {"Api-Key": self.api_key, "Accept": "application/json", "User-Agent": self.user_agent}
            if self.token:
                headers["Authorization"] = f"Bearer {self.token}"
            try:
                r = self.http.request(method, self._url(path), params=q or None, json=body, headers=headers, timeout=30)
            except _NET_ERR as e:
                if attempt < 3:
                    self.sleep(2 * attempt)
                else:
                    self.job.error(f"    [!] OpenSubtitles lỗi mạng: {e}")
                continue
            self.last_status = r.status_code
            if r.status_code == 200:
                try:
                    return r.json()
                except ValueError:
                    return None
            msg = re.sub(r"\s+", " ", r.text or "")[:300]
            if r.status_code == 403 and re.search(r"(?i)user-agent", msg):
                self.job.error("    [!] OpenSubtitles chặn User-Agent.")
                self.job.warn("        Đặt User-Agent theo dạng 'TênApp v1.0.0' (có đủ 3 số).")
                self.job.warn("        Nếu vẫn bị chặn, đăng ký tên app tại opensubtitles.com › API Consumers.")
                self.disabled = True
                return None
            if r.status_code == 401:
                self.job.warn(f"    [!] OpenSubtitles từ chối xác thực (401) tại {path}")
                if msg:
                    self.job.dim(f"        Máy chủ nói: {msg}")
                if path != "/login" and self.relogins < 2:
                    self.relogins += 1
                    self.job.warn(f"        Đang đăng nhập lại (lần {self.relogins})...")
                    self.token = None
                    self.sleep(2)
                    if self.login(quiet=True):
                        self.job.dim("        Đã lấy token mới, thử lại yêu cầu.")
                        continue
                self.job.error("        Không khắc phục được → tắt nguồn OpenSubtitles.")
                self.disabled = True
                return None
            if r.status_code == 429:
                self.job.warn("    [!] OpenSubtitles giới hạn tần suất, chờ 5 giây...")
                self.sleep(5)
                continue
            if 400 <= r.status_code < 500:
                if msg:
                    self.job.warn(f"    [!] OpenSubtitles {r.status_code} : {msg}")
                return None
            if attempt < 3:
                self.sleep(2 * attempt)
        return None

    # ------------------------------------------------------------------ dang nhap + quota
    def login(self, quiet: bool = False) -> bool:
        """``Connect-OpenSubtitles``. Không có tài khoản → vẫn TÌM được, nhưng không TẢI được."""
        if not self.api_key:
            if not quiet:
                self.job.dim("[i] Chưa có OpenSubtitles API key → bỏ qua OpenSubtitles.")
            return False
        if not self.username or not self.password:
            if not quiet:
                self.job.warn("[!] Thiếu tài khoản OpenSubtitles → chỉ tìm được, KHÔNG tải được.")
            return False
        r = self.request("POST", "/login", body={"username": self.username, "password": self.password})
        if not r or not r.get("token"):
            self.job.error("[!] Đăng nhập OpenSubtitles thất bại.")
            self.disabled = True
            return False
        self.token = str(r["token"])
        if r.get("base_url"):
            self.host = str(r["base_url"]).replace("https://", "").replace("http://", "").strip("/")
        user = r.get("user") or {}
        if user:
            try:
                self.allowed = int(user.get("allowed_downloads"))
            except (TypeError, ValueError):
                pass
            self.vip = bool(user.get("vip"))
            self.level = str(user.get("level") or "")
            if not quiet:
                kind = "VIP" if self.vip else "thường"
                self.job.ok(f"[i] OpenSubtitles: đã đăng nhập ({kind}, mức '{self.level}', {self.allowed} lượt tải/ngày)")
                self.job.dim(f"    Máy chủ: {self.host}")
        if not quiet:
            self.refresh_quota()
        return True

    def refresh_quota(self) -> None:
        u = self.request("GET", "/infos/user")
        data = (u or {}).get("data") or {}
        if data.get("remaining_downloads") is not None:
            self.remaining = int(data["remaining_downloads"])
        if data.get("allowed_downloads") is not None:
            self.allowed = int(data["allowed_downloads"])
        if data:
            self.job.info(f"    Còn lại hôm nay: {self.remaining}/{self.allowed} lượt tải")

    def quota_ok(self) -> bool:
        """``Test-OsQuota``: chưa biết → cứ thử; ≤ ngưỡng ngừng → tắt nguồn; ≤ ngưỡng cảnh báo → báo."""
        if self.remaining < 0:
            return True
        if self.remaining <= self.stop_remaining:
            if not self.disabled:
                self.job.error(f"[!!] OpenSubtitles chỉ còn {self.remaining} lượt tải — DỪNG dùng nguồn này.")
                if self.reset_msg:
                    self.job.warn(f"     {self.reset_msg}")
                self.disabled = True
            return False
        if self.remaining <= self.warn_remaining:
            self.job.warn(f"    [!] Chú ý: OpenSubtitles chỉ còn {self.remaining}/{self.allowed} lượt tải hôm nay.")
        return True

    @property
    def quota_level(self) -> str:
        """'ok' | 'warn' | 'stop' | 'unknown' — để tô màu trên giao diện."""
        if self.remaining < 0:
            return "unknown"
        if self.remaining <= self.stop_remaining:
            return "stop"
        if self.remaining <= self.warn_remaining:
            return "warn"
        return "ok"

    # ------------------------------------------------------------------ tim + tai
    def search(self, imdb: str | None, season: int | None, episode: int | None, lang: str,
               title: str = "", year: int | None = None) -> list[dict]:
        """``Get-OsCandidates``: (1) có ``order_by`` → (2) bỏ sắp xếp → (3) theo tên + năm."""
        if self.disabled:
            return []
        p: dict[str, Any] = {"languages": lang}
        if imdb:
            p["imdb_id"] = re.sub(r"^tt", "", imdb)
        elif title:
            p["query"] = title
            if year:
                p["year"] = year
        else:
            return []
        if episode is not None:
            if season:
                p["season_number"] = season
            p["episode_number"] = episode
        r = self.request("GET", "/subtitles", {**p, "order_by": "download_count", "order_direction": "desc"})
        if r and r.get("data"):
            return list(r["data"])
        if self.disabled:
            return []
        self.sleep(self.pause)
        r = self.request("GET", "/subtitles", p)
        if r and r.get("data"):
            return list(r["data"])
        if "imdb_id" in p and title and not self.disabled:
            self.sleep(self.pause)
            p3 = {k: v for k, v in p.items() if k != "imdb_id"}
            p3["query"] = title
            if year:
                p3["year"] = year
            r = self.request("GET", "/subtitles", p3)
            if r and r.get("data"):
                self.job.dim("        (tìm theo tên thay vì IMDb)")
                return list(r["data"])
        return []

    def download(self, item: dict) -> Path | None:
        """``Save-OsSubtitle``: ``POST /download {file_id}`` → link tạm → GET. Trả file tạm (chưa đặt vào đích)."""
        if self.disabled or not self.quota_ok():
            return None
        if not self.token:
            self.job.warn("        [!] Chưa đăng nhập → không tải được.")
            return None
        files = ((item.get("attributes") or {}).get("files")) or []
        try:
            file_id = int(files[0].get("file_id"))
        except (IndexError, TypeError, ValueError, AttributeError):
            return None
        self.sleep(self.pause)
        self.job.dim(f"        POST /download file_id={file_id} | máy chủ {self.host} | có token ({len(self.token)} ký tự)")
        d = self.request("POST", "/download", body={"file_id": file_id})
        if not d or not d.get("link"):
            if d and d.get("message"):
                self.job.warn(f"        [!] {d['message']}")
            return None
        if d.get("remaining") is not None:
            try:
                self.remaining = int(d["remaining"])
            except (TypeError, ValueError):
                pass
        if d.get("message"):
            self.reset_msg = str(d["message"])
        elif d.get("reset_time"):
            self.reset_msg = f"Quota sẽ hồi lại sau {d['reset_time']}"
        tmp_dir = self.temp_dir or Path(tempfile.mkdtemp(prefix="muxstudio_os_"))
        tmp_dir.mkdir(parents=True, exist_ok=True)
        out = tmp_dir / f"os_{file_id}.srt"
        try:
            r = self.http.request("GET", d["link"], headers={"User-Agent": self.user_agent}, timeout=60)
            if r.status_code != 200:
                raise OSError(f"HTTP {r.status_code}")
            out.write_bytes(r.content)
        except (OSError, *_NET_ERR) as e:
            self.job.error(f"        [!] Tải file thất bại: {e}")
            return None
        if out.stat().st_size < 64:
            out.unlink(missing_ok=True)
            self.job.warn("        [!] File tải về rỗng.")
            return None
        self.job.dim(f"        (còn {self.remaining}/{self.allowed} lượt tải hôm nay)")
        self.quota_ok()
        return out

    # ------------------------------------------------------------------ kiem tra ket noi
    def check(self) -> ConnStatus:
        if not self.api_key:
            return ConnStatus(None, "OpenSubtitles: chưa điền key")
        if not self.username or not self.password:
            r = self.request("GET", "/infos/formats")
            if r is None:
                return ConnStatus(False, f"OpenSubtitles: key/User-Agent bị từ chối ({self.last_status})")
            return ConnStatus(True, "OpenSubtitles: key hợp lệ (chưa có tài khoản → chỉ tìm, không tải)")
        if not self.login(quiet=True):
            return ConnStatus(False, f"OpenSubtitles: đăng nhập lỗi ({self.last_status})")
        self.refresh_quota()
        kind = "VIP" if self.vip else "thường"
        host = self.host.split(".")[0]
        q = f"còn {self.remaining}/{self.allowed} lượt tải" if self.remaining >= 0 else "chưa rõ quota"
        return ConnStatus(True, f"OpenSubtitles: đăng nhập {kind}, máy chủ {host}, {q}")


# ======================================================================
#  Hàm không cần mạng
# ======================================================================


def is_machine(item: dict) -> bool:
    a = item.get("attributes") or {}
    return a.get("machine_translated") is True or a.get("ai_translated") is True


def usable(item: dict, exclude_sdh: bool = True, exclude_forced: bool = True, exclude_machine: bool = True) -> bool:
    """``Test-OsUsable``: bỏ SDH / Forced / dịch máy (nếu ``OsExcludeMachine``) / không có file."""
    a = item.get("attributes")
    if not a:
        return False
    if exclude_sdh and a.get("hearing_impaired") is True:
        return False
    if exclude_forced and a.get("foreign_parts_only") is True:
        return False
    if exclude_machine and is_machine(item):
        return False
    return bool(a.get("files"))
