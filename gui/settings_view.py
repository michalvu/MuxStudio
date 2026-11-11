"""Thẻ "Cài đặt" (PROMPT §5.4, SPEC mục 5).

* Mọi khoá của ``config.FIELDS``, chia 5 nhóm, chú thích tiếng Việt; mục "Nâng cao" ẩn tới khi bật.
* 9 bí mật (7 của .bat + tài khoản Titulky.com): ô ``•••`` + "Hiện"; lưu vào Windows Credential Manager (``core.secrets``), KHÔNG vào ``config.json``.
* "Kiểm tra kết nối" cho SubSource / TMDB / TVDB / OpenSubtitles (dùng giá trị đang gõ, chưa cần lưu).
* "Tự tìm" công cụ (``tools.find_tool``) + hiện phiên bản ``mkvmerge`` đọc được.
* "Khôi phục mặc định" (không đụng bí mật), "Nhập từ .bat…" (``config.import_from_bat`` → bảng xem trước → Nhập).
"""
from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Any, Callable, Mapping

from core import config as C
from core import naming, secrets as S, timing, tools
from core.tmdb import ConnStatus

from . import widgets as W

# Danh sach ma moi muc tren mot dong (gia tri co the chua dau cach)
LINE_LISTS = {"ExtraToolDirs", "RefExcludeFolders"}
TOOL_BY_KEY = {v: k for k, v in tools.CONFIG_PATH_KEY.items()}
DIR_FINDERS = {"MkvToolNixDir": "mkvmerge", "AlassDir": "alass"}
CODEC_PARTS = (("res", "Độ phân giải"), ("hdr", "HDR / DV"), ("vcodec", "Codec video"), ("acodec", "Codec audio"))

SECRET_INFO: dict[str, tuple[str, str, str | None]] = {
    "SubSourceApiKey": ("SubSource API key", "subsource.net › My Profile › API Key.", "subsource"),
    "TmdbApiKey": ("TMDB API key / token", "themoviedb.org › Settings › API: key 32 ký tự hoặc token dài bắt đầu 'eyJ'. "
                   "Dùng ở bước ① (tìm IMDb ID) và ③ (đặt tên).", "tmdb"),
    "TvdbApiKey": ("TVDB API key", "thetvdb.com › Dashboard › API Keys.", "tvdb"),
    "TvdbPin": ("TVDB PIN", "Chỉ cần với key loại 'user-supported'.", None),
    "OsApiKey": ("OpenSubtitles API key", "opensubtitles.com › API consumers.", "opensubtitles"),
    "OsUsername": ("OpenSubtitles — tên đăng nhập", "Cần để TẢI phụ đề (không có thì chỉ tìm được).", None),
    "OsPassword": ("OpenSubtitles — mật khẩu", "", None),
    "TitulkyUsername": ("Titulky.com — tên đăng nhập", "Không bắt buộc (nguồn phụ đề tiếng Séc). Không có tài khoản vẫn tìm "
                        "và tải được kiểu khách, nhưng hay phải gõ mã CAPTCHA. Theo thông báo của trang: tài khoản "
                        "Premium tải được 25 phụ đề mỗi ngày, không phụ thuộc địa chỉ IP.", "titulky"),
    "TitulkyPassword": ("Titulky.com — mật khẩu", "", None),
}
CHECK_NAMES = {"subsource": "SubSource", "tmdb": "TMDB", "tvdb": "TVDB", "opensubtitles": "OpenSubtitles",
               "titulky": "Titulky"}


# ==========================================================================
#  PHẦN THUẦN (test được không cần màn hình)
# ==========================================================================


def field_kind(f: C.Field) -> str:
    if f.key == "CodecTagParts":
        return "parts"
    if f.kind:
        return f.kind
    d = f.default
    if isinstance(d, bool):
        return "bool"
    if isinstance(d, int):
        return "int"
    if isinstance(d, float):
        return "float"
    if isinstance(d, list):
        return "list"
    if isinstance(d, dict):
        return "dict"
    return "str"


def _num_text(v: Any) -> str:
    if isinstance(v, bool):
        return str(v)
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return timing.fmt_inv(v)
    return str(v)


def value_to_text(f: C.Field, v: Any) -> str:
    """Giá trị cấu hình → chữ trong ô nhập."""
    k = field_kind(f)
    if k in ("list", "parts"):
        items = [str(x) for x in (v or [])]
        return "\n".join(items) if f.key in LINE_LISTS else " ".join(items)
    if k == "dict":
        return "\n".join(f"{key} = {_num_text(val)}" for key, val in (v or {}).items())
    if k == "langs":
        return " ".join(v or [])
    if v is None:
        return ""
    return _num_text(v)


def parse_dict_text(text: str) -> dict[str, float | int]:
    """``DonorOffsetMs``: mỗi dòng ``khoá = số`` (khoá có thể có dấu cách: ``tap #643 = -500``)."""
    out: dict[str, float | int] = {}
    for n, raw in enumerate(text.splitlines(), 1):
        ln = raw.strip()
        if not ln or ln.startswith("#") and "=" not in ln:
            continue
        m = re.match(r"^(.*?)\s*[=:]\s*([-+]?\d+(?:[.,]\d+)?)\s*$", ln)
        if not m or not m.group(1).strip():
            raise ValueError(f"dòng {n}: cần dạng 'khoá = số' (vd S01E03 = 1200)")
        key = m.group(1).strip().strip("'\"")
        num = m.group(2).replace(",", ".")
        out[key] = int(num) if re.fullmatch(r"[-+]?\d+", num) else float(num)
    return out


