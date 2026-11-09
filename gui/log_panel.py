"""Khung log màu luôn hiện ở dưới cửa sổ (thay ``W``/``Write-Step`` của ba file .bat).

Mức: ``info`` (thường), ``ok`` (xanh), ``warn`` (vàng), ``error`` (đỏ), ``dim`` (xám), ``head`` (đậm, xanh dương).
Nút: Thu gọn/Mở rộng, Xoá, Lưu log (UTF-8), Mở thư mục. Mọi dòng đã đi qua bộ lọc bí mật trước khi tới đây.
"""
from __future__ import annotations

import datetime as _dt
import os
from pathlib import Path
from typing import Any, Callable

from . import widgets as W

LEVELS = ("info", "ok", "warn", "error", "dim", "head")
MAX_LINES = 20000  # dong giu trong khung (file "Luu log" giu du)


def format_log(lines: list[tuple[str, str, str]]) -> str:
    """Nội dung file "Lưu log": ``HH:MM:SS  [mức]  chữ`` (mức ``info`` không ghi nhãn)."""
    tag = {"ok": "[OK]   ", "warn": "[!]    ", "error": "[LỖI]  ", "dim": "       ", "head": "", "info": ""}
    out = []
    for t, lv, msg in lines:
        out.append(f"{t}  {tag.get(lv, '')}{msg}")
    return "\n".join(out) + ("\n" if out else "")


class LogPanel:
    def __init__(self, master: Any, folder: Callable[[], str | None], redact: Callable[[str], str] | None = None):
        ctk = W.ctk
        self.folder = folder
        self.redact = redact
        self.lines: list[tuple[str, str, str]] = []  # (gio, muc, chu) - day du cho "Luu log"
        self.frame = ctk.CTkFrame(master)
        bar = ctk.CTkFrame(self.frame, fg_color="transparent")
        bar.pack(fill="x", padx=6, pady=(4, 0))
        self.toggle_btn = ctk.CTkButton(bar, text="▾ Nhật ký", width=110, command=self.toggle,
                                        fg_color="transparent", text_color=("gray10", "gray90"), anchor="w",
                                        hover_color=("gray80", "gray25"))
        self.toggle_btn.pack(side="left")
        self.count = ctk.CTkLabel(bar, text="", text_color=W.LEVEL_COLORS["dim"])
        self.count.pack(side="left", padx=8)
        for txt, cmd in (("Mở thư mục", self.open_folder), ("Lưu log…", self.save), ("Xoá", self.clear)):
            ctk.CTkButton(bar, text=txt, width=96, command=cmd).pack(side="right", padx=3)
        self.box = ctk.CTkTextbox(self.frame, height=120, wrap="word",
                                  font=ctk.CTkFont(family=W.mono_family(), size=12))
        self.box.pack(fill="both", expand=True, padx=6, pady=6)
        self._apply_colors()
        self.box.configure(state="disabled")
        self.expanded = True
        self.n_warn = 0
        self.n_err = 0

    def _apply_colors(self) -> None:
        dark = W.is_dark()
        for lv in LEVELS:
            kw: dict[str, Any] = {"foreground": W.level_color(lv, dark)}
            self.box.tag_config(lv, **kw)

    # ---- ghi ----
    def write(self, level: str, msg: str) -> None:
        self.write_many([(level, msg)])

    def write_many(self, items: list[tuple[str, str]]) -> None:
        if not items:
            return
        now = _dt.datetime.now().strftime("%H:%M:%S")
        self.box.configure(state="normal")
        for level, msg in items:
            if self.redact:
                msg = self.redact(msg)
            lv = level if level in LEVELS else "info"
            self.lines.append((now, lv, msg))
            self.box.insert("end", msg + "\n", lv)
            self.n_warn += lv == "warn"
            self.n_err += lv == "error"
        n = int(self.box.index("end-1c").split(".")[0])
        if n > MAX_LINES:
            self.box.delete("1.0", f"{n - MAX_LINES}.0")
        self.box.configure(state="disabled")
        self.box.see("end")
        self._count()

    def _count(self) -> None:
        parts = [f"{len(self.lines)} dòng"]
        if self.n_warn:
            parts.append(f"{self.n_warn} cảnh báo")
        if self.n_err:
            parts.append(f"{self.n_err} lỗi")
        self.count.configure(text=" · ".join(parts))

    def clear(self) -> None:
        self.box.configure(state="normal")
        self.box.delete("1.0", "end")
        self.box.configure(state="disabled")
        self.lines.clear()
        self.n_warn = self.n_err = 0
        self._count()

    # ---- nut ----
    def toggle(self) -> None:
        if self.expanded:
            self.box.pack_forget()
            self.toggle_btn.configure(text="▸ Nhật ký")
        else:
            self.box.pack(fill="both", expand=True, padx=6, pady=6)
            self.toggle_btn.configure(text="▾ Nhật ký")
        self.expanded = not self.expanded

    def save(self) -> Path | None:
        d = self.folder() or os.path.expanduser("~")
        name = f"MuxStudio_log_{_dt.datetime.now().strftime('%Y%m%d_%H%M%S')}.txt"
        p = W.filedialog.asksaveasfilename(parent=self.frame, title="Lưu log", initialdir=d, initialfile=name,
                                           defaultextension=".txt", filetypes=[("Văn bản", "*.txt"), ("Mọi file", "*.*")])
        if not p:
            return None
        Path(p).write_text(format_log(self.lines), encoding="utf-8")
        self.write("ok", f"Đã lưu log: {p}")
        return Path(p)

    def open_folder(self) -> None:
        d = self.folder()
        if not d or not W.open_path(d):
            W.warn(self.frame, "Mở thư mục", "Chưa chọn thư mục phim (hoặc thư mục không còn).")
