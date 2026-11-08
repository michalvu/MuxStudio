"""Đọc mốc thời gian từ phụ đề dạng ảnh PGS (``.sup``) — KHÔNG cần OCR.

Port ``Convert-PgsToTimingSrt`` (giống hệt ở cả 3 file .bat; chỉ khác chữ giữ chỗ).

Một segment:  ``'PG' | PTS(4) | DTS(4) | loại(1) | kích thước(2) | dữ liệu``
Loại ``0x16`` = PCS (Presentation Composition Segment):
  ``dữ liệu[8]``  = cờ "chỉ đổi bảng màu" → khác 0 thì bỏ qua
  ``dữ liệu[10]`` = số đối tượng đồ hoạ: > 0 = bắt đầu hiện, = 0 = xoá
Dòng chưa đóng mà gặp dòng mới thì tự đóng; dòng ≤ 0,1 s bị bỏ; dòng cuối chưa đóng bị bỏ (như .bat).
"""
from __future__ import annotations

import os

from . import encoding
from .srt import NEWLINE, format_stamp

PTS_HZ = 90000.0


def pgs_cues(data: bytes) -> list[tuple[float, float]]:
    """Danh sách (bắt đầu, kết thúc) theo giây."""
    if len(data) < 16:
        return []
    cues: list[tuple[float, float]] = []
    start = -1.0
    i = 0
    n = len(data)
    b = data
    while i + 13 <= n:
        if b[i] != 0x50 or b[i + 1] != 0x47:  # 'P' 'G'
            break
        pts = ((b[i + 2] << 24) | (b[i + 3] << 16) | (b[i + 4] << 8) | b[i + 5]) / PTS_HZ
        stype = b[i + 10]
        size = (b[i + 11] << 8) | b[i + 12]
        body = i + 13
        if stype == 0x16 and size >= 11 and (body + 10) < n:
            if b[body + 8] == 0:  # bo qua PCS chi doi bang mau
                nobj = b[body + 10]
                if start >= 0 and (pts - start) > 0.1:
                    cues.append((start, pts))
                start = pts if nobj > 0 else -1.0
        i = body + size
    return cues


def timing_srt_text(cues: list[tuple[float, float]], placeholder: str = "*") -> str:
    parts = [
        f"{k}{NEWLINE}{format_stamp(s)} --> {format_stamp(e)}{NEWLINE}{placeholder}{NEWLINE}{NEWLINE}"
        for k, (s, e) in enumerate(cues, 1)
    ]
    return "".join(parts)


def pgs_to_timing_srt(sup_path: str | os.PathLike[str], srt_path: str | os.PathLike[str], placeholder: str = "*") -> int:
    """Ghi SRT chỉ chứa mốc thời gian (mỗi dòng là ``placeholder`` — ``$PgsTimingText``). Trả số dòng; 0 = thất bại."""
    try:
        with open(sup_path, "rb") as fh:
            data = fh.read()
    except OSError:
        return 0
    cues = pgs_cues(data)
    if not cues:
        return 0
    try:
        encoding.write_text_utf8(srt_path, timing_srt_text(cues, placeholder))
    except OSError:
        return 0
    return len(cues)