def text_to_value(f: C.Field, text: str) -> Any:
    """Chữ trong ô nhập → giá trị đúng kiểu (``ValueError`` kèm lý do tiếng Việt nếu sai)."""
    k = field_kind(f)
    t = text if text is not None else ""
    if k in ("list", "parts"):
        items = [x.strip() for x in (t.splitlines() if f.key in LINE_LISTS else t.split())]
        return [x for x in items if x]
    if k == "langs":
        return [x.strip().lower() for x in t.replace(",", " ").split() if x.strip()]
    if k == "dict":
        return C.coerce(f.key, parse_dict_text(t))
    if k in ("int", "float") and not t.strip():
        raise ValueError(f"{f.label}: không được để trống")
    try:
        return C.coerce(f.key, t.strip())
    except ValueError as e:
        msg = str(e).replace(f"{f.key}:", f"{f.label}:")
        raise ValueError(msg) from None


def langs_from_parts(switches: Mapping[str, bool], extra_text: str) -> list[str]:
    """12 ô tick + ô "mã khác" → ``Languages`` (thứ tự = thứ tự xử lý: vi en cs ja … rồi mã thêm)."""
    out = [c for c in naming.FETCH_LANG_SWITCHES if switches.get(c)]
    for x in extra_text.replace(",", " ").split():
        x = x.strip().lower()
        code = naming.lang_from_word(x) or x
        if code and code not in out:
            out.append(code)
    return out


def langs_to_parts(langs: list[str]) -> tuple[dict[str, bool], str]:
    sw = {c: c in langs for c in naming.FETCH_LANG_SWITCHES}
    extra = [c for c in langs if c not in naming.FETCH_LANG_SWITCHES]
    return sw, " ".join(extra)


def check_connection(which: str, vals: Mapping[str, str], cfg: Mapping[str, Any], job: tools.Job | None = None,
                     http: Any = None, sleep: Callable[[float], None] | None = None) -> ConnStatus:
    """Nút "Kiểm tra kết nối" (chạy ở luồng phụ). ``vals``: bí mật đang gõ (chưa cần lưu)."""
    import time  # noqa: PLC0415

    from core import opensubs, subsource, titulky  # noqa: PLC0415
    from core.tmdb import TmdbClient  # noqa: PLC0415
    from core.tvdb import TvdbClient  # noqa: PLC0415

    sl = sleep or time.sleep
    if which == "subsource":
        return subsource.SubSourceClient(vals.get("SubSourceApiKey", ""), cfg["SubSourceApiBase"], cfg["ApiPauseMs"], 1,
                                         http, sl, job=job).check()
    if which == "tmdb":
        return TmdbClient(vals.get("TmdbApiKey", ""), cfg["TmdbLang"], cfg["TmdbRetry"], http, sl, job).check()
    if which == "tvdb":
        return TvdbClient(vals.get("TvdbApiKey", ""), vals.get("TvdbPin", ""), cfg["TvdbLang"], cfg["TvdbSeasonType"],
                          cfg["TmdbRetry"], http, sl, job).check()
    if which == "opensubtitles":
        return opensubs.OpenSubtitlesClient(vals.get("OsApiKey", ""), vals.get("OsUsername", ""),
                                            vals.get("OsPassword", ""), cfg["OsUserAgent"], cfg["ApiPauseMs"],
                                            cfg["OsWarnRemaining"], cfg["OsStopRemaining"], http, sl, job).check()
    if which == "titulky":
        return titulky.TitulkyClient(vals.get("TitulkyUsername", ""), vals.get("TitulkyPassword", ""), cfg["ApiPauseMs"],
                                     http, sl, job).check()
    raise ValueError(which)


def tool_status_text(info: tools.ToolInfo, auto: bool) -> str:
    """Dòng dưới ô đường dẫn công cụ: ``✓ v82.0 — tự tìm thấy: C:\\…\\mkvmerge.exe``."""
    if info.path is None:
        role, must = tools.TOOL_ROLES.get(info.name, ("", False))
        tail = " — BẮT BUỘC" if must else ""
        return f"✗ không tìm thấy{tail} (dùng cho: {role}). Bấm \"Chọn…\" để chỉ tới {tools.exe_name(info.name)}."
    where = ("tự tìm thấy: " if auto else "") + info.path
    if not info.ok:
        return f"✗ {info.problem} — {where}"
    return f"✓ {info.version + ' — ' if info.version else ''}{where}"


def tool_status_color(info: tools.ToolInfo) -> tuple[str, str]:
    if info.ok:
        return W.LEVEL_COLORS["ok"]
    return W.LEVEL_COLORS["error"] if tools.TOOL_ROLES.get(info.name, ("", False))[1] else W.LEVEL_COLORS["warn"]


def tool_summary(res: Mapping[str, tuple[tools.ToolInfo, bool]]) -> list[tuple[str, str]]:
    """Dòng log sau khi kiểm tra công cụ: một dòng tóm tắt + một dòng cho mỗi công cụ thiếu / hỏng."""
    parts = []
    out: list[tuple[str, str]] = []
    for n in tools.TOOLS:
        info, _auto = res[n]
        parts.append(f"{n} {'✓' if info.ok else '✗'}" + (f" {info.version}" if info.ok and info.version else ""))
        if not info.ok:
            role, must = tools.TOOL_ROLES.get(n, ("", False))
            lvl = "error" if must else "warn"
            why = "chưa tìm thấy" if info.path is None else info.problem
            out.append((lvl, f"[!] {tools.exe_name(n)}: {why} → không làm được: {role}. "
                             "Vào Cài đặt › Chung › Công cụ ngoài để chọn đường dẫn."))
    return [("ok" if not out else "warn", "Công cụ: " + " · ".join(parts)), *out]


