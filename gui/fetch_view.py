"""Thẻ "① Tải phụ đề" (thay SubFetch — PROMPT §5.1, SPEC mục 6 #3).

Luồng: **Quét thư mục** (``FetchSession.scan``: phim/tập, FPS, phụ đề gốc lấy từ đâu, file có sẵn trong
``srt_can_sync``) → **Tìm phụ đề** (``gather`` + ``auto_select`` cho từng tập × ngôn ngữ; dòng còn lại hiện
"CẦN CHỌN") → bảng ứng viên: bấm đúp để chọn, "Bỏ qua ngôn ngữ này", ô "Dùng lại lựa chọn này cho các tập sau cùng
season" → **Tải phụ đề** (``execute``) → bảng tổng kết. Bước ① KHÔNG sửa thời gian phụ đề.
"""
from __future__ import annotations

import os
from typing import Any

from core import naming, pool, tools
from core.subfetch import Candidate, FetchItem, FetchReport, FetchSession
from core.tmdb import ConnStatus

from . import widgets as W

SOURCES = (("subsource", "SubSource"), ("pool", "Kho nội bộ (srt_pool)"), ("opensubtitles", "OpenSubtitles"))


# ==========================================================================
#  PHẦN THUẦN
# ==========================================================================


def lang_label(code: str) -> str:
    n = naming.lang_name(code)
    return f"{code} — {n}" if n and n.lower() != code else code


def item_status(it: FetchItem) -> tuple[str, str]:
    """(chữ, nhãn màu) cho cột Trạng thái của một dòng tập × ngôn ngữ."""
    if it.result is not None:
        what = "đã chép file có sẵn" if it.result_src == "có sẵn" else f"đã tải ({it.result_src})"
        return f"{what}: {it.result.name}", ("good" if not it.timing_note or "Khớp" in it.timing_note else "need")
    if it.status == "lấy thất bại":
        return "lấy thất bại (mọi bản đều không lấy được)", "bad"
    if it.already is not None:
        return f"đã có: {it.already.name}", "dim"
    if it.existing is not None:
        return f"dùng file có sẵn ({it.existing.reason}): {it.existing.file.name}", "good"
    if it.skip:
        return "bỏ qua", "dim"
    if it.chosen is not None:
        return f"đã chọn ({it.choice_reason})", "ok"
    if it.needs_choice:
        return f"CẦN CHỌN — {len(it.candidates)} ứng viên", "need"
    if it.gathered:
        return it.status or "không tìm thấy", "bad"
    return "chưa tìm", ""


def chosen_text(it: FetchItem) -> str:
    if it.chosen is not None:
        return f"[{it.chosen.src}] {W.ellipsize_middle(it.chosen.release, 70)}"
    if it.existing is not None:
        return it.existing.file.name
    return ""


def candidate_row(c: Candidate, i: int) -> dict[str, Any]:
    """Một dòng của bảng ứng viên (thay ``Show-CandidateTable``)."""
    return {"n": i + 1, "src": c.src, "fps": c.fps_label, "score": f"{round(c.score * 100)}%", "off": c.offset_label,
            "rel": W.ellipsize_middle(c.release, 90), "info": c.info}


def candidate_tag(c: Candidate) -> str:
    if c.tier == 3:
        return "bad"
    if c.fps == "đo: nghi ngờ":
        return "need"
    if c.off is not None and c.off.total < 0:
        return "good"
    return ""


def video_summary(items: list[FetchItem]) -> str:
    need = sum(1 for it in items if it.needs_choice)
    done = sum(1 for it in items if it.result or it.already or it.existing or it.chosen)
    parts = [f"{done}/{len(items)} ngôn ngữ xong/đã chọn"]
    if need:
        parts.append(f"{need} cần chọn")
    return ", ".join(parts)


def summary_text(rep: FetchReport) -> str:
    """Bảng tổng kết (§5.1 mục 5)."""
    s = (f"Tải được: {rep.ok}  |  Dùng file có sẵn: {rep.used_existing}  |  Đã có từ trước: {rep.already}  |  "
         f"Thiếu: {rep.missing}  |  Bỏ qua: {rep.skipped}")
    if rep.os_remaining >= 0:
        s += f"  |  OpenSubtitles còn {rep.os_remaining}/{rep.os_allowed} lượt tải hôm nay"
    if rep.stopped:
        s += "  |  (đã dừng giữa chừng)"
    return s


