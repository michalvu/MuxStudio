"""Thành phần giao diện dùng chung.

* Phần THUẦN (test được không cần màn hình, không cần tkinter): ``ellipsize_middle`` (thay ``Limit-Text`` của
  SubFetch), ``fit_text`` / ``shorten_name`` (rút gọn tên file dài cho vừa ô mà GIỮ số tập + đuôi tên),
  ``plan_window`` / ``ui_factor`` (cỡ cửa sổ, mật độ bố cục theo màn hình 720p / 1080p / 1440p), ``GridModel`` (logic bảng chọn phụ đề theo tập: tick ô / cả cột / cả dòng, "Trả về mặc định"),
  ``Runner`` (chạy việc nặng ở luồng phụ, log + tiến độ qua ``queue``, giao diện đọc bằng ``after(100)``,
  nút Dừng), ``open_path``, ``resource_path`` / ``is_frozen`` (file đi kèm — cả khi chạy bản ``.exe``).
* Phần Tk: ``setup_style`` (màu ``ttk.Treeview`` hợp CustomTkinter, sáng/tối), ``Table`` (Treeview có cột ☑/☐ —
  không tạo ``CTkCheckBox`` cho từng ô), ``CheckGrid`` (lưới ☑/☐ vẽ trên Canvas: ô xám = không có, ô vàng = khác
  quy tắc tự động — Treeview không tô màu được từng ô), ``Tooltip``, các hộp thoại nhỏ.

tkinter chỉ được dùng bên trong hàm/phương thức (không kế thừa lớp Tk ở cấp module), nên ``import gui.widgets``
vẫn chạy được trên máy thiếu tkinter (test của phần thuần).
"""
from __future__ import annotations

import math
import os
import queue
import re
import subprocess
import sys
import threading
import traceback
from dataclasses import dataclass
from typing import Any, Callable, Iterable, Sequence

from core import tools

try:  # pragma: no cover - phu thuoc may
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk

    import customtkinter as ctk
except Exception:  # pragma: no cover - may khong co tkinter (chi chay test phan thuan)
    tk = ttk = filedialog = messagebox = ctk = None  # type: ignore[assignment]

CHECK_ON = "☑"
CHECK_OFF = "☐"
NO_CELL = "—"

# Mau (sang, toi) — dung cho khung log va bang
LEVEL_COLORS: dict[str, tuple[str, str]] = {
    "info": ("#1f1f1f", "#e6e6e6"),
    "ok": ("#1b7a2f", "#7ddc85"),
    "warn": ("#9a6200", "#ffcc66"),
    "error": ("#c62828", "#ff7b72"),
    "dim": ("#6b6b6b", "#9aa0a6"),
    "head": ("#1a5fb4", "#8ab4f8"),
}
ROW_BG: dict[str, tuple[str, str]] = {
    "need": ("#fff3c4", "#4a3d00"),  # Can chon / khac quy tac
    "bad": ("#fde0dc", "#5a1f1f"),
    "good": ("#e3f5e1", "#1d3b21"),
    "absent": ("#e2e2e2", "#3a3a3a"),
}


# ==========================================================================
#  PHẦN THUẦN
# ==========================================================================


def ellipsize_middle(text: str | None, n: int) -> str:
    """``Limit-Text``: cắt cho vừa ô, giữ phần ĐUÔI (tên nhóm release nằm ở cuối)."""
    if not text:
        return ""
    if len(text) <= n:
        return text
    if n < 12:
        return text[:n]
    head = int(round((n - 3) * 0.55))  # [int] cua PowerShell = lam tron ve so chan, nhu round()
    tail = n - 3 - head
    return text[:head] + "..." + text[len(text) - tail:]


ELL = "…"
#: Mốc số tập trong tên file (để rút gọn tên dài mà KHÔNG mất số tập): S01E05, S01E05-E06, 1x05, " - 0643", EP 055
_EP_RX = re.compile(r"(?i)S\d{1,2}\s?E\d{1,4}(?:-?E\d{1,4})?|(?<!\d)\d{1,2}x\d{2}(?!\d)"
                    r"|\s-\s(?:E|EP)?\d{1,4}(?:v\d)?(?=\s|$|\.|\[|\()|\b(?:Episode|Ep|Tap)\s?\d{1,4}\b")

#: Duoi file giu nguyen khi rut gon: .mkv, .srt, .vi.srt, .cs.ass
_EXT_RX = re.compile(r"(?:\.[a-z]{2,3})?\.(?:mkv|mp4|m4v|avi|ts|mov|mka|srt|ass|ssa|vtt|sup)$", re.I)

Measure = Callable[[str], float]


def _fit_end(text: str, width: float, measure: Measure) -> str:
    """Cắt đuôi + "…" cho vừa ``width`` (không vừa nổi "…" → chuỗi rỗng)."""
    if measure(text) <= width:
        return text
    if measure(ELL) > width:
        return ""
    lo, hi, best = 0, len(text) - 1, ELL
    while lo <= hi:
        n = (lo + hi) // 2
        cand = text[:n].rstrip() + ELL
        if measure(cand) <= width:
            best, lo = cand, n + 1
        else:
            hi = n - 1
    return best


def _fit_middle(text: str, width: float, measure: Measure, head_ratio: float = 0.55) -> str:
    """Bỏ phần giữa, giữ đầu (``head_ratio``) và đuôi (tên nhóm release, đuôi file)."""
    if measure(text) <= width:
        return text
    if measure(ELL) > width:
        return ""
    lo, hi, best = 0, len(text) - 1, ELL
    while lo <= hi:
        n = (lo + hi) // 2
        h = int(math.ceil(n * head_ratio))
        t = n - h
        cand = text[:h] + ELL + (text[len(text) - t:] if t else "")
        if measure(cand) <= width:
            best, lo = cand, n + 1
        else:
            hi = n - 1
    return best


def fit_text(text: Any, width: float, measure: Measure = len, mode: str = "end") -> str:
    """Rút gọn chữ cho vừa ô rộng ``width`` (đơn vị của ``measure``: pixel, hoặc số ký tự khi ``measure=len``).

    * ``end``: cắt đuôi + "…" (cột trạng thái, ghi chú).
    * ``name``: tên file — GIỮ số tập (``S01E05``, ``- 0643``…) và phần đuôi (nhóm release, đuôi file), bỏ phần giữa:
      ``[Judas] Sousou no Frieren - S01E05 - The Jour…[Multi-Subs].mkv``. Không có số tập → bỏ phần giữa.
    """
    s = "" if text is None else str(text)
    if not s or measure(s) <= width:
        return s
    if mode != "name":
        return _fit_end(s, width, measure)
    ext = _EXT_RX.search(s)
    if ext and measure(ext.group(0)) * 4 <= width:  # giu nguyen duoi file (.mkv, .vi.srt) neu con cho
        e = ext.group(0)
        return _fit_name(s[: ext.start()], width - measure(e), measure) + e
    return _fit_name(s, width, measure)


