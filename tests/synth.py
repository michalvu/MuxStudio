"""Sinh dữ liệu phụ đề giả lập (không cần phim thật)."""
from __future__ import annotations

import random

from core.srt import NEWLINE, format_stamp


def make_ref_times(n: int = 700, start: float = 30.0, seed: int = 1, min_gap: float = 1.2, max_gap: float = 12.0) -> list[float]:
    """Mốc phụ đề "gốc" của một phim: khoảng cách ngẫu nhiên giữa các câu thoại."""
    rng = random.Random(seed)
    t = start
    out = []
    for _ in range(n):
        out.append(round(t, 3))
        t += rng.uniform(min_gap, max_gap)
    return out


def transform_times(ref: list[float], scale: float, offset: float, keep: float = 0.9,
                    jitter: float = 0.03, extra: int = 15, seed: int = 2) -> list[float]:
    """Mốc của "phụ đề cần đo" sao cho ``ref ≈ tgt × scale + offset``.

    Bỏ ngẫu nhiên một phần câu (``keep``), thêm nhiễu ``jitter`` giây, thêm ``extra`` câu lạ.
    """
    rng = random.Random(seed)
    out = []
    for r in ref:
        if rng.random() > keep:
            continue
        t = (r - offset) / scale + rng.uniform(-jitter, jitter)
        if t > 0:
            out.append(t)
    hi = max(out) if out else 100.0
    for _ in range(extra):
        out.append(rng.uniform(0, hi))
    return sorted(out)


def srt_text(times: list[float], dur: float = 2.0, texts: list[str] | None = None) -> str:
    parts = []
    for i, t in enumerate(times, 1):
        txt = texts[i - 1] if texts else f"Dòng {i}"
        parts.append(f"{i}{NEWLINE}{format_stamp(t)} --> {format_stamp(t + dur)}{NEWLINE}{txt}{NEWLINE}{NEWLINE}")
    return "".join(parts)


def pgs_segment(t: float, seg_type: int, payload: bytes) -> bytes:
    pts = int(round(t * 90000)) & 0xFFFFFFFF
    head = b"PG" + pts.to_bytes(4, "big") + b"\x00\x00\x00\x00" + bytes([seg_type]) + len(payload).to_bytes(2, "big")
    return head + payload


def pcs_payload(nobj: int, palette_only: bool = False) -> bytes:
    """PCS: rộng(2) cao(2) fps(1) số(2) trạng thái(1) cờ_bảng_màu(1) id_bảng_màu(1) số_đối_tượng(1) [+8 byte/đối tượng]."""
    body = bytearray(11)
    body[0:2] = (1920).to_bytes(2, "big")
    body[2:4] = (1080).to_bytes(2, "big")
    body[4] = 0x10
    body[8] = 0x80 if palette_only else 0x00
    body[10] = nobj
    body += bytes(8 * nobj)
    return bytes(body)


def pgs_stream(events: list[tuple[float, str]]) -> bytes:
    """events: (giây, loại) với loại ∈ show | hide | palette | other."""
    out = bytearray()
    for t, kind in events:
        if kind == "show":
            out += pgs_segment(t, 0x16, pcs_payload(1))
            out += pgs_segment(t, 0x17, bytes(10))  # WDS
            out += pgs_segment(t, 0x14, bytes(20))  # PDS
            out += pgs_segment(t, 0x15, bytes(30))  # ODS
            out += pgs_segment(t, 0x80, b"")  # END
        elif kind == "hide":
            out += pgs_segment(t, 0x16, pcs_payload(0))
            out += pgs_segment(t, 0x80, b"")
        elif kind == "palette":
            out += pgs_segment(t, 0x16, pcs_payload(1, palette_only=True))
            out += pgs_segment(t, 0x80, b"")
        else:
            out += pgs_segment(t, 0x14, bytes(20))
    return bytes(out)
