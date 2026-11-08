"""Đọc/ghi phụ đề và mốc thời gian.

Thay ``Format-SrtStamp`` (cả 3 file — đã SỬA lỗi giờ, TĐ-01), ``ConvertFrom-Stamp``,
``Read-SrtBlocks``, ``Write-SrtBlocks``, ``Get-MaxShift`` (SubPrep), ``Get-CueTimes``
(bản SubFetch: mẫu ASS rộng hơn), ``Get-PreviewTimes`` (SubFetch).

Định dạng: SRT, ASS/SSA (chỉ đọc mốc ``Dialogue:``), WebVTT (đọc mốc, kể cả dạng ``mm:ss.ttt``).
Phụ đề ASS giữ nguyên là ASS (Q7): module này không bao giờ chuyển ASS sang SRT.
"""
from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Iterable, Sequence

from . import encoding
from .timing import to_double

#: Xuống dòng khi GHI file (StringBuilder.AppendLine trên Windows = CRLF, như .bat)
NEWLINE = "\r\n"

# Moc SRT (va VTT co gio): "00:01:02,345 -->" hoac "00:01:02.345 -->", 2-3 chu so le
_SRT_CUE = re.compile(r"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->")
# ASS/SSA: "Dialogue: 0,0:01:02.34," va "Dialogue: Marked=0,0:01:02.34," (mau cua SubFetch)
_ASS_CUE = re.compile(r"(?im)^Dialogue:\s*[^,]*,(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),")
# WebVTT: gio co the bo qua: "01:02.345 -->" hoac "00:01:02.345 -->"
_VTT_CUE = re.compile(r"(?m)(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})\s*-->")

# Khoi SRT (Read-SrtBlocks cua SubPrep; nhan ca dau cham mili-giay)
_SRT_BLOCK = re.compile(
    r"(?m)^\s*(\d+)\s*\r?\n(\d{1,2}:\d{2}:\d{2}[,.]\d{2,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{2,3})"
    r"([^\r\n]*)\r?\n([\s\S]*?)(?=\r?\n\s*\r?\n|\r?\n?\Z)"
)


# --------------------------------------------------------------------------
#  Mốc thời gian
# --------------------------------------------------------------------------


def _hms(h: str, m: str, s: str, frac: str) -> float:
    return int(h) * 3600 + int(m) * 60 + int(s) + to_double("0." + frac)


def parse_stamp(t: str) -> float:
    """``"01:02:03,456"`` → 3723.456 (nhận cả ``,`` và ``.``; 2–3 chữ số lẻ như .bat: ``"0." + lẻ``)."""
    p = t.strip().split(":")
    sec, _, frac = p[2].replace(".", ",").partition(",")
    return int(p[0]) * 3600 + int(p[1]) * 60 + int(sec) + to_double("0." + (frac or "0"))


def format_stamp(sec: float) -> str:
    """Giây → ``HH:MM:SS,mmm``.

    SỬA LỖI .bat (TĐ-01): bản cũ dùng ``[int]$ts.TotalHours`` — PowerShell làm tròn nên
    0:45:00 thành ``01:45:00``. Ở đây giờ = phần nguyên. Mili-giây làm tròn tới gần nhất
    (như ``TimeSpan.FromSeconds`` của .NET Framework mà PowerShell 5.1 dùng).
    """
    if sec < 0:
        sec = 0.0
    total_ms = int(sec * 1000 + 0.5)
    h, rem = divmod(total_ms, 3_600_000)
    m, rem = divmod(rem, 60_000)
    s, ms = divmod(rem, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def cue_times_from_text(txt: str | None) -> list[float] | None:
    """Mốc BẮT ĐẦU của mọi dòng (giây, đã sắp xếp); ``None`` nếu không có (``Get-CueTimes``)."""
    if not txt:
        return None
    head = txt.lstrip("﻿").lstrip()
    if head.startswith("WEBVTT"):
        lst = [_hms(m.group(1) or "0", m.group(2), m.group(3), m.group(4)) for m in _VTT_CUE.finditer(txt)]
    else:
        lst = [_hms(*m.groups()) for m in _SRT_CUE.finditer(txt)]
        if not lst:
            lst = [_hms(*m.groups()) for m in _ASS_CUE.finditer(txt)]
    if not lst:
        return None
    return sorted(lst)


def cue_times(path: str | os.PathLike[str]) -> list[float] | None:
    """Đọc mốc bắt đầu từ file SRT/ASS/SSA/VTT. Không đọc được → ``None``."""
    try:
        txt = encoding.read_text(path)
    except OSError:
        return None
    return cue_times_from_text(txt)


def preview_times(txt: str | None) -> list[float]:
    """Mốc trong trường ``preview`` của SubSource (``Get-PreviewTimes``) — chỉ mẫu SRT, rỗng → ``[]``."""
    if not txt:
        return []
    return sorted(_hms(*m.groups()) for m in _SRT_CUE.finditer(txt))


def max_shift(before: Sequence[float] | None, after: Sequence[float] | None) -> float | None:
    """Độ dịch lớn nhất giữa trước/sau sync: lớn hơn giữa dòng đầu và dòng cuối (``Get-MaxShift``)."""
    if not before or not after:
        return None
    n = min(len(before), len(after))
    if n < 1:
        return None
    d1 = abs(after[0] - before[0])
    d2 = abs(after[n - 1] - before[n - 1])
    return d2 if d2 > d1 else d1


# --------------------------------------------------------------------------
#  Khối SRT
# --------------------------------------------------------------------------


@dataclass
class Block:
    start: float
    end: float
    tail: str  # phan sau moc thoi gian tren cung dong (vd toa do X1:...)
    text: str


def read_blocks_text(txt: str | None) -> list[Block] | None:
    if not txt:
        return None
    out = [
        Block(parse_stamp(m.group(2)), parse_stamp(m.group(3)), m.group(4), m.group(5).rstrip())
        for m in _SRT_BLOCK.finditer(txt)
    ]
    return out or None


def read_blocks(path: str | os.PathLike[str]) -> list[Block] | None:
    """``Read-SrtBlocks``: chỉ đọc SRT. File ASS/VTT → ``None`` (bước gọi tự bỏ qua, không đổi định dạng)."""
    try:
        txt = encoding.read_text(path)
    except OSError:
        return None
    return read_blocks_text(txt)


def blocks_to_text(blocks: Iterable[Block]) -> str:
    parts: list[str] = []
    for i, b in enumerate(blocks, 1):
        parts.append(f"{i}{NEWLINE}{format_stamp(b.start)} --> {format_stamp(b.end)}{b.tail}{NEWLINE}{b.text}{NEWLINE}{NEWLINE}")
    return "".join(parts)


def write_blocks(path: str | os.PathLike[str], blocks: Iterable[Block]) -> bool:
    """``Write-SrtBlocks``: đánh số lại, UTF-8 không BOM, CRLF."""
    try:
        encoding.write_text_utf8(path, blocks_to_text(blocks))
        return True
    except OSError:
        return False


def is_srt_path(path: str | os.PathLike[str]) -> bool:
    return os.fspath(path).lower().endswith(".srt")