def _fit_name(s: str, width: float, measure: Measure) -> str:
    if measure(s) <= width:
        return s
    m = _EP_RX.search(s)
    if m:
        head, rest = s[: m.end()], s[m.end():]
        if measure(head + ELL) <= width:
            return head + _fit_middle(rest, width - measure(head), measure, 0.4)
        tok = s[m.start(): m.end()]
        if measure(ELL + tok + ELL) <= width:  # ten series qua dai: rut gon ten, van giu so tap
            pre = _fit_end(s[: m.start()], (width - measure(tok)) * (0.6 if rest else 1.0), measure)
            left = width - measure(pre) - measure(tok)
            tail = _fit_middle(rest, left, measure, 0.4) if rest and left >= measure(ELL) else ""
            return pre + tok + tail
    return _fit_middle(s, width, measure)


def shorten_name(text: str | None, n: int) -> str:
    """Tên file rút gọn còn tối đa ``n`` ký tự, giữ số tập + đuôi (dùng cho nhãn, tiêu đề)."""
    return fit_text(text or "", n, len, "name")


@dataclass(frozen=True)
class WindowPlan:
    """Kích thước cửa sổ chính theo màn hình (đơn vị LOGIC = pixel thật ÷ tỉ lệ phóng to của Windows)."""

    width: int
    height: int
    min_width: int
    min_height: int
    zoomed: bool
    density: str  # compact (720p, 1080p @150%) | normal (1080p) | large (1440p+)


def density_for(screen_h: float) -> str:
    if screen_h < 900:
        return "compact"
    if screen_h >= 1250:
        return "large"
    return "normal"


def plan_window(screen_w: float, screen_h: float, saved: str = "", zoomed: bool = False) -> WindowPlan:
    """Cửa sổ vừa màn hình: 720p (và 1080p đặt phóng to 150%) → phóng to hết màn hình; 1080p / 1440p → ~86% màn
    hình, tối đa 1900×1250. Kích thước lần trước (``saved`` = ``RxC``) chỉ dùng lại nếu còn vừa màn hình."""
    sw, sh = max(640, int(screen_w)), max(480, int(screen_h))
    avail_w, avail_h = sw - 16, sh - 72  # chua thanh tac vu + thanh tieu de
    dens = density_for(sh)
    min_w, min_h = min(960, avail_w), min(560, avail_h)
    w = int(min(max(1060, sw * 0.86), 1900, avail_w))
    h = int(min(max(680, sh * 0.86), 1250, avail_h))
    small = sw < 1400 or sh < 860
    mt = re.fullmatch(r"\s*(\d+)x(\d+)(?:[+-]\d+[+-]\d+)?\s*", saved or "")
    if mt:
        sw_, sh_ = int(mt.group(1)), int(mt.group(2))
        if min_w <= sw_ <= avail_w and min_h <= sh_ <= avail_h:
            w, h = sw_, sh_
    if small:
        w, h = avail_w, avail_h
    return WindowPlan(w, h, min_w, min_h, bool(zoomed or small), dens)


def ui_factor(choice: str, screen_w: float, screen_h: float) -> float:
    """Ô "Cỡ chữ và giao diện": ``auto`` → 1,1 trên màn hình lớn (1440p / 4K đặt 100%), còn lại 1,0; hoặc số %."""
    c = str(choice or "auto").strip().lower().rstrip("%")
    if c == "auto":
        return 1.1 if screen_w >= 2400 and screen_h >= 1300 else 1.0
    try:
        return max(0.75, min(2.0, int(c) / 100))
    except ValueError:
        return 1.0


def level_color(level: str, dark: bool) -> str:
    pair = LEVEL_COLORS.get(level, LEVEL_COLORS["info"])
    return pair[1] if dark else pair[0]


def open_path(path: str | os.PathLike[str]) -> bool:
    """Mở thư mục/file bằng chương trình mặc định (Explorer trên Windows)."""
    p = os.fspath(path)
    if not os.path.exists(p):
        return False
    try:
        if sys.platform.startswith("win"):
            os.startfile(p)  # type: ignore[attr-defined]  # noqa: S606
        elif sys.platform == "darwin":
            subprocess.Popen(["open", p])  # noqa: S603,S607
        else:
            subprocess.Popen(["xdg-open", p], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)  # noqa: S603,S607
        return True
    except OSError:
        return False


def is_frozen() -> bool:
    """Đang chạy bản đóng gói (``MuxStudio.exe`` do PyInstaller tạo), không phải từ mã nguồn."""
    return bool(getattr(sys, "frozen", False))


def resource_path(*parts: str) -> str:
    """Đường dẫn tới file dữ liệu đi kèm chương trình (vd ``assets/muxstudio.ico``): bản ``.exe`` → thư mục
    ``_internal`` của PyInstaller (``sys._MEIPASS``); chạy từ mã nguồn → thư mục gốc repo."""
    base = getattr(sys, "_MEIPASS", None) if is_frozen() else None
    if not base:
        base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, *parts)


ICON_ICO = ("assets", "muxstudio.ico")
ICON_PNG = ("assets", "muxstudio.png")


def set_window_icon(win: Any, default: bool = False) -> bool:
    """Đặt icon MuxStudio cho cửa sổ (thay icon CustomTkinter). Windows: ``iconbitmap(.ico)`` — gọi NGAY khi tạo
    cửa sổ: CustomTkinter chỉ tự đặt icon của nó (sau 200 ms) khi ``iconbitmap`` chưa được gọi. Nơi khác:
    ``iconphoto(.png)`` (giữ ảnh trên cửa sổ để không bị thu hồi). Thiếu file / lỗi → bỏ qua, trả ``False``."""
    try:
        if sys.platform.startswith("win"):
            ico = resource_path(*ICON_ICO)
            if not os.path.isfile(ico):
                return False
            win.iconbitmap(ico)
            return True
        png = resource_path(*ICON_PNG)
        if not os.path.isfile(png):
            return False
        img = tk.PhotoImage(master=win, file=png)
        win.iconphoto(default, img)
        win._muxstudio_icon = img  # noqa: SLF001 - giu tham chieu
        return True
    except Exception:  # noqa: BLE001 - icon khong quan trong bang chuong trinh chay duoc
        return False


class GridModel:
    """Logic của bảng chọn phụ đề theo tập (``Show-SubPickerGrid``), không phụ thuộc giao diện.

    ``cells[(r, c)]``: ``True``/``False`` = ô có, đang tick / không tick; không có khoá = tập đó KHÔNG có loại phụ đề
    này (ô xám). ``auto[(r, c)]`` = quyết định của quy tắc tự động (ô khác ``auto`` hiện vàng).
    """

    def __init__(self, rows: Sequence[str], cols: Sequence[str], auto: dict[tuple[str, str], bool],
                 current: dict[tuple[str, str], bool] | None = None):
        self.rows = list(rows)
        self.cols = list(cols)
        self.auto = dict(auto)
        self.cells: dict[tuple[str, str], bool] = dict(auto)
        if current:
            for k, v in current.items():
                if k in self.cells:
                    self.cells[k] = bool(v)

    def present(self, r: str, c: str) -> bool:
        return (r, c) in self.cells

    def get(self, r: str, c: str) -> bool | None:
        return self.cells.get((r, c))

    def differs(self, r: str, c: str) -> bool:
        k = (r, c)
        return k in self.cells and self.cells[k] != self.auto.get(k)

    def toggle_cell(self, r: str, c: str) -> bool:
        k = (r, c)
        if k not in self.cells:
            return False
        self.cells[k] = not self.cells[k]
        return True

    def _toggle_many(self, keys: list[tuple[str, str]]) -> bool:
        keys = [k for k in keys if k in self.cells]
        if not keys:
            return False
        new = not all(self.cells[k] for k in keys)  # con o chua tick -> tick het; da tick het -> bo het
        for k in keys:
            self.cells[k] = new
        return True

    def toggle_col(self, c: str) -> bool:
        """Bấm tiêu đề cột → tick/bỏ cả cột."""
        return self._toggle_many([(r, c) for r in self.rows])

    def toggle_row(self, r: str) -> bool:
        """Bấm tên tập → tick/bỏ cả dòng."""
        return self._toggle_many([(r, c) for c in self.cols])

    def set_all(self, state: bool) -> None:
        for k in self.cells:
            self.cells[k] = state

    def reset(self) -> None:
        """Nút "Trả về mặc định"."""
        self.cells = dict(self.auto)

    def row_values(self, r: str) -> dict[str, bool]:
        return {c: self.cells[(r, c)] for c in self.cols if (r, c) in self.cells}

    def row_differs(self, r: str) -> bool:
        return any(self.differs(r, c) for c in self.cols)

    def changed_rows(self) -> list[str]:
        return [r for r in self.rows if self.row_differs(r)]


