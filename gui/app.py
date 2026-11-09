"""Cửa sổ chính MuxStudio (PROMPT §5): thư mục phim chọn MỘT lần ở đầu cửa sổ, bốn thẻ theo đúng thứ tự quy trình
(① Tải phụ đề → ② Chuẩn bị phụ đề → ③ Mux → Cài đặt), thanh tiến độ + nút Dừng, khung log màu luôn hiện ở dưới,
màn hình Hoàn tác chung cho ba bước.

Chạy: ``python -m gui.app`` (hoặc bấm đúp ``chay_muxstudio.bat`` trên Windows).
"""
from __future__ import annotations

import os
import re
import sys
import traceback
from dataclasses import dataclass
from typing import Any, Mapping

from core import config as C
from core import secrets as S
from core import tools, undo

from . import widgets as W

TAB_FETCH = "① Tải phụ đề"
TAB_PREP = "② Chuẩn bị phụ đề"
TAB_MUX = "③ Mux"
TAB_SETTINGS = "Cài đặt"
TABS = (TAB_FETCH, TAB_PREP, TAB_MUX, TAB_SETTINGS)

STEP_LABEL = {"subfetch": "① Tải phụ đề", "subprep": "② Chuẩn bị phụ đề", "mux": "③ Mux"}
STEP_LOGDIR = {"subfetch": "SubFetchLogDir", "subprep": "SubPrepLogDir", "mux": "MuxLogDir"}
ACTION_TEXT = {undo.CREATE: "tạo", undo.COPY: "chép", undo.MOVE: "di chuyển", undo.RENAME: "đổi tên",
               undo.MKDIR: "thư mục mới", undo.DELETE: "vào Thùng rác", undo.ENCODING: "chuyển mã (.bat cũ)",
               "Skip": "bỏ qua", "Fail": "lỗi"}
OP_TEXT = {"trash": "Vào Thùng rác", "move_back": "Trả về chỗ cũ", "rename_back": "Đổi lại tên cũ",
           "rmdir": "Xoá thư mục (nếu rỗng)"}


# ==========================================================================
#  HOÀN TÁC — phần thuần
# ==========================================================================


@dataclass
class UndoEntry:
    step: str
    run: undo.RunInfo
    stamp: str

    @property
    def label(self) -> str:
        return STEP_LABEL.get(self.step, self.step)

    @property
    def when(self) -> str:
        s = self.stamp
        if len(s) >= 15:
            return f"{s[6:8]}/{s[4:6]}/{s[0:4]} {s[9:11]}:{s[11:13]}:{s[13:15]}"
        return self.run.name


def counts_text(counts: Mapping[str, int]) -> str:
    """``{"Create": 3, "Rename": 2}`` → ``3 tạo, 2 đổi tên``."""
    parts = [f"{n} {ACTION_TEXT.get(a, a)}" for a, n in counts.items() if n]
    return ", ".join(parts) if parts else "(trống)"


def undo_entries(folder: str | os.PathLike[str], cfg: Mapping[str, Any]) -> list[UndoEntry]:
    """Mọi lần chạy của ba bước trong thư mục phim (mới nhất trước) — cho màn hình Hoàn tác."""
    out: list[UndoEntry] = []
    for step, key in STEP_LOGDIR.items():
        for r in undo.list_runs(folder, cfg[key], step):
            m = re.search(r"(\d{8}_\d{6})", r.name)
            out.append(UndoEntry(step, r, m.group(1) if m else ""))
    out.sort(key=lambda e: (e.stamp, e.run.name), reverse=True)
    return out


def plan_lines(plan: undo.UndoPlan, root: str | os.PathLike[str] | None = None) -> list[str]:
    """Dòng của hộp thoại xác nhận hoàn tác."""

    def rel(p: str) -> str:
        if root and tools.is_under(p, root):
            return os.path.relpath(p, root)
        return p

    lines = []
    for op in plan.ops:
        t = OP_TEXT.get(op.kind, op.kind)
        if op.kind in ("move_back", "rename_back"):
            lines.append(f"{t}: {rel(op.path)}  →  {rel(op.target)}")
        else:
            lines.append(f"{t}: {rel(op.path)}")
    for m in plan.missing:
        lines.append(f"(không còn, bỏ qua): {rel(m)}")
    for n in plan.notes:
        lines.append(f"[i] {n}")
    return lines


