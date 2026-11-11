"""Thẻ "Giới thiệu" (bản 1.2.0): tác giả, phiên bản, bản quyền, và ghi nhận phần mềm / dịch vụ của bên khác mà MuxStudio
dùng. Mỗi phần mềm thuộc bản quyền của tác giả riêng của nó; MuxStudio chỉ dùng theo giấy phép ghi ở đây.

Phần dữ liệu (``AUTHOR``, ``CREDITS``…) là thuần — test được không cần Tk.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from core import __version__

from . import widgets as W

AUTHOR = "Vũ Hoàng Anh"
COPYRIGHT_YEAR = 2026
COPYRIGHT = f"© {COPYRIGHT_YEAR} {AUTHOR}"

#: Nhóm: thư viện nằm TRONG bản .exe | công cụ ngoài MuxStudio chỉ GỌI (không đi kèm) | dịch vụ mạng
BUNDLED, TOOL, SERVICE = "bundled", "tool", "service"
KIND_TITLES = {
    BUNDLED: "Thư viện đi kèm trong bản MuxStudio.exe",
    TOOL: "Công cụ ngoài (không đi kèm — bạn tự tải; MuxStudio chỉ gọi chúng)",
    SERVICE: "Dịch vụ dữ liệu trên mạng",
}


@dataclass(frozen=True)
class Credit:
    name: str
    kind: str
    owner: str  # chu ban quyen / nhom tac gia
    license: str  # ten giay phep (dich vu: dieu khoan su dung)
    url: str
    use: str  # MuxStudio dung de lam gi


CREDITS: tuple[Credit, ...] = (
    Credit("Python", BUNDLED, "Python Software Foundation", "PSF License Agreement", "https://www.python.org",
           "Ngôn ngữ và bộ chạy của chương trình"),
    Credit("Tcl/Tk", BUNDLED, "Regents of the University of California, Sun Microsystems, ActiveState và các tác giả khác",
           "Tcl/Tk License (kiểu BSD)", "https://www.tcl-lang.org", "Nền giao diện cửa sổ (tkinter)"),
    Credit("CustomTkinter", BUNDLED, "Tom Schimansky", "MIT", "https://customtkinter.tomschimansky.com",
           "Bộ nút, khung, thẻ của giao diện"),
    Credit("Roboto (phông chữ đi kèm CustomTkinter)", BUNDLED, "Google", "Apache-2.0", "https://fonts.google.com/specimen/Roboto",
           "Phông dự phòng của giao diện"),
    Credit("darkdetect", BUNDLED, "Alberto Sottile", "BSD-3-Clause", "https://github.com/albertosottile/darkdetect",
           "Nhận biết Windows đang ở chế độ Sáng hay Tối"),
    Credit("packaging", BUNDLED, "Donald Stufft và các tác giả packaging", "Apache-2.0 hoặc BSD-2-Clause",
           "https://github.com/pypa/packaging", "Thư viện phụ của CustomTkinter"),
    Credit("Pillow", BUNDLED, "Jeffrey A. Clark và các tác giả Pillow", "MIT-CMU", "https://python-pillow.github.io",
           "Hiện ảnh mã CAPTCHA (JPEG) của Titulky.com"),
    Credit("Requests", BUNDLED, "Kenneth Reitz và các tác giả Requests", "Apache-2.0", "https://requests.readthedocs.io",
           "Gọi API tải phụ đề và tra tên phim"),
    Credit("urllib3", BUNDLED, "Andrey Petrov và các tác giả urllib3", "MIT", "https://github.com/urllib3/urllib3",
           "Kết nối mạng (Requests dùng)"),
    Credit("certifi", BUNDLED, "Kenneth Reitz; danh sách chứng chỉ của Mozilla", "MPL-2.0", "https://github.com/certifi/python-certifi",
           "Chứng chỉ gốc để kiểm tra kết nối HTTPS"),
    Credit("idna", BUNDLED, "Kim Davies", "BSD-3-Clause", "https://github.com/kjd/idna", "Tên miền quốc tế (Requests dùng)"),
    Credit("charset-normalizer", BUNDLED, "Ahmed TAHRI", "MIT", "https://github.com/jawah/charset_normalizer",
           "Nhận diện bảng mã phản hồi mạng (Requests dùng)"),
    Credit("keyring", BUNDLED, "Jason R. Coombs và các tác giả keyring", "MIT", "https://github.com/jaraco/keyring",
           "Cất key / mật khẩu vào Windows Credential Manager"),
    Credit("jaraco.classes, jaraco.functools, jaraco.context, more-itertools", BUNDLED,
           "Jason R. Coombs; Erik Rose và các tác giả more-itertools", "MIT", "https://github.com/jaraco", "Thư viện phụ của keyring"),
    Credit("pywin32-ctypes", BUNDLED, "Enthought, Inc.", "BSD-3-Clause", "https://github.com/enthought/pywin32-ctypes",
           "Cầu nối tới Credential Manager trên Windows"),
    Credit("Send2Trash", BUNDLED, "Virgil Dupras, Andrew Senetar", "BSD-3-Clause", "https://github.com/arsenetar/send2trash",
           "Đưa file vào Thùng rác thay vì xoá hẳn"),
    Credit("PyInstaller (phần khởi động của file .exe)", BUNDLED, "PyInstaller Development Team",
           "GPL-2.0-or-later kèm ngoại lệ cho phần khởi động (chương trình đóng gói được dùng giấy phép riêng)",
           "https://pyinstaller.org", "Đóng gói thành MuxStudio.exe"),
    Credit("MKVToolNix (mkvmerge, mkvextract)", TOOL, "Moritz Bunkus", "GPL-2.0", "https://mkvtoolnix.download",
           "Đọc track, trích phụ đề gốc, tạo file MKV"),
    Credit("FFmpeg (ffmpeg, ffprobe)", TOOL, "Các tác giả FFmpeg", "LGPL-2.1-or-later / GPL-2.0-or-later (tuỳ bản dựng)",
           "https://ffmpeg.org", "Trích audio làm mốc, đọc bitrate / HDR, đổi tốc độ audio ghép"),
    Credit("alass", TOOL, "kaegi", "GPL-3.0", "https://github.com/kaegi/alass", "Đồng bộ thời gian phụ đề"),
    Credit("TMDB", SERVICE, "The Movie Database (TMDB)", "Điều khoản sử dụng API của TMDB", "https://www.themoviedb.org",
           "Tên phim, tên tập, IMDb ID"),
    Credit("TheTVDB", SERVICE, "TheTVDB.com (TV Time, Inc.)", "Điều khoản sử dụng API của TheTVDB", "https://thetvdb.com",
           "Tên phim bộ, tên tập, số tập tuyệt đối"),
    Credit("OpenSubtitles.com", SERVICE, "OpenSubtitles", "Điều khoản sử dụng API của OpenSubtitles", "https://www.opensubtitles.com",
           "Nguồn phụ đề"),
    Credit("SubSource", SERVICE, "SubSource", "Điều khoản sử dụng API của SubSource", "https://subsource.net", "Nguồn phụ đề"),
    Credit("Titulky.com", SERVICE, "Titulky.com", "Điều khoản sử dụng của Titulky.com", "https://www.titulky.com",
           "Nguồn phụ đề tiếng Séc"),
)

#: Câu ghi nhận mà TMDB và TheTVDB yêu cầu chương trình dùng API của họ phải hiển thị (giữ nguyên tiếng Anh).
ATTRIBUTIONS = (
    "This product uses the TMDB API but is not endorsed or certified by TMDB.",
    "Metadata provided by TheTVDB. Please consider adding missing information or subscribing.",
)

OWNERSHIP_NOTE = ("MuxStudio do {author} viết. Các phần mềm, thư viện và dịch vụ liệt kê dưới đây KHÔNG thuộc MuxStudio: "
                  "bản quyền của chúng thuộc về tác giả / tổ chức tương ứng, và chúng được dùng theo giấy phép ghi ở cột "
                  "\"Giấy phép\". MuxStudio không sửa đổi các công cụ ngoài, chỉ gọi chúng như chương trình riêng.")


def by_kind(kind: str) -> list[Credit]:
    return [c for c in CREDITS if c.kind == kind]


def about_lines(version: str = __version__) -> list[str]:
    """Nội dung trang Giới thiệu dạng chữ thuần (cũng dùng cho nút "Chép nội dung")."""
    out = [f"MuxStudio {version}", f"Tác giả: {AUTHOR}", f"{COPYRIGHT}. Giữ mọi quyền với phần mã của MuxStudio.", "",
           OWNERSHIP_NOTE.format(author=AUTHOR), ""]
    for kind in (BUNDLED, TOOL, SERVICE):
        out.append(KIND_TITLES[kind] + ":")
        for c in by_kind(kind):
            out.append(f"  • {c.name} — {c.owner} — {c.license} — {c.url}")
        out.append("")
    out += list(ATTRIBUTIONS)
    return out


class AboutView:
    def __init__(self, app: Any, parent: Any):
        ctk = W.ctk
        self.app = app
        self.frame = ctk.CTkFrame(parent, fg_color="transparent")
        self.frame.pack(fill="both", expand=True)
        head = ctk.CTkFrame(self.frame)
        head.pack(fill="x")
        self.title = ctk.CTkLabel(head, text=f"MuxStudio {__version__}", font=ctk.CTkFont(size=24, weight="bold"), anchor="w")
        self.title.pack(fill="x", padx=16, pady=(12, 0))
        self.author = ctk.CTkLabel(head, text=f"Tác giả: {AUTHOR}", font=ctk.CTkFont(size=16, weight="bold"), anchor="w")
        self.author.pack(fill="x", padx=16)
        self.copyright = ctk.CTkLabel(head, text=f"{COPYRIGHT}. Giữ mọi quyền với phần mã của MuxStudio.", anchor="w")
        self.copyright.pack(fill="x", padx=16)
        self.what = ctk.CTkLabel(head, text="Tải phụ đề → đồng bộ phụ đề → dọn track và mux MKV, đặt tên chuẩn Jellyfin.",
                                 anchor="w", text_color=W.LEVEL_COLORS["dim"])
        self.what.pack(fill="x", padx=16, pady=(0, 10))
        self.copy_btn = ctk.CTkButton(head, text="Chép nội dung trang này", width=190, command=self.copy,
                                      fg_color=("gray70", "gray30"), text_color=("gray10", "gray90"))
        self.copy_btn.place(relx=1.0, x=-16, y=14, anchor="ne")

        self.note = ctk.CTkLabel(self.frame, text=OWNERSHIP_NOTE.format(author=AUTHOR), anchor="w", justify="left",
                                 wraplength=1000)
        self.note.pack(fill="x", padx=12, pady=(8, 4))
        self.frame.bind("<Configure>", self._wrap, add="+")
        cols = [W.Col("name", "Phần mềm / dịch vụ", 250), W.Col("owner", "Bản quyền thuộc về", 260, stretch=True),
                W.Col("lic", "Giấy phép", 230, stretch=True), W.Col("use", "MuxStudio dùng để", 280, stretch=True),
                W.Col("url", "Trang chủ", 250)]
        self.table = W.Table(self.frame, cols, height=8, tree="Nhóm", tree_width=40)
        self.table.frame.pack(fill="both", expand=True, padx=4, pady=4)
        self.table.on_double = self._open
        self.table.tip = lambda iid, _c: "Bấm đúp để mở trang chủ" if iid.startswith("c") else None
        n = 0
        for kind in (BUNDLED, TOOL, SERVICE):
            self.table.add(f"k_{kind}", {"name": KIND_TITLES[kind], "owner": "", "lic": "", "use": "", "url": ""},
                           tags=("head",))
            for c in by_kind(kind):
                self.table.add(f"c{n}", {"name": c.name, "owner": c.owner, "lic": c.license, "use": c.use, "url": c.url},
                               parent=f"k_{kind}")
                n += 1
        self._urls = [c.url for kind in (BUNDLED, TOOL, SERVICE) for c in by_kind(kind)]
        self.attr = ctk.CTkLabel(self.frame, text="\n".join(ATTRIBUTIONS), anchor="w", justify="left",
                                 text_color=W.LEVEL_COLORS["dim"])
        self.attr.pack(fill="x", padx=12, pady=(2, 6))

    def _wrap(self, e: Any) -> None:
        try:
            sc = float(W.ctk.ScalingTracker.get_widget_scaling(self.frame))
            w = max(300, int(e.width / sc) - 40)
            if abs(int(self.note.cget("wraplength")) - w) > 20:
                self.note.configure(wraplength=w)
        except Exception:  # noqa: BLE001
            pass

    def _open(self, iid: str, _col: str) -> None:
        if iid.startswith("c"):
            import webbrowser  # noqa: PLC0415

            try:
                webbrowser.open(self._urls[int(iid[1:])])
            except Exception:  # noqa: BLE001
                pass

    def copy(self) -> None:
        self.app.root.clipboard_clear()
        self.app.root.clipboard_append("\n".join(about_lines()))
        self.app.log("ok", "Đã chép nội dung trang Giới thiệu vào bộ nhớ tạm.")

    def update_buttons(self) -> None:
        pass