class Runner:
    """Chạy MỘT việc nặng ở luồng phụ (giao diện không bao giờ đơ).

    * Log và tiến độ đi qua ``queue.Queue``; ``poll()`` (gọi bằng ``after(100)``) đọc ra và gọi hàm hiển thị.
    * ``fn(job)`` chạy ở luồng phụ; ``on_done(kết_quả)`` / ``on_error(lỗi)`` chạy ở luồng giao diện.
    * Nút **Dừng sau file này** → ``stop_after_current()``; **Dừng ngay** → ``cancel_now()`` (kết thúc tiến trình con,
      chạy ở luồng riêng vì có thể chờ tới 3 s).
    * ``schedule(ms, cb)`` = ``widget.after``; ``None`` (test) → tự gọi ``poll()``.
    """

    POLL_MS = 100

    def __init__(self, schedule: Callable[[int, Callable[[], None]], Any] | None,
                 log: Callable[[str, str], None], progress: Callable[[int, int, str], None] | None = None,
                 on_busy: Callable[[bool, str], None] | None = None, redact: Callable[[str], str] | None = None):
        self.schedule = schedule
        self.log = log
        self.progress = progress
        self.on_busy = on_busy
        self.redact = redact
        self.q: queue.Queue = queue.Queue()
        self.job: tools.Job | None = None
        self.title = ""
        self._thread: threading.Thread | None = None
        self._polling = False
        self._waiting: list[tuple[str, Any, Any, Any]] = []

    @property
    def busy(self) -> bool:
        return self.job is not None

    def start(self, title: str, fn: Callable[[tools.Job], Any], on_done: Callable[[Any], None] | None = None,
              on_error: Callable[[BaseException], None] | None = None, when_free: bool = False) -> bool:
        """Bắt đầu một việc (chỉ một việc mỗi lúc). Đang có việc khác: ``when_free`` → xếp hàng, chạy ngay khi việc
        đang chạy xong (dùng cho việc tự động như đếm file, kiểm tra key); không thì báo và trả ``False``."""
        if self.busy:
            if when_free:
                if all(w[0] != title for w in self._waiting):
                    self._waiting.append((title, fn, on_done, on_error))
                return True
            self.log("warn", f"[!] Đang chạy \"{self.title}\" — đợi xong (hoặc bấm Dừng) rồi bấm lại.")
            return False
        q = self.q
        job = tools.Job(log=lambda lv, m: q.put(("log", lv, m)),
                        progress=lambda d, t, lb: q.put(("prog", d, t, lb)), redact=self.redact)
        self.job = job
        self.title = title
        if self.on_busy:
            self.on_busy(True, title)
        th = threading.Thread(target=self._work, args=(fn, job, on_done, on_error), name=f"muxstudio-{title}", daemon=True)
        self._thread = th
        th.start()
        self._ensure_polling()
        return True

    def _work(self, fn: Callable[[tools.Job], Any], job: tools.Job, on_done: Any, on_error: Any) -> None:
        try:
            res = fn(job)
        except tools.Cancelled:
            self.q.put(("end", "cancel", None, on_done, on_error))
        except BaseException as e:  # noqa: BLE001 - bao cho nguoi dung, khong de mat luong
            tb = traceback.format_exc()
            self.q.put(("end", "error", (e, tb), on_done, on_error))
        else:
            self.q.put(("end", "ok", res, on_done, on_error))

    def _ensure_polling(self) -> None:
        if self.schedule is not None and not self._polling:
            self._polling = True
            self.schedule(self.POLL_MS, self._tick)

    def _tick(self) -> None:
        self._polling = False
        self.poll()
        if self.busy or not self.q.empty():
            self._ensure_polling()

    def poll(self, max_items: int = 500) -> int:
        """Đọc hàng đợi (luồng giao diện). Trả số mục đã xử lý."""
        n = 0
        last_prog = None
        while n < max_items:
            try:
                item = self.q.get_nowait()
            except queue.Empty:
                break
            n += 1
            kind = item[0]
            if kind == "log":
                self.log(item[1], item[2])
            elif kind == "prog":
                last_prog = item[1:]
            elif kind == "end":
                if last_prog and self.progress:
                    self.progress(*last_prog)
                    last_prog = None
                self._finish(*item[1:])
        if last_prog and self.progress:
            self.progress(*last_prog)
        return n

    def _finish(self, status: str, payload: Any, on_done: Any, on_error: Any) -> None:
        title = self.title
        self.job = None
        self.title = ""
        self._thread = None
        if self.on_busy:
            self.on_busy(False, title)
        try:
            if status == "ok":
                if on_done:
                    on_done(payload)
            elif status == "cancel":
                self.log("warn", f"[!] Đã dừng ngay: {title}.")
                if on_error:
                    on_error(tools.Cancelled())
            else:
                e, tb = payload
                self.log("error", f"[LỖI] {title}: {e}")
                for ln in tb.strip().splitlines()[-6:]:
                    self.log("dim", "    " + ln)
                if on_error:
                    on_error(e)
        except Exception as e:  # noqa: BLE001 - loi trong ham hien thi khong duoc lam chet vong poll
            self.log("error", f"[LỖI giao diện] {e}")
            for ln in traceback.format_exc().strip().splitlines()[-6:]:
                self.log("dim", "    " + ln)
        if not self.busy and self._waiting:
            nxt = self._waiting.pop(0)
            self.start(*nxt)

    def stop_after_current(self) -> None:
        if self.job:
            self.job.stop_after_current.set()
            self.log("warn", "[i] Sẽ dừng sau file đang chạy…")

    def cancel_now(self) -> None:
        job = self.job
        self._waiting.clear()
        if job:
            self.log("warn", "[!] Đang dừng ngay (kết thúc tiến trình con)…")
            threading.Thread(target=job.cancel_now, daemon=True).start()

    def wait(self, timeout: float = 30.0) -> None:
        """Cho test: chờ luồng phụ xong (kể cả việc được nối tiếp trong ``on_done``) rồi xử lý hết hàng đợi."""
        import time  # noqa: PLC0415

        end = time.monotonic() + timeout
        while time.monotonic() < end:
            th = self._thread
            if th is not None:
                th.join(max(0.0, end - time.monotonic()))
            self.poll()
            if not self.busy and self.q.empty():
                return
            time.sleep(0.005)


