"""Bí mật (API key, tài khoản) — lưu trong Windows Credential Manager qua ``keyring``.

Không bao giờ nằm trong ``config.json``, không bao giờ in ra log (``Redactor`` thay bằng bản
che), không có trong mã nguồn, che khi hiển thị (``mask``).
"""
from __future__ import annotations

import sys
from typing import Any, Iterable

SERVICE = "MuxStudio"

#: 7 bí mật (docs/SPEC.md mục 2.4)
SECRET_KEYS: tuple[str, ...] = (
    "SubSourceApiKey",
    "TmdbApiKey",
    "TvdbApiKey",
    "TvdbPin",
    "OsApiKey",
    "OsUsername",
    "OsPassword",
)


class SecretStoreError(RuntimeError):
    """Không đọc/ghi được Windows Credential Manager."""


_backend_checked = False


def ensure_backend(kr: Any = None, platform: str | None = None) -> str:
    """Bản ``.exe`` (PyInstaller) có thể không thấy backend của ``keyring`` (keyring tìm backend qua "entry points"
    của gói cài đặt) → keyring rơi về backend ``fail`` và MỌI lần đọc/ghi bí mật đều lỗi. Trên Windows, gặp ``fail``
    thì đặt thẳng ``WinVaultKeyring`` (= Windows Credential Manager). Không đụng tới backend khác (vd keyring giả
    trong test). Trả tên backend đang dùng (``module.Lớp``)."""
    if kr is None:
        kr = _keyring_module()
    plat = sys.platform if platform is None else platform
    cur = kr.get_keyring()
    if plat.startswith("win") and type(cur).__module__.endswith("backends.fail"):
        from keyring.backends import Windows  # noqa: PLC0415

        kr.set_keyring(Windows.WinVaultKeyring())
        cur = kr.get_keyring()
    return f"{type(cur).__module__}.{type(cur).__name__}"


def _keyring_module():
    try:
        import keyring  # noqa: PLC0415 (nap muon: test thay backend)
    except Exception as e:  # pragma: no cover - chi xay ra khi thieu thu vien
        raise SecretStoreError(f"Thiếu thư viện keyring: {e}") from e
    return keyring


def _keyring():
    global _backend_checked
    kr = _keyring_module()
    if not _backend_checked:
        _backend_checked = True
        try:
            ensure_backend(kr)
        except Exception:  # noqa: BLE001 - de loi that hien ra o lan doc/ghi
            pass
    return kr


class SecretStore:
    """Đọc/ghi bí mật. Mỗi bí mật là một mục ``MuxStudio / <tên>`` trong Credential Manager."""

    def __init__(self, service: str = SERVICE):
        self.service = service

    @staticmethod
    def _check(name: str) -> None:
        if name not in SECRET_KEYS:
            raise KeyError(f"Không phải bí mật: {name}")

    def get(self, name: str) -> str:
        self._check(name)
        try:
            v = _keyring().get_password(self.service, name)
        except Exception as e:
            raise SecretStoreError(f"Không đọc được {name} từ Windows Credential Manager: {e}") from e
        return v or ""

    def set(self, name: str, value: str) -> None:
        """Ghi bí mật. Giá trị rỗng = xoá."""
        self._check(name)
        value = (value or "").strip()
        if not value:
            self.delete(name)
            return
        try:
            _keyring().set_password(self.service, name, value)
        except Exception as e:
            raise SecretStoreError(f"Không ghi được {name} vào Windows Credential Manager: {e}") from e

    def delete(self, name: str) -> None:
        self._check(name)
        kr = _keyring()
        try:
            kr.delete_password(self.service, name)
        except Exception as e:  # khong co muc nay -> coi nhu da xoa
            if type(e).__name__ != "PasswordDeleteError":
                raise SecretStoreError(f"Không xoá được {name}: {e}") from e

    def has(self, name: str) -> bool:
        return bool(self.get(name))

    def all_values(self) -> dict[str, str]:
        out: dict[str, str] = {}
        for k in SECRET_KEYS:
            try:
                v = self.get(k)
            except SecretStoreError:
                v = ""
            if v:
                out[k] = v
        return out


def mask(value: str | None) -> str:
    """Bản che để hiển thị: ``sk_5…a69a`` (dài > 8 ký tự), ``••••••`` (ngắn)."""
    if not value:
        return ""
    if len(value) <= 8:
        return "•" * len(value)
    return f"{value[:4]}…{value[-4:]}"


class Redactor:
    """Thay mọi bí mật đang có trong một dòng log bằng bản che. Dùng cho khung log và file log."""

    def __init__(self, values: Iterable[str] = ()):
        self._vals: list[str] = []
        self.update(values)

    def update(self, values: Iterable[str]) -> None:
        vals = {v for v in values if v and len(v) >= 4}
        self._vals = sorted(vals, key=len, reverse=True)

    def add(self, value: str | None) -> None:
        """Thêm một bí mật mới biết lúc chạy (vd token đăng nhập)."""
        if value:
            self.update([*self._vals, value])

    def redact(self, text: str) -> str:
        for v in self._vals:
            if v in text:
                text = text.replace(v, mask(v) if len(v) > 8 else "•••")
        return text


#: Dùng chung trong chương trình
store = SecretStore()
