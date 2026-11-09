"""Công cụ ngoài + tiến trình con + đối tượng ``Job`` (log, tiến độ, nút Dừng).

Thay ``Find-Exe`` (MT), ``Find-MkvTool``/``Find-Alass``/``Find-Near`` (SP), vòng tìm
mkvmerge đầu SubFetch, ``Join-Safe`` (cả 3), ``Save-ToDest`` (SF).

Quy tắc an toàn (§6.1): gọi tiến trình con bằng DANH SÁCH tham số, không ``shell=True``,
``CREATE_NO_WINDOW`` trên Windows, đọc output UTF-8 (``errors='replace'``). Đường dẫn dùng
nguyên văn — không bao giờ đưa vào ``glob``.
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping, Sequence

IS_WINDOWS = os.name == "nt"

# ==========================================================================
#  JOB: log + tiến độ + dừng
# ==========================================================================

LogFn = Callable[[str, str], None]  # (muc, noi dung) - muc: info ok warn error dim head


class Cancelled(Exception):
    """Người dùng bấm "Dừng ngay"."""


class Job:
    """Truyền log/tiến độ từ lõi về giao diện và nhận lệnh dừng.

    * ``stop_after_current``: dừng sau file đang chạy (lõi kiểm tra giữa các file).
    * ``cancel_now()``: dừng ngay — kết thúc mọi tiến trình con đang chạy.
    """

    def __init__(self, log: LogFn | None = None, progress: Callable[[int, int, str], None] | None = None,
                 redact: Callable[[str], str] | None = None):
        self._log = log
        self._progress = progress
        self._redact = redact
        self.stop_after_current = threading.Event()
        self.stop_now = threading.Event()
        self._procs: set[subprocess.Popen] = set()
        self._lock = threading.Lock()
        self.lines: list[tuple[str, str]] = []  # ban sao log (de luu / test)

    # ---- log ----
    def log(self, msg: str, level: str = "info") -> None:
        if self._redact:
            msg = self._redact(msg)
        self.lines.append((level, msg))
        if self._log:
            self._log(level, msg)

    def info(self, msg: str) -> None:
        self.log(msg, "info")

    def ok(self, msg: str) -> None:
        self.log(msg, "ok")

    def warn(self, msg: str) -> None:
        self.log(msg, "warn")

    def error(self, msg: str) -> None:
        self.log(msg, "error")

    def dim(self, msg: str) -> None:
        self.log(msg, "dim")

    def head(self, msg: str) -> None:
        self.log(msg, "head")

    def progress(self, done: int, total: int, label: str = "") -> None:
        if self._progress:
            self._progress(done, total, label)

    # ---- dung ----
    def check(self) -> None:
        if self.stop_now.is_set():
            raise Cancelled()

    def should_stop(self) -> bool:
        return self.stop_after_current.is_set() or self.stop_now.is_set()

    def register(self, p: subprocess.Popen) -> None:
        with self._lock:
            self._procs.add(p)

    def unregister(self, p: subprocess.Popen) -> None:
        with self._lock:
            self._procs.discard(p)

    def cancel_now(self) -> None:
        self.stop_now.set()
        with self._lock:
            procs = list(self._procs)
        for p in procs:
            try:
                p.terminate()
            except OSError:
                pass
        deadline = time.monotonic() + 3.0
        for p in procs:
            try:
                p.wait(max(0.0, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                try:
                    p.kill()
                except OSError:
                    pass


NULL_JOB = Job()


# ==========================================================================
#  CHẠY TIẾN TRÌNH CON
# ==========================================================================


@dataclass
class RunResult:
    code: int
    stdout: str
    stderr: str

    @property
    def output(self) -> str:
        return (self.stdout + ("\n" if self.stdout and self.stderr else "") + self.stderr).strip()


def run(args: Sequence[str | os.PathLike[str]], job: Job | None = None, timeout: float | None = None,
        cwd: str | os.PathLike[str] | None = None) -> RunResult:
    """Chạy một lệnh. KHÔNG dùng shell. Không tìm thấy chương trình → mã 127."""
    argv = [os.fspath(a) for a in args]
    kwargs: dict[str, Any] = {"stdin": subprocess.DEVNULL, "stdout": subprocess.PIPE, "stderr": subprocess.PIPE}
    if cwd is not None:
        kwargs["cwd"] = os.fspath(cwd)
    if IS_WINDOWS:
        kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW  # type: ignore[attr-defined]
    job = job or NULL_JOB
    job.check()
    try:
        p = subprocess.Popen(argv, **kwargs)
    except (FileNotFoundError, PermissionError, OSError) as e:
        return RunResult(127, "", str(e))
    job.register(p)
    try:
        try:
            out, err = p.communicate(timeout=timeout)
            code = p.returncode
        except subprocess.TimeoutExpired:
            p.kill()
            out, err = p.communicate()
            code = -9
    finally:
        job.unregister(p)
    job.check()
    return RunResult(code, out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace"))


# ==========================================================================
#  ĐƯỜNG DẪN
# ==========================================================================


def join_safe(base: str | os.PathLike[str] | None, child: str) -> str | None:
    """``Join-Safe``: nối đường dẫn, vế trái rỗng → ``None``."""
    if base is None or not str(base).strip():
        return None
    return os.path.join(os.fspath(base), child)


def copy_file(src: str | os.PathLike[str], dst: str | os.PathLike[str]) -> None:
    """``Save-ToDest``: chép bằng đường dẫn nguyên văn (tên có ``[ ]`` vẫn đúng), tạo thư mục đích."""
    d = os.path.dirname(os.fspath(dst))
    if d:
        os.makedirs(d, exist_ok=True)
    shutil.copyfile(src, dst)


def is_under(path: str | os.PathLike[str], folder: str | os.PathLike[str]) -> bool:
    """``path`` nằm trong (hoặc chính là) ``folder`` — so không phân biệt hoa thường như Windows."""
    p = os.path.normcase(os.path.abspath(os.fspath(path)))
    f = os.path.normcase(os.path.abspath(os.fspath(folder)))
    return p == f or p.startswith(f.rstrip("\\/") + os.sep)


def walk_files(root: str | os.PathLike[str], recursive: bool = True,
               exts: Iterable[str] | None = None, skip_dirs: Iterable[str | os.PathLike[str]] = ()) -> list[Path]:
    """Liệt kê file (không dùng glob). ``skip_dirs``: thư mục bỏ qua (cả cây con). Sắp xếp theo đường dẫn."""
    want = {e.lower() for e in exts} if exts else None
    skips = [os.path.normcase(os.path.abspath(os.fspath(s))) for s in skip_dirs]
    out: list[Path] = []

    def skipped(d: str) -> bool:
        nd = os.path.normcase(os.path.abspath(d))
        return any(nd == s or nd.startswith(s.rstrip("\\/") + os.sep) for s in skips)

    r = os.fspath(root)
    if not os.path.isdir(r):
        return out
    if recursive:
        for dirpath, dirnames, filenames in os.walk(r):
            if skipped(dirpath):
                dirnames[:] = []
                continue
            dirnames[:] = [d for d in dirnames if not skipped(os.path.join(dirpath, d))]
            for f in filenames:
                if want is None or os.path.splitext(f)[1].lower() in want:
                    out.append(Path(dirpath) / f)
    else:
        for e in os.scandir(r):
            if e.is_file() and (want is None or os.path.splitext(e.name)[1].lower() in want):
                out.append(Path(e.path))
    out.sort(key=lambda p: os.fspath(p).lower())  # nhu Sort-Object FullName: khong phan biet hoa thuong
    return out


# ==========================================================================
#  TÌM CÔNG CỤ ("Tự tìm")
# ==========================================================================

TOOLS = ("mkvmerge", "mkvextract", "ffmpeg", "ffprobe", "alass")
CONFIG_PATH_KEY = {"mkvmerge": "MkvmergePath", "mkvextract": "MkvextractPath", "ffmpeg": "FfmpegPath",
                   "ffprobe": "FfprobePath", "alass": "AlassPath"}


def exe_name(name: str) -> str:
    return name + ".exe" if IS_WINDOWS else name


def desktop_dir() -> str | None:
    """Desktop THẬT (OneDrive có thể chuyển Desktop sang ``...\\OneDrive\\Desktop``)."""
    if IS_WINDOWS:
        try:
            import ctypes
            from ctypes import wintypes

            buf = ctypes.create_unicode_buffer(wintypes.MAX_PATH)
            if ctypes.windll.shell32.SHGetFolderPathW(None, 0x0010, None, 0, buf) == 0:  # CSIDL_DESKTOPDIRECTORY
                return buf.value
        except Exception:
            pass
    home = os.environ.get("USERPROFILE") or os.path.expanduser("~")
    return os.path.join(home, "Desktop") if home else None


def _registry_mkvtoolnix_dirs() -> list[str]:
    if not IS_WINDOWS:
        return []
    out: list[str] = []
    try:
        import winreg
    except ImportError:
        return out
    roots = [(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
             (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
             (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    for hive, path in roots:
        try:
            with winreg.OpenKey(hive, path) as k:
                for i in range(winreg.QueryInfoKey(k)[0]):
                    sub = winreg.EnumKey(k, i)
                    if "mkvtoolnix" not in sub.lower():
                        continue
                    with winreg.OpenKey(k, sub) as sk:
                        try:
                            loc, _ = winreg.QueryValueEx(sk, "InstallLocation")
                            if loc:
                                out.append(loc)
                        except OSError:
                            pass
        except OSError:
            continue
    return out


def tool_search_dirs(name: str, cfg: Mapping[str, Any], target_dir: str | os.PathLike[str] | None = None,
                     near: str | None = None) -> list[str]:
    """Thứ tự thư mục tìm (hợp các danh sách của ba file .bat)."""
    desk = desktop_dir()
    prof = os.environ.get("USERPROFILE")
    td = os.fspath(target_dir) if target_dir else None
    mkv = name in ("mkvmerge", "mkvextract")
    dirs: list[str | None] = []
    if name in ("ffmpeg", "ffprobe") and near:
        dirs.append(near)  # Find-Near: canh alass.exe truoc
    if mkv:
        dirs += [cfg.get("MkvToolNixDir")]
    else:
        dirs += [cfg.get("AlassDir")]
        if name == "ffprobe":
            dirs += [cfg.get("MkvToolNixDir")]
    dirs += list(cfg.get("ExtraToolDirs") or [])
    sub = "mkvtoolnix" if mkv else "alass"
    dirs += [join_safe(desk, sub), join_safe(prof, os.path.join("Desktop", sub))]
    if not mkv:
        dirs += [join_safe(desk, os.path.join("alass-windows64", "bin", "alass"))]
    dirs += [td, join_safe(td, sub)]
    if mkv:
        for env in ("ProgramFiles", "ProgramFiles(x86)"):
            dirs.append(join_safe(os.environ.get(env), "MKVToolNix"))
        dirs.append(join_safe(os.environ.get("LOCALAPPDATA"), os.path.join("Programs", "MKVToolNix")))
        dirs += _registry_mkvtoolnix_dirs()
    seen: set[str] = set()
    out: list[str] = []
    for d in dirs:
        if d and os.path.normcase(d) not in seen:
            seen.add(os.path.normcase(d))
            out.append(d)
    return out


def find_tool(name: str, cfg: Mapping[str, Any], target_dir: str | os.PathLike[str] | None = None,
              near: str | None = None, use_config_path: bool = True) -> str | None:
    """Tìm một công cụ. Đường dẫn trong Cài đặt (nếu có) được ưu tiên."""
    if use_config_path:
        p = cfg.get(CONFIG_PATH_KEY.get(name, ""), "") if CONFIG_PATH_KEY.get(name) else ""
        if p and os.path.isfile(p):
            return os.path.abspath(p)
    exe = exe_name(name)
    for d in tool_search_dirs(name, cfg, target_dir, near):
        p = os.path.join(d, exe)
        if os.path.isfile(p):
            return os.path.abspath(p)
    w = shutil.which(exe) or shutil.which(name)
    return w


def find_all_tools(cfg: Mapping[str, Any], target_dir: str | os.PathLike[str] | None = None) -> dict[str, str | None]:
    alass = find_tool("alass", cfg, target_dir)
    near = os.path.dirname(alass) if alass else None
    return {
        "mkvmerge": find_tool("mkvmerge", cfg, target_dir),
        "mkvextract": find_tool("mkvextract", cfg, target_dir),
        "alass": alass,
        "ffmpeg": find_tool("ffmpeg", cfg, target_dir, near),
        "ffprobe": find_tool("ffprobe", cfg, target_dir, near),
    }


@dataclass
class ToolVersion:
    ok: bool
    major: int
    text: str


def mkvmerge_version(mkvmerge: str, job: Job | None = None) -> ToolVersion:
    """``mkvmerge --version`` → bản chính (vd 82). Tìm thấy mà không chạy được → ``ok=False``
    (thường do thiếu DLL hoặc Windows chặn file tải từ Internet: chuột phải › Properties › Unblock)."""
    r = run([mkvmerge, "--version"], job, timeout=30)
    txt = r.output
    ok = r.code == 0 and "mkvmerge" in txt.lower()
    m = re.search(r"v(\d+)\.", txt)
    return ToolVersion(ok, int(m.group(1)) if m else 0, txt.splitlines()[0] if txt else "")


#: Công cụ dùng ở bước nào — hiện trong Cài đặt và khi báo thiếu công cụ
TOOL_ROLES: dict[str, tuple[str, bool]] = {
    "mkvmerge": ("tạo file MKV (bước ③); đọc FPS / track (bước ① ②)", True),
    "mkvextract": ("trích phụ đề gốc từ MKV (bước ① ②), đo audio ghép (bước ③)", False),
    "alass": ("đồng bộ phụ đề (bước ②)", True),
    "ffprobe": ("audio track của file mẫu (bước ②), bitrate + HDR (bước ③)", False),
    "ffmpeg": ("trích WAV khi đồng bộ theo audio (bước ②), đổi tốc độ audio ghép (bước ③)", False),
}
_INFO_ARGS = {"mkvmerge": ["--version"], "mkvextract": ["--version"], "ffmpeg": ["-version"], "ffprobe": ["-version"],
              "alass": ["--help"]}
_INFO_MARK = {"mkvmerge": "mkvmerge", "mkvextract": "mkvextract", "ffmpeg": "ffmpeg version", "ffprobe": "ffprobe version",
              "alass": "alass"}
BLOCKED_HINT = "thiếu DLL hoặc bị Windows chặn: chuột phải vào file › Properties › Unblock"


@dataclass
class ToolInfo:
    """Kết quả kiểm tra một công cụ (ô "Công cụ ngoài" trong Cài đặt)."""

    name: str
    path: str | None
    ok: bool
    version: str = ""
    problem: str = ""

    @property
    def text(self) -> str:
        if self.ok:
            return self.version or "chạy được"
        return self.problem


def tool_info(name: str, path: str | os.PathLike[str] | Sequence[str] | None, job: Job | None = None) -> ToolInfo:
    """Chạy thử công cụ (``--version`` / ``-version`` / ``alass --help``): có chạy được không, phiên bản, có đúng là công
    cụ đó không (vd chọn nhầm ``ffmpeg.exe`` vào ô ffprobe). Không tìm thấy → ``ok=False``."""
    if not path:
        return ToolInfo(name, None, False, problem="không tìm thấy")
    cmd = [os.fspath(path)] if isinstance(path, (str, os.PathLike)) else [os.fspath(x) for x in path]
    shown = " ".join(cmd)
    if len(cmd) == 1 and not os.path.isfile(cmd[0]):
        return ToolInfo(name, shown, False, problem="không có file này")
    r = run([*cmd, *_INFO_ARGS.get(name, ["--version"])], job, timeout=30)
    out = r.output
    if r.code == 127 or not out:
        return ToolInfo(name, shown, False, problem=f"tìm thấy nhưng KHÔNG chạy được ({BLOCKED_HINT})")
    if _INFO_MARK.get(name, name) not in out.lower():
        first = out.splitlines()[0].split(" Copyright")[0][:60] if out else ""
        return ToolInfo(name, shown, False, problem=f"không phải {exe_name(name)}? (chương trình trả lời: {first})")
    ver = ""
    if name in ("mkvmerge", "mkvextract"):
        m = re.search(r"\bv(\d+\.\d+(?:\.\d+)?)", out)
        ver = f"v{m.group(1)}" if m else ""
    elif name in ("ffmpeg", "ffprobe"):
        m = re.search(r"version\s+(\S+)", out)
        ver = m.group(1)[:24] if m else ""
    elif name == "alass":
        m = re.search(r"alass\S*\s+v?(\d+\.\d+(?:\.\d+)?)", out, re.I)
        ver = m.group(1) if m else ""
    return ToolInfo(name, shown, True, ver)


@dataclass
class AlassFlags:
    ok: list[str]
    unsupported: list[str]
    help_read: bool


def alass_supported_flags(alass_cmd: str | Sequence[str], wanted: Sequence[str], job: Job | None = None,
                          help_text: str | None = None) -> AlassFlags:
    """``Get-SupportedAlassFlags``: cờ ``--xxx`` nào bản alass này hiểu (đọc ``alass --help``).

    Không đọc được ``--help`` → dùng đủ tham số (như .bat). Khác .bat: cờ không được hỗ trợ thì
    bỏ luôn GIÁ TRỊ đi kèm nó (vd ``--split-penalty 7``), tránh để ``7`` lạc thành tên file.
    """
    wanted = [str(w) for w in wanted]
    if not wanted:
        return AlassFlags([], [], True)
    if help_text is None:
        cmd = [alass_cmd] if isinstance(alass_cmd, str) else list(alass_cmd)
        help_text = run([*cmd, "--help"], job, timeout=30).output
    if not help_text:
        return AlassFlags(list(wanted), [], False)
    ok: list[str] = []
    no: list[str] = []
    dropping = False
    low = help_text.lower()
    for f in wanted:
        if f.startswith("--"):
            dropping = f.lower() not in low  # -match cua PowerShell: khong phan biet hoa thuong
            (no if dropping else ok).append(f)
        elif not dropping:
            ok.append(f)  # gia tri di kem tham so truoc do
    return AlassFlags(ok, no, True)


def mkvmerge_flag_names(major: int) -> tuple[str, str]:
    """Từ v74 mkvmerge đổi tên cờ (§6.2)."""
    if major >= 74:
        return "--default-track-flag", "--forced-display-flag"
    return "--default-track", "--forced-track"