# ==========================================================================
#  PHẦN Tk
# ==========================================================================


def is_dark() -> bool:
    try:
        return ctk.get_appearance_mode().lower() == "dark"
    except Exception:  # pragma: no cover
        return False


def pick(pair: tuple[str, str]) -> str:
    return pair[1] if is_dark() else pair[0]


_USER_FACTOR = 1.0  # "Co chu va giao dien" (nhan them vao ti le phong to cua Windows)


def scaling(widget: Any) -> float:
    """Tỉ lệ pixel thật / đơn vị logic của widget (= phóng to của Windows × "Cỡ chữ và giao diện")."""
    try:
        return float(ctk.ScalingTracker.get_widget_scaling(widget))
    except Exception:
        return _USER_FACTOR


def px(widget: Any, n: float) -> int:
    """Kích thước logic → pixel thật (độ rộng cột Treeview, Canvas… không tự phóng to như widget CustomTkinter)."""
    return int(round(n * scaling(widget)))


def dpi_scaling(root: Any) -> float:
    try:
        return float(ctk.ScalingTracker.window_dpi_scaling_dict.get(root, 1.0))
    except Exception:  # pragma: no cover
        return 1.0


def apply_user_scale(root: Any, factor: float) -> None:
    """Áp "Cỡ chữ và giao diện" (gọi ngay sau khi tạo cửa sổ chính, trước khi dựng widget)."""
    from tkinter import font as tkfont  # noqa: PLC0415

    global _USER_FACTOR
    _USER_FACTOR = factor
    st = ctk.ScalingTracker
    if abs(st.widget_scaling - factor) > 1e-3 or abs(st.window_scaling - factor) > 1e-3:
        ctk.set_widget_scaling(factor)
        ctk.set_window_scaling(factor)
        # CustomTkinter tam khoa co toi da = co cu (600x500) trong 1 giay sau khi doi ti le -> mo khoa ngay, khong
        # thi lenh dat co cua so ngay sau do bi bo qua (cua so mo ra be ti)
        try:
            root.maxsize(100000, 100000)
        except Exception:  # pragma: no cover
            pass
    if abs(factor - 1.0) < 0.01:
        return
    for name in ("TkDefaultFont", "TkTextFont", "TkHeadingFont", "TkMenuFont", "TkTooltipFont", "TkCaptionFont",
                 "TkFixedFont"):
        try:
            f = tkfont.nametofont(name, root=root)
            size = int(f.cget("size"))
            f.configure(size=int(round(size * factor)) or size)
        except Exception:  # pragma: no cover
            pass


def screen_logical(root: Any) -> tuple[float, float]:
    """Kích thước màn hình theo đơn vị logic (cửa sổ CustomTkinter tính bằng đơn vị này)."""
    try:
        sc = float(ctk.ScalingTracker.get_window_scaling(root))
    except Exception:  # pragma: no cover
        sc = 1.0
    return root.winfo_screenwidth() / sc, root.winfo_screenheight() / sc


def _theme_color(widget: str, key: str, fallback: tuple[str, str]) -> str:
    try:
        v = ctk.ThemeManager.theme[widget][key]
        if isinstance(v, (list, tuple)):
            return v[1] if is_dark() else v[0]
        return str(v)
    except Exception:
        return pick(fallback)


def font_family() -> str:
    """Phông có đủ dấu tiếng Việt: Segoe UI (Windows 10/11), DejaVu Sans (Linux); macOS giữ mặc định."""
    if sys.platform.startswith("win"):
        return "Segoe UI"
    if sys.platform == "darwin":
        return ""
    return "DejaVu Sans"


def mono_family() -> str:
    return "Consolas" if sys.platform.startswith("win") else "DejaVu Sans Mono"


def setup_fonts(root: Any) -> None:
    """Đặt phông cho cả CustomTkinter lẫn ttk (gọi ngay sau khi tạo cửa sổ, trước khi dựng widget)."""
    from tkinter import font as tkfont  # noqa: PLC0415

    fam = font_family()
    if not fam:
        return
    try:
        ctk.ThemeManager.theme["CTkFont"]["family"] = fam
    except Exception:  # pragma: no cover
        pass
    for name in ("TkDefaultFont", "TkTextFont", "TkHeadingFont", "TkMenuFont", "TkTooltipFont", "TkCaptionFont"):
        try:
            tkfont.nametofont(name, root=root).configure(family=fam)
        except Exception:  # pragma: no cover
            pass


def ui_font(size: int = 10, weight: str = "normal") -> Any:
    """Font cho phần ttk/Canvas (cùng họ với phông mặc định của hệ điều hành)."""
    from tkinter import font as tkfont  # noqa: PLC0415

    f = tkfont.nametofont("TkDefaultFont").copy()
    f.configure(size=int(round(size * _USER_FACTOR)), weight=weight)
    return f


def setup_style(root: Any) -> None:
    """Chỉnh màu ``ttk.Treeview`` cho hợp CustomTkinter (sáng/tối) + màu các nhãn dòng."""
    from tkinter import font as tkfont  # noqa: PLC0415

    st = ttk.Style(root)
    try:
        st.theme_use("clam")
    except tk.TclError:  # pragma: no cover
        pass
    bg = _theme_color("CTkFrame", "fg_color", ("#f2f2f2", "#2b2b2b"))
    fg = _theme_color("CTkLabel", "text_color", ("#1f1f1f", "#e6e6e6"))
    head_bg = _theme_color("CTkFrame", "top_fg_color", ("#e5e5e5", "#333333"))
    sel = _theme_color("CTkButton", "fg_color", ("#3b8ed0", "#1f6aa5"))
    f = tkfont.nametofont("TkDefaultFont")
    row_h = max(22, int(f.metrics("linespace") * 1.45))
    field = pick(("#ffffff", "#242424"))
    st.configure("Treeview", background=field, fieldbackground=field, foreground=fg, rowheight=row_h,
                 borderwidth=0, font=f)
    st.map("Treeview", background=[("selected", sel)], foreground=[("selected", "#ffffff")])
    st.configure("Treeview.Heading", background=head_bg, foreground=fg, relief="flat", font=f)
    st.map("Treeview.Heading", background=[("active", bg)])
    st.configure("TFrame", background=bg)
    st.configure("Vertical.TScrollbar", background=head_bg, troughcolor=bg, borderwidth=0, arrowcolor=fg)
    st.configure("Horizontal.TScrollbar", background=head_bg, troughcolor=bg, borderwidth=0, arrowcolor=fg)


def tag_rows(tv: Any) -> None:
    """Màu nền/chữ cho các nhãn dòng dùng chung."""
    for name, pair in ROW_BG.items():
        tv.tag_configure(name, background=pick(pair))
    for lvl in ("ok", "warn", "error", "dim"):
        tv.tag_configure(lvl, foreground=level_color(lvl, is_dark()))
    tv.tag_configure("head", foreground=level_color("head", is_dark()))


