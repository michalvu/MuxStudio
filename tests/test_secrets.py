"""Bí mật: keyring, che khi hiển thị, không ra log."""
from __future__ import annotations

import pytest

from core import secrets as S


def test_store_roundtrip(memory_keyring):
    st = S.SecretStore()
    assert st.get("TmdbApiKey") == ""
    st.set("TmdbApiKey", "  0123456789abcdef0123456789abcdef  ")
    assert st.get("TmdbApiKey") == "0123456789abcdef0123456789abcdef"
    assert memory_keyring.store[("MuxStudio", "TmdbApiKey")] == "0123456789abcdef0123456789abcdef"
    assert st.has("TmdbApiKey")
    st.set("TmdbApiKey", "")  # rong = xoa
    assert not st.has("TmdbApiKey")
    st.delete("TmdbApiKey")  # xoa lan nua khong loi


def test_only_known_secret_names():
    with pytest.raises(KeyError):
        S.SecretStore().set("MinVerifyVotes", "20")


def test_all_values(memory_keyring):
    st = S.SecretStore()
    st.set("OsUsername", "nguoidung")
    st.set("OsPassword", "matkhau")
    assert st.all_values() == {"OsUsername": "nguoidung", "OsPassword": "matkhau"}


@pytest.mark.parametrize("v,m", [("", ""), ("abc", "•••"), ("matkhau1", "••••••••"),
                                 ("demo-key-0000111122223333444455556666", "demo…6666")])
def test_mask(v, m):
    assert S.mask(v) == m


def test_redactor_hides_every_secret_in_logs():
    key = "demo-key-aaaabbbbccccddddeeee9999"
    r = S.Redactor([key, "matkhau", "ab"])
    line = f"GET /x?api_key={key} user=matkhau ab"
    out = r.redact(line)
    assert key not in out and "matkhau" not in out
    assert "demo…9999" in out
    assert out.endswith(" ab")  # qua ngan (< 4) -> khong thay (tranh lam hong log)
