"""Plugin pytest GIẢ LẬP hai hành vi của Windows (tiếng Séc) trên Linux:

1. Ghi file ở chế độ chữ (open / Path.write_text, newline=None): "\n" -> "\r\n" (như os.linesep của Windows).
2. Tiến trình Python con ghi stdout/stderr bằng bảng mã ANSI cp1250 (Windows tiếng Séc), lỗi = strict.

Dùng (Linux): ``PYTHONPATH=tools xvfb-run -a python -m pytest -q -p no:cacheprovider -p winemu``.
Tái hiện được 12/15 lỗi của ``run_tests.bat`` trên Windows ở lần chạy đầu (2026-11-09): test ghi SRT có sẵn ``\r\n``
bằng ``write_text`` (thành ``\r\r\n``), alass giả in chữ có dấu ra ống dẫn cp1250. Mọi test phải qua cả khi bật plugin.
"""
import builtins
import io
import os

_real_open = io.open


def _win_open(file, mode="r", buffering=-1, encoding=None, errors=None, newline=None, closefd=True, opener=None):
    if "b" not in mode and any(c in mode for c in "wax+") and newline is None:
        newline = "\r\n"
    return _real_open(file, mode, buffering, encoding, errors, newline, closefd, opener)


def pytest_configure(config):
    builtins.open = _win_open
    io.open = _win_open
    os.environ["PYTHONIOENCODING"] = "cp1250"  # chi anh huong tien trinh con
    os.environ.pop("PYTHONUTF8", None)