def status_color(st: ConnStatus | None) -> tuple[str, str]:
    if st is None or st.ok is None:
        return W.LEVEL_COLORS["dim"]
    return W.LEVEL_COLORS["ok"] if st.ok else W.LEVEL_COLORS["error"]


@dataclass
class ImportRow:
    """Một dòng của bảng "Nhập từ .bat": khoá — giá trị hiện tại — giá trị từ .bat."""

    key: str
    label: str
    current: str
    new: str
    source: str
    secret: bool
    group: str = ""  # '' = dong thuong; khac '' = mot lua chon cua khoa xung dot (cung group)
    value: Any = None
    same: bool = False


def _show(f: C.Field | None, v: Any) -> str:
    if f is None:
        return str(v)
    k = field_kind(f)
    if k == "bool":
        return "bật" if v else "tắt"
    txt = value_to_text(f, v)
    return txt.replace("\n", " | ")


def import_rows(imp: C.BatImport, cfg: Mapping[str, Any], current_secret: Callable[[str], str]) -> list[ImportRow]:
    """Dựng bảng xem trước (bí mật luôn được che)."""
    rows: list[ImportRow] = []
    for k, it in imp.values.items():
        f = C.FIELD_BY_KEY.get(k)
        cur = cfg[k]
        rows.append(ImportRow(k, f.label if f else k, _show(f, cur), _show(f, it.value), f"{it.source}:{it.line}", False,
                              value=it.value, same=cur == it.value))
    for k, items in imp.conflicts.items():
        f = C.FIELD_BY_KEY.get(k)
        cur = cfg[k]
        for it in items:
            rows.append(ImportRow(k, f.label if f else k, _show(f, cur), _show(f, it.value), f"{it.source}:{it.line}",
                                  False, group=k, value=it.value, same=cur == it.value))
    for k, it in imp.secrets.items():
        cur = current_secret(k)
        rows.append(ImportRow(k, SECRET_INFO.get(k, (k,))[0], S.mask(cur) or "(trống)", S.mask(it.value),
                              f"{it.source}:{it.line}", True, value=it.value, same=cur == it.value))
    for k, items in imp.secret_conflicts.items():
        cur = current_secret(k)
        for it in items:
            rows.append(ImportRow(k, SECRET_INFO.get(k, (k,))[0], S.mask(cur) or "(trống)", S.mask(it.value),
                                  f"{it.source}:{it.line}", True, group=k, value=it.value, same=cur == it.value))
    return rows


def filtered_import(imp: C.BatImport, keep_values: set[str], keep_secrets: set[str]) -> C.BatImport:
    """Bản sao chỉ còn các khoá được tick (bỏ tick = không nhập khoá đó)."""
    out = C.BatImport(files=list(imp.files), ignored=list(imp.ignored), problems=list(imp.problems))
    out.values = {k: v for k, v in imp.values.items() if k in keep_values}
    out.conflicts = dict(imp.conflicts)
    out.secrets = {k: v for k, v in imp.secrets.items() if k in keep_secrets}
    out.secret_conflicts = dict(imp.secret_conflicts)
    return out


# ==========================================================================
#  PHẦN GIAO DIỆN
# ==========================================================================