class Tooltip:
    """Chú thích khi rê chuột. ``text`` là chuỗi hoặc hàm ``(event) -> str | None`` (cho ô trong bảng)."""

    def __init__(self, widget: Any, text: str | Callable[[Any], str | None], delay_ms: int = 450):
        self.w = widget
        self.text = text
        self.delay = delay_ms
        self._after: Any = None
        self._tip: Any = None
        self._last = None
        widget.bind("<Motion>", self._motion, add="+")
        widget.bind("<Leave>", self._leave, add="+")
        widget.bind("<ButtonPress>", self._leave, add="+")

    def _get(self, ev: Any) -> str | None:
        return self.text(ev) if callable(self.text) else self.text

    def _motion(self, ev: Any) -> None:
        txt = self._get(ev)
        if txt != self._last:
            self._hide()
            self._last = txt
            if txt:
                self._after = self.w.after(self.delay, lambda: self._show(txt))
        elif self._tip is not None:
            self._place(ev.x_root, ev.y_root)

    def _leave(self, _ev: Any = None) -> None:
        self._last = None
        self._hide()

    def _hide(self) -> None:
        if self._after is not None:
            try:
                self.w.after_cancel(self._after)
            except Exception:
                pass
            self._after = None
        if self._tip is not None:
            self._tip.destroy()
            self._tip = None

    def _place(self, x: int, y: int) -> None:
        if self._tip is not None:
            self._tip.wm_geometry(f"+{x + 14}+{y + 16}")

    def _show(self, txt: str) -> None:
        self._after = None
        tw = tk.Toplevel(self.w)
        tw.wm_overrideredirect(True)
        try:
            tw.attributes("-topmost", True)
        except tk.TclError:  # pragma: no cover
            pass
        lb = tk.Label(tw, text=txt, justify="left", background=pick(("#ffffe6", "#3c3c3c")),
                      foreground=pick(("#1f1f1f", "#f0f0f0")), relief="solid", borderwidth=1, padx=6, pady=3,
                      wraplength=px(self.w, 560))
        lb.pack()
        self._tip = tw
        self._place(self.w.winfo_pointerx(), self.w.winfo_pointery())


def split_pane(pw: Any, fraction: float = 0.5) -> None:
    """Đặt vạch chia của ``tk.PanedWindow`` dọc theo tỉ lệ ở LẦN ĐẦU hiện (sau đó bạn kéo tuỳ ý) — tránh khung dưới
    bị ép còn 1 dòng trên màn hình thấp (720p)."""
    state = {"done": False}

    def placed(ev: Any) -> None:
        if state["done"] or ev.height < 120:
            return
        state["done"] = True
        try:
            pw.sash_place(0, 0, int(ev.height * fraction))
        except Exception:  # pragma: no cover
            pass

    pw.bind("<Configure>", placed, add="+")


@dataclass
class Col:
    """Một cột. ``width`` theo đơn vị logic (tự nhân tỉ lệ phóng to). ``fit``: cách rút gọn chữ dài cho vừa cột —
    ``end`` (cắt đuôi + "…"), ``name`` (tên file: giữ số tập + đuôi), ``""`` (không rút gọn)."""

    id: str
    title: str
    width: int = 100
    anchor: str = "w"
    stretch: bool = False
    fit: str = "end"


