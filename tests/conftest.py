"""Tiện ích chung cho test: keyring giả trong bộ nhớ, thư mục cấu hình tạm, thư mục tên có [ ]."""
from __future__ import annotations

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


@pytest.fixture
def movie_dir(tmp_path):
    """Thư mục phim có ký tự [ ] trong tên (§6.1)."""
    d = tmp_path / "[YTS.MX] Phim Thử (2020)"
    d.mkdir()
    return d


REFERENCE = ROOT / "reference"
