"""Tiện ích chung cho test: keyring giả trong bộ nhớ, thư mục cấu hình tạm, thư mục tên có [ ]."""
from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import keyring  # noqa: E402
from keyring.backend import KeyringBackend  # noqa: E402
from keyring.errors import PasswordDeleteError  # noqa: E402


class MemoryKeyring(KeyringBackend):
    """Keyring giả: test KHÔNG BAO GIỜ chạm vào Windows Credential Manager thật."""

    priority = 1  # type: ignore[assignment]

    def __init__(self):
        super().__init__()
        self.store: dict[tuple[str, str], str] = {}

    def get_password(self, service, username):
        return self.store.get((service, username))

    def set_password(self, service, username, password):
        self.store[(service, username)] = password

    def delete_password(self, service, username):
        if (service, username) not in self.store:
            raise PasswordDeleteError("không có")
        del self.store[(service, username)]


@pytest.fixture(autouse=True)
def memory_keyring():
    kr = MemoryKeyring()
    old = keyring.get_keyring()
    keyring.set_keyring(kr)
    yield kr
    keyring.set_keyring(old)


@pytest.fixture(autouse=True)
def config_dir(tmp_path, monkeypatch):
    d = tmp_path / "[cfg] MuxStudio"
    monkeypatch.setenv("MUXSTUDIO_CONFIG_DIR", str(d))
    return d


@pytest.fixture(autouse=True)
def vietnamese_ui():
    """Mọi test chạy với giao diện tiếng Việt (ngôn ngữ gốc); test nào đổi sang English thì tự trả lại ở đây."""
    from core import i18n

    i18n.set_language("vi")
    yield
    i18n.set_language("vi")


@pytest.fixture
def movie_dir(tmp_path):
    """Thư mục phim có ký tự [ ] trong tên (§6.1)."""
    d = tmp_path / "[YTS.MX] Phim Thử (2020)"
    d.mkdir()
    return d


REFERENCE = ROOT / "reference"


# ---------------------------------------------------------------------------
# Công cụ phát triển (bản 1.2.0): MUXSTUDIO_I18N_RECORD=<file> → gom MỌI câu hiển thị (chữ giao diện + dòng nhật ký của
# lõi) trong lúc chạy test vào file JSON; ``python tools/i18n_residue.py <file>`` liệt kê câu dịch sang tiếng Anh còn
# sót chữ tiếng Việt. Không đặt biến môi trường thì không làm gì.
# ---------------------------------------------------------------------------
_REC = os.environ.get("MUXSTUDIO_I18N_RECORD")
if _REC:
    import atexit
    import json as _json

    from core import i18n as _i18n
    from core import tools as _tools

    _i18n.recorded = set()
    _orig_log = _tools.Job.log

    def _rec_log(self, msg, level="info"):  # noqa: ANN001
        _i18n.recorded.add(str(msg))
        return _orig_log(self, msg, level)

    _tools.Job.log = _rec_log

    def _dump() -> None:
        with open(_REC, "w", encoding="utf-8") as fh:
            _json.dump(sorted(_i18n.recorded), fh, ensure_ascii=False, indent=0)

    atexit.register(_dump)