class Table:
    """``ttk.Treeview`` + thanh cuộn + một cột ô tick ☑/☐ (tuỳ chọn).

    * Bấm ô tick → đổi; bấm tiêu đề cột tick → tick/bỏ cả bảng. Dòng không có ô tick (``checked=None``) để trống.
    * Chữ dài tự rút gọn cho vừa cột (theo ``Col.fit``) mỗi khi cửa sổ / cột đổi độ rộng; rê chuột lên ô bị rút gọn →
      hiện chữ đầy đủ. ``full(iid)`` = giá trị đầy đủ.
    * ``on_toggle(iid, state)``, ``on_double(iid, col_id)``, ``on_select(iids)``, ``tip(iid, col_id) -> str|None``.
    """

    CHECK = "_chk"
    TREE = "#0"

    def __init__(self, master: Any, cols: Sequence[Col], check: str | None = None, height: int = 8,
                 tree: str | None = None, tree_width: int = 260, selectmode: str = "browse", tree_fit: str = "name"):
        from tkinter import font as tkfont  # noqa: PLC0415

        self.frame = ttk.Frame(master)
        self.cols = list(cols)
        self.check_title = check
        self.tree_fit = tree_fit if tree else ""
        ids = ([self.CHECK] if check else []) + [c.id for c in self.cols]
        self.tv = ttk.Treeview(self.frame, columns=ids, show=("tree headings" if tree else "headings"),
                               height=height, selectmode=selectmode)
        if tree:
            self.tv.heading("#0", text=tree, anchor="w")
            self.tv.column("#0", width=px(self.frame, tree_width), stretch=True, anchor="w",
                           minwidth=px(self.frame, tree_width * 0.5))
        if check:
            self.tv.heading(self.CHECK, text=check, anchor="center")
            self.tv.column(self.CHECK, width=px(self.frame, max(46, 9 * len(check) + 16)), stretch=False, anchor="center")
        for c in self.cols:
            self.tv.heading(c.id, text=c.title, anchor=c.anchor if c.anchor != "e" else "e",
                            command=(lambda cid=c.id: self._heading(cid)))
            self.tv.column(c.id, width=px(self.frame, c.width), stretch=c.stretch, anchor=c.anchor,
                           minwidth=px(self.frame, c.width * 0.5 if c.stretch else 30))
        vs = ttk.Scrollbar(self.frame, orient="vertical", command=self.tv.yview)
        hs = ttk.Scrollbar(self.frame, orient="horizontal", command=self.tv.xview)
        self.tv.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.tv.grid(row=0, column=0, sticky="nsew")
        vs.grid(row=0, column=1, sticky="ns")
        hs.grid(row=1, column=0, sticky="ew")
        self.frame.rowconfigure(0, weight=1)
        self.frame.columnconfigure(0, weight=1)
        tag_rows(self.tv)
        self._font = tkfont.nametofont("TkDefaultFont")
        self._pad = px(self.frame, 12)
        self._indent = px(self.frame, 20)
        self._full: dict[str, dict[str, Any]] = {}  # gia tri day du cua tung dong (khong rut gon)
        self._full_text: dict[str, str] = {}
        self._refit_after: Any = None
        self.checked: dict[str, bool] = {}
        self.on_toggle: Callable[[str, bool], None] | None = None
        self.on_toggle_all: Callable[[bool], None] | None = None
        self.on_double: Callable[[str, str], None] | None = None
        self.on_select: Callable[[list[str]], None] | None = None
        self.on_heading: Callable[[str], None] | None = None
        self.tip: Callable[[str, str], str | None] | None = None
        self.tv.bind("<Button-1>", self._click, add="+")
        self.tv.bind("<Double-1>", self._double, add="+")
        self.tv.bind("<<TreeviewSelect>>", self._selected, add="+")
        self.tv.bind("<space>", self._space, add="+")
        self.tv.bind("<Configure>", lambda _e: self.schedule_refit(), add="+")
        self.tv.bind("<ButtonRelease-1>", self._released, add="+")
        Tooltip(self.tv, self._tip_text)

    # ---- rut gon chu cho vua cot ----
    def _measure(self, s: str) -> int:
        return self._font.measure(s)

    def _col_width(self, cid: str) -> int:
        try:
            return int(self.tv.column(cid, "width"))
        except Exception:  # pragma: no cover
            return 0

    def _depth(self, iid: str) -> int:
        d = 0
        p = self.tv.parent(iid)
        while p:
            d += 1
            p = self.tv.parent(p)
        return d

    def _shown(self, iid: str) -> list[Any]:
        """Giá trị hiển thị (đã rút gọn theo độ rộng cột hiện tại)."""
        full = self._full.get(iid, {})
        out: list[Any] = []
        if self.check_title:
            st = self.checked.get(iid)
            out.append("" if st is None else (CHECK_ON if st else CHECK_OFF))
        for c in self.cols:
            v = full.get(c.id, "")
            v = "" if v is None else v
            if c.fit and isinstance(v, str) and v:
                v = fit_text(v, self._col_width(c.id) - self._pad, self._measure, c.fit)
            out.append(v)
        return out

    def _shown_text(self, iid: str) -> str:
        t = self._full_text.get(iid, "")
        if not t or not self.tree_fit:
            return t
        lead = len(t) - len(t.lstrip(" "))
        w = self._col_width(self.TREE) - self._pad - (self._depth(iid) + 1) * self._indent
        return t[:lead] + fit_text(t[lead:], w - self._measure(t[:lead]), self._measure, self.tree_fit)

    def schedule_refit(self) -> None:
        if self._refit_after is None:
            self._refit_after = self.tv.after(60, self.refit)

    def refit(self) -> None:
        """Rút gọn lại mọi ô theo độ rộng cột hiện tại (sau khi đổi cỡ cửa sổ hoặc kéo cột)."""
        self._refit_after = None
        try:
            for iid in self.ids():
                kw: dict[str, Any] = {"values": self._shown(iid)}
                if self.tree_fit and iid in self._full_text:
                    kw["text"] = self._shown_text(iid)
                self.tv.item(iid, **kw)
        except Exception:  # pragma: no cover - widget da bi huy
            pass

    def _released(self, ev: Any) -> None:
        if self.tv.identify_region(ev.x, ev.y) == "separator":  # vua keo do rong cot
            self.schedule_refit()

    # ---- du lieu ----
    def clear(self) -> None:
        self.tv.delete(*self.tv.get_children(""))
        self.checked.clear()
        self._full.clear()
        self._full_text.clear()

    def _store(self, iid: str, values: Sequence[Any] | dict[str, Any]) -> None:
        if isinstance(values, dict):
            self._full[iid] = {c.id: values.get(c.id, "") for c in self.cols}
        else:
            vals = list(values)
            self._full[iid] = {c.id: (vals[i] if i < len(vals) else "") for i, c in enumerate(self.cols)}

    def full(self, iid: str) -> dict[str, Any]:
        """Giá trị đầy đủ (chưa rút gọn) của một dòng; khoá ``#0`` = chữ ở cột cây."""
        out = dict(self._full.get(iid, {}))
        if iid in self._full_text:
            out[self.TREE] = self._full_text[iid]
        return out

    def add(self, iid: str, values: Sequence[Any] | dict[str, Any], tags: Iterable[str] = (), parent: str = "",
            checked: bool | None = None, text: str = "", open_: bool = True) -> str:
        if checked is not None:
            self.checked[iid] = bool(checked)
        else:
            self.checked.pop(iid, None)
        self._store(iid, values)
        self._full_text[iid] = text
        self.tv.insert(parent, "end", iid=iid, text="", values=self._shown(iid), tags=tuple(tags), open=open_)
        if text:
            self.tv.item(iid, text=self._shown_text(iid))
        return iid

    def update(self, iid: str, values: Sequence[Any] | dict[str, Any] | None = None, tags: Iterable[str] | None = None,
               checked: bool | None = None, text: str | None = None) -> None:
        if not self.tv.exists(iid):
            return
        if checked is not None:
            self.checked[iid] = bool(checked)
        if values is not None:
            self._store(iid, values)
        kw: dict[str, Any] = {}
        if values is not None or checked is not None:
            kw["values"] = self._shown(iid)
        if tags is not None:
            kw["tags"] = tuple(tags)
        if text is not None:
            self._full_text[iid] = text
            kw["text"] = self._shown_text(iid)
        if kw:
            self.tv.item(iid, **kw)

    def set_cell(self, iid: str, col: str, value: Any) -> None:
        """Đổi một ô (giữ nguyên các ô khác)."""
        if iid in self._full:
            self._full[iid][col] = value
            self.tv.item(iid, values=self._shown(iid))

    def set_checked(self, iid: str, state: bool) -> None:
        if iid in self.checked:
            self.update(iid, checked=state)

    def checked_ids(self) -> list[str]:
        return [i for i, v in self.checked.items() if v]

    def selected(self) -> list[str]:
        return list(self.tv.selection())

    def select(self, iid: str) -> None:
        if self.tv.exists(iid):
            self.tv.selection_set(iid)
            self.tv.see(iid)

    def ids(self) -> list[str]:
        out: list[str] = []

        def walk(p: str) -> None:
            for c in self.tv.get_children(p):
                out.append(c)
                walk(c)

        walk("")
        return out

    # ---- su kien ----
    def _col_id(self, x: int) -> str:
        c = self.tv.identify_column(x)
        if c == "#0":
            return "#0"
        try:
            i = int(c.lstrip("#")) - 1
        except ValueError:
            return ""
        ids = ([self.CHECK] if self.check_title else []) + [c.id for c in self.cols]
        return ids[i] if 0 <= i < len(ids) else ""

    def _toggle(self, iid: str) -> None:
        if iid not in self.checked:
            return
        new = not self.checked[iid]
        self.update(iid, checked=new)
        if self.on_toggle:
            self.on_toggle(iid, new)

    def _click(self, ev: Any) -> str | None:
        region = self.tv.identify_region(ev.x, ev.y)
        col = self._col_id(ev.x)
        if region == "heading" and col == self.CHECK and self.checked:
            new = not all(self.checked.values())
            for iid in list(self.checked):
                self.update(iid, checked=new)
            if self.on_toggle_all:
                self.on_toggle_all(new)
            elif self.on_toggle:
                for iid in list(self.checked):
                    self.on_toggle(iid, new)
            return "break"
        if region == "cell" and col == self.CHECK:
            iid = self.tv.identify_row(ev.y)
            if iid:
                self._toggle(iid)
                return "break"
        return None

    def _space(self, _ev: Any) -> str:
        for iid in self.selected():
            self._toggle(iid)
        return "break"

    def _double(self, ev: Any) -> None:
        if self.tv.identify_region(ev.x, ev.y) not in ("cell", "tree"):
            return
        iid = self.tv.identify_row(ev.y)
        col = self._col_id(ev.x)
        if iid and col != self.CHECK and self.on_double:
            self.on_double(iid, col)

    def _selected(self, _ev: Any) -> None:
        if self.on_select:
            self.on_select(self.selected())

    def _heading(self, cid: str) -> None:
        if self.on_heading:
            self.on_heading(cid)

    def cut_text(self, iid: str, col: str) -> str | None:
        """Chữ đầy đủ của ô nếu ô đang hiện bản rút gọn, không thì ``None``."""
        if col == self.TREE:
            full = self._full_text.get(iid, "")
            shown = self.tv.item(iid, "text")
        else:
            full = self._full.get(iid, {}).get(col, "")
            try:
                i = ([self.CHECK] if self.check_title else []).__len__() + [c.id for c in self.cols].index(col)
            except ValueError:
                return None
            vals = self.tv.item(iid, "values")
            shown = vals[i] if i < len(vals) else ""
        full = "" if full is None else str(full)
        return full.strip() if full and str(shown) != full else None

    def _tip_text(self, ev: Any) -> str | None:
        if self.tv.identify_region(ev.x, ev.y) not in ("cell", "tree"):
            return None
        iid = self.tv.identify_row(ev.y)
        if not iid:
            return None
        col = self._col_id(ev.x)
        custom = self.tip(iid, col) if self.tip else None
        cut = self.cut_text(iid, col)
        if custom and cut and cut not in custom:
            return f"{cut}\n\n{custom}"
        return custom or cut


