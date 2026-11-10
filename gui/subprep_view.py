"""Thẻ "② Chuẩn bị phụ đề" (thay SubPrep — PROMPT §5.2, SPEC mục 6 #4–#17).

1. Số phim / phụ đề trong ``srt_can_sync`` / phụ đề gốc trong ``srt_goc``.
2. **Mốc đồng bộ**: phụ đề gốc trong ``srt_goc`` hoặc audio của file phim (bảng audio track của file mẫu + tự động /
   theo số thứ tự / theo ngôn ngữ).
3. **Ngôn ngữ**: tự nhận diện (thư mục con / hậu tố) hoặc ép cố định — file nằm trong thư mục ngôn ngữ khác hiện đỏ.
4. Nút "Trích phụ đề gốc từ MKV" (bảng xem trước → trích) — ẩn khi mốc = audio (không cần phụ đề gốc).
5. **Bảng xem trước đổi tên** (nút "Quét phụ đề") → "Đổi tên".
6. **Bảng cặp đồng bộ** → "Chạy đồng bộ" → sức khoẻ, dịch đầu/cuối, chênh, còn lệch, mốc khớp, recap đã cắt, cờ;
   lọc "chỉ hiện file cần xem", xuất CSV, "Xem lệnh".
7. Hộp thoại dọn dẹp sau khi chạy.
8. Nút "↺ Làm lại từ đầu": xoá mọi bảng / tiến độ của lượt này (không xoá file nào) để làm lô file mới.
"""
from __future__ import annotations

import csv
import os
from pathlib import Path
from typing import Any

from core import naming, probe, tools
from core.subprep import (LEVEL_TEXT, AudioChoice, CleanupPlan, ExtractRow, PrepCounts, PrepSession, RenameRow,
                          SyncPair, VideoAudio, audio_layout_groups, pick_audio_stream)

from . import widgets as W

AUTO_LANG = "Tự nhận diện (thư mục con vi\\ cs\\ en\\ hoặc hậu tố tên file)"
OTHER_LANG = "Khác…"
LEVEL_TAG = {0: "good", 1: "need", 2: "bad"}
MODE_SUB = "sub"
MODE_AUDIO = "audio"
# the con (khong ghi so buoc: moc = audio thi thanh cac buoc bot buoc "Trich phu de goc" va danh so lai)
INNER_RENAME = "Đổi tên"
INNER_SYNC = "Đồng bộ"
RESET_TEXT = "↺ Làm lại từ đầu"
REF_MARK = "◄ mốc"


# ==========================================================================
#  PHẦN THUẦN
# ==========================================================================


def lang_options(extra: list[str] | None = None) -> list[str]:
    """Các lựa chọn của ô "Ngôn ngữ" (mặc định = tự nhận diện)."""
    out = [AUTO_LANG]
    for c in [*naming.FETCH_LANG_SWITCHES, *(extra or [])]:
        lab = f"{c} — {naming.lang_name(c)}" if naming.lang_name(c) else c
        if lab not in out:
            out.append(lab)
    out.append(OTHER_LANG)
    return out


def lang_from_option(opt: str) -> str:
    if not opt or opt == AUTO_LANG:
        return "auto"
    return opt.split(" — ", 1)[0].strip().lower()


def counts_text(c: PrepCounts, cfg: Any) -> str:
    return (f"Phim: {c.videos}   |   Phụ đề trong '{cfg['SubFolderName']}': {c.subs}   |   "
            f"Phụ đề gốc trong '{cfg['ExtractFolder']}': {c.refs}")


def audio_iid(fi: int, index: int) -> str:
    return f"a{fi}_{index}"


def audio_iid_index(iid: str) -> int:
    """Số thứ tự audio (0:a:N) của một dòng bảng audio."""
    return int(iid.rsplit("_", 1)[1])


def audio_rows(files: list[VideoAudio], choice: AudioChoice | None) -> list[tuple[str, dict[str, Any], str]]:
    """Bảng audio của MỌI file phim (TĐ-35): ``[(iid, giá trị, nhãn màu)]`` — mỗi dòng một track của một file. Tên file /
    tập chỉ ghi ở dòng đầu của file. Cột ``ref`` đánh dấu track sẽ làm mốc theo ``choice`` (``None`` = chưa chọn).
    Nhãn ``need`` (vàng) = file có bố cục audio KHÁC đa số; ``bad`` = không đọc được audio."""
    groups = audio_layout_groups(files)
    main = groups[0][0].layout if groups else ()
    out: list[tuple[str, dict[str, Any], str]] = []
    for fi, f in enumerate(files):
        if not f.streams:
            out.append((f"a{fi}_x", {"ep": f.ep_label, "file": f.video.name, "idx": "", "lang": "", "codec": "", "ch": "",
                                     "title": "(không đọc được audio track)", "def": "", "ref": ""}, "bad"))
            continue
        pick = miss = None
        if choice is not None:
            pick, _desc, miss = pick_audio_stream(f.streams, choice)
        tag = "need" if f.layout != main else ""
        for n, a in enumerate(f.streams):
            mark = ""
            if pick == a.index:
                mark = REF_MARK + (" (Default — không có lựa chọn)" if miss else "")
            out.append((audio_iid(fi, a.index), {
                "ep": f.ep_label if n == 0 else "", "file": f.video.name if n == 0 else "", "idx": a.index,
                "lang": a.lang, "codec": a.codec, "ch": a.channels or "", "title": a.title,
                "def": "✓" if a.default else "", "ref": mark}, tag))
    return out


