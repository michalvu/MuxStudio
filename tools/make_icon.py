"""Vẽ icon MuxStudio → ``assets/muxstudio.ico`` (16–256 px, cho ``MuxStudio.exe`` + cửa sổ Windows) và
``assets/muxstudio.png`` (256 px, cho cửa sổ trên Linux / tài liệu).

Python thuần (không cần Pillow): mỗi hình là một "hàm khoảng cách có dấu" (SDF), độ phủ điểm ảnh = 0,5 − khoảng
cách (đơn vị điểm ảnh) → viền mượt ở mọi cỡ. Hình: ô vuông bo góc màu xanh (theme "blue" của CustomTkinter), nút
▶ trắng (phim) + hai vạch phụ đề (vàng, trắng). Cỡ ≤ 24 px vẽ bản đơn giản (một vạch dày) cho dễ nhìn.

File .ico: cỡ 16–128 dạng BMP 32 bit (kèm mặt nạ AND), 256 dạng PNG — như icon của chính Windows.

Chạy lại (chỉ khi muốn đổi icon): ``python tools/make_icon.py``. File kết quả đã có sẵn trong repo, ``build.bat``
không cần chạy script này.
"""
from __future__ import annotations

import math
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ICO_PATH = ROOT / "assets" / "muxstudio.ico"
PNG_PATH = ROOT / "assets" / "muxstudio.png"
SIZES = (16, 24, 32, 48, 64, 128, 256)
PNG_IN_ICO = (256,)  # cac co ghi dang PNG trong .ico (con lai BMP)

TOP = (59, 142, 208)  # #3B8ED0 - mau nut cua theme "blue" (CustomTkinter)
BOTTOM = (20, 78, 138)  # xanh dam o day
WHITE = (255, 255, 255)
YELLOW = (255, 213, 74)  # #FFD54A - phu de

Color = tuple[int, int, int]


# ---------------------------------------------------------------- hinh hoc (toa do 0..1, y huong xuong)


def sd_round_box(px: float, py: float, x0: float, y0: float, x1: float, y1: float, r: float) -> float:
    """Khoảng cách có dấu tới hình chữ nhật bo góc (âm = bên trong)."""
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    bx, by = (x1 - x0) / 2 - r, (y1 - y0) / 2 - r
    qx, qy = abs(px - cx) - bx, abs(py - cy) - by
    ox, oy = max(qx, 0.0), max(qy, 0.0)
    return math.sqrt(ox * ox + oy * oy) + min(max(qx, qy), 0.0) - r


def sd_triangle(px: float, py: float, pts: tuple[tuple[float, float], ...]) -> float:
    """Khoảng cách có dấu (xấp xỉ: giao của 3 nửa mặt phẳng — đủ cho làm mượt viền)."""
    cx = sum(p[0] for p in pts) / 3
    cy = sum(p[1] for p in pts) / 3
    d = -1e9
    for i in range(3):
        ax, ay = pts[i]
        bx, by = pts[(i + 1) % 3]
        nx, ny = by - ay, -(bx - ax)
        ln = math.sqrt(nx * nx + ny * ny)
        nx, ny = nx / ln, ny / ln
        if (cx - ax) * nx + (cy - ay) * ny > 0:  # phap tuyen phai huong ra ngoai
            nx, ny = -nx, -ny
        d = max(d, (px - ax) * nx + (py - ay) * ny)
    return d


def design(size: int) -> tuple[tuple, tuple, list[tuple]]:
    """(nền, tam giác, [vạch phụ đề…]) cho một cỡ."""
    if size <= 24:  # ban don gian: hinh to, mot vach day
        bg = (0.0, 0.0, 1.0, 1.0, 0.2)
        tri = ((0.34, 0.14), (0.34, 0.60), (0.76, 0.37))
        bars = [((0.16, 0.70, 0.84, 0.88, 0.09), YELLOW)]
    else:
        bg = (0.03, 0.03, 0.97, 0.97, 0.22)
        tri = ((0.39, 0.19), (0.39, 0.59), (0.72, 0.39))
        bars = [((0.20, 0.675, 0.80, 0.765, 0.045), YELLOW), ((0.31, 0.805, 0.69, 0.885, 0.04), WHITE)]
    return bg, tri, bars


def render(size: int) -> list[list[tuple[int, int, int, int]]]:
    """Ảnh RGBA (alpha thẳng, không nhân trước) — danh sách hàng, mỗi hàng ``size`` điểm ảnh."""
    bg, tri, bars = design(size)
    rows = []
    for j in range(size):
        y = (j + 0.5) / size
        t = min(1.0, max(0.0, (y - bg[1]) / (bg[3] - bg[1])))
        base = tuple(TOP[k] + (BOTTOM[k] - TOP[k]) * t for k in range(3))
        row = []
        for i in range(size):
            x = (i + 0.5) / size

            def cov(d: float) -> float:
                return min(1.0, max(0.0, 0.5 - d * size))

            a = cov(sd_round_box(x, y, *bg))
            if a <= 0.0:
                row.append((0, 0, 0, 0))
                continue
            col = list(base)
            for c, layer in [(cov(sd_triangle(x, y, tri)), WHITE)] + [(cov(sd_round_box(x, y, *b)), clr) for b, clr in bars]:
                if c > 0.0:
                    col = [layer[k] * c + col[k] * (1.0 - c) for k in range(3)]
            row.append((round(col[0]), round(col[1]), round(col[2]), round(a * 255)))
        rows.append(row)
    return rows


# ---------------------------------------------------------------- ghi file


def png_bytes(rows: list[list[tuple[int, int, int, int]]]) -> bytes:
    h, w = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + bytes(v for px in r for v in px) for r in rows)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def bmp_icon_bytes(rows: list[list[tuple[int, int, int, int]]]) -> bytes:
    """Ảnh trong .ico dạng BMP 32 bit: BITMAPINFOHEADER (cao ×2) + điểm ảnh BGRA từ dưới lên + mặt nạ AND 1 bit."""
    h, w = len(rows), len(rows[0])
    xor = b"".join(bytes(v for r_, g, b, a in r for v in (b, g, r_, a)) for r in reversed(rows))
    stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for r in reversed(rows):
        line = bytearray(stride)
        for i, px in enumerate(r):
            if px[3] == 0:  # trong suot hoan toan
                line[i // 8] |= 0x80 >> (i % 8)
        mask += line
    hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return hdr + xor + bytes(mask)


def ico_bytes(images: dict[int, list[list[tuple[int, int, int, int]]]]) -> bytes:
    sizes = sorted(images)
    blobs = [png_bytes(images[s]) if s in PNG_IN_ICO else bmp_icon_bytes(images[s]) for s in sizes]
    out = struct.pack("<HHH", 0, 1, len(sizes))
    off = 6 + 16 * len(sizes)
    for s, b in zip(sizes, blobs):
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(b), off)
        off += len(b)
    return out + b"".join(blobs)


def main(argv: list[str] | None = None) -> int:
    images = {s: render(s) for s in SIZES}
    ICO_PATH.parent.mkdir(parents=True, exist_ok=True)
    ICO_PATH.write_bytes(ico_bytes(images))
    PNG_PATH.write_bytes(png_bytes(images[256]))
    print(f"Đã ghi {ICO_PATH} ({ICO_PATH.stat().st_size} byte, cỡ {', '.join(map(str, SIZES))})")
    print(f"Đã ghi {PNG_PATH} ({PNG_PATH.stat().st_size} byte)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
