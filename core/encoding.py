"""Mã hoá ký tự của phụ đề.

Thay ``Get-SubEncodingKind`` (SubPrep = MuxTool, giống hệt), ``Get-LegacyCodePage`` +
``ConvertTo-Utf8Sub`` (SubPrep), ``Get-SubCharsetArg`` (MuxTool).

Khác .bat: ``to_utf8`` ghi ra file MỚI, không ghi đè file nguồn (TĐ-12).
"""
from __future__ import annotations

import codecs
import os
import shutil
from typing import Literal

from . import naming

Kind = Literal["empty", "utf8bom", "utf16le", "utf16be", "utf8", "legacy", "error"]


def _c1_passthrough(err: UnicodeError) -> tuple[str, int]:
    """Byte không có trong bảng mã cũ → ký tự cùng mã (như .NET làm với cp1252: 0x81 → U+0081)."""
    if isinstance(err, UnicodeDecodeError):
        bad = err.object[err.start:err.end]
        return "".join(chr(b) for b in bad), err.end
    raise err


codecs.register_error("muxstudio_c1", _c1_passthrough)


def detect_kind_bytes(data: bytes) -> Kind:
    """Kiểu mã hoá (``Get-SubEncodingKind``): BOM UTF-8/UTF-16 → UTF-8 nghiêm ngặt → ``legacy``."""
    if len(data) == 0:
        return "empty"
    if data[:3] == b"\xef\xbb\xbf":
        return "utf8bom"
    if data[:2] == b"\xff\xfe":
        return "utf16le"
    if data[:2] == b"\xfe\xff":
        return "utf16be"
    try:
        data.decode("utf-8", errors="strict")
        return "utf8"
    except UnicodeDecodeError:
        return "legacy"


def detect_kind(path: str | os.PathLike[str]) -> Kind:
    try:
        with open(path, "rb") as fh:
            data = fh.read()
    except OSError:
        return "error"
    return detect_kind_bytes(data)


def legacy_codepage(lang: str | None) -> int:
    """Bảng mã cũ theo ngôn ngữ (``Get-LegacyCodePage``): vi 1258, cs 1250, ja 932… mặc định 1252."""
    code = naming.lang_from_word((lang or "").lstrip(".")) or (lang or "").lstrip(".").lower()
    lg = naming.get_lang(code)
    return lg.codepage if lg else 1252


def decode_bytes(data: bytes, lang: str | None = None) -> tuple[str, str]:
    """Giải mã nội dung phụ đề. Trả (văn bản, kiểu ban đầu) — kiểu: ``utf8``, ``utf8bom``, ``utf16le``,
    ``utf16be``, ``cp1258``… (``legacy`` được giải bằng bảng mã cũ theo ngôn ngữ)."""
    kind = detect_kind_bytes(data)
    if kind == "empty":
        return "", "empty"
    if kind == "utf8bom":
        return data[3:].decode("utf-8", errors="replace"), kind
    if kind == "utf16le":
        return data[2:].decode("utf-16-le", errors="replace"), kind
    if kind == "utf16be":
        return data[2:].decode("utf-16-be", errors="replace"), kind
    if kind == "utf8":
        return data.decode("utf-8"), kind
    cp = legacy_codepage(lang)
    try:
        return data.decode(f"cp{cp}", errors="muxstudio_c1"), f"cp{cp}"
    except LookupError:
        return data.decode("cp1252", errors="muxstudio_c1"), "cp1252"


def read_text(path: str | os.PathLike[str]) -> str:
    """Đọc văn bản như ``[IO.File]::ReadAllText``: theo BOM nếu có, còn lại UTF-8 (byte hỏng → U+FFFD).

    Dùng để đọc mốc thời gian (chỉ là ký tự ASCII) và kiểm tra ký tự hỏng.
    """
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:3] == b"\xef\xbb\xbf":
        return data[3:].decode("utf-8", errors="replace")
    if data[:4] in (b"\xff\xfe\x00\x00", b"\x00\x00\xfe\xff"):
        return data[4:].decode("utf-32-le" if data[0] == 0xFF else "utf-32-be", errors="replace")
    if data[:2] == b"\xff\xfe":
        return data[2:].decode("utf-16-le", errors="replace")
    if data[:2] == b"\xfe\xff":
        return data[2:].decode("utf-16-be", errors="replace")
    return data.decode("utf-8", errors="replace")


def write_text_utf8(path: str | os.PathLike[str], text: str) -> None:
    """Ghi UTF-8 KHÔNG BOM, giữ nguyên ký tự xuống dòng có trong ``text``."""
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


def to_utf8(src: str | os.PathLike[str], dst: str | os.PathLike[str], lang: str | None = None) -> str:
    """Chuyển phụ đề về UTF-8 không BOM, GHI RA ``dst`` (``ConvertTo-Utf8Sub``, không ghi đè nguồn).

    Trả kiểu ban đầu: ``utf8`` (đã đúng, chỉ chép), ``empty``, ``error`` (không làm gì),
    hoặc ``utf8bom``/``utf16le``/``utf16be``/``cp1258``… (đã chuyển).
    """
    try:
        with open(src, "rb") as fh:
            data = fh.read()
    except OSError:
        return "error"
    kind = detect_kind_bytes(data)
    same = os.path.abspath(os.fspath(src)) == os.path.abspath(os.fspath(dst))
    if kind in ("utf8", "empty"):
        if not same:
            shutil.copyfile(src, dst)
        return kind
    text, how = decode_bytes(data, lang)
    try:
        write_text_utf8(dst, text)
    except OSError:
        return "error"
    return how


def mkvmerge_charset(path: str | os.PathLike[str], lang: str | None) -> str:
    """Tên bảng mã cho ``mkvmerge --sub-charset 0:<...>``; '' nếu file đã là UTF-8 (``Get-SubCharsetArg``)."""
    kind = detect_kind(path)
    if kind in ("utf8", "utf8bom", "empty", "error"):
        return ""
    if kind == "utf16le":
        return "UTF-16LE"
    if kind == "utf16be":
        return "UTF-16BE"
    code = naming.lang_from_word((lang or "").lstrip(".")) or (lang or "").lower()
    lg = naming.get_lang(code)
    return lg.charset if lg else "windows-1252"
