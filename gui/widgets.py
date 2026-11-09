"""Thành phần giao diện dùng chung.

* Phần THUẦN (test được không cần màn hình, không cần tkinter): ``ellipsize_middle`` (thay ``Limit-Text`` của
  SubFetch), ``GridModel`` (logic bảng chọn phụ đề theo tập: tick ô / cả cột / cả dòng, "Trả về mặc định"),
  ``Runner`` (chạy việc nặng ở luồng phụ, log + tiến độ qua ``queue``, giao diện đọc bằng ``after(100)``,
  nút Dừng), ``open_path``.
* Phần Tk: ``setup_style`` (màu ``ttk.Treeview`` hợp CustomTkinter, sáng/tối), ``Table`` (Treeview có cột ☑/☐ —
  không tạo ``CTkCheckBox`` cho từng ô), ``CheckGrid`` (lưới ☑/☐ vẽ trên Canvas: ô xám = không có, ô vàng = khác
  quy tắc tự động — Treeview không tô màu được từng ô), ``Tooltip``, các hộp thoại nhỏ.

tkinter chỉ được dùng bên trong hàm/phương thức (không kế thừa lớp Tk ở cấp module), nên ``import gui.widgets``
vẫn chạy được trên máy thiếu tkinter (test của phần thuần).
"""
from __future__ import annotations

import os
import queue
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
    f.configure(size=size, weight=weight)
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
                      wraplength=520)
        lb.pack()
        self._tip = tw
        self._place(self.w.winfo_pointerx(), self.w.winfo_pointery())


@dataclass
class Col:
    id: str
    title: str
    width: int = 100
    anchor: str = "w"
    stretch: bool = False


class Table:
    """``ttk.Treeview`` + thanh cuộn + một cột ô tick ☑/☐ (tuỳ chọn).

    * Bấm ô tick → đổi; bấm tiêu đề cột tick → tick/bỏ cả bảng. Dòng không có ô tick (``checked=None``) để trống.
    * ``on_toggle(iid, state)``, ``on_double(iid, col_id)``, ``on_select(iids)``, ``tip(iid, col_id) -> str|None``.
    """

    CHECK = "_chk"

    def __init__(self, master: Any, cols: Sequence[Col], check: str | None = None, height: int = 8,
                 tree: str | None = None, tree_width: int = 260, selectmode: str = "browse"):
        self.frame = ttk.Frame(master)
        self.cols = list(cols)
        self.check_title = check
        ids = ([self.CHECK] if check else []) + [c.id for c in self.cols]
        self.tv = ttk.Treeview(self.frame, columns=ids, show=("tree headings" if tree else "headings"),
                               height=height, selectmode=selectmode)
        if tree:
            self.tv.heading("#0", text=tree, anchor="w")
            self.tv.column("#0", width=tree_width, stretch=True, anchor="w")
        if check:
            self.tv.heading(self.CHECK, text=check, anchor="center")
            self.tv.column(self.CHECK, width=max(46, 9 * len(check) + 16), stretch=False, anchor="center")
        for c in self.cols:
            self.tv.heading(c.id, text=c.title, anchor=c.anchor if c.anchor != "e" else "e",
                            command=(lambda cid=c.id: self._heading(cid)))
            self.tv.column(c.id, width=c.width, stretch=c.stretch, anchor=c.anchor, minwidth=30)
        vs = ttk.Scrollbar(self.frame, orient="vertical", command=self.tv.yview)
        hs = ttk.Scrollbar(self.frame, orient="horizontal", command=self.tv.xview)
        self.tv.configure(yscrollcommand=vs.set, xscrollcommand=hs.set)
        self.tv.grid(row=0, column=0, sticky="nsew")
        vs.grid(row=0, column=1, sticky="ns")
        hs.grid(row=1, column=0, sticky="ew")
        self.frame.rowconfigure(0, weight=1)
        self.frame.columnconfigure(0, weight=1)
        tag_rows(self.tv)
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
        Tooltip(self.tv, self._tip_text)

    # ---- du lieu ----
    def clear(self) -> None:
        self.tv.delete(*self.tv.get_children(""))
        self.checked.clear()

    def _values(self, iid: str, values: Sequence[Any] | dict[str, Any]) -> list[Any]:
        if isinstance(values, dict):
            vals = [values.get(c.id, "") for c in self.cols]
        else:
            vals = list(values)
        if self.check_title:
            st = self.checked.get(iid)
            vals = [("" if st is None else (CHECK_ON if st else CHECK_OFF)), *vals]
        return ["" if v is None else v for v in vals]

    def add(self, iid: str, values: Sequence[Any] | dict[str, Any], tags: Iterable[str] = (), parent: str = "",
            checked: bool | None = None, text: str = "", open_: bool = True) -> str:
        if checked is not None:
            self.checked[iid] = bool(checked)
        else:
            self.checked.pop(iid, None)
        self.tv.insert(parent, "end", iid=iid, text=text, values=self._values(iid, values), tags=tuple(tags), open=open_)
        return iid

    def update(self, iid: str, values: Sequence[Any] | dict[str, Any] | None = None, tags: Iterable[str] | None = None,
               checked: bool | None = None, text: str | None = None) -> None:
        if not self.tv.exists(iid):
            return
        if checked is not None:
            self.checked[iid] = bool(checked)
        kw: dict[str, Any] = {}
        if values is not None:
            kw["values"] = self._values(iid, values)
        elif checked is not None and self.check_title:
            cur = list(self.tv.item(iid, "values"))
            if cur:
                cur[0] = CHECK_ON if self.checked[iid] else CHECK_OFF
                kw["values"] = cur
        if tags is not None:
            kw["tags"] = tuple(tags)
        if text is not None:
            kw["text"] = text
        if kw:
            self.tv.item(iid, **kw)

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

    def _tip_text(self, ev: Any) -> str | None:
        if not self.tip:
            return None
        if self.tv.identify_region(ev.x, ev.y) not in ("cell", "tree"):
            return None
        iid = self.tv.identify_row(ev.y)
        return self.tip(iid, self._col_id(ev.x)) if iid else None


