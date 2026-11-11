"""Giai đoạn 6 — đóng gói: số phiên bản, điểm vào ``MuxStudio.pyw``, chế độ tự kiểm tra ``--kiem-tra``, đường dẫn
file dữ liệu khi chạy bản ``.exe`` (``sys.frozen`` / ``sys._MEIPASS``), icon, ``MuxStudio.spec``, ``build.bat``,
keyring dự phòng cho bản ``.exe``.

Không cần tkinter (trừ ca tự kiểm tra có dựng cửa sổ — tự bỏ qua nếu không có màn hình). Không chạm Windows
Credential Manager thật: trong tiến trình test dùng keyring giả (conftest), tiến trình con dùng backend ``null``.
"""
from __future__ import annotations

import ast
import os
import re
import struct
import subprocess
import sys
import zlib
from pathlib import Path

import pytest

import core
from core import config as C
from core import secrets as S
from gui import app as A
from gui import widgets as W

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
import make_icon  # noqa: E402

try:
    import tkinter

    import customtkinter  # noqa: F401

    _r = tkinter.Tk()
    _r.destroy()
    HAS_TK = True
except Exception:  # noqa: BLE001
    HAS_TK = False


# ==========================================================================
#  phiên bản
# ==========================================================================


def test_version_same_everywhere():
    assert re.fullmatch(r"\d+\.\d+\.\d+", core.__version__)
    py = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    assert re.search(r'^version = "([^"]+)"', py, re.M).group(1) == core.__version__
    assert A.APP_TITLE == f"MuxStudio {core.__version__}"
    assert "from core import __version__ as VERSION" in (ROOT / "MuxStudio.spec").read_text(encoding="utf-8")


def test_build_info_source_vs_exe(monkeypatch, tmp_path):
    s = A.build_info()
    assert s.startswith(f"{A.APP_TITLE} (chạy từ mã nguồn)") and f"Python {sys.version_info.major}." in s
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "_MEIPASS", str(tmp_path), raising=False)
    assert W.is_frozen()
    assert A.build_info().startswith(f"{A.APP_TITLE} (bản .exe)")
    # ban .exe: file du lieu nam trong _internal (sys._MEIPASS), khong phai canh ma nguon
    assert W.resource_path("assets", "muxstudio.ico") == os.path.join(str(tmp_path), "assets", "muxstudio.ico")


def test_resource_path_from_source():
    assert not W.is_frozen()
    assert Path(W.resource_path(*W.ICON_ICO)) == ROOT / "assets" / "muxstudio.ico"
    assert os.path.isfile(W.resource_path(*W.ICON_ICO)) and os.path.isfile(W.resource_path(*W.ICON_PNG))


@pytest.mark.parametrize("release, ver, want", [
    ("10", "10.0.22631", "Windows 11 (10.0.22631)"),  # Python 3.11 bao Windows 11 la "10"
    ("10", "10.0.19045", "Windows 10 (10.0.19045)"),
    ("11", "10.0.26100", "Windows 11 (10.0.26100)"),
])
def test_os_name_windows(monkeypatch, release, ver, want):
    import platform

    monkeypatch.setattr(platform, "system", lambda: "Windows")
    monkeypatch.setattr(platform, "release", lambda: release)
    monkeypatch.setattr(platform, "version", lambda: ver)
    assert A.os_name() == want


# ==========================================================================
#  keyring dự phòng (bản .exe không thấy backend → Credential Manager)
# ==========================================================================


class _FakeKeyringModule:
    def __init__(self, backend):
        self.backend = backend

    def get_keyring(self):
        return self.backend

    def set_keyring(self, b):
        self.backend = b


def test_ensure_backend_windows_fail_uses_credential_manager():
    from keyring.backends import fail

    kr = _FakeKeyringModule(fail.Keyring())
    assert S.ensure_backend(kr, platform="win32") == "keyring.backends.Windows.WinVaultKeyring"
    assert type(kr.backend).__name__ == "WinVaultKeyring"


def test_ensure_backend_leaves_other_backends(memory_keyring):
    from keyring.backends import fail

    kr = _FakeKeyringModule(memory_keyring)
    assert S.ensure_backend(kr, platform="win32") == "conftest.MemoryKeyring"  # keyring gia cua test: giu nguyen
    assert kr.backend is memory_keyring
    kr = _FakeKeyringModule(fail.Keyring())
    assert S.ensure_backend(kr, platform="linux") == "keyring.backends.fail.Keyring"  # chi Windows moi doi
    # SecretStore van dung keyring gia cua test (khong cham Credential Manager that)
    S.SecretStore().set("TmdbApiKey", "demo-tmdb-0123")
    assert memory_keyring.store[("MuxStudio", "TmdbApiKey")] == "demo-tmdb-0123"