class FieldRow:
    """Một ô cấu hình trên màn hình (nhãn + ô nhập + chú thích)."""

    def __init__(self, view: "SettingsView", parent: Any, f: C.Field, row: int):
        ctk = W.ctk
        self.view, self.f = view, f
        self.kind = field_kind(f)
        self.parent = parent
        self.widgets: list[Any] = []
        self.vars: dict[str, Any] = {}
        self.entry: Any = None
        self.text: Any = None
        self.status: Any = None
        lab_txt = f.label + ("  (nâng cao)" if f.advanced else "")
        if self.kind == "bool":
            self.var = W.tk.BooleanVar(value=False)
            cb = ctk.CTkCheckBox(parent, text=lab_txt, variable=self.var)
            cb.grid(row=row, column=0, columnspan=2, sticky="w", padx=(4, 8), pady=(8, 0))
            self.label = cb
            self.widgets.append(cb)
        else:
            self.label = ctk.CTkLabel(parent, text=lab_txt, anchor="w", justify="left", wraplength=290)
            self.label.grid(row=row, column=0, sticky="nw", padx=(4, 8), pady=(8, 0))
            box = ctk.CTkFrame(parent, fg_color="transparent")
            box.grid(row=row, column=1, sticky="ew", pady=(8, 0))
            self.widgets += [self.label, box]
            self._build(box)
        help_txt = f.help
        if help_txt:
            h = ctk.CTkLabel(parent, text=help_txt, anchor="w", justify="left", wraplength=view.help_wrap,
                             text_color=W.LEVEL_COLORS["dim"], font=ctk.CTkFont(size=12))
            view.help_labels.append(h)
            h.grid(row=row + 1, column=0 if self.kind == "bool" else 1, columnspan=2 if self.kind == "bool" else 1,
                   sticky="w", padx=(28 if self.kind == "bool" else 0, 4))
            self.widgets.append(h)

    def _build(self, box: Any) -> None:
        ctk = W.ctk
        k = self.kind
        if k == "langs":
            sw = ctk.CTkFrame(box, fg_color="transparent")
            sw.pack(fill="x")
            for i, c in enumerate(naming.FETCH_LANG_SWITCHES):
                v = W.tk.BooleanVar(value=False)
                self.vars[c] = v
                ctk.CTkCheckBox(sw, text=c, variable=v, width=60).grid(row=i // 6, column=i % 6, sticky="w", padx=2, pady=2)
            rowf = ctk.CTkFrame(box, fg_color="transparent")
            rowf.pack(fill="x", pady=(2, 0))
            ctk.CTkLabel(rowf, text="Mã khác:").pack(side="left")
            self.entry = ctk.CTkEntry(rowf, width=220, placeholder_text="vd: pt ru it")
            self.entry.pack(side="left", padx=6)
            return
        if k == "parts":
            for c, lab in CODEC_PARTS:
                v = W.tk.BooleanVar(value=False)
                self.vars[c] = v
                ctk.CTkCheckBox(box, text=lab, variable=v, width=120).pack(side="left", padx=(0, 8))
            return
        if k == "choice":
            self.var = W.tk.StringVar(value=str(self.f.default))
            ctk.CTkOptionMenu(box, values=list(self.f.choices), variable=self.var, width=160).pack(side="left")
            return
        if k == "dict" or (k == "list" and self.f.key in LINE_LISTS):
            self.text = ctk.CTkTextbox(box, height=72 if k == "list" else 90, wrap="none")
            self.text.pack(fill="x", expand=True)
            note = "mỗi dòng một thư mục" if k == "list" else "mỗi dòng: khoá = số ms (vd S01E03 = 1200, tap #643 = -500)"
            ctk.CTkLabel(box, text=note, text_color=W.LEVEL_COLORS["dim"], font=ctk.CTkFont(size=11)).pack(anchor="w")
            return
        if k == "file":
            line = ctk.CTkFrame(box, fg_color="transparent")
            line.pack(fill="x")
            self.entry = ctk.CTkEntry(line, width=420, placeholder_text="để trống = tự tìm")
            self.entry.pack(side="left", fill="x", expand=True)
            ctk.CTkButton(line, text="Chọn…", width=70, command=self._pick_file).pack(side="left", padx=4)
            ctk.CTkButton(line, text="Tự tìm", width=70, command=self._auto_file).pack(side="left")
            # file dang dung + phien ban (ca khi de trong cho tu tim)
            self.status = ctk.CTkLabel(box, text="đang kiểm tra…", text_color=W.LEVEL_COLORS["dim"], anchor="w",
                                       justify="left", wraplength=self.view.help_wrap)
            self.status.pack(fill="x", anchor="w")
            self.view.help_labels.append(self.status)
            self.entry.bind("<Return>", lambda _e: self.view.show_tool_status(self), add="+")
            self.entry.bind("<FocusOut>", lambda _e: self.view.show_tool_status(self, only_if_changed=True), add="+")
            return
        width = 120 if k in ("int", "float") else 520
        self.entry = ctk.CTkEntry(box, width=width)
        self.entry.pack(side="left", fill="x", expand=k not in ("int", "float"))
        if k == "list":
            self.entry.configure(placeholder_text="cách nhau bằng dấu cách")
        if k == "dir":
            ctk.CTkButton(box, text="Chọn…", width=70, command=self._pick_dir).pack(side="left", padx=4)
            if self.f.key in DIR_FINDERS:
                ctk.CTkButton(box, text="Tự tìm", width=70, command=self._auto_dir).pack(side="left")

    # ---- doc / ghi ----
    def set_value(self, v: Any) -> None:
        k = self.kind
        if k == "bool":
            self.var.set(bool(v))
        elif k == "langs":
            sw, extra = langs_to_parts(list(v or []))
            for c, var in self.vars.items():
                var.set(sw.get(c, False))
            self.entry.delete(0, "end")
            self.entry.insert(0, extra)
        elif k == "parts":
            for c, var in self.vars.items():
                var.set(c in (v or []))
        elif k == "choice":
            self.var.set(str(v))
        elif self.text is not None:
            self.text.delete("1.0", "end")
            self.text.insert("1.0", value_to_text(self.f, v))
        else:
            self.entry.delete(0, "end")
            self.entry.insert(0, value_to_text(self.f, v))
        self.mark(False)

    def get_value(self) -> Any:
        k = self.kind
        if k == "bool":
            return bool(self.var.get())
        if k == "langs":
            return langs_from_parts({c: v.get() for c, v in self.vars.items()}, self.entry.get())
        if k == "parts":
            return [c for c, _ in CODEC_PARTS if self.vars[c].get()]
        if k == "choice":
            return C.coerce(self.f.key, self.var.get())
        txt = self.text.get("1.0", "end-1c") if self.text is not None else self.entry.get()
        return text_to_value(self.f, txt)

    def mark(self, bad: bool) -> None:
        try:
            self.label.configure(text_color=W.LEVEL_COLORS["error"] if bad else W.ctk.ThemeManager.theme["CTkLabel"]["text_color"])
        except Exception:  # pragma: no cover
            pass

    def set_visible(self, on: bool) -> None:
        for w in self.widgets:
            if on:
                w.grid()
            else:
                w.grid_remove()

    # ---- nut ----
    def _pick_dir(self) -> None:
        cur = self.entry.get().strip()
        d = W.filedialog.askdirectory(parent=self.parent, title=W.tr(self.f.label),
                                      initialdir=cur if cur and os.path.isdir(cur) else None)
        if d:
            self.entry.delete(0, "end")
            self.entry.insert(0, os.path.normpath(d))

    def _pick_file(self) -> None:
        cur = self.entry.get().strip()
        p = W.filedialog.askopenfilename(parent=self.parent, title=W.tr(self.f.label),
                                         initialdir=os.path.dirname(cur) if cur else None,
                                         filetypes=[("Chương trình", "*.exe"), ("Mọi file", "*.*")]
                                         if os.name == "nt" else [("Mọi file", "*")])
        if p:
            self.entry.delete(0, "end")
            self.entry.insert(0, os.path.normpath(p))
            self.view.show_tool_status(self)

    def _auto_file(self) -> None:
        name = TOOL_BY_KEY[self.f.key]
        p = tools.find_tool(name, self.view.current_cfg(), self.view.app.folder, use_config_path=False)
        if not p:
            self.status.configure(text=f"✗ không tìm thấy {tools.exe_name(name)} ở các thư mục quen thuộc — bấm \"Chọn…\" "
                                  "để chỉ tới file", text_color=W.LEVEL_COLORS["error"])
            return
        self.entry.delete(0, "end")
        self.entry.insert(0, p)
        self.view.show_tool_status(self)

    def _auto_dir(self) -> None:
        name = DIR_FINDERS[self.f.key]
        cfg = dict(self.view.current_cfg())
        cfg[self.f.key] = ""
        p = tools.find_tool(name, cfg, self.view.app.folder, use_config_path=False)
        if p:
            self.entry.delete(0, "end")
            self.entry.insert(0, os.path.dirname(p))
        else:
            W.warn(self.parent, "Tự tìm", f"Không tìm thấy {tools.exe_name(name)}.")


class SecretRow:
    def __init__(self, view: "SettingsView", parent: Any, key: str, row: int):
        ctk = W.ctk
        self.key = key
        label, help_txt, check = SECRET_INFO[key]
        ctk.CTkLabel(parent, text="🔒 " + label, anchor="w").grid(row=row, column=0, sticky="w", padx=(4, 8), pady=(8, 0))
        box = ctk.CTkFrame(parent, fg_color="transparent")
        box.grid(row=row, column=1, sticky="ew", pady=(8, 0))
        self.entry = ctk.CTkEntry(box, width=360, show="•")
        self.entry.pack(side="left")
        self.shown = W.tk.BooleanVar(value=False)
        ctk.CTkCheckBox(box, text="Hiện", variable=self.shown, width=60,
                        command=lambda: self.entry.configure(show="" if self.shown.get() else "•")).pack(side="left", padx=6)
        self.check = check
        self.status: Any = None
        if check:
            ctk.CTkButton(box, text="Kiểm tra kết nối", width=130, command=lambda: view.check(check)).pack(side="left")
            self.status = ctk.CTkLabel(box, text="", anchor="w", wraplength=420, justify="left")
            self.status.pack(side="left", padx=8)
        if help_txt:
            h = ctk.CTkLabel(parent, text=help_txt, anchor="w", justify="left", wraplength=view.help_wrap,
                             text_color=W.LEVEL_COLORS["dim"], font=ctk.CTkFont(size=12))
            h.grid(row=row + 1, column=1, sticky="w")
            view.help_labels.append(h)
        self.saved = ""

    def set_value(self, v: str) -> None:
        self.saved = v or ""
        self.entry.delete(0, "end")
        self.entry.insert(0, self.saved)

    def get_value(self) -> str:
        return self.entry.get().strip()


class SettingsView:
    def __init__(self, app: Any, parent: Any):
        ctk = W.ctk
        self.app = app
        self.frame = ctk.CTkFrame(parent, fg_color="transparent")
        self.frame.pack(fill="both", expand=True)
        top = ctk.CTkFrame(self.frame, fg_color="transparent")
        top.pack(fill="x", pady=(0, 6))
        self.group_var = W.TrChoice(C.GROUPS["chung"])  # hien ten nhom da dich; get() van la ten tieng Viet
        self.seg = ctk.CTkSegmentedButton(top, values=self.group_var.values(C.GROUPS.values()), variable=self.group_var.var,
                                          command=lambda _v: self.show_group())
        self.seg.pack(side="left")
        self.adv = W.tk.BooleanVar(value=False)
        ctk.CTkCheckBox(top, text="Hiện mục nâng cao", variable=self.adv, command=self._toggle_adv).pack(side="left", padx=12)
        self.body = ctk.CTkFrame(self.frame, fg_color="transparent")
        self.body.pack(fill="both", expand=True)
        bar = ctk.CTkFrame(self.frame, fg_color="transparent")
        bar.pack(fill="x", pady=(6, 0))
        ctk.CTkButton(bar, text="Lưu cài đặt", width=130, command=self.save).pack(side="left")
        ctk.CTkButton(bar, text="Bỏ thay đổi", width=110, command=self.reload,
                      fg_color=("gray70", "gray30"), text_color=("gray10", "gray90")).pack(side="left", padx=6)
        ctk.CTkButton(bar, text="Nhập từ .bat…", width=120, command=self.import_bat).pack(side="left", padx=6)
        ctk.CTkButton(bar, text="Khôi phục mặc định…", width=150, command=self.restore_defaults,
                      fg_color=("gray70", "gray30"), text_color=("gray10", "gray90")).pack(side="left", padx=6)
        ctk.CTkButton(bar, text="Mở thư mục cấu hình", width=150,
                      command=lambda: W.open_path(C.app_config_dir()) or W.warn(self.frame, "Cấu hình",
                                                                                 "Chưa có thư mục cấu hình (chưa lưu lần nào).")
                      ).pack(side="right")
        self.msg = ctk.CTkLabel(bar, text="", anchor="w")
        self.msg.pack(side="left", padx=10)
        self.groups: dict[str, Any] = {}
        self.help_labels: list[Any] = []
        self.help_wrap = 640
        self._body_w = 0
        self._rewrap_after: Any = None
        self.body.bind("<Configure>", self._body_resized, add="+")
        self.rows: dict[str, FieldRow] = {}
        self.secret_rows: dict[str, SecretRow] = {}
        self.tool_label: Any = None
        self.show_group()

    # ---- dung nhom (luoi: chi dung khi mo lan dau) ----
    def _gid(self) -> str:
        label = self.group_var.get()
        return next((g for g, t in C.GROUPS.items() if t == label), "chung")

    def show_group(self) -> None:
        gid = self._gid()
        for g, fr in self.groups.items():
            if g != gid:
                fr.pack_forget()
        if gid not in self.groups:
            self.groups[gid] = self._build_group(gid)
        self.groups[gid].pack(fill="both", expand=True)

    def _build_group(self, gid: str) -> Any:
        ctk = W.ctk
        sf = ctk.CTkScrollableFrame(self.body)
        sf.columnconfigure(1, weight=1)
        r = 0
        if gid == "chung":
            box = ctk.CTkFrame(sf, fg_color="transparent")
            box.grid(row=r, column=0, columnspan=2, sticky="ew", pady=(2, 0))
            ctk.CTkLabel(box, text="Công cụ ngoài", font=ctk.CTkFont(weight="bold")).pack(side="left")
            ctk.CTkButton(box, text="Kiểm tra lại", width=110, command=self.check_tools).pack(side="left", padx=10)
            self.tool_label = ctk.CTkLabel(box, text="Ô để trống = chương trình tự tìm; dòng chữ dưới mỗi ô cho biết file "
                                           "đang được dùng.", anchor="w", justify="left", text_color=W.LEVEL_COLORS["dim"])
            self.tool_label.pack(side="left", fill="x", expand=True)
            r += 1
        if gid == "api":
            ctk.CTkLabel(sf, text="Bí mật — lưu trong Windows Credential Manager, KHÔNG lưu trong config.json, không bao "
                         "giờ in ra log.", anchor="w", font=ctk.CTkFont(weight="bold")).grid(row=r, column=0, columnspan=2,
                                                                                         sticky="w", pady=(2, 0))
            r += 1
            vals = self.app.secrets()
            for k in S.SECRET_KEYS:
                sr = SecretRow(self, sf, k, r)
                sr.set_value(vals.get(k, ""))
                self.secret_rows[k] = sr
                r += 2
            ctk.CTkLabel(sf, text="Cấu hình API", anchor="w", font=ctk.CTkFont(weight="bold")).grid(
                row=r, column=0, columnspan=2, sticky="w", pady=(16, 0))
            r += 1
        for f in C.FIELDS:
            if f.group != gid:
                continue
            fr = FieldRow(self, sf, f, r)
            fr.set_value(self.app.cfg[f.key])
            if f.advanced and not self.adv.get():
                fr.set_visible(False)
            self.rows[f.key] = fr
            r += 2
        if gid == "chung":
            self.frame.after(10, lambda: self.check_tools(startup=True))
        return sf

    def _body_resized(self, ev: Any) -> None:
        # chi nghe khung NGOAI (be ngang theo cua so, khong theo noi dung) -> khong vong lap doi co
        self._body_w = ev.width
        if self._rewrap_after is None:
            self._rewrap_after = self.frame.after(80, self._rewrap)

    def _rewrap(self) -> None:
        """Chú thích xuống dòng theo bề ngang cửa sổ (720p hẹp → nhiều dòng; 1440p rộng → ít dòng)."""
        self._rewrap_after = None
        w = int(self._body_w / max(W.scaling(self.frame), 0.1)) - 360  # tru cot nhan + thanh cuon + le
        w = max(300, min(1100, w))
        if abs(w - self.help_wrap) < 24:
            return
        self.help_wrap = w
        for h in self.help_labels:
            try:
                h.configure(wraplength=w)
            except Exception:  # noqa: BLE001 - nhan da bi huy
                pass

    def _toggle_adv(self) -> None:
        for fr in self.rows.values():
            if fr.f.advanced:
                fr.set_visible(bool(self.adv.get()))

    # ---- gia tri ----
    def current_cfg(self) -> dict[str, Any]:
        """Cấu hình theo các ô đang gõ (ô sai kiểu → giữ giá trị cũ). Dùng cho "Tự tìm" / "Kiểm tra kết nối"."""
        out = dict(self.app.cfg)
        for k, fr in self.rows.items():
            try:
                out[k] = fr.get_value()
            except ValueError:
                pass
        return out

    def current_secrets(self) -> dict[str, str]:
        vals = dict(self.app.secrets())
        for k, sr in self.secret_rows.items():
            vals[k] = sr.get_value()
        return vals

    def collect(self) -> tuple[dict[str, Any], list[str]]:
        vals: dict[str, Any] = {}
        errors: list[str] = []
        for k, fr in self.rows.items():
            try:
                vals[k] = fr.get_value()
                fr.mark(False)
            except ValueError as e:
                errors.append(str(e))
                fr.mark(True)
        return vals, errors

    def is_dirty(self) -> bool:
        vals, errors = self.collect()
        if errors:
            return True
        for k, v in vals.items():
            cur = self.app.cfg[k]
            if self.rows[k].kind in ("langs", "parts"):  # o tick: thu tu do chuong trinh quyet
                if sorted(cur) != sorted(v):
                    return True
            elif cur != v:
                return True
        return any(sr.get_value() != sr.saved for sr in self.secret_rows.values())

    def save(self) -> bool:
        vals, errors = self.collect()
        if errors:
            W.warn(self.frame, "Chưa lưu được", "Sửa các ô sau rồi lưu lại:\n\n• " + "\n• ".join(errors))
            return False
        cfg = self.app.cfg
        for k, v in vals.items():
            cfg[k] = v
        for c in cfg["Languages"]:
            naming.register_language(c)
        try:
            C.save_config(cfg)
        except OSError as e:
            W.warn(self.frame, "Không lưu được", f"Không ghi được file cấu hình: {e}")
            return False
        n_sec = 0
        for k, sr in self.secret_rows.items():
            v = sr.get_value()
            if v != sr.saved:
                try:
                    self.app.secret_store.set(k, v)
                    sr.saved = v
                    n_sec += 1
                except S.SecretStoreError as e:
                    W.warn(self.frame, "Không lưu được bí mật", str(e))
                    return False
        self.app.on_config_changed(secrets_changed=n_sec > 0)
        self.msg.configure(text="✓ Đã lưu" + (f" (cả {n_sec} bí mật)" if n_sec else ""), text_color=W.LEVEL_COLORS["ok"])
        self.app.log("ok", f"Đã lưu cài đặt: {C.config_path()}" + (f" + {n_sec} bí mật vào Credential Manager" if n_sec else ""))
        return True

    def reload(self) -> None:
        for k, fr in self.rows.items():
            fr.set_value(self.app.cfg[k])
        vals = self.app.secrets()
        for k, sr in self.secret_rows.items():
            sr.set_value(vals.get(k, ""))
        self.msg.configure(text="")

    def on_config_changed(self) -> None:
        self.reload()

    def restore_defaults(self) -> None:
        if not W.ask_yes_no(self.frame, "Khôi phục mặc định",
                            "Đưa MỌI cài đặt về giá trị mặc định?\n\n(API key / tài khoản trong Credential Manager "
                            "KHÔNG bị xoá.)"):
            return
        self.app.cfg.reset_defaults()
        C.save_config(self.app.cfg)
        self.reload()
        self.app.on_config_changed()
        self.app.log("ok", "Đã khôi phục cài đặt mặc định.")

    # ---- cong cu ----
    def _tool_rows(self) -> dict[str, FieldRow]:
        return {TOOL_BY_KEY[k]: fr for k, fr in self.rows.items() if k in TOOL_BY_KEY}

    def _show_info(self, fr: FieldRow, info: tools.ToolInfo, auto: bool) -> None:
        fr._checked = fr.entry.get().strip()
        fr.status.configure(text=tool_status_text(info, auto), text_color=tool_status_color(info))

    def show_tool_status(self, fr: FieldRow, only_if_changed: bool = False) -> None:
        """Kiểm tra lại MỘT công cụ (sau Chọn… / Tự tìm / gõ đường dẫn)."""
        name = TOOL_BY_KEY.get(fr.f.key)
        if not name:
            return
        p = fr.entry.get().strip()
        if only_if_changed and getattr(fr, "_checked", None) == p:
            return
        cfg, folder = self.current_cfg(), self.app.folder

        def work(job: tools.Job) -> tuple[tools.ToolInfo, bool]:
            path = p or tools.find_all_tools(cfg, folder).get(name)
            return tools.tool_info(name, path, job), not p

        self.app.runner.start(f"Kiểm tra {name}", work, lambda res: self._show_info(fr, *res), when_free=True)

    def check_tools(self, startup: bool = False) -> None:
        """Nút "Kiểm tra lại": file nào đang được dùng cho từng công cụ + phiên bản. Lúc mở chương trình: báo công cụ
        còn thiếu (và việc gì sẽ không làm được) trong khung log."""
        cfg = self.current_cfg()
        folder = self.app.folder
        rows = self._tool_rows()

        def work(job: tools.Job) -> dict[str, tuple[tools.ToolInfo, bool]]:
            found = tools.find_all_tools(cfg, folder)
            out = {}
            for n in tools.TOOLS:
                own = str(cfg.get(tools.CONFIG_PATH_KEY[n]) or "").strip()
                out[n] = (tools.tool_info(n, found.get(n), job), not own)
            return out

        def done(res: dict[str, tuple[tools.ToolInfo, bool]]) -> None:
            for n, (info, auto) in res.items():
                if n in rows:
                    self._show_info(rows[n], info, auto)
            lines = tool_summary(res)
            for lvl, msg in lines:
                if not startup or lvl != "ok":
                    self.app.log(lvl, msg)
            if startup and all(lvl == "ok" for lvl, _m in lines):
                self.app.log("ok", lines[0][1])

        self.app.runner.start("Kiểm tra công cụ", work, done, when_free=True)

    # ---- kiem tra ket noi ----
    def check(self, which: str) -> None:
        cfg = self.current_cfg()
        vals = self.current_secrets()
        row = next((r for r in self.secret_rows.values() if r.check == which), None)
        if row and row.status:
            row.status.configure(text="đang kiểm tra…", text_color=W.LEVEL_COLORS["dim"])
        self.app.redactor.update([*self.app.secrets().values(), *vals.values()])

        def work(job: tools.Job) -> ConnStatus:
            return check_connection(which, vals, cfg, job)

        def done(st: ConnStatus) -> None:
            if row and row.status:
                row.status.configure(text=st.text, text_color=status_color(st))
            self.app.log("ok" if st.ok else ("dim" if st.ok is None else "error"), st.text)

        self.app.runner.start(f"Kiểm tra kết nối {CHECK_NAMES.get(which, which)}", work, done)

    # ---- nhap tu .bat ----
    def import_bat(self) -> None:
        paths = W.filedialog.askopenfilenames(parent=self.frame, title=W.tr("Chọn SubFetch / SubPrep / MuxTool .bat"),
                                              filetypes=[("File .bat", "*.bat"), ("Mọi file", "*.*")])
        if not paths:
            return
        imp = C.import_from_bat(paths)
        store = self.app.secret_store

        def cur_secret(k: str) -> str:
            try:
                return store.get(k)
            except S.SecretStoreError:
                return ""

        rows = import_rows(imp, self.app.cfg, cur_secret)
        if not rows:
            msg = "Không đọc được khoá cấu hình nào."
            if imp.problems:
                msg += "\n\n" + "\n".join(f"• {f}: {why}" for f, _v, _l, why in imp.problems[:10])
            W.warn(self.frame, "Nhập từ .bat", msg)
            return
        ImportDialog(self, imp, rows).run()


class ImportDialog:
    def __init__(self, view: SettingsView, imp: C.BatImport, rows: list[ImportRow]):
        ctk = W.ctk
        self.view, self.imp, self.rows = view, imp, rows
        self.d = W.Dialog(view.frame, "Nhập cấu hình từ file .bat", 1100, 640)
        kinds = {"subfetch": "SubFetch", "subprep": "SubPrep", "muxtool": "MuxTool"}
        files = ", ".join(f"{os.path.basename(p)} → {kinds.get(k or '', 'không nhận ra')}" for p, k in imp.files)
        ctk.CTkLabel(self.d.body, text=f"Đã đọc: {files}\nTick những khoá muốn nhập. Khoá có hai giá trị khác nhau: chọn "
                     "một (hoặc bỏ cả hai). Bí mật được ghi thẳng vào Windows Credential Manager.",
                     anchor="w", justify="left", wraplength=self.d.wrap).pack(fill="x")
        cols = [W.Col("label", "Khoá", 260), W.Col("current", "Giá trị hiện tại", 270), W.Col("new", "Giá trị từ .bat", 300),
                W.Col("source", "Nguồn (file:dòng)", 170)]
        self.t = W.Table(self.d.body, cols, check="Nhập", height=16, selectmode="browse")
        self.t.frame.pack(fill="both", expand=True, pady=6)
        self.groups: dict[str, list[str]] = {}
        for i, r in enumerate(rows):
            iid = f"r{i}"
            tags = ["dim"] if r.same else []
            if r.group:
                tags.append("need")
                self.groups.setdefault(("S|" if r.secret else "V|") + r.group, []).append(iid)
            self.t.add(iid, {"label": r.label + (" (chọn một)" if r.group else ""), "current": r.current,
                             "new": r.new + ("  (giống hiện tại)" if r.same else ""), "source": r.source},
                       tags=tags, checked=not r.group)
        self.t.on_toggle = self._toggle
        notes = []
        if imp.ignored:
            notes.append("Bỏ qua (thuộc console / được thay bằng nút): " + ", ".join(sorted({v for _f, v, _w in imp.ignored})))
        if imp.problems:
            notes.append("Không đọc được: " + "; ".join(f"{f} {v} (dòng {ln}): {why}" for f, v, ln, why in imp.problems[:8]))
        if notes:
            ctk.CTkLabel(self.d.body, text="\n".join(notes), anchor="w", justify="left", wraplength=self.d.wrap,
                         text_color=W.LEVEL_COLORS["dim"]).pack(fill="x")
        self.d.button("Nhập", self.apply, primary=True)
        self.d.button("Huỷ", self.d.close)

    def _toggle(self, iid: str, state: bool) -> None:
        if not state:
            return
        for ids in self.groups.values():
            if iid in ids:
                for other in ids:
                    if other != iid:
                        self.t.set_checked(other, False)

    def apply(self) -> None:
        keep_v: set[str] = set()
        keep_s: set[str] = set()
        res: dict[str, Any] = {}
        for i, r in enumerate(self.rows):
            if not self.t.checked.get(f"r{i}"):
                continue
            if r.group:
                res[r.key] = r.value
            elif r.secret:
                keep_s.add(r.key)
            else:
                keep_v.add(r.key)
        imp = filtered_import(self.imp, keep_v, keep_s)
        app = self.view.app
        try:
            done = C.apply_bat_import(app.cfg, imp, res, app.secret_store)
            C.save_config(app.cfg)
        except (OSError, S.SecretStoreError, ValueError) as e:
            W.warn(self.d.win, "Không nhập được", str(e))
            return
        self.d.close(True)
        self.view.reload()
        app.on_config_changed(secrets_changed=True)
        app.log("ok", f"Đã nhập {len(done)} khoá từ .bat (bí mật đã vào Credential Manager).")

    def run(self) -> Any:
        return self.d.run()
