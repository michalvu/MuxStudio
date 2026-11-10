"""Thẻ "③ Mux" (thay MuxTool — PROMPT §5.3, SPEC mục 6 #18–#22).

1. **Database đặt tên: TMDB / TVDB / Không đổi tên — BẮT BUỘC chọn mỗi lần, KHÔNG chọn sẵn.** Nút quét / Bắt đầu khoá
   tới khi chọn. Cạnh mỗi lựa chọn: *sẵn sàng* / *chưa điền key* / *key bị từ chối* / *đăng nhập lỗi*.
2. **Bảng tổng quan**: tên gốc → tên mới (bấm đúp để sửa / chọn kết quả cho dòng "Cần chọn"), audio / phụ đề giữ,
   audio ghép, thẻ codec, trạng thái.
3. **Audio** của file đang chọn: tick track muốn XOÁ, ô "Áp dụng cho mọi file còn lại", "Giữ cả hai, Default = bitrate
   cao nhất" (Q3).
4. **Bảng chọn phụ đề theo tập** (ô xám = không có, ô vàng = khác quy tắc tự động; bấm tiêu đề cột / tên tập để
   tick cả cột / dòng; "Trả về mặc định").
5. **Audio ghép từ file khác**: file nguồn, độ lệch (bấm đúp để sửa tay), cách đo, khác FPS.
6. "Xem lệnh" (ffmpeg + mkvmerge, không ghi gì), "Bắt đầu mux" → tiến độ, log, tổng kết.
"""
from __future__ import annotations

import os
import re
from typing import Any, Callable

from core import naming, probe, tools
from core.mux import MuxPlan, MuxReport, MuxSession, TrashReport, UsedFile, used_sources
from core.tmdb import ConnStatus

from . import widgets as W

DBS = (("tmdb", "TMDB"), ("tvdb", "TVDB"), ("none", "Không đổi tên"))
FIXED_AUDIO = ("Commentary", "Audio Description")


# ==========================================================================
#  PHẦN THUẦN
# ==========================================================================


def key_of(p: MuxPlan) -> str:
    return os.path.normcase(os.path.abspath(p.src))


def out_rel(plan: MuxPlan, out_dir: str | os.PathLike[str]) -> str:
    if plan.out_path is None:
        return ""
    try:
        return os.path.relpath(plan.out_path, out_dir)
    except ValueError:  # khac o dia
        return os.fspath(plan.out_path)


def overview_values(plan: MuxPlan, out_dir: str | os.PathLike[str]) -> tuple[dict[str, Any], str]:
    """Một dòng bảng tổng quan + nhãn màu."""
    n_a = len(plan.audio)
    k_a = len(plan.keep_audio)
    audio = f"{k_a}/{n_a}" if n_a else "0"
    if plan.donor.tracks:
        audio += f" + {len(plan.donor.tracks)} ghép"
    subs = f"{len(plan.keep_subs)} trong + {len(plan.ext_subs)} ngoài" if (plan.int_subs or plan.ext_subs) else "0"
    st = plan.status_text
    if plan.reasons:
        st += ": " + "; ".join(plan.reasons)
    tag = {"need_choice": "need", "skip": "dim", "exists": "dim", "fail": "bad"}.get(plan.status, "")
    if plan.result is not None:
        r = plan.result
        if r.ok:
            st = "ĐÃ MUX (có cảnh báo)" if r.warning else "ĐÃ MUX"
            tag = "need" if r.warning else "good"
        else:
            st = f"THẤT BẠI (mã thoát {r.code})"
            tag = "bad"
    vals = {"src": plan.rel, "new": out_rel(plan, out_dir) if plan.status not in ("fail",) else "",
            "audio": audio if plan.status not in ("fail", "exists") else "", "subs": subs if plan.json else "",
            "donor": plan.donor.summary if plan.json else "", "tag": plan.codec_tag, "status": st}
    return vals, tag


def audio_rows(plan: MuxPlan) -> list[tuple[str, dict[str, Any], bool | None, str]]:
    """Bảng audio của một file: (iid, giá trị, ô tick "Xoá" — None = không cho đổi, nhãn màu)."""
    out = []
    for a in plan.audio:
        fixed = a.reason in FIXED_AUDIO
        note = a.reason or "giữ"
        if a.default:
            note += " — Default"
        vals = {"id": a.id, "lang": naming.lang_name(plan.lang_override.get(a.id, a.code)) or a.code, "codec": a.codec,
                "ch": a.channels, "br": probe.format_kbps(a.bitrate), "brsrc": a.bitrate_src, "name": a.track_name,
                "note": note}
        tag = "dim" if not a.keep else ("ok" if a.default else "")
        out.append((f"a{a.id}", vals, None if fixed else (not a.keep), tag))
    return out


def multi_en(plan: MuxPlan) -> bool:
    return any("audio tiếng Anh" in r for r in plan.reasons)


def grid_data(plans: list[MuxPlan], choices: dict[str, Any] | None = None) -> dict[str, Any]:
    """Dữ liệu cho bảng chọn phụ đề theo tập: dòng = file, cột = loại phụ đề (sub ngoài trước, sub trong MKV sau)."""
    rows: list[str] = []
    labels: dict[str, str] = {}
    headers: dict[str, str] = {}
    auto: dict[tuple[str, str], bool] = {}
    current: dict[tuple[str, str], bool] = {}
    tips: dict[tuple[str, str], str] = {}
    col_order: dict[str, tuple[int, int, int]] = {}
    n = 0
    for i, p in enumerate(plans):
        if not p.sub_items:
            continue
        r = f"m{i}"
        rows.append(r)
        labels[r] = p.rel
        ch = (choices or {}).get(key_of(p))
        for it in p.sub_items:
            headers.setdefault(it.key, it.header)
            if it.key not in col_order:
                col_order[it.key] = (0 if it.kind == "ext" else 1, it.order, n)
                n += 1
            auto[(r, it.key)] = bool(it.auto_keep)
            tips[(r, it.key)] = it.tip
            if ch is not None:
                current[(r, it.key)] = (it.code in ch.ext) if it.kind == "ext" else (it.id in ch.int_ids)
    cols = sorted(col_order, key=lambda k: col_order[k])
    return {"rows": rows, "cols": cols, "labels": labels, "headers": headers, "auto": auto, "current": current,
            "tips": tips}