def audio_summary(files: list[VideoAudio]) -> tuple[str, str]:
    """Dòng tiêu đề của bảng audio → (chữ, mức màu ``info``/``warn``/``error``)."""
    if not files:
        return "Không tìm thấy file phim (hoặc thiếu ffprobe) để đọc audio track.", "warn"
    unread = sum(1 for f in files if not f.streams)
    if unread == len(files):
        return f"Audio track của {len(files)} file phim: KHÔNG đọc được file nào (thiếu ffprobe?).", "error"
    groups = audio_layout_groups([f for f in files if f.streams])
    txt = f"Audio track của {len(files)} file phim"
    other = sum(len(g) for g in groups[1:])
    level = "info"
    if len(files) == 1:
        txt += f": {files[0].video.name}"
    elif not other:
        txt += " — mọi file cùng bố cục audio."
    else:
        txt += f" — {other} file có bố cục audio KHÁC đa số (dòng vàng): kiểm tra track làm mốc của từng file."
        level = "warn"
    if unread:
        txt += f"  {unread} file không đọc được audio (đỏ)."
        level = "warn"
    return txt, level


def extract_values(r: ExtractRow) -> tuple[dict[str, Any], str]:
    vals = {"video": r.video.name, "track": r.track.describe() if r.track else "",
            "dest": r.final.name if r.final else "", "why": r.status or r.reason}
    tag = "good" if r.status == "đã trích" else ("bad" if r.status else ("dim" if r.reason else ""))
    return vals, tag


def rename_values(r: RenameRow) -> tuple[dict[str, Any], str]:
    """Một dòng bảng xem trước đổi tên."""
    why = r.status or r.reason or ""
    if r.warn:
        why = (why + " | " if why else "") + "⚠ " + r.warn
    vals = {"ep": r.ep_label, "old": r.src.name, "new": r.new_path.name if r.new_path else "",
            "lang": r.lang or "?", "why": why}
    if r.status == "đã đổi tên":
        tag = "good"
    elif r.status:
        tag = "bad"
    elif r.warn:
        tag = "bad"  # canh bao DO: file nam trong thu muc ngon ngu khac lua chon
    elif r.reason == "đã đúng tên":
        tag = "dim"
    elif r.reason:
        tag = "need"
    else:
        tag = ""
    return vals, tag


def rename_tip(r: RenameRow) -> str:
    lines = [f"Nguồn: {r.src}"]
    if r.new_path:
        lines.append(f"Tên mới: {r.new_path.name}")
    if r.ref is not None:
        lines.append(f"Mốc: {r.ref.file.name}" + (f" ({r.how})" if r.how else ""))
    lines += r.details
    return "\n".join(lines)


def _f(x: float | None, d: int = 2, signed: bool = True) -> str:
    if x is None:
        return ""
    return f"{x:+.{d}f}" if signed else f"{x:.{d}f}"


def pair_values(p: SyncPair) -> tuple[dict[str, Any], str]:
    """Một dòng bảng cặp đồng bộ (trước và sau khi chạy)."""
    vals: dict[str, Any] = {"sub": p.sub.name, "ref": (p.ref.file.name if p.ref else ""),
                            "kind": p.ref_kind_text if p.ref else "", "out": p.out_path.name if p.out_path else "",
                            "forecast": "; ".join(p.forecast()), "health": "", "first": "", "last": "", "spread": "",
                            "resid": "", "votes": "", "cut": "", "flags": p.reason}
    res = p.result
    tag = "dim" if p.reason else ""
    if p.relation is not None and p.relation.fps_mismatch and not p.reason:
        tag = "need"
    if res is not None:
        h = res.health
        if res.ok and h is not None:
            vals["health"] = h.text
            vals["first"] = _f(h.shift_first)
            vals["last"] = _f(h.shift_last)
            vals["spread"] = _f(h.spread)
            if h.res_votes >= 0 and h.res_offset is not None:
                vals["resid"] = _f(h.res_offset)
                vals["votes"] = str(h.res_votes)
            vals["cut"] = str(res.cut) if res.cut else ""
            flags = list(h.flags)
            if res.fixed:
                flags.insert(0, f"bỏ {res.fixed} dòng dồn về 00:00:00")
            vals["flags"] = "; ".join(flags + res.notes)
            tag = LEVEL_TAG.get(h.level, "")
        else:
            vals["health"] = "TỪ CHỐI" if res.rejected else "LỖI"
            vals["flags"] = "; ".join(res.notes) or p.status
            tag = "bad"
    elif p.status:
        vals["flags"] = p.status
    return vals, tag


def pair_needs_review(p: SyncPair) -> bool:
    """Lọc "chỉ hiện file cần xem": đã chạy mà CẦN XEM / CÓ VẤN ĐỀ / lỗi / từ chối."""
    r = p.result
    if r is None:
        return False
    return not r.ok or (r.level or 0) >= 1


def pair_tip(p: SyncPair) -> str:
    lines = [f"Phụ đề: {p.sub}"]
    if p.ref is not None:
        lines.append(f"Mốc ({p.ref_kind_text}): {p.ref.file}" + (f" — {p.audio_desc}" if p.audio_desc else ""))
    if p.out_path:
        lines.append(f"Kết quả: {p.out_path}")
    lines += p.forecast() + p.notes + p.details
    if p.result is not None:
        lines += [f"({n})" for n in p.result.notes]
        lines += p.result.output_tail[-4:]
    return "\n".join(lines)


EXPORT_COLS = (("sub", "Phụ đề"), ("ref", "Mốc"), ("kind", "Loại mốc"), ("out", "Kết quả"), ("forecast", "Dự báo"),
               ("health", "Sức khoẻ"), ("first", "Dịch đầu (s)"), ("last", "Dịch cuối (s)"), ("spread", "Chênh (s)"),
               ("resid", "Còn lệch (s)"), ("votes", "Mốc khớp"), ("cut", "Cắt recap"), ("flags", "Cờ / lý do"))