def gather_all(s: FetchSession, items: list[FetchItem], job: tools.Job) -> None:
    """Gom ứng viên + tự chọn cho mọi dòng chưa xong (giống ``plan_all`` nhưng KHÔNG hỏi: dòng cần bạn chọn để lại)."""
    todo = [it for it in items if not (it.already or it.existing or it.skip or it.result or it.chosen)]
    for n, it in enumerate(todo, 1):
        if job.should_stop():
            job.warn("[!] Đã dừng tìm phụ đề (các dòng còn lại để sau).")
            break
        job.progress(n, len(todo), f"{it.video.path.name} [{it.lang}]")
        job.head(f"=== {it.video.path.name} [{it.lang}]")
        s.gather(it)
        if it.candidates:
            s.auto_select(it)
            ensure_pack(s, it)


def ensure_pack(s: FetchSession, it: FetchItem) -> bool:
    """Bản đã chọn là gói season SubSource chưa tải → tải trước (miễn phí) để đo thẳng file của từng tập."""
    c = it.chosen
    if c is None or c.src != "SubSource" or not c.is_pack or not s.ss:
        return False
    if s.ss.is_downloaded(int(c.payload.get("subtitleId"))):
        return False
    return s.prefetch(c)


def after_choice(s: FetchSession, it: FetchItem, items: list[FetchItem], job: tools.Job) -> int:
    """Sau khi bạn chọn cho một dòng: tải trước gói (nếu là gói season), đo lại các tập cùng season, rồi dùng lại lựa
    chọn cho các dòng còn "cần chọn" (``ChoiceMemory``). Trả số dòng được tự chọn thêm."""
    got_pack = ensure_pack(s, it)
    rest = [x for x in items if x is not it and x.needs_choice and x.memory_key == it.memory_key]
    if got_pack and rest:
        job.info(f"[i] Đã tải gói season → đo lại {len(rest)} tập cùng season.")
        s.remeasure(rest)
    n = 0
    if s.cfg["RememberChoice"]:
        for x in rest:
            if x.needs_choice and s.auto_select(x) is not None:
                n += 1
    return n


# ==========================================================================
#  GIAO DIỆN
# ==========================================================================