# ==========================================================================
#  tự kiểm tra (--kiem-tra)
# ==========================================================================


def test_report_pass_fail_lines():
    ok = [A.CheckItem("A", "ok", "x"), A.CheckItem("B", "warn", "y")]
    t = A.self_check_report(ok)
    assert t.startswith(f"{A.APP_TITLE} — tự kiểm tra\n")
    assert "[OK]   A: x" in t and "[!]    B: y" in t
    assert "KẾT QUẢ: ĐẠT — chương trình chạy được (1 mục [!] nên xem)." in t
    t = A.self_check_report([*ok, A.CheckItem("C", "fail", "z")])
    assert "[LỖI]  C: z" in t and "KẾT QUẢ: KHÔNG ĐẠT — 1 mục [LỖI], 1 mục [!]" in t


def test_run_check_turns_exception_into_fail_or_warn():
    items: list[A.CheckItem] = []
    A._run_check(items, "hỏng", lambda: 1 / 0)
    A._run_check(items, "phụ", lambda: [][1], critical=False)
    A._run_check(items, "cảnh báo", lambda: ("warn", "nên xem"))
    A._run_check(items, "đạt", lambda: "chi tiết")
    assert [(i.status, i.detail.split(":")[0]) for i in items] == [
        ("fail", "ZeroDivisionError"), ("warn", "IndexError"), ("warn", "nên xem"), ("ok", "chi tiết")]


def test_self_check_without_window(tmp_path, config_dir):
    out = tmp_path / "[báo cáo] kiểm tra.txt"
    rc = A.self_check(out, with_window=False)
    text = out.read_text(encoding="utf-8")
    items = {ln.split(":")[0][7:]: ln[:6].strip() for ln in text.splitlines() if ln.startswith("[")}
    for name in ("Thư viện lõi (core)", "Thùng rác", "Kết nối HTTPS", "Thư mục cấu hình", "Chạy công cụ ngoài"):
        assert items[name] == "[OK]", text
    assert items["Windows Credential Manager (keyring)"] in ("[OK]", "[!]")  # keyring gia cua test
    assert "conftest.MemoryKeyring" in text
    assert items["File icon"] == "[OK]"
    assert ("KẾT QUẢ: ĐẠT" in text) == (rc == 0)
    assert rc == (0 if W.ctk is not None else 1)  # khong co tkinter -> muc "Giao dien" hong
    assert not list(config_dir.glob("kiem_tra_*"))  # file thu ghi da xoa


def test_main_dispatches_self_check(monkeypatch):
    seen = []
    monkeypatch.setattr(A, "self_check", lambda out=None, with_window=True: seen.append(out) or 7)
    assert A.main(["--kiem-tra", "x.txt"]) == 7
    assert A.main(["--self-check"]) == 7
    assert seen == ["x.txt", None]


def test_startup_error_file_has_version(config_dir):
    p = A.write_startup_error("Traceback (most recent call last):\nRuntimeError: thử")
    t = Path(p).read_text(encoding="utf-8")
    assert Path(p) == config_dir / "loi_khoi_dong.txt"
    assert t.startswith(A.build_info()) and "RuntimeError: thử" in t


def test_entry_point_script(tmp_path, config_dir):
    """``MuxStudio.pyw`` (điểm vào của PyInstaller) chạy được từ thư mục KHÁC, nhận ``--kiem-tra``."""
    src = (ROOT / "MuxStudio.pyw").read_text(encoding="utf-8")
    tree = ast.parse(src)
    assert any(isinstance(n, ast.ImportFrom) and n.module == "gui.app" and n.names[0].name == "main"
               for n in ast.walk(tree))
    assert 'if __name__ == "__main__":' in src
    out = tmp_path / "kt.txt"
    env = {**os.environ, "MUXSTUDIO_CONFIG_DIR": str(config_dir),
           "PYTHON_KEYRING_BACKEND": "keyring.backends.null.Keyring"}  # khong cham Credential Manager that
    r = subprocess.run([sys.executable, str(ROOT / "MuxStudio.pyw"), "--kiem-tra", str(out)], cwd=tmp_path, env=env,
                       capture_output=True, timeout=180)
    text = out.read_text(encoding="utf-8")
    assert text.startswith(f"{A.APP_TITLE} — tự kiểm tra"), r.stderr
    assert "keyring.backends.null.Keyring" in text
    assert ("KẾT QUẢ: ĐẠT" in text) == (r.returncode == 0)
    if HAS_TK:
        assert r.returncode == 0, text
        assert re.search(r"\[OK\]   Cửa sổ chính: dựng được 5 thẻ", text)