def export_csv(pairs: list[SyncPair], path: str | os.PathLike[str]) -> int:
    """Nút "Xuất CSV" (UTF-8 có BOM để Excel đọc đúng dấu)."""
    with open(path, "w", encoding="utf-8-sig", newline="") as fh:
        w = csv.writer(fh, quoting=csv.QUOTE_ALL, lineterminator="\r\n")
        w.writerow([t for _k, t in EXPORT_COLS])
        for p in pairs:
            vals, _tag = pair_values(p)
            w.writerow([vals.get(k, "") for k, _t in EXPORT_COLS])
    return len(pairs)


def cleanup_lines(plan: CleanupPlan, which: str, root: str | os.PathLike[str]) -> list[str]:
    out = []
    for p in plan.targets(which):
        out.append(os.path.relpath(p, root) if tools.is_under(p, root) else os.fspath(p))
    return out


# ==========================================================================
#  GIAO DIỆN
# ==========================================================================


class SubprepView:
    def __init__(self, app: Any, parent: Any):
        ctk = W.ctk
        self.app = app
        self.session: PrepSession | None = None
        self.renames: list[RenameRow] = []
        self.pairs: list[SyncPair] = []
        self.streams: list[probe.AudioStream] = []
        self.sample: Path | None = None
        self.frame = ctk.CTkFrame(parent, fg_color="transparent")
        self.frame.pack(fill="both", expand=True)

        # --- 1. dem ---
        cf = ctk.CTkFrame(self.frame)
        cf.pack(fill="x")
        self.counts = ctk.CTkLabel(cf, text="Chọn thư mục phim ở đầu cửa sổ.", anchor="w")
        self.counts.pack(side="left", padx=10, pady=6)
        self.recount_btn = ctk.CTkButton(cf, text="Làm mới", width=90, command=self.refresh_counts)
        self.recount_btn.pack(side="left", padx=6)
        self.reset_btn = ctk.CTkButton(cf, text=RESET_TEXT, width=150, command=self.reset_all,
                                       fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.reset_btn.pack(side="right", padx=10)
        W.Tooltip(self.reset_btn, "Xoá mọi bảng và tiến độ của lượt này (thanh các bước về như chưa làm gì) để làm lô file "
                  "mới. KHÔNG xoá file nào; Mốc đồng bộ và Ngôn ngữ đang chọn được giữ.")

        # --- 2-4. moc / ngon ngu / trich ---
        of = ctk.CTkFrame(self.frame)
        of.pack(fill="x", pady=6)
        ctk.CTkLabel(of, text="Mốc đồng bộ:", font=ctk.CTkFont(weight="bold")).grid(row=0, column=0, sticky="w", padx=10, pady=6)
        self.mode = W.tk.StringVar(value=MODE_SUB)
        self.mode_sub = ctk.CTkRadioButton(of, text=f"Phụ đề gốc trong '{app.cfg['ExtractFolder']}'", variable=self.mode,
                                           value=MODE_SUB, command=self._mode_changed)
        self.mode_sub.grid(row=0, column=1, sticky="w")
        ctk.CTkRadioButton(of, text="Audio của file phim", variable=self.mode, value=MODE_AUDIO,
                           command=self._mode_changed).grid(row=0, column=2, sticky="w", padx=12)
        ctk.CTkLabel(of, text="Ngôn ngữ:", font=ctk.CTkFont(weight="bold")).grid(row=0, column=3, sticky="e", padx=(24, 6))
        self.lang = W.tk.StringVar(value=AUTO_LANG)
        self.lang_menu = ctk.CTkOptionMenu(of, values=lang_options(), variable=self.lang, width=330,
                                           command=self._lang_changed)
        self.lang_menu.grid(row=0, column=4, sticky="w")
        self.extract_hint = ctk.CTkLabel(of, text="", text_color=W.LEVEL_COLORS["warn"], anchor="w")
        self.extract_hint.grid(row=1, column=1, columnspan=5, sticky="w")
        self.extract_hint.grid_remove()  # chi hien khi co dieu can nhac (tiet kiem chieu cao man hinh 720p)
        of.columnconfigure(6, weight=1)

        # bang audio cua MOI file phim (chi hien khi moc = audio) - TD-35
        self.audio_box = ctk.CTkFrame(self.frame)
        self.audio_title = ctk.CTkLabel(self.audio_box, text="Audio track của các file phim:", anchor="w")
        self.audio_title.pack(fill="x", padx=10, pady=(6, 0))
        arow = ctk.CTkFrame(self.audio_box, fg_color="transparent")
        arow.pack(fill="x", padx=10, pady=4)
        ctk.CTkLabel(arow, text="Audio làm mốc:", font=ctk.CTkFont(weight="bold")).pack(side="left", padx=(0, 8))
        self.amode = W.tk.StringVar(value="")
        ctk.CTkRadioButton(arow, text="Tự động (audio Default của file)", variable=self.amode, value="auto",
                           command=self.mark_audio).pack(side="left")
        ctk.CTkRadioButton(arow, text="Theo số thứ tự (chọn dòng bên dưới)", variable=self.amode, value="index",
                           command=self.mark_audio).pack(side="left", padx=12)
        ctk.CTkRadioButton(arow, text="Theo ngôn ngữ:", variable=self.amode, value="lang",
                           command=self.mark_audio).pack(side="left")
        self.alang = ctk.CTkEntry(arow, width=70)
        self.alang.pack(side="left", padx=4)
        self.alang.bind("<KeyRelease>", lambda _e: self.mark_audio(), add="+")
        acols = [W.Col("ep", "Tập", 70), W.Col("file", "File phim", 320, stretch=True, fit="name"),
                 W.Col("idx", "Số thứ tự", 70, "e"), W.Col("lang", "Ngôn ngữ", 80), W.Col("codec", "Codec", 80),
                 W.Col("ch", "Kênh", 50, "e"), W.Col("title", "Tên track", 220, stretch=True), W.Col("def", "Default", 60),
                 W.Col("ref", "Làm mốc", 260)]
        # so dong toi da theo co man hinh (nhieu hon thi cuon): khong de bang nay che mat bang doi ten / dong bo
        self._audio_max_rows = {"compact": 3, "large": 8}.get(app.density, 4)
        self.atable = W.Table(self.audio_box, acols, height=3, selectmode="browse")
        self.atable.frame.pack(fill="x", padx=10, pady=(0, 8))
        self.atable.on_select = self._audio_selected
        self.audio_files: list[VideoAudio] = []

        # --- cac buoc: nut TO theo thu tu, buoc ke tiep sang mau ---
        self._counts: PrepCounts | None = None
        self._renames_planned = self._pairs_planned = self._cleaned = False
        self.steps = W.StepBar(self.frame, [
            ("extract", "Trích phụ đề gốc", self.plan_extract), ("preview", "Quét phụ đề", self.plan_rename),
            ("rename", "Đổi tên", self.do_rename), ("pairs", "Lập cặp đồng bộ", self.plan_sync),
            ("sync", "Chạy đồng bộ", self.do_sync), ("cleanup", "Dọn dẹp", self.cleanup),
            ("next", "Sang ③ Mux →", lambda: self.app.goto("③ Mux"))], compact=app.density == "compact", plain=("next",))
        self.steps.frame.pack(fill="x", pady=(0, 4))
        (self.extract_btn, self.plan_rename_btn, self.rename_btn, self.plan_sync_btn, self.sync_btn,
         self.cleanup_btn) = (self.steps.buttons[k] for k in ("extract", "preview", "rename", "pairs", "sync", "cleanup"))

        # --- doi ten / dong bo ---
        self.inner = ctk.CTkTabview(self.frame, height=200)
        self.inner.pack(fill="both", expand=True)
        t1 = self.inner.add(INNER_RENAME)
        t2 = self.inner.add(INNER_SYNC)
        b1 = ctk.CTkFrame(t1, fg_color="transparent")
        b1.pack(fill="x")
        self.rename_info = ctk.CTkLabel(b1, text="", anchor="w")
        self.rename_info.pack(side="left", padx=10)
        rcols = [W.Col("ep", "Tập", 80), W.Col("old", "Tên cũ", 380, stretch=True, fit="name"),
                 W.Col("new", "→ Tên mới", 380, stretch=True, fit="name"), W.Col("lang", "Ngôn ngữ", 90),
                 W.Col("why", "Lý do / cảnh báo", 280, stretch=True)]
        self.rtable = W.Table(t1, rcols, check="Đổi", height=5)
        self.rtable.frame.pack(fill="both", expand=True, pady=(6, 0))
        self.rtable.on_toggle = self._rename_toggle
        self.rtable.on_toggle_all = self._rename_toggle_all
        self.rtable.tip = lambda iid, _c: rename_tip(self.renames[int(iid[1:])]) if iid.startswith("r") else None

        b2 = ctk.CTkFrame(t2, fg_color="transparent")
        b2.pack(fill="x")
        self.cmd_btn = ctk.CTkButton(b2, text="Xem lệnh", width=90, command=self.show_commands,
                                     fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.cmd_btn.pack(side="left", padx=(0, 6))
        self.only_bad = W.tk.BooleanVar(value=False)
        ctk.CTkCheckBox(b2, text="Chỉ hiện file cần xem", variable=self.only_bad, command=self.fill_pairs).pack(side="left", padx=12)
        self.export_btn = ctk.CTkButton(b2, text="Xuất CSV…", width=100, command=self.export,
                                        fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.export_btn.pack(side="left")
        self.sync_info = ctk.CTkLabel(b2, text="", anchor="w")
        self.sync_info.pack(side="left", padx=10)
        scols = [W.Col("sub", "Phụ đề", 260, stretch=True, fit="name"), W.Col("ref", "Mốc", 220, fit="name"),
                 W.Col("kind", "Loại mốc", 90), W.Col("out", "Kết quả (cạnh phim)", 240, stretch=True, fit="name"),
                 W.Col("forecast", "Dự báo", 220), W.Col("health", "Sức khoẻ", 90),
                 W.Col("first", "Dịch đầu", 75, "e"), W.Col("last", "Dịch cuối", 75, "e"), W.Col("spread", "Chênh", 65, "e"),
                 W.Col("resid", "Còn lệch", 75, "e"), W.Col("votes", "Mốc khớp", 75, "e"), W.Col("cut", "Cắt recap", 70, "e"),
                 W.Col("flags", "Cờ / lý do", 360)]
        self.stable = W.Table(t2, scols, check="Chạy", height=5)
        self.stable.frame.pack(fill="both", expand=True, pady=(6, 0))
        self.stable.on_toggle = self._pair_toggle
        self.stable.on_toggle_all = self._pair_toggle_all
        self.stable.tip = lambda iid, _c: pair_tip(self.pairs[int(iid[1:])]) if iid.startswith("p") else None
        self.stable.on_double = lambda iid, _c: self.show_commands(iid)
        self.on_config_changed()

    # ------------------------------------------------------------------ chung
    def _mode(self) -> str:
        return self.mode.get()

    def _lang(self) -> str:
        return lang_from_option(self.lang.get())

    def _lang_changed(self, opt: str) -> None:
        if opt != OTHER_LANG:
            return
        code = W.ask_string(self.frame, "Ngôn ngữ khác", "Nhập mã ngôn ngữ (vd ja, pt, ru):")
        code = (code or "").strip().lower().lstrip(".")
        if not code:
            self.lang.set(AUTO_LANG)
            return
        code = naming.lang_from_word(code) or code
        naming.register_language(code)
        opts = lang_options([code])
        self.lang_menu.configure(values=opts)
        self.lang.set(next((o for o in opts if lang_from_option(o) == code), AUTO_LANG))

    def _mode_changed(self) -> None:
        audio = self._mode() == MODE_AUDIO
        # moc = audio: khong can phu de goc -> an han buoc "Trich phu de goc" (cac buoc con lai danh so lai)
        self.steps.set_hidden(("extract",) if audio else ())
        if audio:
            self.audio_box.pack(fill="x", pady=(0, 6), before=self.steps.frame)
            if not self.audio_files:
                self.load_streams()
        else:
            self.audio_box.pack_forget()
        self._show_extract_hint()
        self.pairs = []
        self._pairs_planned = False
        self.fill_pairs()
        self.update_buttons()

    def _show_extract_hint(self) -> None:
        """Dòng nhắc "srt_goc đang trống": chỉ khi mốc = phụ đề gốc (mốc = audio thì không cần phụ đề gốc)."""
        c, cfg = self._counts, self.app.cfg
        if self._mode() == MODE_SUB and c is not None and c.refs == 0 and c.videos:
            self.extract_hint.configure(text=f"'{cfg['ExtractFolder']}' đang trống → bấm \"1. Trích phụ đề gốc\" "
                                        "(hoặc chọn mốc = Audio của file phim).")
            self.extract_hint.grid()
        else:
            self.extract_hint.configure(text="")
            self.extract_hint.grid_remove()

    def _audio_selected(self, ids: list[str]) -> None:
        if ids and not ids[0].endswith("_x"):
            self.amode.set("index")
        self.mark_audio()

    def audio_choice(self) -> AudioChoice | None:
        m = self.amode.get()
        if m == "auto":
            return AudioChoice("auto")
        if m == "index":
            sel = [i for i in self.atable.selected() if not i.endswith("_x")]
            if sel:
                return AudioChoice("index", audio_iid_index(sel[0]))
            return AudioChoice("index", int(self.app.cfg["SyncAudioIndex"]))
        if m == "lang":
            lg = self.alang.get().strip().lower() or str(self.app.cfg["SyncAudioLang"])
            return AudioChoice("lang", lg)
        return None

    def on_config_changed(self, secrets_changed: bool = False) -> None:
        cfg = self.app.cfg
        self.mode_sub.configure(text=f"Phụ đề gốc trong '{cfg['ExtractFolder']}'")
        m = str(cfg["SyncAudioMode"])
        self.amode.set("" if m == "ask" else m)
        self.alang.delete(0, "end")
        self.alang.insert(0, str(cfg["SyncAudioLang"]))
        if self.session is not None:
            self.reset("Cài đặt đã đổi → lập lại bảng ở thẻ ②.")
        self.refresh_counts()

    def on_folder_changed(self) -> None:
        self.reset()
        self.refresh_counts()
        self._reload_audio()

    def on_files_changed(self) -> None:
        self.reset("File trong thư mục đã thay đổi (hoàn tác) → lập lại bảng ở thẻ ②.")
        self.refresh_counts()
        self._reload_audio()

    def _reload_audio(self) -> None:
        if self._mode() == MODE_AUDIO and self.app.folder:
            self.load_streams()

    def reset_all(self) -> None:
        """Nút "↺ Làm lại từ đầu": mọi bảng + tiến độ về như chưa làm gì (phiên mới = nhật ký mới). Không xoá file."""
        if self.app.runner.busy:
            return
        pending = [r for r in self.renames if r.ok and r.selected and not r.status] + \
                  [p for p in self.pairs if p.ok and p.selected and p.result is None]
        if pending and not W.ask_yes_no(self.frame, "Làm lại từ đầu", f"Còn {len(pending)} dòng CHƯA chạy trong các bảng. "
                                        "Xoá hết bảng và tiến độ của lượt này để bắt đầu lại?\n\n(Không xoá file nào.)"):
            return
        self.reset()
        self.only_bad.set(False)
        self.inner.set(INNER_RENAME)
        self.app.log("info", "② Đã làm lại từ đầu: các bảng và thanh các bước về mặc định (không xoá file nào).")
        self.refresh_counts()
        self._reload_audio()

    def on_show(self) -> None:
        self.refresh_counts()

    def reset(self, note: str = "") -> None:
        if self.session is not None:
            self.session.close()
        self.session = None
        self.renames, self.pairs = [], []
        self._counts = None
        self._renames_planned = self._pairs_planned = self._cleaned = False
        self.streams, self.sample = [], None
        self.audio_files = []
        self.atable.clear()
        self.audio_title.configure(text="Audio track của các file phim:", text_color=W.LEVEL_COLORS["info"])
        self.fill_renames()
        self.fill_pairs()
        self.rename_info.configure(text="")
        self.sync_info.configure(text="")
        if note:
            self.app.log("warn", note)
        self.update_buttons()

    def _session(self, job: tools.Job) -> PrepSession:
        """Một phiên cho cả lượt (đổi tên + đồng bộ + dọn dẹp ghi chung MỘT nhật ký). Chạy ở luồng phụ."""
        if self.session is None or os.path.normcase(str(self.session.td)) != os.path.normcase(str(self.app.folder)):
            if self.session is not None:
                self.session.close()
            self.session = PrepSession(self.app.folder, self.app.cfg, job)
        else:
            self.session.set_job(job)
        return self.session

    def update_buttons(self) -> None:
        busy = self.app.runner.busy
        folder = bool(self.app.folder)

        def st(on: bool) -> str:
            return "normal" if on and not busy else "disabled"

        ok_ren = [r for r in self.renames if r.ok and r.selected and not r.status]
        ok_pairs = [p for p in self.pairs if p.ok and p.selected and p.result is None]
        processed = self.session is not None and bool(self.session.processed)
        c = self._counts
        synced = self._pairs_planned and not ok_pairs
        done = [self._mode() == MODE_AUDIO or (c is not None and c.refs > 0),  # moc = audio: khong can phu de goc
                self._renames_planned, self._renames_planned and not ok_ren,
                self._pairs_planned, synced,
                self._cleaned,  # chi ✓ khi da qua hop thoai don dep cua LUOT NAY
                False]
        self.steps.update(done, [folder, folder, bool(ok_ren), folder, bool(ok_pairs), processed, True], busy)
        self.recount_btn.configure(state=st(folder))
        self.reset_btn.configure(state=st(folder))
        self.cmd_btn.configure(state=st(any(p.ok for p in self.pairs)))
        self.export_btn.configure(state=st(bool(self.pairs)))

    # ------------------------------------------------------------------ 1. dem
    def refresh_counts(self) -> None:
        folder = self.app.folder
        if not folder:
            self.counts.configure(text="Chọn thư mục phim ở đầu cửa sổ.")
            return
        cfg = self.app.cfg

        def work(job: tools.Job) -> PrepCounts:
            return self._session(job).counts()

        def done(c: PrepCounts) -> None:
            self._counts = c
            self.counts.configure(text=counts_text(c, cfg))
            self._show_extract_hint()
            self.update_buttons()

        self.app.runner.start("Đếm file (bước ②)", work, done, when_free=True)

    # ------------------------------------------------------------------ 2. audio cua moi file
    def load_streams(self) -> None:
        if not self.app.folder:
            return

        def work(job: tools.Job) -> list[VideoAudio]:
            return self._session(job).all_audio_streams()

        def done(files: list[VideoAudio]) -> None:
            self.audio_files = files
            self.sample = files[0].video if files else None
            self.streams = files[0].streams if files else []
            self.fill_audio()

        self.app.runner.start("Đọc audio track của các file phim", work, done, when_free=True)

    def fill_audio(self) -> None:
        self.atable.clear()
        txt, level = audio_summary(self.audio_files)
        self.audio_title.configure(text=txt if len(self.audio_files) != 1 else
                                   f"Audio track của file phim: {W.shorten_name(self.audio_files[0].video.name, 90)}",
                                   text_color=W.LEVEL_COLORS[level])
        rows = audio_rows(self.audio_files, self._choice_for_marks())
        self.atable.tv.configure(height=max(2, min(len(rows), self._audio_max_rows)))
        for iid, vals, tag in rows:
            self.atable.add(iid, vals, tags=(tag,) if tag else ())
        if self.amode.get() == "index":  # so thu tu trong Cai dat -> chon san dong do cua file dau
            self.atable.select(audio_iid(0, int(self.app.cfg["SyncAudioIndex"])))

    def _choice_for_marks(self) -> AudioChoice | None:
        """Lựa chọn đang có hiệu lực để đánh dấu "◄ mốc" (chưa chọn cách lấy audio → không đánh dấu)."""
        try:
            return self.audio_choice()
        except ValueError:
            return None

    def mark_audio(self) -> None:
        """Đổi cách chọn audio / chọn dòng khác → cập nhật cột "Làm mốc" (không dựng lại bảng: giữ dòng đang chọn)."""
        for iid, vals, _tag in audio_rows(self.audio_files, self._choice_for_marks()):
            if self.atable.full(iid).get("ref") != vals["ref"]:
                self.atable.set_cell(iid, "ref", vals["ref"])

    # ------------------------------------------------------------------ 4. trich
    def plan_extract(self) -> None:
        if not self.app.need_folder():
            return

        def work(job: tools.Job) -> list[ExtractRow]:
            return self._session(job).plan_extract_refs()

        self.app.runner.start("Lập kế hoạch trích phụ đề gốc", work, self._extract_dialog)

    def _extract_dialog(self, rows: list[ExtractRow]) -> None:
        if not rows:
            W.info(self.frame, "Trích phụ đề gốc", "Không có file MKV nào để trích (xem nhật ký ở dưới).")
            return
        ctk = W.ctk
        d = W.Dialog(self.frame, "Trích phụ đề gốc từ MKV", 1100, 560)
        n_ok = sum(1 for r in rows if r.ok)
        ctk.CTkLabel(d.body, text=f"{n_ok}/{len(rows)} file sẽ được trích phụ đề không SDH (theo thứ tự ngôn ngữ trong Cài đặt; "
                     f"chỉ có PGS thì đọc mốc thời gian) vào '{self.app.cfg['ExtractFolder']}'. Bỏ tick dòng không muốn.",
                     anchor="w", justify="left", wraplength=d.wrap).pack(fill="x")
        cols = [W.Col("video", "File phim", 400, stretch=True, fit="name"), W.Col("track", "Track", 300),
                W.Col("dest", "Ghi thành", 260, fit="name"), W.Col("why", "Lý do bỏ qua", 200)]
        t = W.Table(d.body, cols, check="Trích", height=14)
        t.frame.pack(fill="both", expand=True, pady=6)
        for i, r in enumerate(rows):
            vals, tag = extract_values(r)
            t.add(f"x{i}", vals, tags=(tag,) if tag else (), checked=True if r.ok else None)

        def go() -> None:
            for i, r in enumerate(rows):
                r.selected = bool(t.checked.get(f"x{i}", False))
            d.close(True)

        d.button("Trích các dòng đã tick", go, primary=True)
        d.button("Huỷ", d.close)
        if not d.run():
            return

        def work(job: tools.Job) -> Any:
            return self._session(job).extract_refs(rows)

        self.app.runner.start("Trích phụ đề gốc", work, lambda _r: self.refresh_counts_later())

    def refresh_counts_later(self) -> None:
        self.app.root.after(50, self.refresh_counts)
        self.update_buttons()

    # ------------------------------------------------------------------ 5. doi ten
    def plan_rename(self) -> None:
        if not self.app.need_folder():
            return
        mode, lang = self._mode(), self._lang()

        def work(job: tools.Job) -> list[RenameRow]:
            return self._session(job).plan_rename(mode, lang)

        def done(rows: list[RenameRow]) -> None:
            self.renames = rows
            self._renames_planned = True
            self._cleaned = False  # quet lai = luot moi: buoc "Don dep" khong con ✓ cua luot truoc
            if self._pairs_planned and not any(p.ok and p.selected and p.result is None for p in self.pairs):
                self.pairs, self._pairs_planned = [], False  # cac cap cu da chay xong -> bang dong bo lam lai
                self.fill_pairs()
                self.sync_info.configure(text="")
            self.fill_renames()
            self.inner.set(INNER_RENAME)
            self.update_buttons()

        self.app.runner.start("Quét phụ đề (xem trước đổi tên)", work, done)

    def fill_renames(self) -> None:
        self.rtable.clear()
        n_ok = n_warn = 0
        for i, r in enumerate(self.renames):
            vals, tag = rename_values(r)
            self.rtable.add(f"r{i}", vals, tags=(tag,) if tag else (), checked=(r.selected if r.ok and not r.status else None))
            n_ok += r.ok
            n_warn += bool(r.warn)
        if self.renames:
            txt = f"{n_ok} file sẽ đổi tên, {len(self.renames) - n_ok} bỏ qua"
            if n_warn:
                txt += f" — {n_warn} file nằm trong thư mục ngôn ngữ KHÁC lựa chọn (đỏ)"
            self.rename_info.configure(text=txt, text_color=W.LEVEL_COLORS["error"] if n_warn else W.LEVEL_COLORS["info"])

    def _rename_toggle(self, iid: str, state: bool) -> None:
        self.renames[int(iid[1:])].selected = state
        self.update_buttons()

    def _rename_toggle_all(self, state: bool) -> None:
        for iid in self.rtable.checked:
            self.renames[int(iid[1:])].selected = state
        self.update_buttons()

    def do_rename(self) -> None:
        rows = self.renames
        todo = [r for r in rows if r.ok and r.selected and not r.status]
        if not todo:
            return
        warn = [r for r in todo if r.warn]
        msg = f"Đổi tên {len(todo)} file?"
        if warn:
            msg += f"\n\n⚠ {len(warn)} file nằm trong thư mục ngôn ngữ KHÁC lựa chọn của bạn — kiểm tra kỹ!"
        if not W.ask_yes_no(self.frame, "Đổi tên", msg):
            return

        def work(job: tools.Job) -> Any:
            return self._session(job).execute_rename(rows)

        def done(rep: Any) -> None:
            self.fill_renames()
            self.rename_info.configure(text=f"Đã đổi tên {rep.ok} file | {rep.skipped} bỏ qua | {rep.errors} lỗi")
            self.pairs = []
            self._pairs_planned = False
            self.fill_pairs()
            self.update_buttons()
            if rep.ok:
                self.app.log("info", "→ Bấm \"Lập cặp đồng bộ\".")
                self.inner.set(INNER_SYNC)
            self.refresh_counts_later()

        self.app.runner.start("Đổi tên phụ đề", work, done)

    # ------------------------------------------------------------------ 6. dong bo
    def plan_sync(self) -> None:
        if not self.app.need_folder():
            return
        mode, lang = self._mode(), self._lang()
        audio = None
        if mode == MODE_AUDIO:
            audio = self.audio_choice()
            if audio is None:
                W.warn(self.frame, "Chọn audio làm mốc", "Chọn cách lấy audio làm mốc: Tự động / Theo số thứ tự / "
                       "Theo ngôn ngữ (ở bảng audio phía trên).")
                return

        def work(job: tools.Job) -> list[SyncPair]:
            return self._session(job).plan_sync(mode, audio, lang)

        def done(pairs: list[SyncPair]) -> None:
            self.pairs = pairs
            self._pairs_planned = True
            self.fill_pairs()
            self.inner.set(INNER_SYNC)
            self.update_buttons()

        self.app.runner.start("Lập cặp đồng bộ", work, done)

    def fill_pairs(self) -> None:
        self.stable.clear()
        only = bool(self.only_bad.get())
        for i, p in enumerate(self.pairs):
            if only and not pair_needs_review(p):
                continue
            vals, tag = pair_values(p)
            self.stable.add(f"p{i}", vals, tags=(tag,) if tag else (),
                            checked=(p.selected if p.ok and p.result is None else None))
        if self.pairs:
            done = [p for p in self.pairs if p.result is not None]
            if done:
                lv = [p.result.level for p in done if p.result and p.result.ok]
                txt = (f"{sum(1 for x in lv if x == 0)} {LEVEL_TEXT[0]} | {sum(1 for x in lv if x == 1)} {LEVEL_TEXT[1]} | "
                       f"{sum(1 for x in lv if x == 2)} {LEVEL_TEXT[2]} | {len(done) - len(lv)} lỗi/từ chối")
            else:
                n_ok = sum(1 for p in self.pairs if p.ok)
                txt = f"{n_ok} cặp sẽ đồng bộ, {len(self.pairs) - n_ok} bỏ qua"
            self.sync_info.configure(text=txt)

    def _pair_toggle(self, iid: str, state: bool) -> None:
        self.pairs[int(iid[1:])].selected = state
        self.update_buttons()

    def _pair_toggle_all(self, state: bool) -> None:
        for iid in self.stable.checked:
            self.pairs[int(iid[1:])].selected = state
        self.update_buttons()

    def show_commands(self, iid: str | None = None) -> None:
        s = self.session
        if s is None:
            return
        if iid is None:
            sel = self.stable.selected()
            iid = sel[0] if sel else None
        if iid is not None:
            ps = [self.pairs[int(iid[1:])]]
        else:
            ps = [p for p in self.pairs if p.ok][:20]
        lines: list[str] = []
        for p in ps:
            cmds = s.preview_commands(p)
            if cmds:
                lines.append(f"### {p.sub.name}")
                lines += cmds
                lines.append("")
        if not lines:
            W.info(self.frame, "Xem lệnh", "Dòng này không có lệnh (bị bỏ qua — xem cột Cờ / lý do).")
            return
        W.show_text(self.frame, "Xem lệnh (không ghi file nào)", "\n".join(lines),
                    note="Đúng các lệnh ffmpeg / alass sẽ chạy cho " + ("dòng đã chọn." if iid else "các cặp (tối đa 20)."))

    def do_sync(self) -> None:
        pairs = self.pairs
        todo = [p for p in pairs if p.ok and p.selected and p.result is None]
        if not todo:
            return
        if not W.ask_yes_no(self.frame, "Chạy đồng bộ", f"Đồng bộ {len(todo)} phụ đề bằng alass?\n\n(Kết quả ghi cạnh "
                            "file phim; file trong srt_can_sync không bị sửa.)"):
            return

        def work(job: tools.Job) -> Any:
            return self._session(job).execute_sync([p for p in pairs if p.result is None])

        def done(_rep: Any) -> None:
            self.fill_pairs()
            self.update_buttons()
            s = self.session
            if s is not None and self.app.cfg["AskCleanup"] and s.processed:
                self.app.root.after(100, self.cleanup)

        self.app.runner.start("Đồng bộ phụ đề", work, done)

    def export(self) -> None:
        if not self.pairs:
            return
        p = W.filedialog.asksaveasfilename(parent=self.frame, title="Xuất CSV", initialdir=self.app.folder,
                                           initialfile="dong_bo.csv", defaultextension=".csv",
                                           filetypes=[("CSV", "*.csv")])
        if p:
            n = export_csv(self.pairs, p)
            self.app.log("ok", f"Đã xuất {n} dòng: {p}")

    # ------------------------------------------------------------------ 7. don dep
    def cleanup(self) -> None:
        s = self.session
        plan = s.cleanup_targets() if s is not None else None
        if s is None or plan is None:
            W.info(self.frame, "Dọn dẹp", "Không có file nguồn nào để dọn (chỉ dọn file của cặp đã đồng bộ thành công).")
            return
        ctk = W.ctk
        d = W.Dialog(self.frame, "Dọn dẹp file nguồn", 900, 520)
        ctk.CTkLabel(d.body, text=f"{plan.done} phụ đề đã đồng bộ xong. Xoá file nguồn đã dùng? Nơi xoá: {plan.where}.",
                     anchor="w", justify="left", wraplength=d.wrap,
                     text_color=W.LEVEL_COLORS["error"] if not plan.to_recycle_bin else W.LEVEL_COLORS["info"]).pack(fill="x")
        root = self.app.folder or ""
        txt = (f"— '{self.app.cfg['SubFolderName']}' ({len(plan.sub_files)} file):\n"
               + "\n".join("   " + x for x in cleanup_lines(plan, "subs", root))
               + f"\n\n— '{self.app.cfg['ExtractFolder']}' ({len(plan.ref_files)} file):\n"
               + "\n".join("   " + x for x in cleanup_lines(plan, "refs", root)))
        tb = W.text_box(d.body, txt)
        tb.configure(state="disabled")
        tb.pack(fill="both", expand=True, pady=6)
        d.button("Không xoá", lambda: d.close("none"))
        d.button(f"Chỉ '{self.app.cfg['ExtractFolder']}'", lambda: d.close("refs"))
        d.button(f"Chỉ '{self.app.cfg['SubFolderName']}'", lambda: d.close("subs"))
        d.button("Xoá cả hai", lambda: d.close("both"), primary=True)
        which = d.run()
        self._cleaned = True  # da hoi (ke ca "Khong xoa") -> buoc ke tiep: sang ③
        self.update_buttons()
        if not which or which == "none":
            return

        def work(job: tools.Job) -> Any:
            return self._session(job).execute_cleanup(plan, which)

        def done(rep: Any) -> None:
            if rep.failed:
                W.warn(self.frame, "Dọn dẹp", f"Không xoá được {len(rep.failed)} file (xem nhật ký).")
            self.refresh_counts_later()

        self.app.runner.start("Dọn dẹp", work, done)

    def close(self) -> None:
        if self.session is not None:
            self.session.close()
            self.session = None