class FetchView:
    def __init__(self, app: Any, parent: Any):
        ctk = W.ctk
        self.app = app
        self.session: FetchSession | None = None
        self.items: list[FetchItem] = []
        self.iid_item: dict[str, FetchItem] = {}
        self.cur_item: FetchItem | None = None
        self.checked_sources = False
        self.frame = ctk.CTkFrame(parent, fg_color="transparent")
        self.frame.pack(fill="both", expand=True)

        # --- ngon ngu ---
        lf = ctk.CTkFrame(self.frame)
        lf.pack(fill="x")
        ctk.CTkLabel(lf, text="Ngôn ngữ cần tải:", font=ctk.CTkFont(weight="bold")).grid(row=0, column=0, sticky="w",
                                                                                        padx=8, pady=6)
        self.lang_vars: dict[str, Any] = {}
        for i, c in enumerate(naming.FETCH_LANG_SWITCHES):
            v = W.tk.BooleanVar(value=False)
            self.lang_vars[c] = v
            ctk.CTkCheckBox(lf, text=c, variable=v, width=54).grid(row=0, column=1 + i, padx=1, pady=6)
        ctk.CTkLabel(lf, text="Mã khác:").grid(row=0, column=14, padx=(10, 4))
        self.lang_extra = ctk.CTkEntry(lf, width=110, placeholder_text="vd: pt ru")
        self.lang_extra.grid(row=0, column=15, padx=(0, 8))

        # --- nguon ---
        ctk.CTkLabel(lf, text="Nguồn:", font=ctk.CTkFont(weight="bold")).grid(row=1, column=0, sticky="w", padx=8, pady=6)
        sf = ctk.CTkFrame(lf, fg_color="transparent")
        sf.grid(row=1, column=1, columnspan=15, sticky="w")
        self.src_vars: dict[str, Any] = {}
        self.src_status: dict[str, Any] = {}
        for k, label in SOURCES:
            v = W.tk.BooleanVar(value=True)
            self.src_vars[k] = v
            ctk.CTkCheckBox(sf, text=label, variable=v).pack(side="left", padx=(0, 4))
            st = ctk.CTkLabel(sf, text="", text_color=W.LEVEL_COLORS["dim"])
            st.pack(side="left", padx=(0, 16))
            self.src_status[k] = st
        self.check_btn = ctk.CTkButton(sf, text="Kiểm tra nguồn", width=120, command=self.check_sources)
        self.check_btn.pack(side="left")

        # --- nut ---
        bf = ctk.CTkFrame(self.frame, fg_color="transparent")
        bf.pack(fill="x", pady=6)
        self.scan_btn = ctk.CTkButton(bf, text="1. Quét thư mục", width=140, command=self.scan)
        self.scan_btn.pack(side="left")
        self.gather_btn = ctk.CTkButton(bf, text="2. Tìm phụ đề", width=140, command=self.gather)
        self.gather_btn.pack(side="left", padx=6)
        self.run_btn = ctk.CTkButton(bf, text="3. Tải phụ đề", width=140, command=self.execute)
        self.run_btn.pack(side="left")
        self.skipall_btn = ctk.CTkButton(bf, text="Bỏ qua mọi dòng cần chọn", width=180, command=self.skip_all,
                                         fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.skipall_btn.pack(side="left", padx=6)
        ctk.CTkButton(bf, text="Sang bước ② →", width=140, command=lambda: self.app.goto("② Chuẩn bị phụ đề")).pack(side="right")
        self.hint = ctk.CTkLabel(bf, text="", text_color=W.LEVEL_COLORS["dim"], anchor="w")
        self.hint.pack(side="left", padx=10)

        # --- bang phim / ngon ngu + bang ung vien ---
        pw = W.tk.PanedWindow(self.frame, orient="vertical", sashwidth=6, borderwidth=0,
                              background=W.pick(("#dbdbdb", "#2b2b2b")))
        pw.pack(fill="both", expand=True)
        top = W.ttk.Frame(pw)
        cols = [W.Col("kind", "Loại", 110), W.Col("fps", "FPS", 70, "e"), W.Col("ref", "Phụ đề gốc (mốc)", 280),
                W.Col("status", "Trạng thái", 320, stretch=True), W.Col("chosen", "Bản đã chọn", 320)]
        self.tree = W.Table(top, cols, height=12, tree="Phim / ngôn ngữ", tree_width=360)
        self.tree.frame.pack(fill="both", expand=True)
        self.tree.on_select = self._on_select
        self.tree.tip = self._tree_tip
        pw.add(top, stretch="always", minsize=160)

        bot = W.ttk.Frame(pw)
        hb = ctk.CTkFrame(bot, fg_color="transparent")
        hb.pack(fill="x", pady=(4, 2))
        self.cand_title = ctk.CTkLabel(hb, text="Chọn một dòng ngôn ngữ ở trên để xem ứng viên.", anchor="w")
        self.cand_title.pack(side="left", padx=4)
        self.remember = W.tk.BooleanVar(value=bool(self.app.cfg["RememberChoice"]))
        self.skip_btn = ctk.CTkButton(hb, text="Bỏ qua ngôn ngữ này", width=150, command=self.skip_current,
                                      fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.skip_btn.pack(side="right", padx=4)
        self.choose_btn = ctk.CTkButton(hb, text="Chọn bản này", width=120, command=self.choose_selected)
        self.choose_btn.pack(side="right", padx=4)
        ctk.CTkCheckBox(hb, text="Dùng lại lựa chọn này cho các tập sau cùng season", variable=self.remember).pack(
            side="right", padx=8)
        ccols = [W.Col("n", "#", 36, "e"), W.Col("src", "Nguồn", 90), W.Col("fps", "FPS", 140), W.Col("score", "Giống tên", 80, "e"),
                 W.Col("off", "Lệch", 80, "e"), W.Col("rel", "Bản release", 460, stretch=True), W.Col("info", "Thông tin", 240)]
        self.cands = W.Table(bot, ccols, height=7)
        self.cands.frame.pack(fill="both", expand=True)
        self.cands.on_double = lambda iid, _c: self.choose(iid)
        self.cands.tip = self._cand_tip
        pw.add(bot, stretch="always", minsize=140)

        self.summary = ctk.CTkLabel(self.frame, text="", anchor="w", justify="left")
        self.summary.pack(fill="x", pady=(4, 0))
        self.on_config_changed()

    # ------------------------------------------------------------------ cau hinh
    def langs(self) -> list[str]:
        from .settings_view import langs_from_parts  # noqa: PLC0415

        return langs_from_parts({c: v.get() for c, v in self.lang_vars.items()}, self.lang_extra.get())

    def sources(self) -> dict[str, bool]:
        return {k: bool(v.get()) for k, v in self.src_vars.items()}

    def on_config_changed(self, secrets_changed: bool = False) -> None:
        from .settings_view import langs_to_parts  # noqa: PLC0415

        cfg = self.app.cfg
        sw, extra = langs_to_parts(list(cfg["Languages"]))
        for c, v in self.lang_vars.items():
            v.set(sw.get(c, False))
        self.lang_extra.delete(0, "end")
        self.lang_extra.insert(0, extra)
        self.src_vars["pool"].set(bool(cfg["UsePool"]))
        self.src_vars["opensubtitles"].set(bool(cfg["UseOpenSubtitles"]))
        self.remember.set(bool(cfg["RememberChoice"]))
        if self.session is not None:
            self.reset("Cài đặt đã đổi → bấm \"Quét thư mục\" lại để áp dụng.")
        if secrets_changed:
            self.checked_sources = False

    def on_folder_changed(self) -> None:
        self.reset()

    def on_files_changed(self) -> None:
        if self.session is not None:
            self.reset("File trong thư mục đã thay đổi (hoàn tác) → bấm \"Quét thư mục\" lại.")

    def on_show(self) -> None:
        if not self.checked_sources:
            self.check_sources()

    def reset(self, note: str = "") -> None:
        if self.session is not None:
            self.session.close()
        self.session = None
        self.items = []
        self.iid_item.clear()
        self.cur_item = None
        self.tree.clear()
        self.cands.clear()
        self.summary.configure(text="")
        self.cand_title.configure(text="Chọn một dòng ngôn ngữ ở trên để xem ứng viên.")
        if note:
            self.app.log("warn", note)
        self.update_buttons()

    def update_buttons(self) -> None:
        busy = self.app.runner.busy
        has = self.session is not None and bool(self.items)
        need = sum(1 for it in self.items if it.needs_choice)

        def st(on: bool) -> str:
            return "normal" if on and not busy else "disabled"

        self.scan_btn.configure(state=st(bool(self.app.folder)))
        self.gather_btn.configure(state=st(has))
        self.run_btn.configure(state=st(has))
        self.skipall_btn.configure(state=st(need > 0))
        self.check_btn.configure(state=st(True))
        cur_ok = self.cur_item is not None and bool(self.cur_item.candidates) and self.cur_item.result is None
        self.choose_btn.configure(state=st(cur_ok))
        self.skip_btn.configure(state=st(cur_ok))
        if has:
            self.hint.configure(text=f"{len(self.items)} dòng (phim × ngôn ngữ)" + (f" — {need} dòng CẦN CHỌN" if need else ""),
                                text_color=W.LEVEL_COLORS["warn"] if need else W.LEVEL_COLORS["dim"])
        else:
            self.hint.configure(text="")

    # ------------------------------------------------------------------ nguon
    def check_sources(self) -> None:
        from .settings_view import check_connection, status_color  # noqa: PLC0415

        self.checked_sources = True
        cfg = self.app.cfg
        vals = self.app.secrets()
        folder = self.app.folder
        for k in self.src_status:
            self.src_status[k].configure(text="đang kiểm tra…", text_color=W.LEVEL_COLORS["dim"])

        def work(job: tools.Job) -> dict[str, ConnStatus]:
            out: dict[str, ConnStatus] = {}
            out["subsource"] = (check_connection("subsource", vals, cfg, job) if vals.get("SubSourceApiKey")
                                else ConnStatus(None, "chưa điền key"))
            out["opensubtitles"] = (check_connection("opensubtitles", vals, cfg, job) if vals.get("OsApiKey")
                                    else ConnStatus(None, "chưa điền key"))
            if folder:
                idx = pool.build_index(os.path.join(folder, cfg["PoolFolder"]), cfg["ExcludeForced"], cfg["ExcludeSdh"],
                                       cfg["PoolDefaultLang"])
                out["pool"] = ConnStatus(True if idx.total else None,
                                         f"{idx.total} file: {idx.summary()}" if idx.total else f"'{cfg['PoolFolder']}' trống")
            else:
                out["pool"] = ConnStatus(None, "chưa chọn thư mục phim")
            return out

        def done(res: dict[str, ConnStatus]) -> None:
            for k, stt in res.items():
                txt = stt.text.split(": ", 1)[1] if stt.text.startswith(("SubSource:", "OpenSubtitles:")) else stt.text
                self.src_status[k].configure(text=txt, text_color=status_color(stt))

        self.app.runner.start("Kiểm tra nguồn phụ đề", work, done, when_free=True)

    # ------------------------------------------------------------------ 1. quet
    def scan(self) -> None:
        folder = self.app.need_folder()
        if not folder:
            return
        langs = self.langs()
        if not langs:
            W.warn(self.frame, "Ngôn ngữ", "Tick ít nhất một ngôn ngữ cần tải.")
            return
        self.reset()
        cfg, secrets, srcs = self.app.cfg, self.app.secrets(), self.sources()

        def work(job: tools.Job) -> FetchSession:
            s = FetchSession(folder, cfg, secrets, job, languages=langs, sources=srcs)
            try:
                s.scan()
            except BaseException:
                s.close()
                raise
            return s

        def done(s: FetchSession) -> None:
            self.session = s
            self.items = list(s.items)
            self.fill()
            self.update_buttons()

        self.app.runner.start("Quét thư mục (bước ①)", work, done)

    # ------------------------------------------------------------------ 2. tim
    def gather(self) -> None:
        s = self.session
        if s is None:
            return
        items = self.items

        def work(job: tools.Job) -> None:
            s.set_job(job)
            gather_all(s, items, job)

        self.app.runner.start("Tìm phụ đề", work, lambda _r: self._after_change(select_next=True))

    # ------------------------------------------------------------------ chon
    def _on_select(self, iids: list[str]) -> None:
        it = self.iid_item.get(iids[0]) if iids else None
        if it is None and iids and iids[0].startswith("v"):
            kids = self.tree.tv.get_children(iids[0])
            it = next((self.iid_item[k] for k in kids if self.iid_item[k].needs_choice), None)
        self.show_candidates(it)

    def show_candidates(self, it: FetchItem | None) -> None:
        self.cur_item = it
        self.cands.clear()
        if it is None:
            self.cand_title.configure(text="Chọn một dòng ngôn ngữ ở trên để xem ứng viên.")
            self.update_buttons()
            return
        head = f"Ứng viên cho: {W.ellipsize_middle(it.video.path.name, 70)}  [{it.lang}]"
        if it.video.fps:
            head += f"  — video {it.video.fps:.3f} fps"
        self.cand_title.configure(text=head)
        for i, c in enumerate(it.candidates):
            tags = [candidate_tag(c)]
            if it.chosen is not None and c.ident == it.chosen.ident:
                tags = ["good"]
            self.cands.add(f"c{i}", candidate_row(c, i), tags=[t for t in tags if t])
        if it.chosen is not None:
            idx = next((i for i, c in enumerate(it.candidates) if c.ident == it.chosen.ident), None)
            if idx is not None:
                self.cands.select(f"c{idx}")
        self.update_buttons()

    def choose_selected(self) -> None:
        sel = self.cands.selected()
        if not sel:
            W.info(self.frame, "Chọn ứng viên", "Bấm chọn một dòng trong bảng ứng viên (hoặc bấm đúp vào dòng đó).")
            return
        self.choose(sel[0])

    def choose(self, cand_iid: str) -> None:
        s, it = self.session, self.cur_item
        if s is None or it is None or self.app.runner.busy:
            return
        try:
            cand = it.candidates[int(cand_iid[1:])]
        except (ValueError, IndexError):
            return
        s.choose(it, cand, bool(self.remember.get()))
        self.app.log("ok", f"[{it.lang}] {it.video.path.name}: chọn [{cand.src}] {cand.release}")
        items = self.items
        remember = bool(self.remember.get())

        def work(job: tools.Job) -> int:
            s.set_job(job)
            if not remember:
                ensure_pack(s, it)
                return 0
            return after_choice(s, it, items, job)

        def done(n: int) -> None:
            if n:
                self.app.log("info", f"[i] Dùng lại lựa chọn cho {n} tập khác cùng season.")
            self._after_change(select_next=True)

        self.app.runner.start("Áp dụng lựa chọn", work, done)

    def skip_current(self) -> None:
        s, it = self.session, self.cur_item
        if s is None or it is None:
            return
        s.skip(it)
        self._after_change(select_next=True)

    def skip_all(self) -> None:
        s = self.session
        if s is None:
            return
        need = [it for it in self.items if it.needs_choice]
        if not need or not W.ask_yes_no(self.frame, "Bỏ qua", f"Bỏ qua {len(need)} dòng còn cần chọn?"):
            return
        for it in need:
            s.skip(it)
        self._after_change()

    # ------------------------------------------------------------------ 3. tai
    def execute(self) -> None:
        s = self.session
        if s is None:
            return
        need = [it for it in self.items if it.needs_choice]
        if need and not W.ask_yes_no(self.frame, "Còn dòng cần chọn",
                                     f"Còn {len(need)} dòng CẦN CHỌN (vàng). Bỏ qua các dòng đó và tải phần còn lại?"):
            return
        not_gathered = [it for it in self.items if not (it.gathered or it.already or it.existing or it.skip)]
        if not_gathered and not W.ask_yes_no(self.frame, "Chưa tìm phụ đề",
                                             f"{len(not_gathered)} dòng chưa được tìm (chưa bấm \"Tìm phụ đề\"). "
                                             "Vẫn chạy? (các dòng đó sẽ được tính là thiếu)"):
            return
        items = self.items

        def work(job: tools.Job) -> FetchReport:
            s.set_job(job)
            return s.execute(items)

        def done(rep: FetchReport) -> None:
            self.summary.configure(text=summary_text(rep))
            self._after_change()

        self.app.runner.start("Tải phụ đề", work, done)

    # ------------------------------------------------------------------ bang
    def fill(self) -> None:
        self.tree.clear()
        self.iid_item.clear()
        by_video: dict[int, list[FetchItem]] = {}
        order: list[Any] = []
        for it in self.items:
            k = id(it.video)
            if k not in by_video:
                by_video[k] = []
                order.append(it.video)
            by_video[k].append(it)
        n = 0
        for vi, v in enumerate(order):
            its = by_video[id(v)]
            ref = v.ref.source if v.ref else "không có (không đo được thời gian)"
            vid = f"v{vi}"
            need = any(x.needs_choice for x in its)
            self.tree.add(vid, {"kind": v.kind_text, "fps": f"{v.fps:.3f}" if v.fps else "?", "ref": ref,
                                "status": video_summary(its), "chosen": ""}, text=v.path.name,
                          tags=("need",) if need else ("head",))
            for it in its:
                iid = f"i{n}"
                n += 1
                self.iid_item[iid] = it
                txt, tag = item_status(it)
                self.tree.add(iid, {"kind": "", "fps": "", "ref": "", "status": txt, "chosen": chosen_text(it)},
                              parent=vid, text="   " + lang_label(it.lang), tags=(tag,) if tag else ())

    def refresh_rows(self) -> None:
        for iid, it in self.iid_item.items():
            txt, tag = item_status(it)
            self.tree.update(iid, {"kind": "", "fps": "", "ref": "", "status": txt, "chosen": chosen_text(it)},
                             tags=(tag,) if tag else ())
        for vid in self.tree.tv.get_children(""):
            its = [self.iid_item[k] for k in self.tree.tv.get_children(vid)]
            vals = list(self.tree.tv.item(vid, "values"))
            if len(vals) >= 4:
                vals[3] = video_summary(its)
                self.tree.tv.item(vid, values=vals, tags=("need",) if any(x.needs_choice for x in its) else ("head",))

    def _after_change(self, select_next: bool = False) -> None:
        self.refresh_rows()
        cur = self.cur_item
        nxt = None
        if select_next:
            nxt = next((iid for iid, it in self.iid_item.items() if it.needs_choice), None)
        if nxt:
            self.tree.select(nxt)
            self.show_candidates(self.iid_item[nxt])
        else:
            self.show_candidates(cur)
        self.update_buttons()

    def _tree_tip(self, iid: str, col: str) -> str | None:
        it = self.iid_item.get(iid)
        if it is None:
            return None
        if col == "status" and it.timing_note:
            return it.timing_note
        if col == "status" and it.existing is not None:
            return f"File có sẵn: {it.existing.file}\n({it.existing.reason}) — sẽ được CHÉP (không di chuyển)."
        if col == "chosen" and it.chosen is not None:
            return f"[{it.chosen.src}] {it.chosen.release}\n{it.chosen.info}"
        return None

    def _cand_tip(self, iid: str, col: str) -> str | None:
        it = self.cur_item
        if it is None:
            return None
        try:
            c = it.candidates[int(iid[1:])]
        except (ValueError, IndexError):
            return None
        if col == "off":
            if c.off is None:
                return "Chưa đo được độ lệch (không có phụ đề gốc, hoặc preview không đủ mốc)."
            return ("Ước tính từ preview của SubSource (~)" if c.off.total > 0 else
                    f"Đo thật với phụ đề gốc: {c.off.votes} mốc khớp")
        return c.release if col == "rel" else None

    def close(self) -> None:
        if self.session is not None:
            self.session.close()
            self.session = None