def grid_choice(plan: MuxPlan, values: dict[str, bool]) -> tuple[list[str], list[int]]:
    """Ô đã tick của một dòng → (mã sub ngoài giữ, ID sub trong giữ) cho ``MuxSession.set_sub_choice``."""
    ext, ints = [], []
    for it in plan.sub_items:
        if values.get(it.key):
            if it.kind == "ext":
                ext.append(it.code)
            else:
                ints.append(it.id)
    return ext, ints


def donor_values(plan: MuxPlan) -> dict[str, Any]:
    d = plan.donor
    t = d.tracks[0] if d.tracks else None
    return {"src": plan.rel, "donor": d.donor.name if d.donor else "", "langs": ", ".join(x.code for x in d.tracks),
            "off": (f"{t.offset_ms:+d}" + (" (tay)" if d.manual else "")) if t else "",
            "how": d.timing.how if d.timing else "", "fps": ("CÓ → đổi tốc độ (encode lại)" if t and t.stretch else
                                                             ("không" if t else "")),
            "note": d.note if not d.tracks else ""}


def parse_ms(text: str) -> float | None:
    """Ô sửa độ lệch tay: trống = dùng kết quả đo; ``+1200`` / ``-500`` / ``1,5`` (ms)."""
    t = (text or "").strip().replace(" ", "").replace(",", ".").lower().removesuffix("ms")
    if not t:
        return None
    if not re.fullmatch(r"[-+]?\d+(\.\d+)?", t):
        raise ValueError("cần một số mili-giây, vd 1200 hoặc -500")
    return float(t)


def parse_year(text: str) -> int | None:
    t = (text or "").strip()
    if not t:
        return None
    if not re.fullmatch(r"(19|20)\d\d", t):
        raise ValueError("năm cần 4 chữ số, vd 2019")
    return int(t)


def plan_details(plan: MuxPlan, out_dir: str | os.PathLike[str]) -> list[str]:
    """Ô "Chi tiết" của file đang chọn."""
    L: list[str] = [f"File: {plan.rel}", f"Trạng thái: {plan.status_text}"]
    if plan.out_path is not None:
        L.append(f"Đầu ra: Output\\{out_rel(plan, out_dir)}")
    if plan.name.note:
        L.append(f"Đặt tên: {plan.name.note}")
    if plan.name.episode_note:
        L.append(f"   {plan.name.episode_note}")
    for r in plan.reasons:
        L.append(f"[!] {r}")
    for n in plan.notes:
        L.append(f"[i] {n}")
    if plan.audio:
        L.append("Audio:")
        for a in plan.audio:
            st = "GIỮ" + (" (Default)" if a.default else "") if a.keep else f"XOÁ ({a.reason})"
            L.append(f"   #{a.id} {a.label} {probe.format_kbps(a.bitrate)}"
                     + (f" [{a.track_name}]" if a.track_name else "") + f" — {st}")
    if plan.rule_applied:
        L.append(f"   (quy tắc đã nhớ: {plan.rule_applied})")
    if plan.int_subs:
        L.append("Phụ đề trong MKV:")
        for s in plan.int_subs:
            st = "GIỮ" + (" (Default)" if s.default else "") if s.keep else "XOÁ"
            L.append(f"   #{s.id} {s.label or s.code} — {st} ({s.reason})")
    if plan.ext_subs:
        L.append("Phụ đề ngoài:")
        kind, val = plan.default_sub
        for i, e in enumerate(plan.ext_subs):
            L.append(f"   {e.path.name} ({e.label})" + (" — Default" if kind == "ext" and i == val else ""))
    if plan.donor.tracks or plan.donor.note:
        L.append(f"Audio ghép: {plan.donor.summary}")
        if plan.donor.donor:
            L.append(f"   từ: {plan.donor.donor}")
        if plan.donor.timing:
            L.append(f"   đo: {plan.donor.timing.how}")
    if plan.codec_tag:
        L.append(f"Thẻ codec: {plan.codec_tag}")
    if plan.result is not None:
        L.append(f"Kết quả mux: mã thoát {plan.result.code}")
        L += ["   " + x for x in plan.result.output[-8:]]
    return L


def summary_text(rep: MuxReport) -> str:
    s = (f"Thành công: {rep.ok}" + (f" ({rep.warning} có cảnh báo)" if rep.warning else "")
         + f"  |  Bỏ qua: {rep.skipped}  |  Cần chọn: {rep.pending}  |  Lỗi: {rep.failed}")
    if rep.stopped:
        s += "  |  (đã dừng giữa chừng)"
    return s


# ==========================================================================
#  GIAO DIỆN
# ==========================================================================


RESET_TEXT = "↺ Làm lại từ đầu"