@pytest.mark.skipif(not HAS_TK, reason="không có tkinter / customtkinter / màn hình")
def test_self_check_builds_window(tmp_path, config_dir):
    out = tmp_path / "kt.txt"
    assert A.self_check(out) == 0, out.read_text(encoding="utf-8")
    text = out.read_text(encoding="utf-8")
    assert f'tiêu đề "{A.APP_TITLE}"' in text and "[OK]   Icon cửa sổ: đã đặt" in text
    assert os.environ["MUXSTUDIO_CONFIG_DIR"] == str(config_dir)  # tra lai cau hinh tam cua test
    assert not (config_dir / "config.json").exists()  # khong ghi cau hinh


# ==========================================================================
#  icon
# ==========================================================================


def _ico_entries(data: bytes) -> list[tuple[int, int, bytes]]:
    res, typ, n = struct.unpack("<HHH", data[:6])
    assert (res, typ) == (0, 1)
    out = []
    for i in range(n):
        w, h, _cc, _r, planes, bpp, size, off = struct.unpack("<BBBBHHII", data[6 + 16 * i:22 + 16 * i])
        assert (planes, bpp) == (1, 32) and w == h
        out.append((w or 256, bpp, data[off:off + size]))
    return out


def _png_pixels(blob: bytes) -> tuple[int, int, list[tuple[int, int, int, int]]]:
    assert blob[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, w, h = 8, b"", 0, 0
    while pos < len(blob):
        ln, tag = struct.unpack(">I4s", blob[pos:pos + 8])
        body = blob[pos + 8:pos + 8 + ln]
        assert zlib.crc32(tag + body) & 0xFFFFFFFF == struct.unpack(">I", blob[pos + 8 + ln:pos + 12 + ln])[0]
        if tag == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", body[:10])
            assert (depth, ctype) == (8, 6)  # RGBA 8 bit
        elif tag == b"IDAT":
            idat += body
        pos += 12 + ln
    raw = zlib.decompress(idat)
    px = []
    for j in range(h):
        row = raw[j * (w * 4 + 1):(j + 1) * (w * 4 + 1)]
        assert row[0] == 0  # khong loc
        px += [tuple(row[1 + 4 * i:5 + 4 * i]) for i in range(w)]
    return w, h, px


def _bmp_pixels(blob: bytes, size: int) -> list[tuple[int, int, int, int]]:
    hdr = struct.unpack("<IiiHHIIiiII", blob[:40])
    assert hdr[:5] == (40, size, size * 2, 1, 32)
    xor = blob[40:40 + size * size * 4]
    rows = [xor[r * size * 4:(r + 1) * size * 4] for r in range(size)][::-1]  # BMP luu tu duoi len
    stride = ((size + 31) // 32) * 4
    assert len(blob) == 40 + size * size * 4 + stride * size and hdr[6] == len(blob) - 40
    return [(r[4 * i + 2], r[4 * i + 1], r[4 * i], r[4 * i + 3]) for r in rows for i in range(size)]


def test_icon_files_match_generator():
    """``assets/muxstudio.ico``: đủ 16–256 px (BMP 32 bit, 256 = PNG) và ĐÚNG ảnh do ``tools/make_icon.py`` vẽ."""
    ents = _ico_entries((ROOT / "assets" / "muxstudio.ico").read_bytes())
    assert sorted(s for s, _b, _d in ents) == list(make_icon.SIZES)
    for size, _bpp, blob in ents:
        flat = [px for row in make_icon.render(size) for px in row]
        if size == 256:
            w, h, px = _png_pixels(blob)
            assert (w, h) == (256, 256)
        else:
            px = _bmp_pixels(blob, size)
        assert px == flat, f"icon {size} px khác bản vẽ — chạy lại: python tools/make_icon.py"
    w, h, px = _png_pixels((ROOT / "assets" / "muxstudio.png").read_bytes())
    assert (w, h) == (256, 256) and px == [p for row in make_icon.render(256) for p in row]
    assert px[0][3] == 0 and px[128 * 256 + 128][3] == 255  # goc trong suot, giua dac


def test_icon_small_sizes_use_simple_design():
    assert len(make_icon.design(16)[2]) == 1 and len(make_icon.design(48)[2]) == 2


# ==========================================================================
#  MuxStudio.spec, build.bat, requirements-build.txt
# ==========================================================================


def test_spec_file():
    t = (ROOT / "MuxStudio.spec").read_text(encoding="utf-8")
    compile(t, "MuxStudio.spec", "exec")
    for need in ('MuxStudio.pyw', 'console=False', 'upx=False', 'collect_data_files("customtkinter")',
                 'copy_metadata("keyring")', 'collect_submodules("keyring.backends")', '"keyring.backends.Windows"',
                 '"win32ctypes.core"', 'name="MuxStudio"', 'exclude_binaries=True', 'COLLECT(',
                 '"assets", "muxstudio.ico"', '"pytest"'):
        assert need in t, need
    assert "onefile" not in t.lower().replace("--onefile", "")  # chi nhac --onefile trong chu thich "KHONG dung"
    for bad in ("tests", "reference"):
        assert not re.search(rf"\(\s*os\.path\.join\(ROOT,\s*\"{bad}\"", t)  # khong dong goi thu muc test / .bat goc


def test_build_bat():
    b = (ROOT / "build.bat").read_bytes()
    assert b.count(b"\n") == b.count(b"\r\n") > 20  # CRLF
    t = b.decode("ascii")  # chu khong dau (cmd.exe)
    for need in ('"%VPY%" -m PyInstaller --noconfirm --clean MuxStudio.spec', "requirements-build.txt",
                 'start "" /wait "%EXE%" --kiem-tra "%BAOCAO%"', "dist\\MuxStudio\\MuxStudio.exe", ".venv-build",
                 "sys.version_info >= (3, 11)", ":loi_kiemtra", ":loi_dangmo", "pause"):
        assert need in t, need
    assert "--onefile" not in t
    labels = set(re.findall(r"^:(\w+)", t, re.M))
    assert set(re.findall(r"goto :(\w+)", t)) <= labels  # moi goto deu co nhan


def test_self_check_bat_next_to_exe():
    """``kiem_tra_MuxStudio.bat`` (đặt cạnh MuxStudio.exe): bấm đúp → tự kiểm tra + mở báo cáo bằng Notepad."""
    b = (ROOT / "assets" / "kiem_tra_MuxStudio.bat").read_bytes()
    assert b.count(b"\n") == b.count(b"\r\n") > 10
    t = b.decode("ascii")
    assert 'start "" /wait "%~dp0MuxStudio.exe" --kiem-tra "%BAOCAO%"' in t and 'notepad "%BAOCAO%"' in t
    assert set(re.findall(r"goto :(\w+)", t)) <= set(re.findall(r"^:(\w+)", t, re.M))
    spec = (ROOT / "MuxStudio.spec").read_text(encoding="utf-8")
    assert '"kiem_tra_MuxStudio.bat"),\n             os.path.join(DISTPATH, "MuxStudio"' in spec


def test_requirements_files():
    build = (ROOT / "requirements-build.txt").read_text().split()
    assert "-r" in build and "requirements.txt" in build and any(x.startswith("pyinstaller") for x in build)
    app = (ROOT / "requirements.txt").read_text().lower()
    assert "pyinstaller" not in app and "pytest" not in app  # .exe chi chua thu vien can chay
    # 1.3.0: Pillow LA thu vien can chay (anh CAPTCHA cua Titulky.com la JPEG, Tk khong tu doc duoc) -> phai co mat va
    # khong con nam trong danh sach loai tru cua ban dong goi
    spec = (ROOT / "MuxStudio.spec").read_text(encoding="utf-8")
    assert "pillow" in app and '"PIL"' not in spec.split("excludes = ")[1].split("]")[0]


def test_gitignore_build_outputs():
    g = (ROOT / ".gitignore").read_text().split()
    for need in ("build/", "dist/", ".venv-build/", "kiem_tra_exe.txt"):
        assert need in g


def test_bat_files_remove_stale_files_of_older_versions():
    """Người dùng chép đè mã mới lên thư mục cũ: file đã xoá khỏi repo (kho srt_pool, 1.2.0) vẫn nằm lại và làm hỏng
    test (đã xảy ra: 4 test hỏng trên Windows) → run_tests.bat / build.bat tự dọn. Các file đó không được có trong repo."""
    for name in ("run_tests.bat", "build.bat"):
        raw = (ROOT / name).read_bytes()
        assert b'for %%F in ("core\\pool.py" "tests\\test_pool.py") do if exist "%%~F" (' in raw, name
        assert b"\n" not in raw.replace(b"\r\n", b"") and raw.isascii(), name  # CRLF + ASCII
    assert raw.index(b"core\\pool.py") < raw.index(b"[1/4]")
    assert not (ROOT / "core" / "pool.py").exists() and not (ROOT / "tests" / "test_pool.py").exists()