class CheckGrid:
    """Lưới ☑/☐ vẽ trên Canvas cho bảng chọn phụ đề theo tập (100 tập × 8 cột vẫn nhanh).

    Ô xám = tập đó không có loại phụ đề này; ô vàng = khác quy tắc tự động. Bấm ô → đổi; bấm tiêu đề cột → cả
    cột; bấm tên tập → cả dòng. ``tips[(dòng, cột)]`` hiện khi rê chuột. ``on_change()`` sau mỗi thay đổi.
    Cột tên tập rộng theo cửa sổ (tối đa 45%); tên dài được rút gọn GIỮ số tập (rê chuột xem tên đầy đủ).
    """

    def __init__(self, master: Any, label_width: int = 300, col_width: int = 118, row_h: int = 0):
        self.frame = ttk.Frame(master)
        self.cv = tk.Canvas(self.frame, highlightthickness=0, borderwidth=0)
        vs = ttk.Scrollbar(self.frame, orient="vertical", command=self.cv.yview)
        hs = ttk.Scrollbar(self.frame, orient="horizontal", command=self.cv.xview)
        self.cv.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.cv.grid(row=0, column=0, sticky="nsew")
        vs.grid(row=0, column=1, sticky="ns")
        hs.grid(row=1, column=0, sticky="ew")
        self.frame.rowconfigure(0, weight=1)
        self.frame.columnconfigure(0, weight=1)
        self.font = ui_font(10)
        self.bold = ui_font(10, "bold")
        lh = self.font.metrics("linespace")
        self.row_h = row_h or int(lh * 1.6)
        self.head_h = int(lh * 2.9)
        self.min_label_w = px(self.frame, label_width)
        self.label_w = self.min_label_w
        self.col_w = px(self.frame, col_width)
        self.model: GridModel | None = None
        self.headers: dict[str, str] = {}
        self.labels: dict[str, str] = {}
        self.tips: dict[tuple[str, str], str] = {}
        self.on_change: Callable[[], None] | None = None
        self._redraw_after: Any = None
        self._last_w = 0
        self.cv.bind("<Button-1>", self._click)
        self.cv.bind("<MouseWheel>", lambda e: self.cv.yview_scroll(int(-e.delta / 120), "units"))
        self.cv.bind("<Button-4>", lambda e: self.cv.yview_scroll(-1, "units"))
        self.cv.bind("<Button-5>", lambda e: self.cv.yview_scroll(1, "units"))
        self.cv.bind("<Configure>", self._resized)
        Tooltip(self.cv, self._tip)

    def _resized(self, ev: Any) -> None:
        if abs(ev.width - self._last_w) > 4 and self._redraw_after is None:
            self._redraw_after = self.cv.after(60, self._redraw_later)

    def _redraw_later(self) -> None:
        self._redraw_after = None
        self.redraw()

    def set_model(self, model: GridModel, headers: dict[str, str], labels: dict[str, str],
                  tips: dict[tuple[str, str], str] | None = None) -> None:
        self.model, self.headers, self.labels, self.tips = model, headers, labels, dict(tips or {})
        self.redraw()

    def _layout(self) -> None:
        """Cột tên tập: đủ rộng cho tên dài nhất nếu cửa sổ cho phép (tối đa 45% bề ngang), tối thiểu ``label_width``."""
        m = self.model
        avail = max(self.cv.winfo_width(), 1)
        self._last_w = avail
        need = max((self.font.measure(self.labels.get(r, r)) for r in (m.rows if m else [])), default=0) + px(self.frame, 20)
        room = int(avail * 0.45)
        self.label_w = max(self.min_label_w, min(need, room)) if avail > 50 else self.min_label_w

    def _hit(self, ev: Any) -> tuple[str, str | None, str | None]:
        """(vùng, dòng, cột) tại vị trí chuột. vùng ∈ head | label | cell | ''."""
        m = self.model
        if m is None:
            return "", None, None
        x, y = self.cv.canvasx(ev.x), self.cv.canvasy(ev.y)
        ci = int((x - self.label_w) // self.col_w) if x >= self.label_w else -1
        ri = int((y - self.head_h) // self.row_h) if y >= self.head_h else -1
        col = m.cols[ci] if 0 <= ci < len(m.cols) else None
        row = m.rows[ri] if 0 <= ri < len(m.rows) else None
        if y < self.head_h:
            return ("head", None, col) if col else ("", None, None)
        if row is None:
            return "", None, None
        if x < self.label_w:
            return "label", row, None
        return ("cell", row, col) if col else ("", None, None)

    def _click(self, ev: Any) -> None:
        m = self.model
        where, r, c = self._hit(ev)
        changed = False
        if m is None:
            return
        if where == "head" and c:
            changed = m.toggle_col(c)
        elif where == "label" and r:
            changed = m.toggle_row(r)
        elif where == "cell" and r and c:
            changed = m.toggle_cell(r, c)
        if changed:
            self.redraw()
            if self.on_change:
                self.on_change()

    def _tip(self, ev: Any) -> str | None:
        where, r, c = self._hit(ev)
        if where == "cell" and r and c:
            if self.model and not self.model.present(r, c):
                return "Tập này không có loại phụ đề này."
            t = self.tips.get((r, c))
            if t and self.model and self.model.differs(r, c):
                t += "\n(khác quy tắc tự động)"
            return t
        if where == "head" and c:
            return "Bấm để tick / bỏ tick cả cột"
        if where == "label" and r:
            return f"{self.labels.get(r, r)}\nBấm để tick / bỏ tick cả dòng"
        return None

    def label_text(self, r: str) -> str:
        """Tên tập hiển thị (đã rút gọn cho vừa cột, giữ số tập)."""
        return fit_text(self.labels.get(r, r), self.label_w - px(self.frame, 16), self.font.measure, "name")

    def redraw(self) -> None:
        cv = self.cv
        cv.delete("all")
        m = self.model
        dark = is_dark()
        bg = pick(("#ffffff", "#242424"))
        fg = pick(("#1f1f1f", "#e6e6e6"))
        line = pick(("#d0d0d0", "#444444"))
        head_bg = pick(("#e5e5e5", "#333333"))
        cv.configure(background=bg)
        if m is None:
            return
        self._layout()
        pad = px(self.frame, 8)
        w = self.label_w + self.col_w * len(m.cols)
        h = self.head_h + self.row_h * len(m.rows)
        cv.create_rectangle(0, 0, w, self.head_h, fill=head_bg, outline=line)
        cv.create_text(pad, self.head_h / 2, text=fit_text("Tập  (bấm tên = cả dòng)", self.label_w - 2 * pad,
                                                            self.bold.measure), anchor="w", font=self.bold, fill=fg)
        for i, c in enumerate(m.cols):
            x0 = self.label_w + i * self.col_w
            cv.create_rectangle(x0, 0, x0 + self.col_w, self.head_h, fill=head_bg, outline=line)
            cv.create_text(x0 + self.col_w / 2, self.head_h / 2, text=self.headers.get(c, c), font=self.font, fill=fg,
                           justify="center", width=self.col_w - 6)
        for j, r in enumerate(m.rows):
            y0 = self.head_h + j * self.row_h
            row_bg = pick(ROW_BG["need"]) if m.row_differs(r) else bg
            cv.create_rectangle(0, y0, self.label_w, y0 + self.row_h, fill=row_bg, outline=line)
            cv.create_text(pad, y0 + self.row_h / 2, text=self.label_text(r), anchor="w", font=self.font, fill=fg)
            for i, c in enumerate(m.cols):
                x0 = self.label_w + i * self.col_w
                st = m.get(r, c)
                if st is None:
                    fill, txt, col = pick(ROW_BG["absent"]), NO_CELL, level_color("dim", dark)
                else:
                    fill = pick(ROW_BG["need"]) if m.differs(r, c) else bg
                    txt, col = (CHECK_ON if st else CHECK_OFF), fg
                cv.create_rectangle(x0, y0, x0 + self.col_w, y0 + self.row_h, fill=fill, outline=line)
                cv.create_text(x0 + self.col_w / 2, y0 + self.row_h / 2, text=txt, font=self.bold, fill=col)
        cv.configure(scrollregion=(0, 0, w, h))


# ---------------------------------------------------------------- hop thoai nho


def _center(win: Any, parent: Any) -> None:
    win.update_idletasks()
    try:
        px, py = parent.winfo_rootx(), parent.winfo_rooty()
        pw, ph = parent.winfo_width(), parent.winfo_height()
        w, h = win.winfo_width(), win.winfo_height()
        win.geometry(f"+{max(0, px + (pw - w) // 2)}+{max(0, py + (ph - h) // 3)}")
    except Exception:  # pragma: no cover
        pass


class Dialog:
    """Hộp thoại modal đơn giản (CTkToplevel). ``result`` = giá trị trả về khi đóng."""

    def __init__(self, parent: Any, title: str, width: int = 640, height: int = 420):
        self.parent = parent
        self.win = ctk.CTkToplevel(parent)
        set_window_icon(self.win)
        self.win.title(title)
        sw, sh = screen_logical(self.win)  # hop thoai khong bao gio to hon man hinh (720p)
        width, height = int(min(width, sw - 40)), int(min(height, sh - 90))
        self.win.geometry(f"{width}x{height}")
        self.win.minsize(min(420, width), min(220, height))
        self.wrap = width - 48  # be ngang cho chu xuong dong trong hop thoai
        self.win.transient(parent.winfo_toplevel())
        self.win.protocol("WM_DELETE_WINDOW", self.close)
        self.result: Any = None
        self.buttons = ctk.CTkFrame(self.win, fg_color="transparent")  # xep truoc, o duoi: nut luon hien
        self.buttons.pack(side="bottom", fill="x", padx=12, pady=(4, 12))
        self.body = ctk.CTkFrame(self.win, fg_color="transparent")
        self.body.pack(fill="both", expand=True, padx=12, pady=(12, 4))

    def button(self, text: str, cmd: Callable[[], None], primary: bool = False, side: str = "right") -> Any:
        b = ctk.CTkButton(self.buttons, text=text, command=cmd, width=120,
                          fg_color=None if primary else ("gray70", "gray30"),
                          text_color=None if primary else ("gray10", "gray90"))
        b.pack(side=side, padx=4)
        return b

    def close(self, result: Any = None) -> None:
        self.result = result
        try:
            self.win.grab_release()
        except Exception:
            pass
        self.win.destroy()

    def run(self) -> Any:
        _center(self.win, self.parent)
        self.win.after(50, self._grab)
        self.parent.wait_window(self.win)
        return self.result

    def _grab(self) -> None:
        try:
            self.win.lift()
            self.win.focus_force()
            self.win.grab_set()
        except Exception:  # pragma: no cover - cua so chua hien
            pass


def text_box(master: Any, text: str = "", height: int = 12, wrap: str = "none", mono: bool = True) -> Any:
    tb = ctk.CTkTextbox(master, height=height * 18, wrap=wrap,
                        font=ctk.CTkFont(family=mono_family(), size=12) if mono else None)
    if text:
        tb.insert("1.0", text)
    return tb


def show_text(parent: Any, title: str, text: str, note: str = "") -> None:
    """Hộp thoại xem chữ (vd "Xem lệnh") + nút "Chép"."""
    d = Dialog(parent, title, 900, 480)
    if note:
        ctk.CTkLabel(d.body, text=note, anchor="w", justify="left", wraplength=d.wrap).pack(fill="x", pady=(0, 6))
    tb = text_box(d.body, text, wrap="word")
    tb.configure(state="disabled")
    tb.pack(fill="both", expand=True)

    def copy() -> None:
        d.win.clipboard_clear()
        d.win.clipboard_append(text)
        cbtn.configure(text="Đã chép ✓")

    d.button("Đóng", d.close, primary=True)
    cbtn = d.button("Chép", copy)
    d.run()


def confirm_list(parent: Any, title: str, message: str, lines: Sequence[str], ok_text: str = "Đồng ý",
                 cancel_text: str = "Huỷ", warn: str = "") -> bool:
    """Hộp thoại xác nhận có danh sách đầy đủ (vd file sẽ vào Thùng rác)."""
    d = Dialog(parent, title, 820, 480)
    ctk.CTkLabel(d.body, text=message, anchor="w", justify="left", wraplength=d.wrap).pack(fill="x", pady=(0, 6))
    if warn:
        ctk.CTkLabel(d.body, text=warn, anchor="w", justify="left", wraplength=d.wrap,
                     text_color=LEVEL_COLORS["error"]).pack(fill="x", pady=(0, 6))
    tb = text_box(d.body, "\n".join(lines) if lines else "(không có)", wrap="none")
    tb.configure(state="disabled")
    tb.pack(fill="both", expand=True)
    d.button(ok_text, lambda: d.close(True), primary=True)
    d.button(cancel_text, lambda: d.close(False))
    return bool(d.run())


def ask_string(parent: Any, title: str, prompt: str, initial: str = "") -> str | None:
    d = Dialog(parent, title, 560, 180)
    ctk.CTkLabel(d.body, text=prompt, anchor="w", justify="left", wraplength=d.wrap).pack(fill="x")
    e = ctk.CTkEntry(d.body)
    e.insert(0, initial)
    e.pack(fill="x", pady=8)
    e.bind("<Return>", lambda _ev: d.close(e.get()))
    d.button("OK", lambda: d.close(e.get()), primary=True)
    d.button("Huỷ", lambda: d.close(None))
    d.win.after(120, e.focus_set)
    return d.run()


def info(parent: Any, title: str, message: str) -> None:
    messagebox.showinfo(title, message, parent=parent)


def warn(parent: Any, title: str, message: str) -> None:
    messagebox.showwarning(title, message, parent=parent)


def ask_yes_no(parent: Any, title: str, message: str) -> bool:
    return bool(messagebox.askyesno(title, message, parent=parent))