class CheckGrid:
    """Lưới ☑/☐ vẽ trên Canvas cho bảng chọn phụ đề theo tập (100 tập × 8 cột vẫn nhanh).

    Ô xám = tập đó không có loại phụ đề này; ô vàng = khác quy tắc tự động. Bấm ô → đổi; bấm tiêu đề cột → cả
    cột; bấm tên tập → cả dòng. ``tips[(dòng, cột)]`` hiện khi rê chuột. ``on_change()`` sau mỗi thay đổi.
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
        self.label_w = label_width
        self.col_w = col_width
        self.model: GridModel | None = None
        self.headers: dict[str, str] = {}
        self.labels: dict[str, str] = {}
        self.tips: dict[tuple[str, str], str] = {}
        self.on_change: Callable[[], None] | None = None
        self.cv.bind("<Button-1>", self._click)
        self.cv.bind("<MouseWheel>", lambda e: self.cv.yview_scroll(int(-e.delta / 120), "units"))
        self.cv.bind("<Button-4>", lambda e: self.cv.yview_scroll(-1, "units"))
        self.cv.bind("<Button-5>", lambda e: self.cv.yview_scroll(1, "units"))
        Tooltip(self.cv, self._tip)

    def set_model(self, model: GridModel, headers: dict[str, str], labels: dict[str, str],
                  tips: dict[tuple[str, str], str] | None = None) -> None:
        self.model, self.headers, self.labels, self.tips = model, headers, labels, dict(tips or {})
        self.redraw()

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
        w = self.label_w + self.col_w * len(m.cols)
        h = self.head_h + self.row_h * len(m.rows)
        cv.create_rectangle(0, 0, w, self.head_h, fill=head_bg, outline=line)
        cv.create_text(8, self.head_h / 2, text="Tập  (bấm tên = cả dòng)", anchor="w", font=self.bold, fill=fg)
        for i, c in enumerate(m.cols):
            x0 = self.label_w + i * self.col_w
            cv.create_rectangle(x0, 0, x0 + self.col_w, self.head_h, fill=head_bg, outline=line)
            cv.create_text(x0 + self.col_w / 2, self.head_h / 2, text=self.headers.get(c, c), font=self.font, fill=fg,
                           justify="center", width=self.col_w - 6)
        for j, r in enumerate(m.rows):
            y0 = self.head_h + j * self.row_h
            row_bg = pick(ROW_BG["need"]) if m.row_differs(r) else bg
            cv.create_rectangle(0, y0, self.label_w, y0 + self.row_h, fill=row_bg, outline=line)
            cv.create_text(8, y0 + self.row_h / 2, text=ellipsize_middle(self.labels.get(r, r), 48), anchor="w",
                           font=self.font, fill=fg)
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
        self.win.title(title)
        self.win.geometry(f"{width}x{height}")
        self.win.transient(parent.winfo_toplevel())
        self.win.protocol("WM_DELETE_WINDOW", self.close)
        self.result: Any = None
        self.body = ctk.CTkFrame(self.win, fg_color="transparent")
        self.body.pack(fill="both", expand=True, padx=12, pady=(12, 4))
        self.buttons = ctk.CTkFrame(self.win, fg_color="transparent")
        self.buttons.pack(fill="x", padx=12, pady=(4, 12))

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
        ctk.CTkLabel(d.body, text=note, anchor="w", justify="left", wraplength=860).pack(fill="x", pady=(0, 6))
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
    ctk.CTkLabel(d.body, text=message, anchor="w", justify="left", wraplength=780).pack(fill="x", pady=(0, 6))
    if warn:
        ctk.CTkLabel(d.body, text=warn, anchor="w", justify="left", wraplength=780,
                     text_color=LEVEL_COLORS["error"]).pack(fill="x", pady=(0, 6))
    tb = text_box(d.body, "\n".join(lines) if lines else "(không có)", wrap="none")
    tb.configure(state="disabled")
    tb.pack(fill="both", expand=True)
    d.button(ok_text, lambda: d.close(True), primary=True)
    d.button(cancel_text, lambda: d.close(False))
    return bool(d.run())


def ask_string(parent: Any, title: str, prompt: str, initial: str = "") -> str | None:
    d = Dialog(parent, title, 560, 180)
    ctk.CTkLabel(d.body, text=prompt, anchor="w", justify="left", wraplength=520).pack(fill="x")
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