# ==========================================================================
#  CỬA SỔ CHÍNH
# ==========================================================================


class App:
    def __init__(self, cfg: C.Config | None = None, store: Any = None, folder: str | None = None,
                 appearance: str = "System"):
        ctk = W.ctk
        if ctk is None:  # pragma: no cover
            raise RuntimeError("Thiếu tkinter / customtkinter")
        ctk.set_appearance_mode(appearance)
        ctk.set_default_color_theme("blue")
        self.cfg = cfg if cfg is not None else C.load_config()
        self.secret_store = store if store is not None else S.store
        self._secret_error_shown = False
        self.redactor = S.Redactor(self.secrets().values())
        if W.font_family():
            ctk.ThemeManager.theme["CTkFont"]["family"] = W.font_family()
        self.root = ctk.CTk()
        self.root.title("MuxStudio")
        # Co chu + co cua so theo man hinh (720p / 1080p / 1440p, ca khi Windows dat phong to 125% / 150%)
        dpi = W.dpi_scaling(self.root)
        self.ui_factor = W.ui_factor(str(self.cfg["UiScale"]), self.root.winfo_screenwidth() / dpi,
                                     self.root.winfo_screenheight() / dpi)
        W.apply_user_scale(self.root, self.ui_factor)
        W.setup_fonts(self.root)
        self.window = W.plan_window(*W.screen_logical(self.root), saved=str(self.cfg.get("UiGeometry") or ""),
                                    zoomed=bool(self.cfg.get("UiZoomed")))
        self.density = self.window.density  # compact | normal | large
        self._place_window()
        self.root.report_callback_exception = self._tk_error
        W.setup_style(self.root)
        self.folder: str | None = None
        self._pending: list[tuple[str, str]] = []
        self._flush_scheduled = False

        # --- thu muc phim ---
        top = ctk.CTkFrame(self.root)
        top.pack(fill="x", padx=4 if self.density == "compact" else 10, pady=(4 if self.density == "compact" else 10, 2))
        ctk.CTkLabel(top, text="Thư mục phim:", font=ctk.CTkFont(weight="bold")).pack(side="left", padx=(10, 6), pady=8)
        self.folder_var = W.tk.StringVar(value="")
        self.folder_entry = ctk.CTkEntry(top, textvariable=self.folder_var,
                                         placeholder_text="Bấm \"Chọn…\" để chọn thư mục chứa phim (dùng chung cho cả ba bước)")
        self.folder_entry.pack(side="left", fill="x", expand=True, pady=8)
        self.folder_entry.bind("<Return>", lambda _e: self.set_folder(self.folder_var.get()))
        ctk.CTkButton(top, text="Chọn…", width=80, command=self.choose_folder).pack(side="left", padx=4)
        ctk.CTkButton(top, text="Mở", width=60, command=self.open_folder).pack(side="left", padx=(0, 4))
        self.undo_btn = ctk.CTkButton(top, text="Hoàn tác…", width=100, command=self.show_undo,
                                      fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.undo_btn.pack(side="left", padx=(8, 10))

        # --- the ---
        self.tabs = ctk.CTkTabview(self.root, command=self._tab_changed)
        for t in TABS:
            self.tabs.add(t)
        self._current_tab = TAB_FETCH

        # --- tien do + dung ---
        st = ctk.CTkFrame(self.root)
        self.status = ctk.CTkLabel(st, text="Sẵn sàng.", anchor="w")
        self.stop_now_btn = ctk.CTkButton(st, text="■ Dừng ngay", width=110, command=self.cancel_now, state="disabled",
                                          fg_color=("#c62828", "#a12020"), hover_color=("#a12020", "#7f1919"))
        self.stop_now_btn.pack(side="right", padx=(4, 10))
        self.stop_btn = ctk.CTkButton(st, text="Dừng sau file này", width=140, command=self.stop_after, state="disabled")
        self.stop_btn.pack(side="right", padx=4)
        bars = ctk.CTkFrame(st, fg_color="transparent")
        bars.pack(side="right", padx=8)
        self.prog = ctk.CTkProgressBar(bars, width=260)
        self.prog.set(0)
        self.prog.pack(pady=(2, 1))
        self.cur = ctk.CTkProgressBar(bars, width=260, height=5, mode="indeterminate")
        self.cur.pack(pady=(1, 2))
        # xep SAU nut Dung: chu dai khong bao gio day nut ra khoi cua so
        self.status.pack(side="left", fill="x", expand=True, padx=10, pady=4 if self.density == "compact" else 6)

        # --- log ---
        from .log_panel import LogPanel  # noqa: PLC0415

        log_h = {"compact": 64, "normal": 120, "large": 180}[self.density]
        self.log_panel = LogPanel(self.root, lambda: self.folder, self.redactor.redact, height=log_h)
        # xep tu duoi len: khung log + thanh tien do luon hien, cac the chiem phan con lai
        gap = 4 if self.density == "compact" else 10
        self.log_panel.frame.pack(side="bottom", fill="x", padx=gap, pady=(2, gap))
        st.pack(side="bottom", fill="x", padx=gap, pady=2)
        self.tabs.pack(fill="both", expand=True, padx=gap, pady=2)

        self.runner = W.Runner(self.root.after, self._log_async, self._progress, self._busy, self.redactor.redact)

        # --- cac the ---
        from .fetch_view import FetchView  # noqa: PLC0415
        from .mux_view import MuxView  # noqa: PLC0415
        from .settings_view import SettingsView  # noqa: PLC0415
        from .subprep_view import SubprepView  # noqa: PLC0415

        self.fetch = FetchView(self, self.tabs.tab(TAB_FETCH))
        self.prep = SubprepView(self, self.tabs.tab(TAB_PREP))
        self.mux = MuxView(self, self.tabs.tab(TAB_MUX))
        self.settings = SettingsView(self, self.tabs.tab(TAB_SETTINGS))
        self.views = [self.fetch, self.prep, self.mux, self.settings]

        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        for w in self.cfg.warnings:
            self.log("warn", w)
        self.log("head", "MuxStudio — chọn thư mục phim ở trên, rồi làm lần lượt ① → ② → ③.")
        start = folder or self.cfg.get("UiLastFolder")
        if start and os.path.isdir(str(start)):
            self.set_folder(str(start), quiet=True)
        self._update_views()

    # ------------------------------------------------------------------ cua so
    def _place_window(self) -> None:
        p = self.window
        try:
            sc = float(W.ctk.ScalingTracker.get_window_scaling(self.root))
        except Exception:  # pragma: no cover
            sc = 1.0
        x = max(0, int((self.root.winfo_screenwidth() - p.width * sc) / 2))
        y = max(0, int((self.root.winfo_screenheight() - p.height * sc) / 3)) if not p.zoomed else 0
        self.root.geometry(f"{p.width}x{p.height}+{x}+{y}")
        self.root.minsize(p.min_width, p.min_height)
        if p.zoomed:
            self.root.after(30, self._zoom)

    def _zoom(self) -> None:
        try:
            if sys.platform.startswith("win"):
                self.root.state("zoomed")
            else:
                self.root.attributes("-zoomed", True)
        except Exception:  # noqa: BLE001 - trinh quan ly cua so khong ho tro: da dat co bang man hinh
            pass

    def _remember_window(self) -> None:
        """Nhớ cỡ cửa sổ cho lần sau: phóng to hết màn hình, hoặc ``RỘNGxCAO`` theo đơn vị LOGIC — ``geometry()`` của
        CustomTkinter, KHÔNG phải ``winfo_geometry()`` (pixel thật: lưu nhầm thì trên máy đặt phóng to 125% / 150% cửa
        sổ to thêm 1,25–1,5 lần mỗi lần mở)."""
        if self._is_zoomed():
            self.cfg["UiZoomed"] = True
            return
        self.cfg["UiZoomed"] = False
        self.cfg["UiGeometry"] = self.root.geometry().split("+")[0]

    def _is_zoomed(self) -> bool:
        try:
            if sys.platform.startswith("win"):
                return str(self.root.state()) == "zoomed"
            return bool(self.root.attributes("-zoomed"))
        except Exception:  # noqa: BLE001
            return False

    # ------------------------------------------------------------------ chung
    def secrets(self) -> dict[str, str]:
        """Bí mật hiện có (Credential Manager). Lỗi đọc → báo một lần, coi như trống."""
        try:
            return self.secret_store.all_values()
        except Exception as e:  # noqa: BLE001
            if not self._secret_error_shown:
                self._secret_error_shown = True
                self._pending.append(("error", f"[!] Không đọc được Windows Credential Manager: {e}"))
            return {}

    def log(self, level: str, msg: str) -> None:
        self.log_panel.write(level, self.redactor.redact(msg))

    def _log_async(self, level: str, msg: str) -> None:
        self._pending.append((level, msg))
        if not self._flush_scheduled:
            self._flush_scheduled = True
            self.root.after_idle(self._flush)

    def _flush(self) -> None:
        self._flush_scheduled = False
        items, self._pending = self._pending, []
        self.log_panel.write_many(items)

    def _tk_error(self, exc: type[BaseException], val: BaseException, tb: Any) -> None:
        self.log("error", f"[LỖI giao diện] {val}")
        for ln in "".join(traceback.format_exception(exc, val, tb)).strip().splitlines()[-8:]:
            self.log("dim", "    " + ln)

    def goto(self, tab: str) -> None:
        self.tabs.set(tab)
        self._tab_changed()

    def _tab_changed(self) -> None:
        new = self.tabs.get()
        old = self._current_tab
        if old == TAB_SETTINGS and new != TAB_SETTINGS and self.settings.is_dirty():
            ans = W.messagebox.askyesnocancel("Cài đặt", "Bạn đã sửa cài đặt mà chưa lưu. Lưu ngay bây giờ?",
                                              parent=self.root)
            if ans is None:
                self.tabs.set(TAB_SETTINGS)
                return
            if ans:
                if not self.settings.save():
                    self.tabs.set(TAB_SETTINGS)
                    return
            else:
                self.settings.reload()
        self._current_tab = new
        v = {TAB_FETCH: self.fetch, TAB_PREP: self.prep, TAB_MUX: self.mux, TAB_SETTINGS: self.settings}[new]
        if hasattr(v, "on_show"):
            v.on_show()

    # ------------------------------------------------------------------ thu muc phim
    def choose_folder(self) -> None:
        cur = self.folder or self.cfg.get("UiLastFolder") or None
        d = W.filedialog.askdirectory(parent=self.root, title="Chọn thư mục phim",
                                      initialdir=cur if cur and os.path.isdir(str(cur)) else None)
        if d:
            self.set_folder(d)

    def set_folder(self, path: str, quiet: bool = False) -> bool:
        p = os.path.normpath(path.strip().strip('"')) if path and path.strip() else ""
        if not p or not os.path.isdir(p):
            if not quiet:
                W.warn(self.root, "Thư mục phim", f"Không có thư mục này:\n{path}")
            return False
        if self.runner.busy:
            W.warn(self.root, "Đang chạy", "Đợi việc đang chạy xong (hoặc bấm Dừng) rồi mới đổi thư mục phim.")
            self.folder_var.set(self.folder or "")
            return False
        self.folder = p
        self.folder_var.set(p)
        self.cfg["UiLastFolder"] = p
        try:
            C.save_config(self.cfg)
        except OSError:
            pass
        self.log("head", f"Thư mục phim: {p}")
        for v in self.views:
            if hasattr(v, "on_folder_changed"):
                v.on_folder_changed()
        self._update_views()
        return True

    def open_folder(self) -> None:
        if not self.folder or not W.open_path(self.folder):
            W.warn(self.root, "Mở thư mục", "Chưa chọn thư mục phim.")

    def need_folder(self) -> str | None:
        if self.folder and os.path.isdir(self.folder):
            return self.folder
        W.warn(self.root, "Chưa chọn thư mục phim", "Chọn thư mục phim ở đầu cửa sổ trước.")
        return None

    def on_config_changed(self, secrets_changed: bool = False) -> None:
        self.redactor.update(self.secrets().values())
        for v in self.views:
            if v is not self.settings and hasattr(v, "on_config_changed"):
                v.on_config_changed(secrets_changed)

    # ------------------------------------------------------------------ tien do / dung
    def _busy(self, busy: bool, title: str) -> None:
        self.stop_btn.configure(state="normal" if busy else "disabled")
        self.stop_now_btn.configure(state="normal" if busy else "disabled")
        self.undo_btn.configure(state="disabled" if busy else "normal")
        if busy:
            self.prog.set(0)
            self.status.configure(text=f"Đang chạy: {title}…")
            self.cur.start()
        else:
            self.cur.stop()
            self.cur.set(0)
            self.status.configure(text=f"Xong: {title}." if title else "Sẵn sàng.")
        self._update_views()

    def _progress(self, done: int, total: int, label: str) -> None:
        if total > 0:
            self.prog.set(max(0.0, min(1.0, done / total)))
        t = self.runner.title or ""
        self.status.configure(text=f"{t}: {done}/{total}" + (f" — {W.ellipsize_middle(label, 90)}" if label else ""))

    def _update_views(self) -> None:
        for v in self.views:
            if hasattr(v, "update_buttons"):
                v.update_buttons()

    def stop_after(self) -> None:
        self.runner.stop_after_current()

    def cancel_now(self) -> None:
        self.runner.cancel_now()

    # ------------------------------------------------------------------ hoan tac
    def show_undo(self) -> None:
        folder = self.need_folder()
        if folder:
            UndoDialog(self, folder).run()

    # ------------------------------------------------------------------ dong
    def on_close(self) -> None:
        if self.runner.busy:
            if not W.ask_yes_no(self.root, "Thoát", "Đang có việc chạy. Dừng ngay và thoát?"):
                return
            job = self.runner.job
            if job is not None:
                job.cancel_now()
        if self.settings.is_dirty():
            ans = W.messagebox.askyesnocancel("Cài đặt", "Lưu cài đặt đã sửa trước khi thoát?", parent=self.root)
            if ans is None:
                return
            if ans and not self.settings.save():
                return
        try:
            self._remember_window()
            C.save_config(self.cfg)
        except Exception:  # noqa: BLE001
            pass
        self.destroy()

    def destroy(self) -> None:
        """Đóng phiên các thẻ (xoá thư mục tạm), huỷ lệnh ``after`` còn chờ, rồi đóng cửa sổ."""
        for v in self.views:
            if hasattr(v, "close"):
                try:
                    v.close()
                except Exception:  # noqa: BLE001
                    pass
        try:
            for aid in self.root.tk.splitlist(self.root.tk.call("after", "info")):
                self.root.tk.call("after", "cancel", aid)  # khong dung after_cancel: no xoa luon lenh Tcl cua widget
        except Exception:  # noqa: BLE001
            pass
        self.root.destroy()

    def run(self) -> None:
        self.root.mainloop()


class UndoDialog:
    """Màn hình Hoàn tác chung ba bước: danh sách tối đa 20 lần chạy mỗi bước, chọn một hoặc nhiều lần."""

    def __init__(self, app: App, folder: str):
        ctk = W.ctk
        self.app, self.folder = app, folder
        self.d = W.Dialog(app.root, "Hoàn tác", 1040, 560)
        ctk.CTkLabel(self.d.body, text="Tick lần chạy muốn hoàn tác (mới nhất ở trên). File tạo ra → Thùng rác; file đã "
                     "đổi tên / di chuyển → trả về chỗ cũ. Không bao giờ xoá hẳn file của bạn.", anchor="w",
                     justify="left", wraplength=self.d.wrap).pack(fill="x")
        cols = [W.Col("step", "Bước", 150), W.Col("when", "Lúc chạy", 150), W.Col("what", "Thao tác", 360),
                W.Col("note", "Ghi chú", 300, stretch=True)]
        self.t = W.Table(self.d.body, cols, check="Chọn", height=14, selectmode="browse")
        self.t.frame.pack(fill="both", expand=True, pady=8)
        self.entries: dict[str, UndoEntry] = {}
        self.fill()
        self.d.button("Đóng", self.d.close)
        self.d.button("Hoàn tác các lần đã tick…", self.do_undo, primary=True)

    def fill(self) -> None:
        self.t.clear()
        self.entries.clear()
        for i, e in enumerate(undo_entries(self.folder, self.app.cfg)):
            iid = f"u{i}"
            self.entries[iid] = e
            self.t.add(iid, {"step": e.label, "when": e.when, "what": counts_text(e.run.counts), "note": e.run.note},
                       tags=() if e.run.undoable else ("dim",), checked=False if e.run.undoable else None)
        if not self.entries:
            self.t.add("none", {"step": "", "when": "", "what": "Chưa có lần chạy nào trong thư mục này.", "note": ""},
                       tags=("dim",))

    def do_undo(self) -> None:
        sel = [self.entries[i] for i in self.t.checked_ids() if i in self.entries]
        if not sel:
            W.info(self.d.win, "Hoàn tác", "Tick ít nhất một lần chạy.")
            return
        plans = []
        lines: list[str] = []
        for e in sel:
            try:
                p = undo.plan_undo(e.run.path)
            except (OSError, ValueError) as ex:
                lines.append(f"[!] {e.label} {e.when}: không đọc được nhật ký ({ex})")
                continue
            plans.append((e, p))
            lines.append(f"=== {e.label} — {e.when}")
            lines += ["   " + x for x in plan_lines(p, self.folder)] or ["   (không có gì để hoàn tác)"]
        rb = bool(self.app.cfg["DeleteToRecycleBin"])
        ok = W.confirm_list(self.d.win, "Xác nhận hoàn tác", f"Sẽ hoàn tác {len(plans)} lần chạy:", lines,
                            ok_text="Hoàn tác",
                            warn="" if rb else "Bạn đã TẮT 'Xoá vào Thùng rác' → file tạo ra sẽ bị XOÁ HẲN.")
        if not ok:
            return
        plans.sort(key=lambda x: (x[0].stamp, x[0].run.name), reverse=True)

        def work(job: tools.Job) -> undo.UndoResult:
            total = undo.UndoResult()
            for n, (e, p) in enumerate(plans, 1):
                job.progress(n, len(plans), f"{e.label} {e.when}")
                r = undo.execute_undo(p, rb)
                for d in r.done:
                    job.ok(f"  [↶] {d}")
                for path, why in r.skipped:
                    job.warn(f"  [-] {path}: {why}")
                total.done += r.done
                total.skipped += r.skipped
            job.head(f"HOÀN TÁC: {len(total.done)} thao tác | {len(total.skipped)} bỏ qua")
            return total

        def done(_r: undo.UndoResult) -> None:
            self.fill()
            for v in self.app.views:
                if hasattr(v, "on_files_changed"):
                    v.on_files_changed()

        self.app.runner.start("Hoàn tác", work, done)

    def run(self) -> None:
        self.d.run()


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    if W.ctk is None:  # pragma: no cover
        print("Thiếu tkinter / customtkinter. Cài: pip install customtkinter", file=sys.stderr)
        return 1
    folder = argv[0] if argv and os.path.isdir(argv[0]) else None
    try:
        app = App(folder=folder)
    except Exception:  # noqa: BLE001 - pythonw khong co console: ghi loi ra file + bao bang hop thoai
        tb = traceback.format_exc()
        p = C.app_config_dir() / "loi_khoi_dong.txt"
        try:
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(tb, encoding="utf-8")
        except OSError:
            pass
        try:
            W.messagebox.showerror("MuxStudio", f"Không khởi động được:\n\n{tb.strip().splitlines()[-1]}\n\nChi tiết: {p}")
        except Exception:  # noqa: BLE001
            print(tb, file=sys.stderr)
        return 1
    app.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