class MuxView:
    def __init__(self, app: Any, parent: Any):
        ctk = W.ctk
        self.app = app
        self.session: MuxSession | None = None
        self.plans: list[MuxPlan] = []
        self.cur: int | None = None
        self.db_checked = False
        self.frame = ctk.CTkFrame(parent, fg_color="transparent")
        self.frame.pack(fill="both", expand=True)

        # --- 1. database (mot hang ngang: tiet kiem chieu cao cho man hinh 720p) ---
        dbf = ctk.CTkFrame(self.frame)
        dbf.pack(fill="x")
        ctk.CTkLabel(dbf, text="Đặt tên theo (bắt buộc chọn):", font=ctk.CTkFont(weight="bold")).pack(
            side="left", padx=(10, 8), pady=6)
        self.db = W.tk.StringVar(value="")  # KHONG chon san
        self.db_status: dict[str, Any] = {}
        self.db_check_btn = ctk.CTkButton(dbf, text="Kiểm tra lại", width=110, command=self.check_db)
        self.db_check_btn.pack(side="right", padx=10)
        for k, label in DBS:
            ctk.CTkRadioButton(dbf, text=label, variable=self.db, value=k, command=self.db_changed).pack(
                side="left", padx=(6, 2))
            lb = ctk.CTkLabel(dbf, text="", text_color=W.LEVEL_COLORS["dim"], anchor="w")
            lb.pack(side="left", padx=(8, 16))
            self.db_status[k] = lb
        self.db_status["none"].configure(text="giữ tên file gốc")

        # --- cac buoc: nut TO theo thu tu, buoc ke tiep sang mau ---
        self._muxed = self._trash_done = False
        self.trash_cands: list[UsedFile] = []
        self.steps = W.StepBar(self.frame, [
            ("plan", "Quét & lập kế hoạch", self.plan_all), ("run", "Bắt đầu mux", self.start_mux),
            ("trash", "Dọn file nguồn đã dùng", self.offer_trash), ("out", "Mở thư mục Output", self.open_output)],
            compact=app.density == "compact", plain=("out",))
        self.steps.frame.pack(fill="x", pady=(6, 2))
        self.plan_btn, self.run_btn, self.trash_btn = (self.steps.buttons[k] for k in ("plan", "run", "trash"))
        # nut phu + goi y
        bf = ctk.CTkFrame(self.frame, fg_color="transparent")
        bf.pack(fill="x", pady=(0, 4))
        self.cmd_btn = ctk.CTkButton(bf, text="Xem lệnh", width=90, command=self.show_commands,
                                     fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.cmd_btn.pack(side="right", padx=(6, 0))
        self.keepall_btn = ctk.CTkButton(bf, text="Giữ mọi audio tiếng Anh (mọi file)", width=230, command=self.keep_all_en,
                                         fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.keepall_btn.pack(side="right", padx=6)
        self.reset_btn = ctk.CTkButton(bf, text=RESET_TEXT, width=150, command=self.reset_all,
                                       fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.reset_btn.pack(side="right", padx=6)
        W.Tooltip(self.reset_btn, "Mux xong một lượt: xoá mọi bảng, tổng kết và tiến độ của lượt này (kể cả lựa chọn "
                  "TMDB / TVDB) để làm lô file mới. KHÔNG xoá file nào; \"Hoàn tác…\" vẫn dùng được.")
        self.hint = ctk.CTkLabel(bf, text="", anchor="w")
        self.hint.pack(side="left", padx=10, fill="x", expand=True)

        # --- bang tong quan + chi tiet ---
        pw = W.tk.PanedWindow(self.frame, orient="vertical", sashwidth=6, borderwidth=0,
                              background=W.pick(("#dbdbdb", "#2b2b2b")))
        pw.pack(fill="both", expand=True)
        W.on_restyle(lambda: pw.configure(background=W.pick(("#dbdbdb", "#2b2b2b"))))
        top = W.ttk.Frame(pw)
        cols = [W.Col("src", "Tên gốc", 250, stretch=True, fit="name"),
                W.Col("new", "→ Tên mới (trong Output)", 330, stretch=True, fit="name"),
                W.Col("audio", "Audio giữ", 75), W.Col("subs", "Phụ đề giữ", 125), W.Col("donor", "Audio ghép", 140),
                W.Col("tag", "Thẻ codec", 140), W.Col("status", "Trạng thái", 240, stretch=True)]
        self.table = W.Table(top, cols, check="Mux", height=6)
        self.table.frame.pack(fill="both", expand=True)
        self.table.on_select = self._on_select
        self.table.on_double = self._on_double
        self.table.on_toggle = self._toggle
        self.table.on_toggle_all = self._toggle_all
        self.table.tip = self._tip
        pw.add(top, stretch="always", minsize=110)

        bot = W.ttk.Frame(pw)
        self.inner = ctk.CTkTabview(bot, height=200)
        self.inner.pack(fill="both", expand=True)
        self.t_audio = self.inner.add("Audio của file đang chọn")
        self.t_grid = self.inner.add("Bảng chọn phụ đề theo tập")
        self.t_donor = self.inner.add("Audio ghép từ file khác")
        self.t_info = self.inner.add("Chi tiết")
        pw.add(bot, stretch="always", minsize=160)
        W.split_pane(pw, 0.42)
        self._build_audio()
        self._build_grid()
        self._build_donor()
        self.info = W.text_box(self.t_info, "", wrap="none")
        self.info.pack(fill="both", expand=True)
        self.info.configure(state="disabled")

        self.summary = ctk.CTkLabel(self.frame, text="", anchor="w")
        self.summary.pack(fill="x", pady=(4, 0))
        self.on_config_changed()

    # ------------------------------------------------------------------ dung cac o
    def _build_audio(self) -> None:
        ctk = W.ctk
        hb = ctk.CTkFrame(self.t_audio, fg_color="transparent")
        hb.pack(fill="x")
        self.audio_title = ctk.CTkLabel(hb, text="Chọn một file ở bảng trên.", anchor="w")
        self.keep_both_btn = ctk.CTkButton(hb, text="Giữ cả hai, Default = bitrate cao nhất", width=250,
                                           command=self.keep_both)
        self.keep_both_btn.pack(side="right", padx=4)
        self.apply_audio_btn = ctk.CTkButton(hb, text="Áp dụng lựa chọn audio", width=170, command=self.apply_audio)
        self.apply_audio_btn.pack(side="right", padx=4)
        self.apply_rest = W.tk.BooleanVar(value=True)
        self.apply_rest_cb = ctk.CTkCheckBox(hb, text="Áp dụng cho mọi file còn lại", variable=self.apply_rest)
        self.apply_rest_cb.pack(side="right", padx=8)
        self.audio_title.pack(side="left", fill="x", expand=True)  # xep sau nut: ten dai khong day nut ra ngoai
        cols = [W.Col("id", "ID", 40, "e"), W.Col("lang", "Ngôn ngữ", 100), W.Col("codec", "Codec", 110),
                W.Col("ch", "Kênh", 50), W.Col("br", "Bitrate", 90, "e"), W.Col("brsrc", "Nguồn bitrate", 110),
                W.Col("name", "Tên track", 260, stretch=True), W.Col("note", "Ghi chú", 220)]
        self.atable = W.Table(self.t_audio, cols, check="Xoá", height=3)
        self.atable.frame.pack(fill="both", expand=True, pady=(4, 0))

    def _build_grid(self) -> None:
        ctk = W.ctk
        hb = ctk.CTkFrame(self.t_grid, fg_color="transparent")
        hb.pack(fill="x")
        self.grid_note = ctk.CTkLabel(hb, text="Lập kế hoạch trước.", anchor="w")
        self.grid_apply_btn = ctk.CTkButton(hb, text="Áp dụng bảng chọn", width=150, command=self.apply_grid)
        self.grid_apply_btn.pack(side="right", padx=4)
        for txt, fn in (("Bỏ tick tất cả", lambda: self._grid_all(False)), ("Tick tất cả", lambda: self._grid_all(True)),
                        ("Trả về mặc định", self._grid_reset)):
            ctk.CTkButton(hb, text=txt, width=120, command=fn, fg_color=("gray70", "gray30"),
                          text_color=("gray10", "gray90")).pack(side="right", padx=4)
        self.grid_note.pack(side="left", fill="x", expand=True)
        self.grid = W.CheckGrid(self.t_grid)
        self.grid.frame.pack(fill="both", expand=True, pady=(4, 0))
        self.grid.on_change = self._grid_changed
        self.grid_rows: dict[str, int] = {}
        self.grid_dirty = False

    def _build_donor(self) -> None:
        ctk = W.ctk
        ctk.CTkLabel(self.t_donor, text="Bấm đúp vào ô \"Độ lệch\" để sửa tay (chỉ cho lượt chạy này; muốn lưu lâu dài thì "
                     "điền 'Độ lệch chỉnh tay' trong Cài đặt).", anchor="w").pack(fill="x")
        cols = [W.Col("src", "Phim", 280, stretch=True, fit="name"), W.Col("donor", "File nguồn", 260, stretch=True, fit="name"),
                W.Col("langs", "Ngôn ngữ", 80),
                W.Col("off", "Độ lệch (ms)", 110, "e"), W.Col("how", "Cách đo", 260), W.Col("fps", "Khác FPS", 170),
                W.Col("note", "Ghi chú", 260)]
        self.dtable = W.Table(self.t_donor, cols, height=3)
        self.dtable.frame.pack(fill="both", expand=True, pady=(4, 0))
        self.dtable.on_double = self._donor_double

    # ------------------------------------------------------------------ trang thai
    def on_config_changed(self, secrets_changed: bool = False) -> None:
        mode = str(self.app.cfg["RememberAudioChoice"])
        self.apply_rest.set(mode in ("ask", "always"))
        if mode == "never":
            self.apply_rest_cb.pack_forget()
        else:
            self.apply_rest_cb.pack(side="right", padx=8)
            self.apply_rest_cb.configure(state="disabled" if mode == "always" else "normal")
        if secrets_changed:
            self.db_checked = False
        if self.session is not None:
            self.reset("Cài đặt đã đổi → bấm \"Quét & lập kế hoạch\" lại để áp dụng.")

    def on_folder_changed(self) -> None:
        self.reset()

    def on_files_changed(self) -> None:
        if self.session is not None:
            self.reset("File trong thư mục đã thay đổi (hoàn tác) → lập kế hoạch lại.")

    def on_show(self) -> None:
        if not self.db_checked:
            self.check_db()

    def reset(self, note: str = "") -> None:
        if self.session is not None:
            self.session.close()
        self.session = None
        self.plans = []
        self.cur = None
        self._muxed = self._trash_done = False
        self.trash_cands = []
        self.fill()
        self.summary.configure(text="")
        if note:
            self.app.log("warn", note)
        self.update_buttons()

    def reset_all(self) -> None:
        """Nút "↺ Làm lại từ đầu": mọi bảng, tổng kết, tiến độ VÀ lựa chọn database về như chưa làm gì. Không xoá file."""
        if self.app.runner.busy:
            return
        pending = [p for p in self.plans if p.selected and p.result is None and p.status in ("ok", "need_choice")]
        if pending and not W.ask_yes_no(self.frame, "Làm lại từ đầu", f"Còn {len(pending)} file đã tick CHƯA mux. Xoá hết "
                                        "bảng và tiến độ của lượt này để bắt đầu lại?\n\n(Không xoá file nào.)"):
            return
        self.db.set("")  # database BAT BUOC chon lai moi luot
        self.reset()
        self.inner.set("Audio của file đang chọn")
        self.app.log("info", "③ Đã làm lại từ đầu: các bảng và thanh các bước về mặc định (không xoá file nào).")

    def update_buttons(self) -> None:
        busy = self.app.runner.busy
        db = self.db.get()

        def st(on: bool) -> str:
            return "normal" if on and not busy else "disabled"

        has = bool(self.plans)
        self.steps.update([has, has and self._muxed, self._trash_done or (self._muxed and not self.trash_cands), False],
                          [bool(db) and bool(self.app.folder), bool(db) and has, bool(self.trash_cands), True], busy)
        self.cmd_btn.configure(state=st(has))
        self.reset_btn.configure(state=st(bool(self.app.folder)))
        self.keepall_btn.configure(state=st(any(multi_en(p) for p in self.plans)))
        self.db_check_btn.configure(state=st(True))
        p = self._plan()
        self.apply_audio_btn.configure(state=st(p is not None and len(p.audio) >= 2))
        self.keep_both_btn.configure(state=st(p is not None and multi_en(p)))
        self.grid_apply_btn.configure(state=st(bool(self.grid_rows)))
        if not db:
            self.hint.configure(text="← Chọn TMDB / TVDB / Không đổi tên trước.", text_color=W.LEVEL_COLORS["warn"])
        elif has:
            n = {k: sum(1 for x in self.plans if x.status == k) for k in ("ok", "need_choice", "skip", "exists", "fail")}
            self.hint.configure(text=f"{n['ok']} sẵn sàng | {n['need_choice']} cần chọn | {n['skip'] + n['exists']} bỏ qua | "
                                f"{n['fail']} lỗi", text_color=W.LEVEL_COLORS["warn"] if n["need_choice"] else
                                W.LEVEL_COLORS["info"])
        else:
            self.hint.configure(text="")

    def _plan(self) -> MuxPlan | None:
        if self.cur is None or not (0 <= self.cur < len(self.plans)):
            return None
        return self.plans[self.cur]

    # ------------------------------------------------------------------ database
    def check_db(self) -> None:
        from .settings_view import check_connection, status_color  # noqa: PLC0415

        self.db_checked = True
        vals, cfg = self.app.secrets(), self.app.cfg
        for k in ("tmdb", "tvdb"):
            self.db_status[k].configure(text="đang kiểm tra…", text_color=W.LEVEL_COLORS["dim"])

        def work(job: tools.Job) -> dict[str, ConnStatus]:
            out = {}
            for k, sk in (("tmdb", "TmdbApiKey"), ("tvdb", "TvdbApiKey")):
                out[k] = check_connection(k, vals, cfg, job) if vals.get(sk) else ConnStatus(None, "chưa điền key")
            return out

        def done(res: dict[str, ConnStatus]) -> None:
            for k, stt in res.items():
                txt = stt.text.split(": ", 1)[1] if stt.text.startswith(("TMDB:", "TVDB:")) else stt.text
                self.db_status[k].configure(text=txt, text_color=status_color(stt))

        self.app.runner.start("Kiểm tra TMDB / TVDB", work, done, when_free=True)

    def db_changed(self) -> None:
        if self.plans and self.session is not None:
            db = self.db.get()
            files = [p.src for p in self.plans]
            s = self.session
            sel = {key_of(p): p.selected for p in self.plans}

            def work(job: tools.Job) -> list[MuxPlan]:
                s.set_job(job)
                return s.plan_all(db, files)

            self.app.runner.start(f"Lập lại kế hoạch ({db.upper() if db != 'none' else 'giữ tên gốc'})", work,
                                  lambda ps: self._set_plans(ps, sel))
        self.update_buttons()

    # ------------------------------------------------------------------ lap ke hoach
    def plan_all(self) -> None:
        folder = self.app.need_folder()
        db = self.db.get()
        if not folder or not db:
            return
        cfg, secrets = self.app.cfg, self.app.secrets()

        def work(job: tools.Job) -> list[MuxPlan]:
            s = self.session
            if s is None or os.path.normcase(str(s.td)) != os.path.normcase(folder):
                if s is not None:
                    s.close()
                s = MuxSession(folder, cfg, secrets, job)
                self.session = s
            else:
                s.set_job(job)
            return s.plan_all(db)

        def done(ps: list[MuxPlan]) -> None:
            self._muxed = self._trash_done = False  # ke hoach moi = luot moi
            self.trash_cands = []
            self._set_plans(ps, {})

        self.app.runner.start("Lập kế hoạch mux", work, done)

    def _set_plans(self, plans: list[MuxPlan], sel: dict[str, bool]) -> None:
        cur_key = key_of(self.plans[self.cur]) if self._plan() is not None else None
        for p in plans:
            k = key_of(p)
            if k in sel:
                p.selected = sel[k]
            elif p.status not in ("ok", "need_choice"):
                p.selected = False
        self.plans = list(plans)
        self.cur = next((i for i, p in enumerate(self.plans) if key_of(p) == cur_key), None)
        self.fill()
        self.update_buttons()

    def _replan(self, title: str, mutate: Callable[[MuxSession], Any] | None = None,
                only: list[MuxPlan] | None = None) -> None:
        s = self.session
        if s is None:
            return
        plans = list(self.plans)
        sel = {key_of(p): p.selected for p in plans}
        targets = {key_of(p) for p in only} if only is not None else None

        def work(job: tools.Job) -> list[MuxPlan]:
            s.set_job(job)
            if mutate:
                mutate(s)
            return [s.plan_file(p.src) if targets is None or key_of(p) in targets else p for p in plans]

        self.app.runner.start(title, work, lambda ps: self._set_plans(ps, sel))

    # ------------------------------------------------------------------ bang tong quan
    def fill(self) -> None:
        out_dir = self.session.out_dir if self.session else os.path.join(self.app.folder or "", "Output")
        self.table.clear()
        for i, p in enumerate(self.plans):
            vals, tag = overview_values(p, out_dir)
            self.table.add(f"p{i}", vals, tags=(tag,) if tag else (), checked=p.selected)
        if self.cur is not None:
            self.table.select(f"p{self.cur}")
        self.show_plan()
        self.fill_grid()
        self.fill_donor()

    def _toggle(self, iid: str, state: bool) -> None:
        self.plans[int(iid[1:])].selected = state

    def _toggle_all(self, state: bool) -> None:
        for p in self.plans:
            p.selected = state

    def _on_select(self, iids: list[str]) -> None:
        self.cur = int(iids[0][1:]) if iids else None
        self.show_plan()
        self.update_buttons()

    def _on_double(self, iid: str, col: str) -> None:
        self.cur = int(iid[1:])
        p = self._plan()
        if p is None:
            return
        if col in ("new", "src") or (col == "status" and p.name.status == "need_choice"):
            self.edit_name(p)
        elif col in ("audio", "status"):
            self.inner.set("Audio của file đang chọn")
        elif col == "subs":
            self.inner.set("Bảng chọn phụ đề theo tập")
        elif col == "donor":
            self.inner.set("Audio ghép từ file khác")

    def _tip(self, iid: str, col: str) -> str | None:
        try:
            p = self.plans[int(iid[1:])]
        except (ValueError, IndexError):
            return None
        if col == "status" and p.reasons:
            return "\n".join(p.reasons)
        if col == "new":
            return (p.name.note or "") + "\nBấm đúp để sửa tên" + (" / chọn kết quả" if p.name.status == "need_choice" else "")
        if col == "donor" and p.donor.timing:
            return p.donor.timing.how
        return None

    def show_plan(self) -> None:
        p = self._plan()
        out_dir = self.session.out_dir if self.session else ""
        self.atable.clear()
        self.info.configure(state="normal")
        self.info.delete("1.0", "end")
        if p is None:
            self.audio_title.configure(text="Chọn một file ở bảng trên.")
            self.info.configure(state="disabled")
            return
        self.info.insert("1.0", "\n".join(plan_details(p, out_dir)))
        self.info.configure(state="disabled")
        n = {"compact": 40, "normal": 60, "large": 90}.get(getattr(self.app, "density", "normal"), 60)
        head = f"{W.shorten_name(p.src.name, n)} — {len(p.audio)} audio"
        if multi_en(p):
            head += "  ⚠ 2+ audio tiếng Anh: tick track muốn xoá"
        elif p.needs_audio_table:
            head += " — tick track muốn XOÁ rồi bấm \"Áp dụng\""
        self.audio_title.configure(text=head, text_color=W.LEVEL_COLORS["warn"] if multi_en(p) else W.LEVEL_COLORS["info"])
        for iid, vals, chk, tag in audio_rows(p):
            self.atable.add(iid, vals, tags=(tag,) if tag else (), checked=chk)

    # ------------------------------------------------------------------ audio
    def apply_audio(self) -> None:
        p = self._plan()
        if p is None:
            return
        drop = [int(i[1:]) for i in self.atable.checked_ids()]
        keepable = [a for a in p.audio if a.reason not in FIXED_AUDIO]
        if keepable and len(drop) >= len(keepable):
            W.warn(self.frame, "Audio", "Không thể xoá HẾT audio của file — bỏ tick ít nhất một track.")
            return
        rest = bool(self.apply_rest.get()) and str(self.app.cfg["RememberAudioChoice"]) != "never"
        self._replan("Áp dụng lựa chọn audio", lambda s: s.set_audio_choice(p, drop, rest), None if rest else [p])

    def keep_both(self) -> None:
        p = self._plan()
        if p is not None:
            self._replan("Giữ cả hai audio", lambda s: s.keep_all_audio(p), [p])

    def keep_all_en(self) -> None:
        if W.ask_yes_no(self.frame, "Giữ mọi audio tiếng Anh", "Mọi file có ≥ 2 audio tiếng Anh: giữ cả hai, Default = "
                        "track bitrate cao nhất?"):
            self._replan("Giữ mọi audio tiếng Anh", lambda s: s.keep_all_audio(None))

    # ------------------------------------------------------------------ phu de theo tap
    def fill_grid(self) -> None:
        cfg = self.app.cfg
        self.grid_rows = {}
        self.grid_dirty = False
        if not self.plans:
            self.grid.set_model(W.GridModel([], [], {}), {}, {})
            self.grid_note.configure(text="Lập kế hoạch trước.")
            return
        if not cfg["UseSubPicker"]:
            self.grid.set_model(W.GridModel([], [], {}), {}, {})
            self.grid_note.configure(text="Bảng chọn phụ đề đang tắt (Cài đặt › ③ Mux › 'Bảng chọn phụ đề theo tập').")
            return
        data = grid_data(self.plans, self.session.sub_choices if self.session else None)
        if len(data["rows"]) < int(cfg["SubPickerMinFiles"]):
            self.grid.set_model(W.GridModel([], [], {}), {}, {})
            self.grid_note.configure(text=f"Chỉ hiện khi có ≥ {cfg['SubPickerMinFiles']} file có phụ đề.")
            return
        self.grid_rows = {r: int(r[1:]) for r in data["rows"]}
        model = W.GridModel(data["rows"], data["cols"], data["auto"], data["current"])
        self.grid.set_model(model, data["headers"], data["labels"], data["tips"])
        self._grid_note()

    def _grid_note(self) -> None:
        m = self.grid.model
        n = len(m.changed_rows()) if m else 0
        txt = f"{len(self.grid_rows)} file. Tick sẵn theo quy tắc tự động; ô vàng = khác quy tắc."
        if n:
            txt += f"  {n} file đã đổi" + (" — bấm \"Áp dụng bảng chọn\"" if self.grid_dirty else "")
        self.grid_note.configure(text=txt, text_color=W.LEVEL_COLORS["warn"] if self.grid_dirty else W.LEVEL_COLORS["info"])

    def _grid_changed(self) -> None:
        self.grid_dirty = True
        self._grid_note()

    def _grid_all(self, state: bool) -> None:
        if self.grid.model:
            self.grid.model.set_all(state)
            self.grid.redraw()
            self._grid_changed()

    def _grid_reset(self) -> None:
        if self.grid.model:
            self.grid.model.reset()
            self.grid.redraw()
            self._grid_changed()

    def apply_grid(self) -> None:
        m = self.grid.model
        if m is None or self.session is None:
            return
        plans = self.plans
        rows = dict(self.grid_rows)
        choices = {r: grid_choice(plans[i], m.row_values(r)) for r, i in rows.items() if m.row_differs(r)}

        def mutate(s: MuxSession) -> None:
            s.reset_sub_choices()
            for r, (ext, ints) in choices.items():
                s.set_sub_choice(plans[rows[r]], ext, ints)

        self._replan("Áp dụng bảng chọn phụ đề", mutate)

    # ------------------------------------------------------------------ audio ghep
    def fill_donor(self) -> None:
        self.dtable.clear()
        for i, p in enumerate(self.plans):
            if p.donor.tracks or p.donor.donor or (p.donor.note and self.app.cfg["UseDonorAudio"]):
                tag = "" if p.donor.tracks else "dim"
                if p.donor.timing is not None and not p.donor.timing.sure and p.donor.tracks:
                    tag = "need"
                self.dtable.add(f"d{i}", donor_values(p), tags=(tag,) if tag else ())

    def _donor_double(self, iid: str, col: str) -> None:
        p = self.plans[int(iid[1:])]
        if not p.donor.tracks and not p.donor.donor:
            return
        cur = f"{p.donor.tracks[0].offset_ms}" if p.donor.tracks else ""
        txt = W.ask_string(self.frame, "Độ lệch audio ghép", f"{p.rel}\n\nĐộ lệch (ms) cho audio ghép — để trống = dùng kết "
                           "quả đo / giá trị trong Cài đặt:", cur if p.donor.manual else "")
        if txt is None:
            return
        try:
            ms = parse_ms(txt)
        except ValueError as e:
            W.warn(self.frame, "Độ lệch", str(e))
            return
        self._replan("Sửa độ lệch audio ghép", lambda s: s.set_donor_offset(p, ms), [p])

    # ------------------------------------------------------------------ ten
    def edit_name(self, p: MuxPlan) -> None:
        db = self.db.get()
        act = NameDialog(self, p, db).run()
        if not act:
            return
        kind = act[0]
        if kind == "choose":
            self._replan("Chọn tên", lambda s: s.choose_name(p, act[1]))
        elif kind == "keep":
            self._replan("Giữ tên gốc", lambda s: s.keep_original_name(p), [p])
        elif kind == "set":
            self._replan("Sửa tên", lambda s: s.set_name(p, act[1]), [p])
        elif kind == "search":
            s = self.session
            if s is None:
                return
            title, year = act[1], act[2]
            sel = {key_of(x): x.selected for x in self.plans}
            plans = list(self.plans)

            def work(job: tools.Job) -> list[MuxPlan]:
                s.set_job(job)
                if s.search_name(p, title, year) is None:
                    job.warn("[!] Database đã chọn không dùng được (chưa có key / key bị từ chối).")
                return [s.plan_file(x.src) if x is p else x for x in plans]

            def done(ps: list[MuxPlan]) -> None:
                self._set_plans(ps, sel)
                np = next((x for x in self.plans if key_of(x) == key_of(p)), None)
                if np is not None:
                    self.app.root.after(50, lambda: self.edit_name(np))

            self.app.runner.start(f"Tìm lại '{title}'", work, done)

    # ------------------------------------------------------------------ chay
    def show_commands(self) -> None:
        s = self.session
        if s is None:
            return
        p = self._plan()
        ps = [p] if p is not None else [x for x in self.plans if x.status in ("ok", "need_choice")][:20]

        def work(job: tools.Job) -> list[str]:
            s.set_job(job)
            lines: list[str] = []
            for x in ps:
                cmds = s.preview_commands(x)
                if cmds:
                    lines.append(f"### {x.rel}")
                    lines += cmds
                    lines.append("")
            return lines

        def done(lines: list[str]) -> None:
            if not lines:
                W.info(self.frame, "Xem lệnh", "File này không có lệnh (bị bỏ qua / lỗi — xem cột Trạng thái).")
                return
            W.show_text(self.frame, "Xem lệnh (không ghi file nào)", "\n".join(lines),
                        note="Đúng các lệnh ffmpeg (đổi tốc độ audio ghép) + mkvmerge sẽ chạy. Kết quả ghi ra tên tạm "
                             "'<tên>.muxstudio-tmp.mkv' rồi mới đổi thành tên thật.")

        self.app.runner.start("Dựng lệnh", work, done)

    def start_mux(self) -> None:
        s = self.session
        if s is None or not self.db.get():
            return
        sel = [p for p in self.plans if p.selected and p.result is None]
        if not sel:
            W.info(self.frame, "Mux", "Không có file nào được tick (file đã mux xong được bỏ tick tự động).")
            return
        need_name = [p for p in sel if p.name.status == "need_choice"]
        keep_names = False
        if need_name:
            ans = W.ask_yes_no_cancel(
                self.frame, "Còn dòng cần chọn tên", f"Còn {len(need_name)} file CẦN CHỌN tên (vàng).\n\nCó = giữ tên file "
                "gốc cho các file đó rồi mux luôn.\nKhông = quay lại để chọn.")
            if not ans:
                return
            keep_names = True
        need_audio = [p for p in sel if p.status == "need_choice" and p.name.status != "need_choice"]
        n_ok = sum(1 for p in sel if p.status in ("ok", "need_choice")) - len(need_audio)
        msg = f"Mux {n_ok} file vào '{s.out_dir}'?"
        if need_audio:
            msg += f"\n\n{len(need_audio)} file còn CẦN CHỌN audio sẽ KHÔNG được mux (xem cột Trạng thái)."
        if not W.ask_yes_no(self.frame, "Bắt đầu mux", msg):
            return
        plans = list(self.plans)
        selmap = {key_of(p): (p.selected and p.result is None) for p in plans}

        def work(job: tools.Job) -> tuple[list[MuxPlan], MuxReport]:
            s.set_job(job)
            ps = s.keep_original_names(plans) if keep_names else plans
            for p in ps:
                p.selected = selmap.get(key_of(p), False)
            return ps, s.execute(ps)

        def done(res: tuple[list[MuxPlan], MuxReport]) -> None:
            ps, rep = res
            for p in ps:
                if p.result is not None and p.result.ok:
                    p.selected = False
            self._muxed, self._trash_done = True, False
            seen = {os.path.normcase(os.fspath(u.path)) for u in self.trash_cands}
            self.trash_cands = [u for u in self.trash_cands if u.path.is_file()] + [
                u for u in used_sources(ps, s.out_dir) if os.path.normcase(os.fspath(u.path)) not in seen]
            self._set_plans(ps, {key_of(p): p.selected for p in ps})
            self.summary.configure(text=summary_text(rep))
            if self.trash_cands and self.app.cfg["AskTrashSourcesAfterMux"]:
                self.app.root.after(150, self.offer_trash)

        self.app.runner.start("Mux", work, done)

    def offer_trash(self) -> None:
        """Mux xong: hỏi đưa file nguồn đã dùng (MKV gốc, phụ đề ngoài, nguồn audio ghép) vào Thùng rác — tick từng file."""
        cands = [u for u in self.trash_cands if u.path.is_file()]
        s = self.session
        if not cands or s is None:
            self.trash_cands, self._trash_done = [], True
            self.update_buttons()
            W.info(self.frame, "Dọn file nguồn", "Không còn file nguồn nào để dọn (chỉ đề xuất file của các file đã mux "
                   "THÀNH CÔNG, không đụng file vẫn cần cho file chưa mux).")
            return
        chosen = TrashDialog(self, cands).run()
        self._trash_done = True
        self.update_buttons()
        if not chosen:
            return

        def work(job: tools.Job) -> TrashReport:
            s.set_job(job)
            return s.trash_sources(chosen)

        def done(rep: TrashReport) -> None:
            self.trash_cands = [u for u in self.trash_cands if u.path.is_file()]
            if rep.failed:
                W.warn(self.frame, "Dọn file nguồn", f"Không xoá được {len(rep.failed)} file (xem nhật ký).")
            self.update_buttons()

        self.app.runner.start("Dọn file nguồn đã mux", work, done)

    def open_output(self) -> None:
        d = os.path.join(self.app.folder, "Output") if self.app.folder else None
        if not d or not W.open_path(d):
            W.info(self.frame, "Output", "Chưa có thư mục Output (chưa mux file nào).")

    def close(self) -> None:
        if self.session is not None:
            self.session.close()
            self.session = None


def size_text(n: int) -> str:
    """``1536`` → ``1,5 KB``; ``734003200`` → ``700 MB``; ``5 GB`` một chữ số lẻ."""
    for unit, k in (("GB", 1 << 30), ("MB", 1 << 20), ("KB", 1 << 10)):
        if n >= k:
            v = n / k
            return (f"{v:.1f}".replace(".", ",") if v < 10 else f"{v:.0f}") + f" {unit}"
    return f"{n} B"


def trash_values(u: UsedFile, root: str | os.PathLike[str] | None) -> dict[str, str]:
    """Một dòng của hộp thoại "Dọn file nguồn đã dùng"."""
    try:
        size = size_text(u.path.stat().st_size)
    except OSError:
        size = "?"
    rel = os.path.relpath(u.path, root) if root and tools.is_under(u.path, root) else os.fspath(u.path)
    return {"kind": u.kind_text, "file": rel, "size": size, "used": ", ".join(u.used_for)}


class TrashDialog:
    """Danh sách file nguồn đã dùng cho file mux thành công, tick sẵn — bấm nút mới đưa vào Thùng rác."""

    def __init__(self, view: "MuxView", cands: list[UsedFile]):
        ctk = W.ctk
        self.cands = cands
        rb = bool(view.app.cfg["DeleteToRecycleBin"])
        self.d = W.Dialog(view.frame, "Dọn file nguồn đã mux xong", 1080, 560)
        ctk.CTkLabel(self.d.body, text=f"{len(cands)} file nguồn đã dùng cho các file mux THÀNH CÔNG (kết quả nằm trong "
                     "Output). Tick file muốn xoá. File vẫn cần cho file chưa mux không có trong danh sách.",
                     anchor="w", justify="left", wraplength=self.d.wrap).pack(fill="x")
        ctk.CTkLabel(self.d.body, text="→ vào Thùng rác (khôi phục được: chuột phải trong Thùng rác › Restore)." if rb else
                     "⚠ Bạn đã TẮT 'Xoá vào Thùng rác' → các file này sẽ bị XOÁ HẲN.", anchor="w",
                     text_color=W.LEVEL_COLORS["ok" if rb else "error"]).pack(fill="x", pady=(4, 0))
        cols = [W.Col("kind", "Loại", 150), W.Col("file", "File", 470, stretch=True, fit="name"),
                W.Col("size", "Dung lượng", 90, "e"), W.Col("used", "Đã dùng cho", 300, stretch=True, fit="name")]
        self.t = W.Table(self.d.body, cols, check="Xoá", height=12, selectmode="browse")
        self.t.frame.pack(fill="both", expand=True, pady=8)
        root = view.app.folder
        for i, u in enumerate(cands):
            self.t.add(f"t{i}", trash_values(u, root), checked=True)
        self.d.button("Không xoá", lambda: self.d.close(None))
        self.d.button("Đưa các file đã tick vào Thùng rác" if rb else "XOÁ HẲN các file đã tick", self._ok, primary=True)

    def _ok(self) -> None:
        self.d.close([self.cands[int(i[1:])] for i in self.t.checked_ids()])

    def run(self) -> list[UsedFile] | None:
        return self.d.run()


class NameDialog:
    """Dòng "Cần chọn" / sửa tên: 5 kết quả gần nhất, "Tìm lại với tên…", "Giữ tên gốc", "Sửa tay…"."""

    def __init__(self, view: MuxView, plan: MuxPlan, db: str):
        ctk = W.ctk
        self.view, self.plan, self.db = view, plan, db
        self.d = W.Dialog(view.frame, "Tên mới", 900, 520)
        nm = plan.name
        ctk.CTkLabel(self.d.body, text=f"File: {plan.rel}\n{nm.note or ''}", anchor="w", justify="left",
                     wraplength=self.d.wrap).pack(fill="x")
        cols = [W.Col("title", "Tên", 360, stretch=True), W.Col("year", "Năm", 70), W.Col("id", "ID", 140),
                W.Col("orig", "Tên gốc", 240)]
        self.t = W.Table(self.d.body, cols, height=6)
        self.t.frame.pack(fill="both", expand=True, pady=6)
        dbn = nm.db or db
        for i, c in enumerate(nm.candidates):
            self.t.add(f"c{i}", {"title": c.title, "year": c.year or "????", "id": f"{dbn}id-{c.id}", "orig": c.original})
        if not nm.candidates:
            self.t.add("none", {"title": "(không có kết quả để chọn)", "year": "", "id": "", "orig": ""}, tags=("dim",))
        self.t.on_double = lambda iid, _c: self._choose(iid)
        sf = ctk.CTkFrame(self.d.body, fg_color="transparent")
        sf.pack(fill="x", pady=4)
        ctk.CTkLabel(sf, text="Tìm lại với tên:").pack(side="left")
        ep = None
        try:
            from core import episodes  # noqa: PLC0415

            ep = episodes.parse_episode(plan.src.stem)
        except Exception:  # noqa: BLE001
            ep = None
        rn = naming.split_release_name(plan.src.stem)
        guess = (ep.series if ep and ep.series else rn.title) or plan.src.stem
        gyear = (ep.year if ep else rn.year) or ""
        self.q = ctk.CTkEntry(sf, width=360)
        self.q.insert(0, guess)
        self.q.pack(side="left", padx=6)
        ctk.CTkLabel(sf, text="Năm:").pack(side="left")
        self.y = ctk.CTkEntry(sf, width=70)
        self.y.insert(0, str(gyear))
        self.y.pack(side="left", padx=6)
        can_search = db in ("tmdb", "tvdb")
        ctk.CTkButton(sf, text="Tìm lại", width=90, command=self._search, state="normal" if can_search else "disabled").pack(
            side="left")
        if not can_search:
            ctk.CTkLabel(sf, text="(đang chọn 'Không đổi tên')", text_color=W.LEVEL_COLORS["dim"]).pack(side="left", padx=6)
        self.d.button("Huỷ", self.d.close)
        self.d.button("Sửa tay…", self._manual)
        self.d.button("Giữ tên gốc", lambda: self.d.close(("keep",)))
        if nm.candidates:
            self.d.button("Chọn kết quả này", lambda: self._choose(None), primary=True)

    def _choose(self, iid: str | None) -> None:
        if iid is None:
            sel = self.t.selected()
            iid = sel[0] if sel else None
        if not iid or not iid.startswith("c"):
            W.info(self.d.win, "Chọn", "Bấm chọn một kết quả trong bảng.")
            return
        c = self.plan.name.candidates[int(iid[1:])]
        self.d.close(("choose", c.id))

    def _search(self) -> None:
        title = self.q.get().strip()
        if not title:
            return
        try:
            year = parse_year(self.y.get())
        except ValueError as e:
            W.warn(self.d.win, "Năm", str(e))
            return
        self.d.close(("search", title, year))

    def _manual(self) -> None:
        cur = self.plan.name.base
        txt = W.ask_string(self.d.win, "Sửa tay", "Tên file mới (không gồm .mkv, không gồm thẻ codec — thẻ codec tự "
                           "gắn thêm; ký tự Windows cấm sẽ bị bỏ):", cur)
        if txt and txt.strip():
            self.d.close(("set", txt.strip()))

    def run(self) -> Any:
        return self.d.run()
