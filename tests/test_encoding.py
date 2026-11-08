"""§6.3 — Chuẩn hoá phụ đề về UTF-8 không BOM; §6.2 — --sub-charset."""
from __future__ import annotations

import unicodedata

import pytest

from core import encoding as enc

VI_CP1258 = b"Xin ch\xe0o Vi\xea\xf2t Nam"  # "Xin chào Việt Nam" (ê + dau nang to hop)
CS_CP1250 = "Příliš žluťoučký kůň".encode("cp1250")


@pytest.mark.parametrize("data,kind", [
    (b"", "empty"),
    (b"\xef\xbb\xbfabc", "utf8bom"),
    (b"\xff\xfea\x00", "utf16le"),
    (b"\xfe\xff\x00a", "utf16be"),
    ("Tiếng Việt".encode("utf-8"), "utf8"),
    (VI_CP1258, "legacy"),
    (CS_CP1250, "legacy"),
])
def test_detect_kind(data, kind):
    assert enc.detect_kind_bytes(data) == kind


def test_detect_kind_missing_file(tmp_path):
    assert enc.detect_kind(tmp_path / "khong-co.srt") == "error"


@pytest.mark.parametrize("lang,cp", [("vi", 1258), (".vi", 1258), ("cs", 1250), ("cz", 1250), ("ja", 932), ("ko", 949),
                                     ("zh", 936), ("th", 874), ("ru", 1251), ("en", 1252), ("", 1252), ("xx", 1252)])
def test_legacy_codepage(lang, cp):
    assert enc.legacy_codepage(lang) == cp


def test_to_utf8_vietnamese_cp1258_new_file_source_untouched(movie_dir):
    src = movie_dir / "Phim.vi.srt"
    src.write_bytes(b"1\r\n00:00:01,000 --> 00:00:02,000\r\n" + VI_CP1258 + b"\r\n")
    before = src.read_bytes()
    dst = movie_dir / "tmp_copy.srt"
    assert enc.to_utf8(src, dst, "vi") == "cp1258"
    assert src.read_bytes() == before  # TD-12: khong ghi de nguon
    raw = dst.read_bytes()
    assert not raw.startswith(b"\xef\xbb\xbf")
    txt = raw.decode("utf-8")
    assert unicodedata.normalize("NFC", txt).endswith("Xin chào Việt Nam\r\n")


def test_to_utf8_czech_cp1250(tmp_path):
    src = tmp_path / "a.cs.srt"
    src.write_bytes(CS_CP1250)
    dst = tmp_path / "b.srt"
    assert enc.to_utf8(src, dst, "cs") == "cp1250"
    assert dst.read_text(encoding="utf-8") == "Příliš žluťoučký kůň"


@pytest.mark.parametrize("bom,codec,kind", [(b"\xef\xbb\xbf", "utf-8", "utf8bom"), (b"\xff\xfe", "utf-16-le", "utf16le"),
                                            (b"\xfe\xff", "utf-16-be", "utf16be")])
def test_to_utf8_bom_variants(tmp_path, bom, codec, kind):
    src = tmp_path / "x.srt"
    src.write_bytes(bom + "Ahoj Tiếng Việt".encode(codec))
    dst = tmp_path / "y.srt"
    assert enc.to_utf8(src, dst, "vi") == kind
    assert dst.read_bytes() == "Ahoj Tiếng Việt".encode("utf-8")


def test_to_utf8_already_utf8_copies(tmp_path):
    src = tmp_path / "x.srt"
    src.write_bytes("ok ✓".encode("utf-8"))
    dst = tmp_path / "y.srt"
    assert enc.to_utf8(src, dst) == "utf8"
    assert dst.read_bytes() == src.read_bytes()


def test_cp1252_undefined_bytes_pass_through_like_dotnet():
    text, how = enc.decode_bytes(b"A\x81B\x9dC\xe9", "en")
    assert how == "cp1252"
    assert text == "A\x81B\x9dCé"


def test_read_text_emulates_readalltext(tmp_path):
    p = tmp_path / "x.srt"
    p.write_bytes(VI_CP1258)
    assert "�" in enc.read_text(p)  # khong phai UTF-8 -> ky tu hong (de suc khoe phat hien)
    p.write_bytes(b"\xef\xbb\xbfabc")
    assert enc.read_text(p) == "abc"


@pytest.mark.parametrize("data,lang,cs", [
    ("ok".encode("utf-8"), "vi", ""),
    (b"\xef\xbb\xbfok", "vi", ""),
    (b"\xff\xfeo\x00k\x00", "vi", "UTF-16LE"),
    (b"\xfe\xff\x00o\x00k", "vi", "UTF-16BE"),
    (VI_CP1258, "vi", "windows-1258"),
    (CS_CP1250, "cs", "windows-1250"),
    (CS_CP1250, "cz", "windows-1250"),
    ("日本".encode("shift_jis"), "ja", "shift_jis"),
    (b"\xe9t\xe9", "fr", "windows-1252"),
    (b"\xe9t\xe9", "", "windows-1252"),
])
def test_mkvmerge_charset(tmp_path, data, lang, cs):
    p = tmp_path / "s.srt"
    p.write_bytes(data)
    assert enc.mkvmerge_charset(p, lang) == cs
